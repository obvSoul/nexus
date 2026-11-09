using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Storage;
using Xunit;

namespace Nexus.Storage.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-stg-" + Guid.NewGuid().ToString("N"));
    private readonly NexusDatabase _db;
    private readonly StorageQueries _q;

    public StorageTests()
    {
        _db = new NexusDatabase(Path.Combine(_dir, "s.db"));
        _db.Initialize();
        _q = new StorageQueries(_db);
        var repo = new FileRepository(_db);
        FileEntry F(string dir, string name, long size) => new(0, dir, name, FileEntry.ExtensionOf(name), size, 1, 1, 1, false);
        FileEntry D(string dir, string name) => new(0, dir, name, "", 0, 1, 1, 1, true);
        repo.UpsertBatch([
            D("C:", "Users"), D(@"C:\Users", "a"), D(@"C:\Users\a", "Docs"), D(@"C:\Users\a", "AppData"), D(@"C:\Users\a\AppData", "Cache"),
            D(@"C:\Users\a\AppData\Cache", "Cache"), D("C:", "Empty"),
            F("C:", "pagefile.sys", 1000),
            F(@"C:\Users\a\Docs", "big.iso", 5000), F(@"C:\Users\a\Docs", "small.txt", 10),
            F(@"C:\Users\a\AppData\Cache", "blob1", 300), F(@"C:\Users\a\AppData\Cache\Cache", "blob2", 200),
            F(@"C:\Users\a\Docs", "scratch.tmp", 40), F(@"C:\Users\a", "dump.dmp", 60),
        ]);
        new DirectoryStatsBuilder(_db).Rebuild();
    }

    [Fact]
    public void Subtree_totals_counts_and_empty_folders()
    {
        var users = _q.GetFolder(@"C:\Users")!;
        Assert.Equal(5000 + 10 + 300 + 200 + 40 + 60, users.Size);
        Assert.Equal(6, users.Files);
        Assert.Equal(0, users.OwnSize);
        var root = _q.GetFolder("C:")!;
        Assert.Equal(users.Size + 1000, root.Size);
        Assert.Equal(2, root.Dirs); // Users, Empty
        Assert.Equal(0, _q.GetFolder(@"C:\Empty")!.Size);
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        var kids = _q.GetChildFolders(@"C:\Users\a");
        Assert.Equal(["Docs", "AppData"], kids.Select(k => k.Name).ToList());
        var (files, count, size) = _q.GetChildFiles(@"C:\Users\a\Docs", 1);
        Assert.Equal("big.iso", files.Single().Name);
        Assert.Equal(3, count);
        Assert.Equal(5050, size);
    }

    [Fact]
    public void Top_files_and_top_folders_by_direct_size_respect_the_scope()
    {
        Assert.Equal("big.iso", _q.TopFiles(null, 1).Single().Name);
        Assert.Equal(["blob1", "blob2"], _q.TopFiles(@"C:\Users\a\AppData", 5).Select(f => f.Name).ToList());
        // C:\Users has a huge subtree but owns nothing: it must not top the "heaviest folders" list.
        Assert.Equal(@"C:\Users\a\Docs", _q.TopFolders(null, 1).Single().Path);
        Assert.DoesNotContain(_q.TopFolders(@"C:\Users\a\AppData", 10), f => f.Path == @"C:\Users\a\Docs");
    }

    [Fact]
    public void Stats_become_stale_when_the_index_changes()
    {
        var b = new DirectoryStatsBuilder(_db);
        _db.SetMeta("index_gen", "1");
        b.Rebuild();
        Assert.False(b.GetInfo().Stale);
        _db.SetMeta("index_gen", "2");
        Assert.True(b.GetInfo().Stale);
        b.Rebuild();
        Assert.False(b.GetInfo().Stale);
    }

    [Fact]
    public void Indexer_commits_bump_the_generation()
    {
        string before = _db.GetMeta("index_gen") ?? "0";
        using (var s = new IndexStore(_db))
        {
            s.Upsert(new FileEntry(0, "C:", "new.txt", "txt", 1, 1, 1, 1, false));
            s.Commit();
        }
        Assert.NotEqual(before, _db.GetMeta("index_gen"));
    }

    [Fact]
    public void Temp_extensions_known_locations_and_caches()
    {
        var a = new TempCacheAnalyzer(_db);
        var tmp = a.TempFilesByExtension();
        Assert.Equal(40, tmp.Single(t => t.Extension == "tmp").Size);
        Assert.Equal(60, tmp.Single(t => t.Extension == "dmp").Size);
        Assert.Equal("dump.dmp", a.BiggestTempFiles(1).Single().Name);

        // Cache\Cache is inside Cache: only the outer one is reported, so totals never double count.
        var caches = a.FindCacheFolders(10);
        Assert.Equal(@"C:\Users\a\AppData\Cache", caches.Single().Path);
        Assert.Equal(500, caches.Single().Size);
    }

    [Theory]
    [InlineData(new double[] { 6, 6, 4, 3, 2, 2, 1 })]
    [InlineData(new double[] { 100 })]
    [InlineData(new double[] { 50, 50 })]
    [InlineData(new double[] { 1000, 10, 5, 1, 1, 1, 1, 1 })]
    public void Treemap_tiles_the_bounds_proportionally_without_overlap(double[] sizes)
    {
        var bounds = new RectD(10, 20, 600, 400);
        var rects = TreemapLayout.Squarify(sizes, bounds);
        double total = sizes.Sum();
        for (int i = 0; i < sizes.Length; i++)
        {
            Assert.InRange(rects[i].X, bounds.X - 1e-6, bounds.X + bounds.W + 1e-6);
            Assert.InRange(rects[i].X + rects[i].W, bounds.X - 1e-6, bounds.X + bounds.W + 1e-6);
            Assert.InRange(rects[i].Y + rects[i].H, bounds.Y - 1e-6, bounds.Y + bounds.H + 1e-6);
            Assert.Equal(bounds.Area * sizes[i] / total, rects[i].Area, 3);
        }
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3);
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
            {
                double ox = Math.Min(rects[i].X + rects[i].W, rects[j].X + rects[j].W) - Math.Max(rects[i].X, rects[j].X);
                double oy = Math.Min(rects[i].Y + rects[i].H, rects[j].Y + rects[j].H) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"rects {i} and {j} overlap");
            }
    }

    [Fact]
    public void Treemap_handles_zero_sizes_and_empty_bounds()
    {
        var r = TreemapLayout.Squarify([10, 0, 0], new RectD(0, 0, 100, 100));
        Assert.Equal(10000, r[0].Area, 3);
        Assert.Equal(0, r[1].Area);
        Assert.All(TreemapLayout.Squarify([1, 1], new RectD(0, 0, 0, 50)), x => Assert.Equal(0, x.Area));
    }

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
