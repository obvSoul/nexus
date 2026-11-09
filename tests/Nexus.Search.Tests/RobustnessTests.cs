using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Xunit;

namespace Nexus.Search.Tests;

public sealed class RobustnessTests : IDisposable
{
    private sealed class FakeHits(params (string, string)[] hits) : IContentHitProvider
    {
        public int Calls;
        public IReadOnlyList<(string Directory, string Name)> FindContent(IReadOnlyList<string> terms, CancellationToken ct) { Calls++; return hits; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-rob-" + Guid.NewGuid().ToString("N"));
    private readonly NexusDatabase _db;
    private readonly SqliteSearchEngine _engine;

    public RobustnessTests()
    {
        _db = new NexusDatabase(Path.Combine(_dir, "r.db"));
        _db.Initialize();
        _engine = new SqliteSearchEngine(_db);
        new FileRepository(_db).UpsertBatch([
            new FileEntry(0, @"C:\a", "rapport.pdf", "pdf", 10, 1, 5, 1, false),
            new FileEntry(0, @"C:\a", "photo 😀😀.png", "png", 20, 1, 5, 1, false),
            new FileEntry(0, @"C:\b", "note \"quote\" %_^.txt", "txt", 30, 1, 5, 1, false),
        ]);
    }

    private SearchPage Run(ISearchEngine e, string text) => e.SearchAsync(QueryParser.Parse(text), 0, 50, CancellationToken.None).GetAwaiter().GetResult();

    private const string Alphabet = "abcXYZ019 \t\"'*?-~/\\:.,;|<>()[]{}^$%_+=&#@!éÉ😀";
    private static readonly string[] Prefixes = ["ext:", "type:", "size:", "dm:", "dc:", "da:", "path:", "regex:", "/", "is:", "sort:", "content:", "-", "~", "*.", "\""];

    [Fact]
    public void Random_input_never_throws()
    {
        var rnd = new Random(1234);
        var runes = Alphabet.EnumerateRunes().Select(r => r.ToString()).ToArray();
        for (int i = 0; i < 4000; i++)
        {
            string text = "";
            int tokens = rnd.Next(1, 5);
            for (int t = 0; t < tokens; t++)
            {
                if (rnd.Next(2) == 0) text += Prefixes[rnd.Next(Prefixes.Length)];
                int len = rnd.Next(0, 9);
                for (int k = 0; k < len; k++) text += runes[rnd.Next(runes.Length)];
                text += " ";
            }
            var q = QueryParser.Parse(text);
            var page = _engine.SearchAsync(q, 0, 10, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(page);
            _engine.CountAsync(q, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Theory]
    [InlineData("😀😀")]      // two characters but four UTF-16 units: too short for the trigram index
    [InlineData("😀😀.png")]
    public void Emoji_names_are_found(string term) => Assert.Single(Run(_engine, term).Items);

    [Fact]
    public void Special_characters_in_names_are_literal()
    {
        Assert.Single(Run(_engine, "%_^").Items);
        Assert.Single(Run(_engine, "\"note \"").Items);
    }

    [Fact]
    public void A_catastrophic_regex_does_not_hang()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Run(_engine, "regex:(a+)+$");
        Run(_engine, @"regex:(a+)+\1$"); // back-reference: cannot use the linear engine
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Content_filter_parses_and_rejects_exclusion()
    {
        Assert.Equal(["budget", "deux mots"], QueryParser.Parse("content:budget contenu:\"deux mots\"").ContentTerms);
        Assert.False(QueryParser.Parse("content:x").IsEmpty);
        Assert.NotNull(QueryParser.Parse("-content:x").Error);
    }

    [Fact]
    public void Windows_search_hits_go_through_the_same_filters_and_are_asked_once()
    {
        var hits = new FakeHits((@"C:\a", "rapport.pdf"), (@"C:\b", "note \"quote\" %_^.txt"), (@"C:\gone", "notindexed.pdf"));
        var engine = new SqliteSearchEngine(_db, hits);
        Assert.Equal(2, Run(engine, "content:mot").Items.Count); // a file unknown to the index is not shown
        Assert.Equal("rapport.pdf", Run(engine, "content:mot ext:pdf").Items.Single().Name);
        Assert.Equal(2, engine.CountAsync(QueryParser.Parse("content:mot"), CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(1, hits.Calls);
    }

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
