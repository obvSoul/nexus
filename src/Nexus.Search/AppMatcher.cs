using System.Globalization;
using System.Text;

namespace Nexus.Search;

/// <summary>An application as Windows lists it: its display name and what to launch.</summary>
public sealed record LaunchableApp(string Name, string Target);

/// <summary>Which applications a few typed letters refer to, best first.</summary>
public static class AppMatcher
{
    private static readonly string[] DocumentExtensions = [".txt", ".chm", ".pdf", ".htm", ".html", ".url", ".rtf", ".ini", ".log", ".md"];
    private static readonly string[] RemovalPrefixes = ["uninstall", "desinstaller", "desinstallation"];

    /// <summary>The Start menu also lists web links, manuals and uninstallers: none of them is an application to open.</summary>
    public static bool IsLaunchable(string name, string target)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target)) return false;
        if (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return false;
        if (DocumentExtensions.Any(e => target.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        string folded = Fold(name);
        if (RemovalPrefixes.Any(p => folded.StartsWith(p, StringComparison.Ordinal))) return false;
        string file = target[(target.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();
        return !(file.StartsWith("unins", StringComparison.Ordinal) && file.EndsWith(".exe", StringComparison.Ordinal));
    }

    public static IReadOnlyList<LaunchableApp> Find(IReadOnlyList<LaunchableApp> apps, IReadOnlyList<string> terms, int max)
    {
        var folded = terms.Select(Fold).Where(t => t.Length > 0).ToList();
        if (folded.Count == 0) return [];
        var found = new List<(LaunchableApp App, int Score)>();
        foreach (var app in apps)
        {
            string name = Fold(app.Name);
            int total = 0;
            foreach (string term in folded)
            {
                int score = Score(name, term);
                if (score < 0) { total = -1; break; }
                total += score;
            }
            if (total >= 0) found.Add((app, total));
        }
        return found.OrderBy(f => f.Score).ThenBy(f => f.App.Name.Length).ThenBy(f => f.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.App).DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    private static int Score(string name, string term)
    {
        if (name == term) return 0;
        if (name.StartsWith(term, StringComparison.Ordinal)) return 1;
        if (StartsAWord(name, term)) return 2;
        if (term.Length >= 2 && Initials(name).StartsWith(term, StringComparison.Ordinal)) return 3;
        if (term.Length >= 2 && name.Contains(term, StringComparison.Ordinal)) return 4;
        if (term.Length >= 4 && FuzzyMatcher.Distance(term, name) <= (term.Length >= 7 ? 2 : 1)) return 5;
        return -1;
    }

    private static bool IsSeparator(char c) => !char.IsLetterOrDigit(c);

    private static bool StartsAWord(string name, string term)
    {
        for (int i = name.IndexOf(term, StringComparison.Ordinal); i > 0; i = name.IndexOf(term, i + 1, StringComparison.Ordinal))
            if (IsSeparator(name[i - 1])) return true;
        return false;
    }

    private static string Initials(string name)
    {
        var sb = new StringBuilder(8);
        for (int i = 0; i < name.Length; i++)
            if (!IsSeparator(name[i]) && (i == 0 || IsSeparator(name[i - 1]))) sb.Append(name[i]);
        return sb.ToString();
    }

    /// <summary>Lower case without accents, so that "desinstaller" and "calculatrice" match however they are typed.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
