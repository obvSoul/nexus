using Microsoft.Data.Sqlite;

namespace Nexus.Database;

internal static class Schema
{
    public const int CurrentVersion = 5;

    public static void Migrate(SqliteConnection c)
    {
        int version = Scalar(c, "PRAGMA user_version");
        if (version >= CurrentVersion) return;

        using var tx = c.BeginTransaction();
        if (version == 0)
        {
            Exec(c, FilesSql);
            Exec(c, UserSql);
            Exec(c, IndexSql);
        }
        else if (version == 1)
        {
            // v1 -> v2: the files table is a rebuildable cache (declared NOCASE columns, reparse flag); user data is kept.
            Exec(c, "DROP TABLE IF EXISTS files_fts; DROP TABLE IF EXISTS files;");
            Exec(c, FilesSql);
            Exec(c, IndexSql);
        }
        if (version < 3) Exec(c, StatsSql);
        if (version < 4) Exec(c, HashSql);
        if (version < 5) Exec(c, ContentSql);
        Exec(c, $"PRAGMA user_version = {CurrentVersion}");
        tx.Commit();
    }

    private static int Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // dir/name are NOCASE columns (Windows paths are case-insensitive). is_dir: 0 file, 1 folder, 2 folder that is a
    // junction/symlink (indexed but never entered, which prevents loops).
    // Trigram FTS5 gives substring search on names; it is "external content" so names are not stored twice.
    private const string FilesSql = """
        CREATE TABLE files(
            id INTEGER PRIMARY KEY,
            dir TEXT NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL COLLATE NOCASE,
            ext TEXT NOT NULL DEFAULT '',
            size INTEGER NOT NULL DEFAULT 0,
            created INTEGER NOT NULL DEFAULT 0,
            modified INTEGER NOT NULL DEFAULT 0,
            accessed INTEGER NOT NULL DEFAULT 0,
            is_dir INTEGER NOT NULL DEFAULT 0);
        CREATE UNIQUE INDEX ux_files_path ON files(dir, name);
        CREATE INDEX ix_files_ext ON files(ext);
        CREATE INDEX ix_files_size ON files(size);
        CREATE INDEX ix_files_modified ON files(modified);
        CREATE INDEX ix_files_name ON files(name);

        CREATE VIRTUAL TABLE files_fts USING fts5(name, content='files', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
            INSERT INTO files_fts(rowid, name) VALUES (new.id, new.name);
        END;
        CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name) VALUES ('delete', old.id, old.name);
        END;
        CREATE TRIGGER files_au AFTER UPDATE OF name ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name) VALUES ('delete', old.id, old.name);
            INSERT INTO files_fts(rowid, name) VALUES (new.id, new.name);
        END;
        """;

    private const string UserSql = """
        CREATE TABLE search_history(query TEXT PRIMARY KEY COLLATE NOCASE, last_used INTEGER NOT NULL, uses INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE favorites(path TEXT PRIMARY KEY COLLATE NOCASE, added INTEGER NOT NULL);
        """;

    // content_fts: words found inside files, rowid = files.id. Contentless: the text itself is not kept, only the index.
    // content_state: which version of each file was read (a stale row is simply ignored by searches).
    private const string ContentSql = """
        CREATE VIRTUAL TABLE content_fts USING fts5(body, content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2');
        CREATE TABLE content_state(file_id INTEGER PRIMARY KEY, size INTEGER NOT NULL, modified INTEGER NOT NULL);
        """;

    // hash_cache: hashes already computed by the duplicate finder; valid only while size and modification time are unchanged.
    private const string HashSql = """
        CREATE TABLE hash_cache(
            file_id INTEGER PRIMARY KEY,
            size INTEGER NOT NULL, modified INTEGER NOT NULL,
            quick INTEGER, sha256 BLOB);
        """;

    // dir_stats: folder sizes computed from the index (logical sizes). size/files = whole subtree, own_size/own_files =
    // files directly inside, dirs = direct sub-folders. Rebuilt when the index changed (meta index_gen != stats_gen).
    private const string StatsSql = """
        CREATE TABLE dir_stats(
            path TEXT PRIMARY KEY COLLATE NOCASE,
            parent TEXT NOT NULL COLLATE NOCASE,
            size INTEGER NOT NULL, own_size INTEGER NOT NULL,
            files INTEGER NOT NULL, own_files INTEGER NOT NULL, dirs INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX ix_dirstats_parent ON dir_stats(parent, size DESC);
        CREATE INDEX ix_dirstats_own ON dir_stats(own_size DESC);
        """;

    // dirs: modification time of each folder as last enumerated (drives incremental scans).
    private const string IndexSql = """
        CREATE TABLE dirs(path TEXT PRIMARY KEY COLLATE NOCASE, mtime INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE index_roots(path TEXT PRIMARY KEY COLLATE NOCASE, added INTEGER NOT NULL);
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        """;
}
