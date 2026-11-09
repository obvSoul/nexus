using Microsoft.Data.Sqlite;
using Nexus.Core.Indexing;

namespace Nexus.Database;

/// <summary>Remembers file hashes between duplicate searches. Losing it only costs a re-hash, so write errors are not fatal.</summary>
public sealed class HashCacheStore : IDisposable
{
    private readonly record struct Pending(long Id, long Size, long Modified, long? Quick, byte[]? Sha);

    private const int FlushEvery = 500;
    private readonly NexusDatabase _db;
    private readonly INexusLog _log;
    private readonly SqliteConnection _c;
    private readonly SqliteCommand _get;
    private readonly Dictionary<long, Pending> _pending = [];

    public HashCacheStore(NexusDatabase db, INexusLog? log = null)
    {
        _db = db;
        _log = log ?? NullLog.Instance;
        _c = db.Open();
        _get = _c.CreateCommand();
        _get.CommandText = "SELECT quick,sha256 FROM hash_cache WHERE file_id=$i AND size=$s AND modified=$m";
        _get.Parameters.Add("$i", SqliteType.Integer);
        _get.Parameters.Add("$s", SqliteType.Integer);
        _get.Parameters.Add("$m", SqliteType.Integer);
        _get.Prepare();
    }

    public (long? Quick, byte[]? Sha) Get(long id, long size, long modified)
    {
        if (_pending.TryGetValue(id, out var p) && p.Size == size && p.Modified == modified) return (p.Quick, p.Sha);
        _get.Parameters["$i"].Value = id;
        _get.Parameters["$s"].Value = size;
        _get.Parameters["$m"].Value = modified;
        using var r = _get.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : (byte[])r.GetValue(1));
    }

    public void Put(long id, long size, long modified, long? quick, byte[]? sha)
    {
        _pending[id] = new Pending(id, size, modified, quick, sha);
        if (_pending.Count >= FlushEvery) Flush();
    }

    public void Flush()
    {
        if (_pending.Count == 0) return;
        try
        {
            using var scope = _db.ExternalWrite();
            using var tx = _c.BeginTransaction();
            using var cmd = _c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO hash_cache(file_id,size,modified,quick,sha256) VALUES($i,$s,$m,$q,$h)
                ON CONFLICT(file_id) DO UPDATE SET size=excluded.size,modified=excluded.modified,quick=excluded.quick,sha256=excluded.sha256
                """;
            var i = cmd.Parameters.Add("$i", SqliteType.Integer);
            var s = cmd.Parameters.Add("$s", SqliteType.Integer);
            var m = cmd.Parameters.Add("$m", SqliteType.Integer);
            var q = cmd.Parameters.Add("$q", SqliteType.Integer);
            var h = cmd.Parameters.Add("$h", SqliteType.Blob);
            foreach (var p in _pending.Values)
            {
                i.Value = p.Id; s.Value = p.Size; m.Value = p.Modified;
                q.Value = p.Quick is { } v ? v : DBNull.Value;
                h.Value = p.Sha is { } b ? b : DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (SqliteException ex)
        {
            _log.Warn("Hash cache not saved: " + ex.Message);
        }
        _pending.Clear();
    }

    /// <summary>Forgets hashes of files that are no longer in the index.</summary>
    public void RemoveOrphans()
    {
        try
        {
            using var scope = _db.ExternalWrite();
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "DELETE FROM hash_cache WHERE file_id NOT IN (SELECT id FROM files)";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) { _log.Warn("Hash cache cleanup skipped: " + ex.Message); }
    }

    public void Dispose()
    {
        Flush();
        _get.Dispose();
        _c.Dispose();
    }
}
