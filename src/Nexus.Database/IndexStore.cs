using Microsoft.Data.Sqlite;
using Nexus.Core.Models;

namespace Nexus.Database;

public readonly record struct StoredEntry(long Size, long Modified, int IsDir);

/// <summary>
/// Single-connection, single-transaction store used by the indexer worker thread: reads see its own pending writes,
/// and writes are grouped (call <see cref="Commit"/> every few thousand operations). Not thread-safe.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly NexusDatabase _db;
    private SqliteTransaction? _tx; // opened by the first write only: the write lock is never held while idle
    private readonly SqliteCommand _upsert, _getMtime, _getEntries, _getSubdirs, _setDir;
    private readonly SqliteParameter _uDir, _uName, _uExt, _uSize, _uCreated, _uModified, _uAccessed, _uIsDir;

    public int PendingOps { get; private set; }

    public IndexStore(NexusDatabase db)
    {
        _db = db;
        _c = db.Open();

        _upsert = Cmd("""
            INSERT INTO files(dir,name,ext,size,created,modified,accessed,is_dir) VALUES($d,$n,$e,$s,$c,$m,$a,$i)
            ON CONFLICT(dir, name) DO UPDATE SET name=excluded.name,ext=excluded.ext,size=excluded.size,
                created=excluded.created,modified=excluded.modified,accessed=excluded.accessed,is_dir=excluded.is_dir
            """);
        _uDir = _upsert.Parameters.Add("$d", SqliteType.Text);
        _uName = _upsert.Parameters.Add("$n", SqliteType.Text);
        _uExt = _upsert.Parameters.Add("$e", SqliteType.Text);
        _uSize = _upsert.Parameters.Add("$s", SqliteType.Integer);
        _uCreated = _upsert.Parameters.Add("$c", SqliteType.Integer);
        _uModified = _upsert.Parameters.Add("$m", SqliteType.Integer);
        _uAccessed = _upsert.Parameters.Add("$a", SqliteType.Integer);
        _uIsDir = _upsert.Parameters.Add("$i", SqliteType.Integer);

        _getMtime = Cmd("SELECT mtime FROM dirs WHERE path=$p");
        _getMtime.Parameters.Add("$p", SqliteType.Text);
        _getEntries = Cmd("SELECT name,size,modified,is_dir FROM files WHERE dir=$p");
        _getEntries.Parameters.Add("$p", SqliteType.Text);
        _getSubdirs = Cmd("SELECT name FROM files WHERE dir=$p AND is_dir=1");
        _getSubdirs.Parameters.Add("$p", SqliteType.Text);
        _setDir = Cmd("INSERT INTO dirs(path,mtime) VALUES($p,$m) ON CONFLICT(path) DO UPDATE SET mtime=excluded.mtime");
        _setDir.Parameters.Add("$p", SqliteType.Text);
        _setDir.Parameters.Add("$m", SqliteType.Integer);
    }

    private SqliteCommand Cmd(string sql)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private SqliteCommand Run(string sql, params (string, object)[] args)
    {
        var cmd = Cmd(sql);
        cmd.Transaction = _tx;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return cmd;
    }

    // ---- writes ----

    private void EnsureTx() => _tx ??= _c.BeginTransaction();

    public void Upsert(FileEntry e)
    {
        EnsureTx();
        _uDir.Value = e.Directory; _uName.Value = e.Name; _uExt.Value = e.Extension; _uSize.Value = e.Size;
        _uCreated.Value = e.CreatedTicks; _uModified.Value = e.ModifiedTicks; _uAccessed.Value = e.AccessedTicks;
        _uIsDir.Value = e.IsDirectory ? (e.IsReparsePoint ? 2 : 1) : 0;
        _upsert.Transaction = _tx;
        _upsert.ExecuteNonQuery();
        PendingOps++;
    }

    public void SetDir(string path, long mtime)
    {
        EnsureTx();
        _setDir.Parameters["$p"].Value = path;
        _setDir.Parameters["$m"].Value = mtime;
        _setDir.Transaction = _tx;
        _setDir.ExecuteNonQuery();
        PendingOps++;
    }

    /// <summary>Removes an entry and, if it was a folder, everything below it (index only; never touches real files).</summary>
    public void DeleteTree(string parentDir, string name)
    {
        EnsureTx();
        string path = parentDir + "\\" + name;
        using (var c1 = Run("DELETE FROM files WHERE dir=$d AND name=$n", ("$d", parentDir), ("$n", name))) c1.ExecuteNonQuery();
        DeleteBelow(path);
        PendingOps++;
    }

    private void DeleteBelow(string path)
    {
        string lo = path + "\\", hi = path + "]"; // '\' and ']' are adjacent: [lo, hi) is every path under `path`
        using (var c1 = Run("DELETE FROM files WHERE dir=$p OR (dir>=$lo AND dir<$hi)", ("$p", path), ("$lo", lo), ("$hi", hi))) c1.ExecuteNonQuery();
        using (var c2 = Run("DELETE FROM dirs WHERE path=$p OR (path>=$lo AND path<$hi)", ("$p", path), ("$lo", lo), ("$hi", hi))) c2.ExecuteNonQuery();
    }

    /// <summary>Renames/moves one entry keeping its id. If it is a folder, the whole subtree follows.</summary>
    public bool Move(string oldDir, string oldName, string newDir, string newName, string newExt)
    {
        EnsureTx();
        string oldPath = oldDir + "\\" + oldName, newPath = newDir + "\\" + newName;
        long? id;
        using (var q = Run("SELECT id,is_dir FROM files WHERE dir=$d AND name=$n", ("$d", oldDir), ("$n", oldName)))
        using (var r = q.ExecuteReader())
        {
            if (!r.Read()) return false;
            id = r.GetInt64(0);
        }
        bool sameKey = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase); // case-only rename
        if (!sameKey) DeleteTree(newDir, newName); // clear any stale entry at the destination
        using (var u = Run("UPDATE files SET dir=$nd,name=$nn,ext=$ne WHERE id=$id",
                   ("$nd", newDir), ("$nn", newName), ("$ne", newExt), ("$id", id.Value))) u.ExecuteNonQuery();

        string lo = oldPath + "\\", hi = oldPath + "]";
        using (var u2 = Run("UPDATE files SET dir=$new || substr(dir,$len) WHERE dir=$old OR (dir>=$lo AND dir<$hi)",
                   ("$new", newPath), ("$len", oldPath.Length + 1), ("$old", oldPath), ("$lo", lo), ("$hi", hi))) u2.ExecuteNonQuery();
        using (var u3 = Run("UPDATE OR REPLACE dirs SET path=$new || substr(path,$len) WHERE path=$old OR (path>=$lo AND path<$hi)",
                   ("$new", newPath), ("$len", oldPath.Length + 1), ("$old", oldPath), ("$lo", lo), ("$hi", hi))) u3.ExecuteNonQuery();
        PendingOps++;
        return true;
    }

    public void Commit()
    {
        if (PendingOps > 0)
        {
            // Generation counter: lets other modules (folder sizes) know the index changed since they last computed.
            using var g = Run("INSERT INTO meta(key,value) VALUES('index_gen','1') ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1");
            g.ExecuteNonQuery();
        }
        if (_tx is null) return;
        _tx.Commit();
        _tx.Dispose();
        _tx = null;
        PendingOps = 0;

        // The indexer writes in long runs: let a waiting writer (folder sizes, history…) through between two batches.
        long start = Environment.TickCount64;
        while (_db.ExternalWriterWaiting && Environment.TickCount64 - start < 10_000) Thread.Sleep(5);
    }

    // ---- reads ----

    public long? GetDirMtime(string path)
    {
        _getMtime.Parameters["$p"].Value = path;
        _getMtime.Transaction = _tx;
        return _getMtime.ExecuteScalar() is long l ? l : null;
    }

    public Dictionary<string, StoredEntry> GetEntries(string dir)
    {
        var map = new Dictionary<string, StoredEntry>(StringComparer.OrdinalIgnoreCase);
        _getEntries.Parameters["$p"].Value = dir;
        _getEntries.Transaction = _tx;
        using var r = _getEntries.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = new StoredEntry(r.GetInt64(1), r.GetInt64(2), (int)r.GetInt64(3));
        return map;
    }

    public List<string> GetSubdirNames(string dir)
    {
        var list = new List<string>();
        _getSubdirs.Parameters["$p"].Value = dir;
        _getSubdirs.Transaction = _tx;
        using var r = _getSubdirs.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public StoredEntry? GetEntry(string dir, string name)
    {
        using var q = Run("SELECT size,modified,is_dir FROM files WHERE dir=$d AND name=$n", ("$d", dir), ("$n", name));
        using var r = q.ExecuteReader();
        return r.Read() ? new StoredEntry(r.GetInt64(0), r.GetInt64(1), (int)r.GetInt64(2)) : null;
    }

    /// <summary>Larger page cache and fewer WAL checkpoints for a long run of writes; call with defaults to give the memory back.</summary>
    public void Tune(int cacheMb, int walPages)
    {
        if (_tx is not null) Commit();
        using var q = Cmd($"PRAGMA cache_size=-{cacheMb * 1024}; PRAGMA wal_autocheckpoint={walPages}; PRAGMA shrink_memory;");
        q.ExecuteNonQuery();
    }

    /// <summary>True if this exact path is indexed as a folder.</summary>
    public bool IsIndexedDirectory(string parentDir, string name)
    {
        using var q = Run("SELECT 1 FROM files WHERE dir=$d AND name=$n AND is_dir<>0", ("$d", parentDir), ("$n", name));
        return q.ExecuteScalar() is not null;
    }

    /// <summary>Full paths of the entries with a tilde somewhere in their path.</summary>
    public List<string> GetPathsWithTilde()
    {
        var list = new List<string>();
        using var q = Run(@"SELECT dir || '\' || name FROM files WHERE dir LIKE '%~%' OR name LIKE '%~%'");
        using var r = q.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public long CountFiles()
    {
        using var q = Run("SELECT COUNT(*) FROM files WHERE is_dir=0");
        return Convert.ToInt64(q.ExecuteScalar());
    }

    // ---- roots & meta ----

    public List<string> GetRoots()
    {
        var list = new List<string>();
        using var q = Run("SELECT path FROM index_roots ORDER BY added");
        using var r = q.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void AddRoot(string path)
    {
        EnsureTx();
        using var q = Run("INSERT OR IGNORE INTO index_roots(path,added) VALUES($p,$t)", ("$p", path), ("$t", DateTime.UtcNow.Ticks));
        q.ExecuteNonQuery();
    }

    public void RemoveRoot(string path)
    {
        EnsureTx();
        using (var q = Run("DELETE FROM index_roots WHERE path=$p", ("$p", path))) q.ExecuteNonQuery();
        DeleteBelow(path);
    }

    public string? GetMeta(string key)
    {
        using var q = Run("SELECT value FROM meta WHERE key=$k", ("$k", key));
        return q.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        EnsureTx();
        using var q = Run("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
        q.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { _tx?.Commit(); } catch (InvalidOperationException) { }
        _tx?.Dispose();
        foreach (var c in new[] { _upsert, _getMtime, _getEntries, _getSubdirs, _setDir }) c.Dispose();
        _c.Dispose();
    }
}
