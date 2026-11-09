using System.Text;

namespace Nexus.AppAnalyzer;

/// <summary>Minimal parser for Valve's text KeyValues format (Steam libraryfolders.vdf and appmanifest_*.acf).</summary>
public static class Vdf
{
    /// <summary>Returns a tree of Dictionary&lt;string, object&gt; (objects) and strings (values). Keys are case-insensitive.</summary>
    public static Dictionary<string, object> Parse(string text)
    {
        int i = 0;
        return ParseObject(text, ref i, topLevel: true);
    }

    private static Dictionary<string, object> ParseObject(string s, ref int i, bool topLevel)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? key = NextToken(s, ref i, out bool isBrace);
            if (key is null) return map;            // end of input
            if (isBrace) { if (key == "}") return map; continue; }
            string? value = NextToken(s, ref i, out bool valueIsBrace);
            if (value is null) return map;
            if (valueIsBrace && value == "{") map[key] = ParseObject(s, ref i, topLevel: false);
            else if (!valueIsBrace) map[key] = value;
        }
    }

    private static string? NextToken(string s, ref int i, out bool isBrace)
    {
        isBrace = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '{' || c == '}') { i++; isBrace = true; return c.ToString(); }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] }); // "\\" → "\", "\"" → "\""
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
            return s[start..i];
        }
        return null;
    }

    public static string? GetString(Dictionary<string, object> map, params string[] path)
    {
        object cur = map;
        foreach (var p in path)
        {
            if (cur is not Dictionary<string, object> d || !d.TryGetValue(p, out var next)) return null;
            cur = next;
        }
        return cur as string;
    }

    public static Dictionary<string, object>? GetObject(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
}
