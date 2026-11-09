namespace Nexus.Search;

/// <summary>Typo-tolerant matching (approximate substring, Levenshtein distance).</summary>
public static class FuzzyMatcher
{
    /// <summary>Allowed number of edits for a term of this length.</summary>
    public static int MaxDistance(int termLength) => Math.Clamp(termLength / 3, 1, 3);

    /// <summary>
    /// Smallest edit distance between <paramref name="term"/> and any substring of <paramref name="text"/>
    /// (Sellers' algorithm). Both are compared case-insensitively.
    /// </summary>
    public static int Distance(string term, string text)
    {
        int m = term.Length, n = text.Length;
        if (m == 0) return 0;
        Span<int> prev = m + 1 <= 256 ? stackalloc int[m + 1] : new int[m + 1];
        Span<int> cur = m + 1 <= 256 ? stackalloc int[m + 1] : new int[m + 1];
        for (int i = 0; i <= m; i++) prev[i] = i;
        int best = prev[m];
        for (int j = 1; j <= n; j++)
        {
            cur[0] = 0; // a match may start anywhere in the text
            char tc = char.ToLowerInvariant(text[j - 1]);
            for (int i = 1; i <= m; i++)
            {
                int cost = char.ToLowerInvariant(term[i - 1]) == tc ? 0 : 1;
                cur[i] = Math.Min(Math.Min(prev[i] + 1, cur[i - 1] + 1), prev[i - 1] + cost);
            }
            if (cur[m] < best) best = cur[m];
            var t = prev; prev = cur; cur = t;
        }
        return best;
    }
}
