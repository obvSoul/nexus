using Nexus.Core.Models;

namespace Nexus.Database;

/// <summary>Write/read access to the files table. Writes are batched in one transaction.</summary>
public sealed class FileRepository(NexusDatabase db)
{
    /// <summary>Inserts or replaces rows (matched on dir+name, case-insensitive). Returns the row count written.</summary>
    public int UpsertBatch(IEnumerable<FileEntry> rows)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO files(dir,name,ext,size,created,modified,accessed,is_dir)
            VALUES($d,$n,$e,$s,$c,$m,$a,$i)
            ON CONFLICT(dir COLLATE NOCASE, name COLLATE NOCASE) DO UPDATE SET name=excluded.name,ext=excluded.ext,size=excluded.size,created=excluded.created,
                modified=excluded.modified,accessed=excluded.accessed,is_dir=excluded.is_dir
            """;
        var d = cmd.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text);
        var n = cmd.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Text);
        var e = cmd.Parameters.Add("$e", Microsoft.Data.Sqlite.SqliteType.Text);
        var s = cmd.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
        var cr = cmd.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Integer);
        var m = cmd.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Integer);
        var a = cmd.Parameters.Add("$a", Microsoft.Data.Sqlite.SqliteType.Integer);
        var i = cmd.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Integer);
        cmd.Prepare();
        int count = 0;
        foreach (var r in rows)
        {
            d.Value = r.Directory; n.Value = r.Name; e.Value = r.Extension; s.Value = r.Size;
            cr.Value = r.CreatedTicks; m.Value = r.ModifiedTicks; a.Value = r.AccessedTicks; i.Value = r.IsDirectory ? (r.IsReparsePoint ? 2 : 1) : 0;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }

    public long Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void DeleteByPath(string directory, string name)
    {
        using var scope = db.ExternalWrite();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        string path = directory + "\\" + name;
        // Also removes what was below it when the entry is a folder ('\' and ']' are adjacent: [lo, hi) = the subtree).
        cmd.CommandText = "DELETE FROM files WHERE (dir=$d AND name=$n) OR dir=$p OR (dir>=$lo AND dir<$hi)";
        cmd.Parameters.AddWithValue("$d", directory);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$lo", path + "\\");
        cmd.Parameters.AddWithValue("$hi", path + "]");
        cmd.ExecuteNonQuery();
    }
}
