using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Search;
using Xunit;

namespace Nexus.Search.Tests;

public sealed class FuzzyLargeIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-fuzzy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_typo_finds_the_file_even_when_its_common_trigrams_match_many_others()
    {
        var db = new NexusDatabase(Path.Combine(_dir, "f.db"));
        db.Initialize();
        using (var store = new IndexStore(db))
        {
            // More files than the candidate limit, sharing trigrams with the term but not a whole piece of it.
            for (int i = 0; i < 21000; i++)
                store.Upsert(new FileEntry(0, @"C:\d", $"rap-{i}-fin-ann.txt", "txt", 1, 1, 1, 1, false));
            store.Upsert(new FileEntry(0, @"C:\d", "rapport-annuel-final.pdf", "pdf", 1, 1, 1, 1, false));
            store.Commit();
        }

        var page = await new SqliteSearchEngine(db).SearchAsync(QueryParser.Parse("rapport-anuel-final"), 0, 10, CancellationToken.None);

        Assert.True(page.UsedFuzzy);
        Assert.Equal("rapport-annuel-final.pdf", Assert.Single(page.Items).Name);
    }

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
