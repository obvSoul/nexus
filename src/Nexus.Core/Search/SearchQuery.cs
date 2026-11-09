using Nexus.Core.Models;

namespace Nexus.Core.Search;

public enum SortField { None, Name, Size, Modified, Path, Extension }

/// <summary>Half-open range [From, To) in UTC ticks; a null bound is open.</summary>
public readonly record struct DateRange(long? FromTicks, long? ToTicks);

/// <summary>A parsed search request. Build it with the parser in Nexus.Search.</summary>
public sealed class SearchQuery
{
    public string Raw { get; set; } = "";
    public List<string> Terms { get; } = [];
    public List<string> ExcludeTerms { get; } = [];
    public List<string> Extensions { get; } = [];
    public List<string> ExcludeExtensions { get; } = [];
    public List<string> PathContains { get; } = [];
    public List<string> ExcludePaths { get; } = [];
    /// <summary>Words that must appear inside the file (content: filter).</summary>
    public List<string> ContentTerms { get; } = [];
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateRange? Modified { get; set; }
    public DateRange? Created { get; set; }
    public DateRange? Accessed { get; set; }
    public string? Regex { get; set; }
    public bool Fuzzy { get; set; }
    public bool? IsDirectory { get; set; }
    public bool FavoritesOnly { get; set; }
    public SortField Sort { get; set; }
    public bool Descending { get; set; }
    /// <summary>Set when the text could not be understood (e.g. invalid regex).</summary>
    public string? Error { get; set; }

    /// <summary>True when only plain words were typed, without any filter.</summary>
    public bool IsNameOnly =>
        Terms.Count > 0 && ContentTerms.Count == 0 && ExcludeTerms.Count == 0 && Extensions.Count == 0 && ExcludeExtensions.Count == 0 &&
        PathContains.Count == 0 && ExcludePaths.Count == 0 && MinSize is null && MaxSize is null &&
        Modified is null && Created is null && Accessed is null && Regex is null && IsDirectory is null && !FavoritesOnly;

    public bool IsEmpty =>
        Terms.Count == 0 && ContentTerms.Count == 0 && ExcludeTerms.Count == 0 && Extensions.Count == 0 && ExcludeExtensions.Count == 0 &&
        PathContains.Count == 0 && ExcludePaths.Count == 0 && MinSize is null && MaxSize is null &&
        Modified is null && Created is null && Accessed is null && Regex is null && IsDirectory is null && !FavoritesOnly;
}

public sealed record SearchPage(IReadOnlyList<FileEntry> Items, bool HasMore, bool UsedFuzzy, double ElapsedMs)
{
    public static SearchPage Empty { get; } = new([], false, false, 0);
}

/// <summary>An external source of files containing given words (Windows Search).</summary>
public interface IContentHitProvider
{
    IReadOnlyList<(string Directory, string Name)> FindContent(IReadOnlyList<string> terms, CancellationToken ct);
}

public interface ISearchEngine
{
    /// <summary>Returns one page of results. Cancel by passing a token; a new search should cancel the previous one.</summary>
    Task<SearchPage> SearchAsync(SearchQuery query, int offset, int limit, CancellationToken ct);

    /// <summary>Exact number of matches (can be slow for very broad queries; run it in the background).</summary>
    Task<long> CountAsync(SearchQuery query, CancellationToken ct);
}
