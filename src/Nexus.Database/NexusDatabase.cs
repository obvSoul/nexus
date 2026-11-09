using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Nexus.Database;

/// <summary>SQLite access point: WAL, pooled connections, REGEXP function, schema migrations.</summary>
public sealed class NexusDatabase
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private readonly string _connectionString;

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\Nexus\nexus.db, or the file named by the NEXUS_DB environment variable (dev/testing).</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("NEXUS_DB") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "nexus.db");

    public NexusDatabase(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        try
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                // recursive_triggers: INSERT OR REPLACE must fire the delete trigger that keeps FTS in sync.
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; " +
                                  "PRAGMA cache_size=-4000; PRAGMA recursive_triggers=ON; PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }
            c.CreateFunction("regexp", (string? pattern, string? input) => IsMatch(pattern, input) ? 1L : 0L);
            return c;
        }
        catch
        {
            c.Dispose(); // do not leak a handle on a corrupt file (it must be movable for recovery)
            throw;
        }
    }

    public void Initialize()
    {
        using var c = Open();
        Schema.Migrate(c);
    }

    private int _externalWriters;

    /// <summary>True while code outside the indexer is about to write; the indexer pauses between batches.</summary>
    public bool ExternalWriterWaiting => Volatile.Read(ref _externalWriters) > 0;

    /// <summary>Wrap writes done outside the indexer so they do not have to compete with its batches.</summary>
    public IDisposable ExternalWrite() => new WriteScope(this);

    private sealed class WriteScope : IDisposable
    {
        private NexusDatabase? _db;
        public WriteScope(NexusDatabase db) { _db = db; Interlocked.Increment(ref db._externalWriters); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _db, null) is { } db) Interlocked.Decrement(ref db._externalWriters);
        }
    }

    public string? GetMeta(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var scope = ExternalWrite();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static bool IsMatch(string? pattern, string? input)
    {
        if (pattern is null || input is null) return false;
        var rx = RegexCache.GetOrAdd(pattern, Compile);
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>
    /// A search runs the pattern on every name, so a pathological pattern must not be able to freeze it:
    /// linear-time engine when possible, otherwise a very short timeout per name.
    /// </summary>
    private static Regex Compile(string pattern)
    {
        const RegexOptions common = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try { return new Regex(pattern, common | RegexOptions.NonBacktracking); }
        catch (NotSupportedException) { return new Regex(pattern, common, TimeSpan.FromMilliseconds(10)); } // look-around, back-references
    }

    /// <summary>Releases pooled file handles (needed before deleting the database file).</summary>
    public static void ReleaseAll() => SqliteConnection.ClearAllPools();
}
