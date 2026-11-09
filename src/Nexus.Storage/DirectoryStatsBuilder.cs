using Microsoft.Data.Sqlite;
using Nexus.Database;

namespace Nexus.Storage;

public sealed record StatsInfo(bool Exists, bool Stale, DateTime? ComputedUtc, long Folders);

/// <summary>
/// Computes folder sizes (subtree totals, file and folder counts) from the file index in one pass and stores them in
/// dir_stats. Nothing is read from the disk: sizes are the *logical* file sizes recorded by the indexer.
/// </summary>
public sealed class DirectoryStatsBuilder(NexusDatabase db)
{
    private sealed class Acc
    {
        public long Size, OwnSize, Files, OwnFiles, Dirs;
    }

    public StatsInfo GetInfo()
    {
        string? gen = db.GetMeta("index_gen"), statsGen = db.GetMeta("stats_gen"), at = db.GetMeta("stats_at");
        bool exists = statsGen is not null;
        DateTime? when = long.TryParse(at, out var t) ? new DateTime(t, DateTimeKind.Utc) : null;
        long folders = 0;
        if (exists)
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM dir_stats";
            folders = Convert.ToInt64(cmd.ExecuteScalar());
        }
        return new StatsInfo(exists, !exists || gen != statsGen, when, folders);
    }

    /// <summary>Rebuilds dir_stats. Takes a few seconds for a million files; safe to run while the indexer works.</summary>
    public long Rebuild(CancellationToken ct = default, IProgress<string>? progress = null)
    {
        lock (RebuildGate) return RebuildCore(ct, progress); // several pages may ask at the same time
    }

    private static readonly object RebuildGate = new();

    private long RebuildCore(CancellationToken ct, IProgress<string>? progress)
    {
        string? genAtStart = db.GetMeta("index_gen") ?? "0";
        var map = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);

        progress?.Report("Lecture de l'index…");
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT dir,name,size,is_dir FROM files";
            using var r = cmd.ExecuteReader();
            long n = 0;
            while (r.Read())
            {
                if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                string dir = r.GetString(0);
                if (!map.TryGetValue(dir, out var parent)) map[dir] = parent = new Acc();
                if (r.GetInt64(3) == 0)
                {
                    long size = r.GetInt64(2);
                    parent.OwnSize += size; parent.OwnFiles++;
                }
                else
                {
                    parent.Dirs++;
                    string child = dir + "\\" + r.GetString(1);
                    if (!map.ContainsKey(child)) map[child] = new Acc();
                }
            }
        }

        progress?.Report("Calcul des totaux…");
        foreach (var a in map.Values) { a.Size = a.OwnSize; a.Files = a.OwnFiles; }
        // Children are always longer than their parent, so processing by decreasing length folds each folder into its parent once.
        foreach (var path in map.Keys.OrderByDescending(k => k.Length).ToList())
        {
            ct.ThrowIfCancellationRequested();
            int i = path.LastIndexOf('\\');
            if (i <= 0) continue;
            if (map.TryGetValue(path[..i], out var parent))
            {
                var me = map[path];
                parent.Size += me.Size; parent.Files += me.Files;
            }
        }

        progress?.Report("Enregistrement…");
        using (db.ExternalWrite())
        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            using (var del = c.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM dir_stats"; del.ExecuteNonQuery(); }
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO dir_stats(path,parent,size,own_size,files,own_files,dirs) VALUES($p,$par,$s,$os,$f,$of,$d)";
            var p = ins.Parameters.Add("$p", SqliteType.Text);
            var par = ins.Parameters.Add("$par", SqliteType.Text);
            var s = ins.Parameters.Add("$s", SqliteType.Integer);
            var os = ins.Parameters.Add("$os", SqliteType.Integer);
            var f = ins.Parameters.Add("$f", SqliteType.Integer);
            var of = ins.Parameters.Add("$of", SqliteType.Integer);
            var d = ins.Parameters.Add("$d", SqliteType.Integer);
            ins.Prepare();
            int n = 0;
            foreach (var (path, a) in map)
            {
                if ((++n & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                int i = path.LastIndexOf('\\');
                p.Value = path; par.Value = i > 0 ? path[..i] : "";
                s.Value = a.Size; os.Value = a.OwnSize; f.Value = a.Files; of.Value = a.OwnFiles; d.Value = a.Dirs;
                ins.ExecuteNonQuery();
            }
            using (var m = c.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = "INSERT INTO meta(key,value) VALUES('stats_gen',$g),('stats_at',$t) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                m.Parameters.AddWithValue("$g", genAtStart);
                m.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks.ToString());
                m.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return map.Count;
    }
}
