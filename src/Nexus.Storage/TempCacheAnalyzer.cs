using Microsoft.Data.Sqlite;
using Nexus.Database;

namespace Nexus.Storage;

public sealed record KnownLocation(string Label, string Path, long Size, long Files);
public sealed record TempExtension(string Extension, long Count, long Size);

/// <summary>
/// Finds temporary files and cache folders from the index. It only measures and lists: nothing is ever deleted by NEXUS
/// (caches belong to applications, which may misbehave if emptied behind their back).
/// </summary>
public sealed class TempCacheAnalyzer(NexusDatabase db)
{
    public static readonly string[] TempExtensions = ["tmp", "temp", "dmp", "chk", "old", "gid", "etl"];

    /// <summary>Folder names that applications use for disposable data.</summary>
    public static readonly string[] CacheFolderNames =
        ["Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnCache", "cache2", "CacheStorage", "Service Worker", "Crashpad", "INetCache", "Temp", "Temporary Internet Files"];

    /// <summary>Well-known system and user temporary locations (measured if present in the index).</summary>
    public static IEnumerable<(string Label, string Path)> KnownTempLocations()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return ("Temp utilisateur", Path.Combine(local, "Temp"));
        yield return ("Temp Windows", Path.Combine(win, "Temp"));
        yield return ("Mises à jour Windows (téléchargées)", Path.Combine(win, "SoftwareDistribution", "Download"));
        yield return ("Fichiers Internet temporaires", Path.Combine(local, "Microsoft", "Windows", "INetCache"));
        yield return ("Rapports d'erreurs Windows", Path.Combine(local, "Microsoft", "Windows", "WER"));
        yield return ("Cache de shaders DirectX", Path.Combine(local, "D3DSCache"));
        yield return ("Journaux Windows", Path.Combine(win, "Logs"));
    }

    public List<KnownLocation> MeasureKnownLocations() =>
        KnownTempLocations().Select(k =>
        {
            var s = new StorageQueries(db).GetFolder(k.Path.TrimEnd('\\'));
            return s is null ? null : new KnownLocation(k.Label, k.Path, s.Size, s.Files);
        }).Where(x => x is not null).Select(x => x!).OrderByDescending(x => x.Size).ToList();

    /// <summary>Count and total size of files with temp-like extensions.</summary>
    public List<TempExtension> TempFilesByExtension()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        var ps = TempExtensions.Select((e, i) => "$e" + i).ToArray();
        cmd.CommandText = $"SELECT ext,COUNT(*),COALESCE(SUM(size),0) FROM files WHERE is_dir=0 AND ext IN ({string.Join(",", ps)}) GROUP BY ext ORDER BY 3 DESC";
        for (int i = 0; i < ps.Length; i++) cmd.Parameters.AddWithValue(ps[i], TempExtensions[i]);
        var list = new List<TempExtension>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new TempExtension(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public List<FileStat> BiggestTempFiles(int limit)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        var ps = TempExtensions.Select((e, i) => "$e" + i).ToArray();
        cmd.CommandText = $"SELECT dir,name,ext,size,modified FROM files WHERE is_dir=0 AND ext IN ({string.Join(",", ps)}) ORDER BY size DESC LIMIT $l";
        for (int i = 0; i < ps.Length; i++) cmd.Parameters.AddWithValue(ps[i], TempExtensions[i]);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<FileStat>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new FileStat(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>
    /// Cache-like folders, biggest first. A folder inside another detected cache folder is not listed again,
    /// so the sizes can be added up without double counting.
    /// </summary>
    public List<FolderStat> FindCacheFolders(int limit, string? scope = null)
    {
        var hits = new List<FolderStat>();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            var clauses = CacheFolderNames.Select((n, i) => $"path LIKE $n{i} ESCAPE '^'").ToArray();
            string within = scope is null ? "" : "AND path>=$lo AND path<$hi";
            cmd.CommandText = $"SELECT path,size,own_size,files,dirs FROM dir_stats WHERE size > 0 {within} AND ({string.Join(" OR ", clauses)}) ORDER BY path";
            if (scope is not null)
            {
                cmd.Parameters.AddWithValue("$lo", scope + "\\");
                cmd.Parameters.AddWithValue("$hi", scope + "]");
            }
            for (int i = 0; i < CacheFolderNames.Length; i++)
                cmd.Parameters.AddWithValue("$n" + i, "%\\" + CacheFolderNames[i].Replace("^", "^^").Replace("%", "^%").Replace("_", "^_"));
            hits = StorageQueries.ReadFolders(cmd);
        }
        // Sorted by path, an ancestor always comes right before its descendants.
        var kept = new List<FolderStat>();
        var keptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits.OrderBy(h => h.Path.Length)) // shallow first: ancestors are decided before descendants
        {
            bool nested = false;
            for (int i = h.Path.LastIndexOf('\\'); i > 0 && !nested; i = h.Path.LastIndexOf('\\', i - 1))
                nested = keptPaths.Contains(h.Path[..i]);
            if (nested) continue;
            kept.Add(h);
            keptPaths.Add(h.Path);
        }
        return kept.OrderByDescending(k => k.Size).Take(limit).ToList();
    }
}
