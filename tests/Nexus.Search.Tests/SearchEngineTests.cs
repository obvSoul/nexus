using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Search;
using Xunit;

namespace Nexus.Search.Tests;

public sealed class SearchEngineTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 15, 0, 0, DateTimeKind.Local);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-tests-" + Guid.NewGuid().ToString("N"));
    private readonly NexusDatabase _db;
    private readonly SqliteSearchEngine _engine;
    private readonly FileRepository _files;

    public SearchEngineTests()
    {
        _db = new NexusDatabase(Path.Combine(_dir, "t.db"));
        _db.Initialize();
        _files = new FileRepository(_db);
        _engine = new SqliteSearchEngine(_db);
        long Utc(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
        _files.UpsertBatch([
            F(@"C:\Users\a\Photos", "vacances.png", 2_000_000, Utc(2026, 11, 5)),
            F(@"C:\Users\a\Photos", "IMG_0001.JPG", 3_000_000, Utc(2026, 1, 5)),
            F(@"C:\Users\a\Documents", "Rapport annuel.pdf", 500_000, Utc(2026, 11, 6)),
            F(@"C:\Users\a\Documents", "rapport.docx", 40_000, Utc(2025, 3, 1)),
            F(@"C:\Users\a\Documents", "notes.txt", 100, Utc(2026, 11, 7)),
            F(@"C:\Data", "backup.iso", 5_000_000_000, Utc(2026, 6, 1)),
            F(@"C:\Data\node_modules", "left-pad.js", 3000, Utc(2026, 6, 1)),
            F(@"C:\Users\a\Documents", "Éléphant.txt", 10, Utc(2026, 11, 7)),
            new FileEntry(0, @"C:\Users\a", "Documents", "", 0, 0, Utc(2026, 1, 1), 0, true),
        ]);
    }

    private static FileEntry F(string dir, string name, long size, long modified) =>
        new(0, dir, name, FileEntry.ExtensionOf(name), size, modified, modified, modified, false);

    private List<string> Run(string text, int limit = 100)
    {
        var page = _engine.SearchAsync(QueryParser.Parse(text, Now), 0, limit, CancellationToken.None).Result;
        return page.Items.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact] public void Substring_is_case_insensitive() => Assert.Equal(["Rapport annuel.pdf", "rapport.docx"], Run("RAPPORT"));
    [Fact] public void All_words_must_match() => Assert.Equal(["Rapport annuel.pdf"], Run("rapport annuel"));
    [Fact] public void Short_terms_use_like() => Assert.Contains("notes.txt", Run("no"));
    [Fact] public void Extension_filter() => Assert.Equal(["IMG_0001.JPG", "vacances.png"], Run("type:image"));
    [Fact] public void Star_dot_ext() => Assert.Equal(["vacances.png"], Run("*.png"));
    [Fact] public void Exclusion_of_terms_and_extensions() => Assert.Equal(["rapport.docx"], Run("rapport -annuel -ext:pdf"));
    [Fact] public void Size_over_one_gigabyte() => Assert.Equal(["backup.iso"], Run("size:>1gb"));
    [Fact] public void Modified_this_week() => Assert.Equal(new List<string> { "Éléphant.txt", "notes.txt", "Rapport annuel.pdf", "vacances.png" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), Run("dm:week is:file"));
    [Fact] public void Path_filter_and_exclusion() => Assert.Equal(["left-pad.js"], Run("path:node_modules"));
    [Fact] public void Exclude_path() => Assert.DoesNotContain("left-pad.js", Run("ext:js -path:node_modules"));
    [Fact] public void Pdf_in_a_folder() => Assert.Equal(["Rapport annuel.pdf"], Run(@"ext:pdf path:Documents"));
    [Fact] public void Regex_on_name() => Assert.Equal(["IMG_0001.JPG"], Run(@"regex:^img_\d+"));
    [Fact] public void Wildcard() => Assert.Equal(["notes.txt"], Run("not*.txt"));
    [Fact] public void Folders_only() => Assert.Equal(["Documents"], Run("is:folder"));
    [Fact] public void Unicode_case_folding() => Assert.Equal(["Éléphant.txt"], Run("éléph"));

    [Fact]
    public void Typos_fall_back_to_approximate_results()
    {
        var page = _engine.SearchAsync(QueryParser.Parse("rappport", Now), 0, 50, CancellationToken.None).Result;
        Assert.True(page.UsedFuzzy);
        Assert.Contains(page.Items, e => e.Name == "rapport.docx");
        Assert.Equal(0, FuzzyMatcher.Distance("rapport", "rapport.docx"));
        Assert.Equal(1, FuzzyMatcher.Distance("rappport", "rapport.docx"));
    }

    [Fact]
    public void Exact_hits_are_not_fuzzy()
    {
        var page = _engine.SearchAsync(QueryParser.Parse("rapport", Now), 0, 50, CancellationToken.None).Result;
        Assert.False(page.UsedFuzzy);
    }

    [Fact]
    public void Paging_and_count()
    {
        var q = QueryParser.Parse("sort:name ext:png,jpg,pdf,docx,txt", Now);
        var p1 = _engine.SearchAsync(q, 0, 2, CancellationToken.None).Result;
        var p2 = _engine.SearchAsync(q, 2, 2, CancellationToken.None).Result;
        Assert.True(p1.HasMore);
        Assert.Equal(2, p1.Items.Count);
        Assert.Empty(p1.Items.Select(e => e.Id).Intersect(p2.Items.Select(e => e.Id)));
        Assert.Equal(6, _engine.CountAsync(q, CancellationToken.None).Result);
    }

    [Fact]
    public void Sort_by_size_descending()
    {
        var page = _engine.SearchAsync(QueryParser.Parse("sort:-size is:file", Now), 0, 3, CancellationToken.None).Result;
        Assert.Equal("backup.iso", page.Items[0].Name);
        Assert.True(page.Items[0].Size >= page.Items[1].Size);
    }

    [Fact]
    public void Upsert_updates_and_fts_stays_in_sync()
    {
        _files.UpsertBatch([F(@"C:\Users\a\Documents", "NOTES.txt", 999, 1)]); // same path, different case
        Assert.Equal(["NOTES.txt"], Run("notes"));
        _files.DeleteByPath(@"C:\Users\a\Documents", "NOTES.txt");
        Assert.Empty(Run("notes"));
    }

    [Fact]
    public void Favorites_filter()
    {
        var user = new UserDataRepository(_db);
        Assert.True(user.ToggleFavorite(@"C:\Users\a\Documents\notes.txt"));
        Assert.Equal(["notes.txt"], Run("is:favorite"));
        Assert.False(user.ToggleFavorite(@"C:\Users\a\Documents\notes.txt"));
        Assert.Empty(Run("is:favorite"));
    }

    [Fact]
    public void History_returns_prefix_matches_most_recent_first()
    {
        var user = new UserDataRepository(_db);
        user.AddHistory("rapport"); user.AddHistory("photos"); user.AddHistory("rapport annuel");
        Assert.Equal(["rapport annuel", "rapport"], user.GetHistory("rap", 5));
    }

    [Fact]
    public void Cancelled_token_cancels_search()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _engine.SearchAsync(QueryParser.Parse("rapport", Now), 0, 10, cts.Token).GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
