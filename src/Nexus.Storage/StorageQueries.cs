using Microsoft.Data.Sqlite;
using Nexus.Database;

namespace Nexus.Storage;

public sealed record FolderStat(string Path, string Name, long Size, long OwnSize, long Files, long Dirs);
public sealed record FileStat(string Directory, string Name, string Extension, long Size, long ModifiedTicks)
{
    public string FullPath => Directory + "\\" + Name;
}

/// <summary>Read-only questions about sizes, answered from the index and dir_stats.</summary>
public sealed class StorageQueries(NexusDatabase db)
{
    public FolderStat? GetFolder(string path)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,size,own_size,files,dirs FROM dir_stats WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Folder(r) : null;
    }

    /// <summary>Creation time (UTC ticks) of an indexed file or folder, or null if it is not in the index.</summary>
    public long? GetCreatedTicks(string path)
    {
        int i = path.LastIndexOf('\\');
        if (i <= 0) return null;
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT created FROM files WHERE dir=$d AND name=$n";
        cmd.Parameters.AddWithValue("$d", path[..i]);
        cmd.Parameters.AddWithValue("$n", path[(i + 1)..]);
        return cmd.ExecuteScalar() is long t && t > 0 ? t : null;
    }

    /// <summary>Direct sub-folders, largest first.</summary>
    public List<FolderStat> GetChildFolders(string path, int limit = 500)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,size,own_size,files,dirs FROM dir_stats WHERE parent=$p ORDER BY size DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadFolders(cmd);
    }

    /// <summary>Files directly inside a folder, largest first, plus how many files exist and their total size.</summary>
    public (List<FileStat> Files, long TotalCount, long TotalSize) GetChildFiles(string path, int limit)
    {
        using var c = db.Open();
        var list = new List<FileStat>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT dir,name,ext,size,modified FROM files WHERE dir=$p AND is_dir=0 ORDER BY size DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(File(r));
        }
        using var tot = c.CreateCommand();
        tot.CommandText = "SELECT COUNT(*),COALESCE(SUM(size),0) FROM files WHERE dir=$p AND is_dir=0";
        tot.Parameters.AddWithValue("$p", path);
        using var r2 = tot.ExecuteReader();
        r2.Read();
        return (list, r2.GetInt64(0), r2.GetInt64(1));
    }

    /// <summary>Largest files anywhere under <paramref name="scope"/> (null = whole index).</summary>
    public List<FileStat> TopFiles(string? scope, int limit)
    {
        // Wide scope: walk the size index from the largest file and filter (unary + keeps the planner off the dir index).
        // Narrow scope: read that folder's few files through the dir index and sort them.
        bool wide = scope is not null && (GetFolder(scope)?.Files ?? 0) > 50_000;
        string d = wide ? "+dir" : "dir";
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        string where = scope is null ? "" : $"AND ({d}=$p OR ({d}>=$lo AND {d}<$hi))";
        cmd.CommandText = $"SELECT dir,name,ext,size,modified FROM files WHERE is_dir=0 {where} ORDER BY size DESC LIMIT $l";
        Scope(cmd, scope);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<FileStat>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(File(r));
        return list;
    }

    /// <summary>Folders holding the most data *directly* (not through sub-folders), so a parent never hides the real culprit.</summary>
    public List<FolderStat> TopFolders(string? scope, int limit)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        string where = scope is null ? "" : "WHERE path=$p OR (path>=$lo AND path<$hi)";
        cmd.CommandText = $"SELECT path,size,own_size,files,dirs FROM dir_stats {where} ORDER BY own_size DESC LIMIT $l";
        Scope(cmd, scope);
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadFolders(cmd);
    }

    /// <summary>Total size and file count of everything under a scope.</summary>
    public (long Size, long Files) Totals(string? scope)
    {
        if (scope is null)
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(own_size),0),COALESCE(SUM(own_files),0) FROM dir_stats";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1));
        }
        var f = GetFolder(scope);
        return (f?.Size ?? 0, f?.Files ?? 0);
    }

    internal static void Scope(SqliteCommand cmd, string? scope)
    {
        if (scope is null) return;
        cmd.Parameters.AddWithValue("$p", scope);
        cmd.Parameters.AddWithValue("$lo", scope + "\\");
        cmd.Parameters.AddWithValue("$hi", scope + "]"); // '\' and ']' are adjacent: [lo,hi) = everything below scope
    }

    internal static List<FolderStat> ReadFolders(SqliteCommand cmd)
    {
        var list = new List<FolderStat>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Folder(r));
        return list;
    }

    private static FolderStat Folder(SqliteDataReader r)
    {
        string path = r.GetString(0);
        int i = path.LastIndexOf('\\');
        return new FolderStat(path, i >= 0 ? path[(i + 1)..] : path, r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    private static FileStat File(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4));
}
