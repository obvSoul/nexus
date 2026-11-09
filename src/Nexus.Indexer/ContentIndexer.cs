using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Nexus.Core.Indexing;
using Nexus.Database;
using Nexus.Infrastructure;

namespace Nexus.Indexer;

public sealed record ContentOptions
{
    public IReadOnlyList<string> Folders { get; init; } = [];
    public long MaxFileSize { get; init; } = 5 * 1024 * 1024;
    public int MaxChars { get; init; } = 2_000_000;
    public bool Throttle { get; init; } = true;
}

public sealed record ContentRunResult(int Indexed, int Unreadable, int Removed);

/// <summary>
/// Reads the text of files in chosen folders into a full-text index (content: searches). Read-only on the files.
/// Only files that are new or changed since the last run are read.
/// </summary>
public sealed class ContentIndexer(NexusDatabase db, INexusLog? log = null) : IDisposable
{
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "rst", "log", "csv", "tsv", "ini", "cfg", "conf", "toml", "yml", "yaml", "json", "xml", "html", "htm", "css",
        "js", "ts", "tsx", "jsx", "py", "java", "c", "h", "cpp", "hpp", "cs", "go", "rs", "rb", "php", "sh", "bat", "cmd", "ps1", "sql", "xaml", "tex", "srt",
    };

    private static readonly Dictionary<string, string[]> ZipParts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docx"] = ["word/document.xml"],
        ["xlsx"] = ["xl/sharedStrings.xml"],
        ["pptx"] = ["ppt/slides/"],
        ["odt"] = ["content.xml"],
        ["ods"] = ["content.xml"],
        ["odp"] = ["content.xml"],
    };

    private static readonly Regex XmlTags = new("<[^>]+>", RegexOptions.CultureInvariant);
    private const FileAttributes NotLocal = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
    private const int BatchSize = 200;

    private readonly INexusLog _log = log ?? NullLog.Instance;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Func<ContentOptions?>? _options;
    private bool _running, _again;

    public event Action<ContentRunResult>? Finished;

    // ---- background scheduling ----

    /// <summary>Starts a run in the background (merged with any run already in progress). The function returns null when the feature is off.</summary>
    public void Request(Func<ContentOptions?> options)
    {
        lock (_gate)
        {
            _options = options;
            if (_running) { _again = true; return; }
            _running = true;
        }
        Task.Factory.StartNew(Loop, _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Loop()
    {
        BackgroundMode.BeginOnCurrentThread();
        while (true)
        {
            try
            {
                var o = _options?.Invoke();
                var r = o is null ? Clear() : Run(o, _stop.Token);
                if (r.Indexed + r.Removed > 0) _log.Info($"Content index: {r.Indexed} read, {r.Unreadable} unreadable, {r.Removed} removed");
                Finished?.Invoke(r);
            }
            catch (OperationCanceledException) { lock (_gate) { _running = false; } return; }
            catch (Exception ex) when (ex is SqliteException or IOException) { _log.Error("Content indexing failed", ex); }
            lock (_gate)
            {
                if (!_again) { _running = false; return; }
                _again = false;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    // ---- one run ----

    public ContentRunResult Run(ContentOptions o, CancellationToken ct)
    {
        int removed = RemoveStale(o.Folders);
        var cpu = new CpuMonitor();
        long lastSample = 0;
        int delay = 0, indexed = 0, unreadable = 0;
        var batch = new List<(long Id, long Size, long Modified, string? Text)>(BatchSize);

        foreach (var c in Candidates(o, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (o.Throttle)
            {
                long now = Environment.TickCount64;
                if (now - lastSample > 500)
                {
                    double other = cpu.SampleOtherLoad();
                    delay = other > 0.85 ? 300 : other > 0.60 ? 80 : 0;
                    lastSample = now;
                }
                if (delay > 0) ct.WaitHandle.WaitOne(delay);
            }
            string? text = Extract(c.Path, c.Ext, o);
            if (text is null) unreadable++; else indexed++;
            batch.Add((c.Id, c.Size, c.Modified, text)); // recorded even if unreadable: no retry until the file changes
            if (batch.Count >= BatchSize) Flush(batch);
        }
        Flush(batch);
        return new ContentRunResult(indexed, unreadable, removed);
    }

    private readonly record struct Candidate(long Id, string Path, string Ext, long Size, long Modified);

    private List<Candidate> Candidates(ContentOptions o, CancellationToken ct)
    {
        var list = new List<Candidate>();
        using var c = db.Open();
        using (var tune = c.CreateCommand())
        {
            // This one query visits every file of the chosen folders; a small cache makes it re-read the same pages constantly.
            tune.CommandText = "PRAGMA cache_size=-32000";
            tune.ExecuteNonQuery();
        }
        foreach (var folder in o.Folders.Select(f => f.TrimEnd('\\')).Where(f => f.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            // Two index range scans (the folder, then what is below it): an OR here makes SQLite walk far more rows.
            foreach (string within in new[] { "f.dir=$p", "f.dir>=$lo AND f.dir<$hi" })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"""
                    SELECT f.id,f.dir,f.name,f.ext,f.size,f.modified FROM files f LEFT JOIN content_state s ON s.file_id=f.id
                    WHERE {within} AND f.is_dir=0 AND f.size BETWEEN 1 AND $max
                      AND (s.file_id IS NULL OR s.modified<>f.modified OR s.size<>f.size)
                    """;
                cmd.Parameters.AddWithValue("$max", o.MaxFileSize);
                cmd.Parameters.AddWithValue("$p", folder);
                cmd.Parameters.AddWithValue("$lo", folder + "\\");
                cmd.Parameters.AddWithValue("$hi", folder + "]");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string ext = r.GetString(3);
                    if (TextExtensions.Contains(ext) || ZipParts.ContainsKey(ext))
                        list.Add(new Candidate(r.GetInt64(0), r.GetString(1) + "\\" + r.GetString(2), ext, r.GetInt64(4), r.GetInt64(5)));
                }
            }
        }
        return list;
    }

    private void Flush(List<(long Id, long Size, long Modified, string? Text)> batch)
    {
        if (batch.Count == 0) return;
        using (db.ExternalWrite())
        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            using var del = Prepare(c, tx, "DELETE FROM content_fts WHERE rowid=$i", "$i");
            using var ins = Prepare(c, tx, "INSERT INTO content_fts(rowid,body) VALUES($i,$b)", "$i", "$b");
            using var st = Prepare(c, tx, "INSERT INTO content_state(file_id,size,modified) VALUES($i,$s,$m) ON CONFLICT(file_id) DO UPDATE SET size=excluded.size,modified=excluded.modified", "$i", "$s", "$m");
            foreach (var (id, size, modified, text) in batch)
            {
                del.Parameters["$i"].Value = id;
                del.ExecuteNonQuery();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ins.Parameters["$i"].Value = id;
                    ins.Parameters["$b"].Value = text;
                    ins.ExecuteNonQuery();
                }
                st.Parameters["$i"].Value = id;
                st.Parameters["$s"].Value = size;
                st.Parameters["$m"].Value = modified;
                st.ExecuteNonQuery();
            }
            tx.Commit();
        }
        batch.Clear();
    }

    private static SqliteCommand Prepare(SqliteConnection c, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var n in names) cmd.Parameters.AddWithValue(n, DBNull.Value);
        return cmd;
    }

    /// <summary>Forgets the text of files that left the index or the chosen folders.</summary>
    private int RemoveStale(IReadOnlyList<string> folders)
    {
        var scopes = folders.Select(f => f.TrimEnd('\\')).Where(f => f.Length > 0).ToList();
        var gone = new List<long>();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT s.file_id,f.dir FROM content_state s LEFT JOIN files f ON f.id=s.file_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                bool keep = !r.IsDBNull(1) && scopes.Any(s =>
                {
                    string d = r.GetString(1);
                    return d.Equals(s, StringComparison.OrdinalIgnoreCase) || d.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase);
                });
                if (!keep) gone.Add(r.GetInt64(0));
            }
        }
        Delete(gone);
        return gone.Count;
    }

    private void Delete(List<long> ids)
    {
        if (ids.Count == 0) return;
        using (db.ExternalWrite())
        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            using var fts = Prepare(c, tx, "DELETE FROM content_fts WHERE rowid=$i", "$i");
            using var st = Prepare(c, tx, "DELETE FROM content_state WHERE file_id=$i", "$i");
            foreach (long id in ids)
            {
                fts.Parameters["$i"].Value = id; fts.ExecuteNonQuery();
                st.Parameters["$i"].Value = id; st.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The feature was turned off: nothing is kept.</summary>
    private ContentRunResult Clear() => new(0, 0, RemoveStale([]));

    public long CountIndexed()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM content_state";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---- reading ----

    /// <summary>Plain text of a file, or null if it cannot or must not be read.</summary>
    public static string? Extract(string path, string ext, ContentOptions o)
    {
        try
        {
            if ((File.GetAttributes(path) & (NotLocal | FileAttributes.ReparsePoint)) != 0) return null; // would trigger a cloud download
            return ZipParts.TryGetValue(ext, out var parts) ? FromZip(path, parts, o.MaxChars) : FromText(path, o.MaxChars);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }

    private static string? FromText(string path, int maxChars)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        byte[] head = new byte[8192];
        int n = fs.Read(head, 0, head.Length);
        bool bom = n >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
        if (!bom && Array.IndexOf(head, (byte)0, 0, n) >= 0) return null; // binary content with a text extension
        fs.Position = 0;
        var buffer = new char[maxChars];
        using var strict = new StreamReader(fs, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        try { return new string(buffer, 0, strict.ReadBlock(buffer, 0, maxChars)); }
        catch (DecoderFallbackException)
        {
            fs.Position = 0; // not UTF-8: old Western-European text files
            using var latin = new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
            return new string(buffer, 0, latin.ReadBlock(buffer, 0, maxChars));
        }
    }

    private static string FromZip(string path, string[] parts, int maxChars)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => parts.Any(p => p.EndsWith('/') ? e.FullName.StartsWith(p, StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal) : e.FullName == p)))
        {
            using var r = new StreamReader(entry.Open(), Encoding.UTF8);
            var buffer = new char[Math.Max(1, maxChars - sb.Length)];
            string xml = new(buffer, 0, r.ReadBlock(buffer, 0, buffer.Length));
            sb.Append(System.Net.WebUtility.HtmlDecode(XmlTags.Replace(xml, " "))).Append(' ');
            if (sb.Length >= maxChars) break;
        }
        return sb.ToString();
    }
}
