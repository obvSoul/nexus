using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;

namespace Nexus.Search;

/// <summary>
/// Internal search engine on the SQLite index. Name terms use the trigram FTS5 table (substring, case-insensitive);
/// terms shorter than 3 characters and wildcards fall back to LIKE. Results stream with LIMIT so the first page is fast.
/// </summary>
public sealed class SqliteSearchEngine(NexusDatabase db, IContentHitProvider? contentHits = null) : ISearchEngine
{
    private (string Key, IReadOnlyList<(string Directory, string Name)> Hits)? _hitCache;

    /// <summary>Asks the external provider once per set of words (scrolling through pages must not repeat the query).</summary>
    private IReadOnlyList<(string Directory, string Name)>? ExternalHits(SearchQuery q, CancellationToken ct)
    {
        if (contentHits is null || q.ContentTerms.Count == 0) return null;
        string key = string.Join('\u0001', q.ContentTerms);
        if (_hitCache is { } c && c.Key == key) return c.Hits;
        var hits = contentHits.FindContent(q.ContentTerms, ct);
        _hitCache = (key, hits);
        return hits;
    }

    /// <summary>Puts the external hits in a per-connection temporary table, so filters, sorting and paging apply to them like the rest.</summary>
    private static void LoadExternalHits(SqliteConnection c, IReadOnlyList<(string Directory, string Name)> hits)
    {
        using var tx = c.BeginTransaction(deferred: true);
        using (var d = c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = "CREATE TEMP TABLE IF NOT EXISTS ws_hits(dir TEXT COLLATE NOCASE, name TEXT COLLATE NOCASE, PRIMARY KEY(dir,name)) WITHOUT ROWID; DELETE FROM temp.ws_hits;";
            d.ExecuteNonQuery();
        }
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT OR IGNORE INTO temp.ws_hits(dir,name) VALUES($d,$n)";
        var dp = ins.Parameters.Add("$d", SqliteType.Text);
        var np = ins.Parameters.Add("$n", SqliteType.Text);
        foreach (var (dir, name) in hits)
        {
            dp.Value = dir; np.Value = name;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private const int FuzzyCandidateLimit = 20000;
    private const string Cols = "f.id,f.dir,f.name,f.ext,f.size,f.created,f.modified,f.accessed,f.is_dir";

    public Task<SearchPage> SearchAsync(SearchQuery query, int offset, int limit, CancellationToken ct) =>
        Task.Run(() => Search(query, offset, limit, ct), ct);

    public Task<long> CountAsync(SearchQuery query, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (query.IsEmpty || query.Error is not null) return 0L;
            ct.ThrowIfCancellationRequested();
            var hits = ExternalHits(query, ct);
            using var c = db.Open();
            if (hits is not null) LoadExternalHits(c, hits);
            using var cmd = c.CreateCommand();
            var b = new Builder(cmd);
            string from = b.BuildFromWhere(query, null, hits is not null);
            cmd.CommandText = $"SELECT COUNT(*) {from}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }, ct);

    private SearchPage Search(SearchQuery q, int offset, int limit, CancellationToken ct)
    {
        if (q.IsEmpty || q.Error is not null) return SearchPage.Empty;
        var sw = Stopwatch.StartNew();
        var hits = ExternalHits(q, ct);
        using var c = db.Open();
        if (hits is not null) LoadExternalHits(c, hits);
        bool useHits = hits is not null;

        string? fuzzyTerm = PickFuzzyTerm(q);
        if (q.Fuzzy && fuzzyTerm is not null)
            return Finish(FuzzySearch(c, q, fuzzyTerm, limit, useHits, ct), limit, true, sw);

        var rows = Query(c, q, null, offset, limit + 1, useHits, ct);
        if (rows.Count == 0 && offset == 0 && fuzzyTerm is not null && q.Regex is null)
        {
            var approx = FuzzySearch(c, q, fuzzyTerm, limit, useHits, ct);
            if (approx.Count > 0) return Finish(approx, limit, true, sw);
        }
        return Finish(rows, limit, false, sw);
    }

    private static SearchPage Finish(List<FileEntry> rows, int limit, bool fuzzy, Stopwatch sw)
    {
        bool more = rows.Count > limit;
        if (more) rows.RemoveRange(limit, rows.Count - limit);
        return new SearchPage(rows, more, fuzzy, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>The longest plain term (≥3 chars, no wildcard) is the one made typo-tolerant.</summary>
    private static string? PickFuzzyTerm(SearchQuery q) =>
        q.Terms.Where(t => IsTrigramSearchable(t) && t.IndexOfAny(['*', '?']) < 0).OrderByDescending(t => t.Length).FirstOrDefault();

    /// <summary>The trigram index works on Unicode characters, not UTF-16 units (an emoji counts for one).</summary>
    private static bool IsTrigramSearchable(string term) => term.EnumerateRunes().Take(3).Count() >= 3;

    private List<FileEntry> FuzzySearch(SqliteConnection c, SearchQuery q, string term, int limit, bool useHits, CancellationToken ct)
    {
        var candidates = Query(c, q, term, 0, FuzzyCandidateLimit, useHits, ct);
        int max = FuzzyMatcher.MaxDistance(term.Length);
        return candidates
            .Select(e => (e, d: FuzzyMatcher.Distance(term, e.Name)))
            .Where(x => x.d <= max)
            .OrderBy(x => x.d).ThenBy(x => x.e.Name.Length)
            .Take(limit + 1)
            .Select(x => x.e)
            .ToList();
    }

    private static List<FileEntry> Query(SqliteConnection c, SearchQuery q, string? fuzzyTerm, int offset, int limit, bool useHits, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        var b = new Builder(cmd);
        string fromWhere = b.BuildFromWhere(q, fuzzyTerm, useHits);
        string order = fuzzyTerm is not null ? "" : OrderBy(q);
        cmd.CommandText = $"SELECT {Cols} {fromWhere} {order} LIMIT {limit} OFFSET {offset}";

        var list = new List<FileEntry>(Math.Min(limit, 512));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if ((list.Count & 255) == 0) ct.ThrowIfCancellationRequested();
            list.Add(new FileEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8) != 0));
        }
        return list;
    }

    private static string OrderBy(SearchQuery q)
    {
        string dir = q.Descending ? "DESC" : "ASC";
        return q.Sort switch
        {
            SortField.Name => $"ORDER BY f.name COLLATE NOCASE {dir}",
            SortField.Size => $"ORDER BY f.size {dir}",
            SortField.Modified => $"ORDER BY f.modified {dir}",
            SortField.Path => $"ORDER BY f.dir COLLATE NOCASE {dir}, f.name COLLATE NOCASE {dir}",
            SortField.Extension => $"ORDER BY f.ext {dir}, f.name COLLATE NOCASE {dir}",
            _ => "",
        };
    }

    /// <summary>Builds the FROM/WHERE part with bound parameters.</summary>
    private sealed class Builder(SqliteCommand cmd)
    {
        private int _n;

        private string P(object value)
        {
            string name = "@p" + _n++;
            cmd.Parameters.AddWithValue(name, value);
            return name;
        }

        public string BuildFromWhere(SearchQuery q, string? fuzzyTerm, bool useHits)
        {
            var where = new List<string>();

            if (q.ContentTerms.Count > 0)
            {
                // A row only counts if the indexed text is the current version of the file.
                string own = $"(f.id IN (SELECT rowid FROM content_fts WHERE content_fts MATCH {P(string.Join(" AND ", q.ContentTerms.Select(Quote)))})" +
                             " AND EXISTS (SELECT 1 FROM content_state s WHERE s.file_id=f.id AND s.modified=f.modified))";
                where.Add(useHits ? $"({own} OR EXISTS (SELECT 1 FROM temp.ws_hits w WHERE w.dir=f.dir AND w.name=f.name))" : own);
            }

            // Name terms through FTS (one MATCH expression), the rest through LIKE.
            var fts = new List<string>();
            if (fuzzyTerm is not null) fts.Add("(" + string.Join(" OR ", FuzzyPieces(fuzzyTerm).Select(Quote)) + ")");
            foreach (var t in q.Terms)
            {
                if (ReferenceEquals(t, fuzzyTerm) || t == fuzzyTerm) continue;
                bool wildcard = t.IndexOfAny(['*', '?']) >= 0;
                if (wildcard) where.Add($"f.name LIKE {P(WildcardToLike(t))} ESCAPE '^'");
                else if (IsTrigramSearchable(t)) fts.Add(Quote(t));
                else where.Add($"f.name LIKE {P("%" + EscapeLike(t) + "%")} ESCAPE '^'");
            }

            string from = fts.Count > 0
                ? $"FROM files_fts JOIN files f ON f.id = files_fts.rowid"
                : "FROM files f";
            if (fts.Count > 0) where.Insert(0, $"files_fts MATCH {P(string.Join(" AND ", fts))}");

            foreach (var t in q.ExcludeTerms)
                where.Add($"f.name NOT LIKE {P(t.IndexOfAny(['*', '?']) >= 0 ? WildcardToLike(t) : "%" + EscapeLike(t) + "%")} ESCAPE '^'");

            if (q.Extensions.Count > 0)
                where.Add($"f.ext IN ({string.Join(",", q.Extensions.Distinct().Select(e => P(e)))})");
            if (q.ExcludeExtensions.Count > 0)
                where.Add($"f.ext NOT IN ({string.Join(",", q.ExcludeExtensions.Distinct().Select(e => P(e)))})");
            foreach (var p in q.PathContains) where.Add($"f.dir LIKE {P("%" + EscapeLike(p) + "%")} ESCAPE '^'");
            foreach (var p in q.ExcludePaths) where.Add($"f.dir NOT LIKE {P("%" + EscapeLike(p) + "%")} ESCAPE '^'");
            if (q.MinSize is { } lo) where.Add($"f.size >= {P(lo)}");
            if (q.MaxSize is { } hi) where.Add($"f.size <= {P(hi)}");
            AddRange(where, "f.modified", q.Modified);
            AddRange(where, "f.created", q.Created);
            AddRange(where, "f.accessed", q.Accessed);
            if (q.Regex is not null) where.Add($"f.name REGEXP {P(q.Regex)}");
            if (q.IsDirectory is { } isDir) where.Add(isDir ? "f.is_dir <> 0" : "f.is_dir = 0");
            if (q.FavoritesOnly)
                where.Add("EXISTS (SELECT 1 FROM favorites v WHERE v.path = f.dir || '\\' || f.name)");

            var sb = new StringBuilder(from);
            if (where.Count > 0) sb.Append(" WHERE ").Append(string.Join(" AND ", where));
            return sb.ToString();
        }

        private void AddRange(List<string> where, string col, DateRange? r)
        {
            if (r is null) return;
            if (r.Value.FromTicks is { } f) where.Add($"{col} >= {P(f)}");
            if (r.Value.ToTicks is { } t) where.Add($"{col} < {P(t)}");
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// What a candidate must contain. With k typos allowed, cutting the term into k+1 pieces leaves at least one piece intact,
    /// and a whole piece is far rarer than a trigram: on a large index, common trigrams alone fill the candidate limit.
    /// </summary>
    private static IEnumerable<string> FuzzyPieces(string term)
    {
        int pieces = FuzzyMatcher.MaxDistance(term.Length) + 1;
        int size = term.Length / pieces;
        if (size < 3 || term.Any(char.IsSurrogate)) return Trigrams(term);
        return Enumerable.Range(0, pieces)
            .Select(i => i == pieces - 1 ? term[(i * size)..] : term.Substring(i * size, size))
            .Distinct();
    }

    private static IEnumerable<string> Trigrams(string term)
    {
        var all = new List<string>();
        for (int i = 0; i + 3 <= term.Length; i++) all.Add(term.Substring(i, 3));
        const int max = 12;
        if (all.Count <= max) return all.Distinct();
        return Enumerable.Range(0, max).Select(i => all[i * (all.Count - 1) / (max - 1)]).Distinct();
    }

    private static string EscapeLike(string s) => s.Replace("^", "^^").Replace("%", "^%").Replace("_", "^_");

    private static string WildcardToLike(string s) =>
        EscapeLike(s).Replace('*', '%').Replace('?', '_');
}
