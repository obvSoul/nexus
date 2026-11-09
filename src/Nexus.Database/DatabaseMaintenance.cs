using Microsoft.Data.Sqlite;
using Nexus.Core.Indexing;

namespace Nexus.Database;

/// <summary>Integrity check, backup and recovery of the index database.</summary>
public static class DatabaseMaintenance
{
    public static string BackupPath(NexusDatabase db) => db.FilePath + ".bak";

    /// <summary>Consistent online copy (SQLite backup API), written atomically next to the database.</summary>
    public static void Backup(NexusDatabase db)
    {
        string tmp = db.FilePath + ".bak.tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var src = db.Open())
        using (var dst = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            dst.Open();
            src.BackupDatabase(dst);
        }
        File.Move(tmp, BackupPath(db), overwrite: true);
    }

    /// <summary>Creates a backup unless a recent one exists. Returns true if one was made.</summary>
    public static bool BackupIfDue(NexusDatabase db, TimeSpan minAge)
    {
        string bak = BackupPath(db);
        if (File.Exists(bak) && DateTime.UtcNow - File.GetLastWriteTimeUtc(bak) < minAge) return false;
        Backup(db);
        return true;
    }

    public static string QuickCheck(NexusDatabase db)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check(1)";
        return Convert.ToString(cmd.ExecuteScalar()) ?? "no result";
    }

    /// <summary>
    /// Opens the database. After an unclean shutdown it runs an integrity check. If the file is corrupt it is set aside
    /// (never deleted), the latest backup is restored when valid, otherwise a fresh index is created (rescan needed).
    /// </summary>
    public static (NexusDatabase Db, string? Notice) OpenWithRecovery(string path, INexusLog? log = null)
    {
        log ??= NullLog.Instance;
        try
        {
            var db = new NexusDatabase(path);
            db.Initialize();
            if (db.GetMeta("clean_shutdown") == "0")
            {
                string check = QuickCheck(db);
                if (check != "ok") throw new InvalidDataException("Integrity check failed: " + check);
            }
            return (db, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            log.Error("Database unusable, starting recovery", ex);
            NexusDatabase.ReleaseAll();
            SetAside(path);

            string bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    File.Copy(bak, path);
                    var restored = new NexusDatabase(path);
                    restored.Initialize();
                    if (QuickCheck(restored) == "ok")
                    {
                        log.Warn("Index restored from backup");
                        return (restored, "L'index était endommagé : il a été restauré depuis la dernière sauvegarde.");
                    }
                }
                catch (Exception ex2) when (ex2 is SqliteException or IOException)
                {
                    log.Error("Backup restore failed", ex2);
                    NexusDatabase.ReleaseAll();
                    SetAside(path);
                }
            }
            var fresh = new NexusDatabase(path);
            fresh.Initialize();
            log.Warn("Fresh index created");
            return (fresh, "L'index était endommagé et aucune sauvegarde valide n'existe : un nouvel index est créé et sera reconstruit.");
        }
    }

    private static void SetAside(string path)
    {
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        if (File.Exists(path)) File.Move(path, $"{path}.corrupt-{stamp}", overwrite: true);
        foreach (var side in new[] { path + "-wal", path + "-shm" })
            if (File.Exists(side)) File.Delete(side);
    }
}
