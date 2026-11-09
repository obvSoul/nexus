using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.Core.Search;

namespace Nexus.Search;

/// <summary>
/// Turns the search box text into a <see cref="SearchQuery"/>.
/// Syntax: words (all must match the name, case-insensitive, substring; * and ? wildcards), "quoted phrase",
/// -exclude, *.png, ext:png,jpg, type:image, size:&gt;1gb | size:1mb..5mb, dm:today|yesterday|week|month|year|7d|2026-01-01|&gt;2026-01-01|a..b
/// (dc: created, da: accessed), path:dir, regex:pattern or /pattern/ (on the name), ~word (typo tolerant),
/// is:folder|file|favorite, sort:name|size|date|path|ext (prefix - for descending).
/// </summary>
public static class QueryParser
{
    private static readonly Regex TokenRx = new(@"(?:[^\s""]+|""[^""]*"")+", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(@"^(\d+(?:[.,]\d+)?)\s*([kmgtp]?)(?:i?b|o)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SearchQuery Parse(string? input, DateTime? nowLocal = null)
    {
        var q = new SearchQuery { Raw = input ?? "" };
        var now = nowLocal ?? DateTime.Now;

        foreach (Match m in TokenRx.Matches(q.Raw))
        {
            string tok = m.Value.Replace("\"", "");
            if (tok.Length == 0) continue;

            bool neg = tok.Length > 1 && tok[0] == '-';
            if (neg) tok = tok[1..];

            if (!neg && tok.Length > 1 && tok[0] == '~')
            {
                q.Fuzzy = true;
                AddTerm(q, tok[1..], false);
                continue;
            }

            if (tok.Length > 2 && tok[0] == '/' && tok[^1] == '/')
            {
                SetRegex(q, tok[1..^1]);
                continue;
            }

            int colon = tok.IndexOf(':');
            if (colon > 0 && TryKeyed(q, tok[..colon].ToLowerInvariant(), tok[(colon + 1)..], neg, now)) continue;

            AddTerm(q, tok, neg);
        }
        return q;
    }

    private static bool TryKeyed(SearchQuery q, string key, string value, bool neg, DateTime now)
    {
        if (value.Length == 0) return key is "ext" or "e" or "type" or "t" or "size" or "s" or "dm" or "dc" or "da"
            or "path" or "in" or "p" or "regex" or "re" or "is" or "sort" or "content" or "contenu"; // swallow while the user is still typing
        switch (key)
        {
            case "content" or "contenu":
                if (neg) q.Error = "L'exclusion par contenu (-content:) n'est pas prise en charge.";
                else q.ContentTerms.Add(value);
                return true;
            case "ext" or "e":
                foreach (var e in value.Split(',', ';', '|'))
                {
                    var ext = e.Trim().TrimStart('.').ToLowerInvariant();
                    if (ext.Length > 0) (neg ? q.ExcludeExtensions : q.Extensions).Add(ext);
                }
                return true;
            case "type" or "t":
                if (FileTypes.TryParseCategory(value, out var cat))
                    (neg ? q.ExcludeExtensions : q.Extensions).AddRange(FileTypes.Extensions(cat));
                else q.Error = $"Type inconnu : « {value} » (image, video, audio, document, archive, code, exe).";
                return true;
            case "size" or "s":
                if (!ParseSize(q, value)) q.Error = $"Taille non comprise : « {value} » (ex. >1gb, <10mb, 1mb..5mb).";
                return true;
            case "dm" or "dc" or "da":
                var range = ParseDate(value, now);
                if (range is null) { q.Error = $"Date non comprise : « {value} » (today, week, 7d, 2026-01-31, >2026-01-01…)."; return true; }
                if (key == "dm") q.Modified = range; else if (key == "dc") q.Created = range; else q.Accessed = range;
                return true;
            case "path" or "in" or "p":
                (neg ? q.ExcludePaths : q.PathContains).Add(value.Replace('/', '\\').Trim('\\'));
                return true;
            case "regex" or "re":
                SetRegex(q, value);
                return true;
            case "is":
                switch (value.ToLowerInvariant())
                {
                    case "folder" or "dir" or "dossier": q.IsDirectory = !neg; break;
                    case "file" or "fichier": q.IsDirectory = neg; break;
                    case "favorite" or "fav" or "favori" or "favoris": q.FavoritesOnly = !neg; break;
                    default: q.Error = $"is: inconnu : « {value} » (folder, file, favorite)."; break;
                }
                return true;
            case "sort":
                bool desc = value.StartsWith('-');
                var f = value.TrimStart('-').ToLowerInvariant() switch
                {
                    "name" or "nom" => SortField.Name,
                    "size" or "taille" => SortField.Size,
                    "date" or "modified" => SortField.Modified,
                    "path" or "chemin" => SortField.Path,
                    "ext" => SortField.Extension,
                    _ => SortField.None,
                };
                if (f == SortField.None) q.Error = $"Tri inconnu : « {value} »."; else { q.Sort = f; q.Descending = desc; }
                return true;
            default:
                return false;
        }
    }

    private static void AddTerm(SearchQuery q, string term, bool neg)
    {
        if (term.Length == 0) return;
        if (term.StartsWith("*.") && term.Length > 2 && term.IndexOfAny(['*', '?', '\\', '/'], 2) < 0)
        {
            (neg ? q.ExcludeExtensions : q.Extensions).Add(term[2..].ToLowerInvariant());
            return;
        }
        if (term.Contains('\\') || term.Contains('/'))
        {
            (neg ? q.ExcludePaths : q.PathContains).Add(term.Replace('/', '\\').Trim('\\'));
            return;
        }
        (neg ? q.ExcludeTerms : q.Terms).Add(term);
    }

    private static void SetRegex(SearchQuery q, string pattern)
    {
        if (pattern.Length == 0) return;
        try { _ = new Regex(pattern); q.Regex = pattern; }
        catch (ArgumentException) { q.Error = $"Expression régulière invalide : « {pattern} »."; }
    }

    // ---- size ----

    private static bool ParseSize(SearchQuery q, string v)
    {
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            bool okLo = dots == 0 || TryBytes(v[..dots], out var lo);
            bool okHi = dots + 2 >= v.Length || TryBytes(v[(dots + 2)..], out var hi);
            if (!okLo || !okHi) return false;
            if (dots > 0) { TryBytes(v[..dots], out var l); q.MinSize = l; }
            if (dots + 2 < v.Length) { TryBytes(v[(dots + 2)..], out var h); q.MaxSize = h; }
            return true;
        }
        if (v.StartsWith(">=")) { if (!TryBytes(v[2..], out var b)) return false; q.MinSize = b; return true; }
        if (v.StartsWith("<=")) { if (!TryBytes(v[2..], out var b)) return false; q.MaxSize = b; return true; }
        if (v.StartsWith('>')) { if (!TryBytes(v[1..], out var b)) return false; q.MinSize = b + 1; return true; }
        if (v.StartsWith('<')) { if (!TryBytes(v[1..], out var b)) return false; q.MaxSize = b - 1; return true; }
        if (v.StartsWith('=')) v = v[1..];
        if (!TryBytes(v, out var exact)) return false;
        q.MinSize = exact; q.MaxSize = exact;
        return true;
    }

    public static bool TryBytes(string s, out long bytes)
    {
        bytes = 0;
        var m = SizeRx.Match(s.Trim());
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
        long mult = char.ToLowerInvariant(m.Groups[2].Value.Length == 0 ? ' ' : m.Groups[2].Value[0]) switch
        {
            'k' => 1L << 10, 'm' => 1L << 20, 'g' => 1L << 30, 't' => 1L << 40, 'p' => 1L << 50, _ => 1L,
        };
        bytes = (long)(n * mult);
        return true;
    }

    // ---- dates ----

    public static DateRange? ParseDate(string v, DateTime nowLocal)
    {
        DateTime today = nowLocal.Date;
        switch (v.ToLowerInvariant())
        {
            case "today" or "aujourdhui" or "aujourd'hui": return Range(today, today.AddDays(1));
            case "yesterday" or "hier": return Range(today.AddDays(-1), today);
            case "week" or "semaine":
                int back = ((int)today.DayOfWeek + 6) % 7; // Monday = start of week
                return Range(today.AddDays(-back), null);
            case "month" or "mois": return Range(new DateTime(today.Year, today.Month, 1), null);
            case "year" or "annee" or "année": return Range(new DateTime(today.Year, 1, 1), null);
        }
        var rel = Regex.Match(v, @"^(\d+)([dwmy])$", RegexOptions.IgnoreCase);
        if (rel.Success)
        {
            int n = int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            DateTime from = char.ToLowerInvariant(rel.Groups[2].Value[0]) switch
            {
                'd' => nowLocal.AddDays(-n), 'w' => nowLocal.AddDays(-7 * n), 'm' => nowLocal.AddMonths(-n), _ => nowLocal.AddYears(-n),
            };
            return Range(from, null);
        }
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            DateTime? a = null, b = null;
            if (dots > 0) { if (!TryDay(v[..dots], out var x)) return null; a = x; }
            if (dots + 2 < v.Length) { if (!TryDay(v[(dots + 2)..], out var y)) return null; b = y.AddDays(1); }
            return Range(a, b);
        }
        if (v.StartsWith(">=")) return TryDay(v[2..], out var d1) ? Range(d1, null) : null;
        if (v.StartsWith('>')) return TryDay(v[1..], out var d2) ? Range(d2.AddDays(1), null) : null;
        if (v.StartsWith("<=")) return TryDay(v[2..], out var d3) ? Range(null, d3.AddDays(1)) : null;
        if (v.StartsWith('<')) return TryDay(v[1..], out var d4) ? Range(null, d4) : null;
        return TryDay(v, out var d) ? Range(d, d.AddDays(1)) : null;
    }

    private static bool TryDay(string s, out DateTime day) =>
        DateTime.TryParseExact(s.Trim(), ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static DateRange Range(DateTime? fromLocal, DateTime? toLocal) =>
        new(fromLocal is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Local).ToUniversalTime().Ticks : null,
            toLocal is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime().Ticks : null);
}
