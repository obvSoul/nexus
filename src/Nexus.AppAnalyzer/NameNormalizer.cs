using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.AppAnalyzer;

/// <summary>Reduces product and folder names to a comparable key ("7-Zip 23.01 (x64)" and "7-Zip" → "7zip").</summary>
public static class NameNormalizer
{
    private static readonly Regex Brackets = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"\b(?:v|version)?\s*\d+(?:\.\d+)+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Arch = new(@"\b(?:x64|x86|amd64|win64|win32|64-?bit|32-?bit)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> CompanyWords = new(StringComparer.OrdinalIgnoreCase)
        { "inc", "incorporated", "corp", "corporation", "ltd", "limited", "llc", "gmbh", "co", "company", "sa", "ab", "ag", "bv", "oy", "the" };

    /// <summary>Folder keys too generic to be attributed to a single application.</summary>
    public static readonly HashSet<string> Generic = new()
    {
        "microsoft", "windows", "temp", "programs", "packages", "common", "commonfiles", "apps", "data", "cache", "caches",
        "crashdumps", "history", "settings", "local", "roaming", "locallow", "microsoftedge", "connecteddevicesplatform",
        "comms", "d3dscache", "diagnostics", "fontcache", "publishers", "placeholdertilelogofolder", "package", "installer", "updates", "update", "downloaded",
    };

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Brackets.Replace(s, " ");
        s = Version.Replace(s, " ");
        s = Arch.Replace(s, " ");
        return Keep(s);
    }

    /// <summary>Same as Normalize but drops legal suffixes ("Google LLC" → "google").</summary>
    public static string NormalizePublisher(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var words = Regex.Split(s, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0 && !CompanyWords.Contains(w));
        return Keep(string.Join(' ', words));
    }

    private static string Keep(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>"Google Chrome" by "Google LLC" → "chrome" (name without the leading publisher).</summary>
    public static string Product(string nameKey, string publisherKey) =>
        publisherKey.Length >= 3 && nameKey.StartsWith(publisherKey, StringComparison.Ordinal) && nameKey.Length > publisherKey.Length + 2
            ? nameKey[publisherKey.Length..]
            : nameKey;

    /// <summary>Splits "Microsoft.WindowsCalculator" style names into words for display.</summary>
    public static string Humanize(string packageName)
    {
        string last = packageName.Contains('.') ? packageName[(packageName.LastIndexOf('.') + 1)..] : packageName;
        return Regex.Replace(last, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }
}
