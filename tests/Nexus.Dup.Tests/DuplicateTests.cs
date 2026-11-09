using System.Diagnostics;
using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Duplicates;
using Xunit;

namespace Nexus.Dup.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "nexus-dup-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly NexusDatabase _db;
    private readonly DuplicateFinder _finder;

    public DuplicateTests()
    {
        _root = Path.Combine(_base, "data");
        Directory.CreateDirectory(_root);
        _db = new NexusDatabase(Path.Combine(_base, "db", "d.db"));
        _db.Initialize();
        _finder = new DuplicateFinder(_db);
    }

    private static byte[] Bytes(int size, byte seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private string Put(string rel, byte[] content)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
        return p;
    }

    private void Index()
    {
        var rows = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(p =>
        {
            var i = new FileInfo(p);
            return new FileEntry(0, i.DirectoryName!, i.Name, FileEntry.ExtensionOf(i.Name), i.Length, i.CreationTimeUtc.Ticks, i.LastWriteTimeUtc.Ticks, i.LastAccessTimeUtc.Ticks, false);
        });
        new FileRepository(_db).UpsertBatch(rows);
    }

    private DuplicateOptions Opt(long min = 1, bool sameExt = true, bool apps = false, string? scope = null) => new()
    {
        MinSize = min, SameExtensionOnly = sameExt, IncludeApplicationFolders = apps, Scope = scope, Throttle = false,
        AlwaysExcluded = [Path.Combine(_root, "system")], ApplicationFolders = [Path.Combine(_root, "apps")],
    };

    private DuplicateResult Run(DuplicateOptions o) => _finder.FindAsync(o, null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Identical_files_are_grouped_and_wasted_space_is_correct()
    {
        var data = Bytes(5000, 1);
        Put(@"a\one.bin", data); Put(@"b\two.bin", data); Put(@"c\three.bin", data);
        Put("other.bin", Bytes(5000, 2));
        Index();
        var r = Run(Opt());
        var g = r.Groups.Single();
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(10000, g.Wasted);
        Assert.Equal(64, g.Sha256.Length);
    }

    [Fact]
    public void A_unique_size_is_never_read()
    {
        Put("a.bin", Bytes(100, 1)); Put("b.bin", Bytes(200, 2)); Put("c.bin", Bytes(300, 3));
        Index();
        var r = Run(Opt());
        Assert.Empty(r.Groups);
        Assert.Equal(0, r.Candidates);
        Assert.Equal(0, r.BytesRead);
    }

    [Fact]
    public void Different_quick_hash_avoids_the_full_read()
    {
        Put("a.bin", Bytes(500_000, 1)); Put("b.bin", Bytes(500_000, 2));
        Index();
        var r = Run(Opt());
        Assert.Empty(r.Groups);
        Assert.Equal(2, r.QuickHashed);
        Assert.Equal(0, r.FullHashed);
        Assert.True(r.BytesRead <= 2 * 3 * 64 * 1024);
    }

    [Fact]
    public void A_difference_outside_the_sampled_blocks_is_caught_by_sha256()
    {
        var data = Bytes(1_000_000, 7);
        var almost = (byte[])data.Clone();
        almost[100_000] ^= 0xFF; // not in the first, middle or last 64 KB
        Put("a.bin", data); Put("b.bin", almost);
        Index();
        var r = Run(Opt());
        Assert.Equal(2, r.FullHashed);
        Assert.Empty(r.Groups);
    }

    [Fact]
    public void Small_files_with_a_difference_anywhere_are_told_apart()
    {
        var data = Bytes(150_000, 9);
        var other = (byte[])data.Clone();
        other[70_000] ^= 1;
        Put("a.bin", data); Put("b.bin", other);
        Index();
        Assert.Empty(Run(Opt()).Groups);
    }

    [Fact]
    public void Minimum_size_extension_and_scope_filters()
    {
        var small = Bytes(100, 1); var big = Bytes(4000, 2);
        Put("s1.bin", small); Put("s2.bin", small);
        Put(@"x\b1.dat", big); Put(@"y\b2.dat", big); Put(@"y\b3.other", big);
        Index();
        Assert.Equal(2, Run(Opt(min: 1000)).Groups.Single().Files.Count);       // .other is apart, s1/s2 too small
        Assert.Equal(3, Run(Opt(min: 1000, sameExt: false)).Groups.Single().Files.Count);
        Assert.Empty(Run(Opt(min: 1000, scope: Path.Combine(_root, "y"), sameExt: true)).Groups.Where(g => g.Files.Any(f => f.Name == "b1.dat")));
    }

    [Fact]
    public void System_and_application_folders_are_protected()
    {
        var d = Bytes(3000, 1);
        Put(@"system\a.bin", d); Put(@"system\b.bin", d);
        Put(@"apps\c.bin", d); Put(@"apps\d.bin", d);
        Index();
        Assert.Empty(Run(Opt()).Groups);
        var withApps = Run(Opt(apps: true)).Groups.Single();
        Assert.All(withApps.Files, f => Assert.DoesNotContain(@"\system", f.Directory));
    }

    [Fact]
    public void A_folder_whose_name_starts_like_an_excluded_one_is_not_excluded()
    {
        var d = Bytes(3000, 1);
        Put(@"systemic\a.bin", d); Put(@"systemic\b.bin", d);
        Index();
        Assert.Single(Run(Opt()).Groups);
    }

    [Fact]
    public void Hard_links_are_not_duplicates()
    {
        string a = Put("real.bin", Bytes(4000, 1));
        string b = Path.Combine(_root, "link.bin");
        var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /H \"{b}\" \"{a}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(File.Exists(b), "hard link could not be created");
        Index();
        var r = Run(Opt());
        Assert.Empty(r.Groups);
        Assert.Equal(1, r.HardLinksIgnored);
    }

    [Fact]
    public void Files_changed_or_deleted_since_indexing_are_skipped()
    {
        var d = Bytes(3000, 1);
        Put("a.bin", d); Put("b.bin", d); string c = Put("c.bin", d); string e = Put("e.bin", d);
        Index();
        File.WriteAllBytes(c, Bytes(3000, 5));
        File.SetLastWriteTimeUtc(c, DateTime.UtcNow.AddHours(1));
        File.Delete(e);
        var r = Run(Opt());
        Assert.Equal(2, r.Skipped);
        Assert.Equal(2, r.Groups.Single().Files.Count);
    }

    [Fact]
    public void A_locked_file_is_skipped_without_failing()
    {
        var d = Bytes(3000, 1);
        Put("a.bin", d); Put("b.bin", d); string c = Put("c.bin", d);
        Index();
        using (new FileStream(c, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = Run(Opt());
            Assert.Equal(1, r.Skipped);
            Assert.Equal(2, r.Groups.Single().Files.Count);
        }
    }

    [Fact]
    public void Second_run_reuses_hashes_and_a_change_invalidates_them()
    {
        var d = Bytes(3000, 1);
        Put("a.bin", d); string b = Put("b.bin", d);
        Index();
        var first = Run(Opt());
        Assert.Equal(2, first.FullHashed);

        var second = Run(Opt());
        Assert.Single(second.Groups);
        Assert.Equal(0, second.BytesRead);
        Assert.Equal(0, second.FullHashed);
        Assert.True(second.CacheHits >= 4);

        File.WriteAllBytes(b, d);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5));
        Index(); // the file keeps its id but gets a new modification time
        var third = Run(Opt());
        Assert.Equal(1, third.FullHashed);
    }

    [Fact]
    public void The_search_never_modifies_or_deletes_files()
    {
        var d = Bytes(3000, 1);
        string a = Put("a.bin", d), b = Put("b.bin", d);
        Index();
        DateTime ta = File.GetLastWriteTimeUtc(a);
        Run(Opt());
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(ta, File.GetLastWriteTimeUtc(a));
        Assert.Equal(d, File.ReadAllBytes(b));
    }

    [Fact]
    public void Cancellation_stops_the_search()
    {
        var d = Bytes(3000, 1);
        Put("a.bin", d); Put("b.bin", d);
        Index();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _finder.FindAsync(Opt(), null, cts.Token).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(100, new long[] { 0 })]
    [InlineData(65536, new long[] { 0 })]
    [InlineData(100000, new long[] { 0, 65536 })]
    [InlineData(1000000, new long[] { 0, 467232, 934464 })]
    public void Sampled_blocks(long size, long[] expected) => Assert.Equal(expected, DuplicateFinder.BlockOffsets(size));

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
