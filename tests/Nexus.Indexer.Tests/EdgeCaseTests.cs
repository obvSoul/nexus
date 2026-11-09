using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Nexus.Core;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Indexer;
using Nexus.Search;
using Xunit;

namespace Nexus.Indexer.Tests;

public sealed class EdgeCaseTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "nexus-edge-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly NexusDatabase _db;
    private readonly SqliteSearchEngine _search;
    private IndexerService? _svc;

    public EdgeCaseTests()
    {
        _root = Path.Combine(_base, "data");
        Directory.CreateDirectory(_root);
        _db = new NexusDatabase(Path.Combine(_base, "db", "e.db"));
        _db.Initialize();
        _search = new SqliteSearchEngine(_db);
    }

    private static void WaitFor(Func<bool> c, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!c()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(); Thread.Sleep(30); }
    }

    private IndexerService Indexed()
    {
        var svc = _svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        svc.Start();
        int before = svc.CompletedScans;
        svc.AddRoot(_root);
        WaitFor(() => svc.CompletedScans > before);
        return svc;
    }

    private void Rescan(IndexerService svc, bool deep = false)
    {
        int before = svc.CompletedScans;
        svc.RequestScan(deep);
        WaitFor(() => svc.CompletedScans > before);
    }

    private List<FileEntry> Find(string text)
    {
        var p = _search.SearchAsync(QueryParser.Parse(text), 0, 1000, CancellationToken.None).GetAwaiter().GetResult();
        return p.UsedFuzzy ? [] : p.Items.ToList();
    }

    private static void Cmd(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd", "/c " + args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    [Fact]
    public void A_drive_letter_means_the_whole_drive_whatever_the_current_folder()
    {
        string temp = Path.GetTempPath().TrimEnd('\\');
        string drive = temp[..2];
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = temp; // what happens when Windows starts the program with its working folder elsewhere
        try
        {
            Assert.Equal(drive, IndexerService.NormalizeRoot(drive));
            Assert.Equal(drive, IndexerService.NormalizeRoot(drive + "\\"));
            Assert.Equal(temp, IndexerService.NormalizeRoot(temp + "\\"));
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public void A_root_wrongly_saved_as_system32_is_repaired_once()
    {
        string sys = Environment.SystemDirectory.TrimEnd('\\');
        using (var s = new IndexStore(_db))
        {
            s.AddRoot(sys);
            s.Upsert(new FileEntry(0, sys, "kernel32.dll", "dll", 1, 1, 1, 1, false));
            s.Commit();
        }
        var svc = _svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        svc.Start();
        WaitFor(() => svc.Roots.Count > 0);
        Assert.Equal([sys[..2]], svc.Roots);
        Assert.Empty(Find("kernel32.dll"));
        svc.Stop();

        // Once repaired, a later deliberate choice of that folder is respected.
        using (var s = new IndexStore(_db)) { s.RemoveRoot(sys[..2]); s.AddRoot(sys); s.Commit(); }
        var again = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        again.Start();
        WaitFor(() => again.Roots.Count > 0);
        Assert.Equal([sys], again.Roots);
        again.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, StringBuilder buffer, uint size);

    /// <summary>The 8.3 form of a folder, or null when the volume has no short names.</summary>
    private static string? ShortForm(string path)
    {
        var sb = new StringBuilder(1024);
        return GetShortPathNameW(path, sb, 1024) > 0 && !sb.ToString().Equals(path, StringComparison.OrdinalIgnoreCase) ? sb.ToString() : null;
    }

    [Fact]
    public void Changes_made_through_a_short_path_reach_the_real_entries()
    {
        // %TEMP% is often a short path (C:\Users\NAMEWI~1\...): Windows may then report the change under that spelling.
        string longRoot = Infrastructure.ShortPaths.ToLong(_root);
        string folder = Path.Combine(longRoot, "A folder with a long name");
        Directory.CreateDirectory(folder);
        string kept = Path.Combine(folder, "kept.txt"), gone = Path.Combine(folder, "gone-shortpath.txt");
        File.WriteAllText(kept, "x");
        File.WriteAllText(gone, "x");
        string? shortFolder = ShortForm(folder);
        if (shortFolder is null) return;

        var svc = _svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        svc.Start();
        int before = svc.CompletedScans;
        svc.AddRoot(longRoot);
        WaitFor(() => svc.CompletedScans > before);
        Assert.Single(Find("gone-shortpath"));

        // Which spelling Windows reports is unspecified, so the short one is fed to the indexer directly.
        File.WriteAllText(Path.Combine(folder, "new-shortpath.txt"), "x");
        File.Delete(gone);
        svc.Enqueue(Path.Combine(shortFolder, "new-shortpath.txt"));
        svc.Enqueue(Path.Combine(shortFolder, "gone-shortpath.txt"));

        WaitFor(() => Find("new-shortpath").Count > 0 && Find("gone-shortpath").Count == 0);
        Assert.Equal(folder, Find("new-shortpath").Single().Directory);
    }

    [Fact]
    public void Entries_indexed_under_a_short_path_are_purged_once()
    {
        string longRoot = Infrastructure.ShortPaths.ToLong(_root);
        string folder = Path.Combine(longRoot, "A folder with a long name");
        Directory.CreateDirectory(folder);
        string? shortFolder = ShortForm(folder);
        if (shortFolder is null) return;
        using (var s = new IndexStore(_db))
        {
            s.Upsert(new FileEntry(0, shortFolder, "phantom.db", "db", 5, 1, 1, 1, false));
            s.Upsert(new FileEntry(0, folder, "genuine.txt", "txt", 5, 1, 1, 1, false));
            s.Upsert(new FileEntry(0, longRoot, "tilde~name.txt", "txt", 5, 1, 1, 1, false));
            s.Commit();
        }
        var svc = _svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        svc.Start();
        WaitFor(() => svc.Status.State == Nexus.Core.Indexing.IndexerState.Idle);
        Assert.Empty(Find("phantom.db"));
        Assert.Single(Find("genuine.txt"));
        Assert.Single(Find("tilde~name"));
    }

    [Fact]
    public async Task Sync_with_disk_finds_changes_the_watcher_did_not_report()
    {
        File.WriteAllText(Path.Combine(_root, "before-sync.txt"), "x");
        var svc = Indexed(); // no watcher: nothing tells the index about what follows
        File.Delete(Path.Combine(_root, "before-sync.txt"));
        Assert.Single(Find("before-sync"));

        await svc.SyncWithDiskAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(Find("before-sync"));
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_indexed()
    {
        string deep = _root;
        for (int i = 0; i < 12; i++) deep = Path.Combine(deep, new string((char)('a' + i), 30));
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "profond.txt"), "x");
        Assert.True(deep.Length > 260);
        Indexed();
        Assert.Single(Find("profond.txt"));
    }

    [Fact]
    public void Unicode_and_unusual_names()
    {
        foreach (var n in new[] { "日本語.txt", "é è ê.txt", "emoji😀.txt", "a'b.txt", "%percent_.txt", "no-extension", ".hidden" })
            File.WriteAllText(Path.Combine(_root, n), "x");
        var svc = Indexed();
        Assert.Equal(7, svc.Status.IndexedFiles);
        Assert.Single(Find("日本語"));
        Assert.Single(Find("emoji😀"));
    }

    [Fact]
    public void A_junction_pointing_to_its_own_parent_does_not_loop()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "f.txt"), "x");
        string loop = Path.Combine(_root, "sub", "loop");
        Cmd($"mklink /J \"{loop}\" \"{_root}\"");
        Assert.True(Directory.Exists(loop));
        var svc = Indexed();
        Assert.Equal(1, svc.Status.IndexedFiles);
        Directory.Delete(loop); // removes the junction only
    }

    [Fact]
    public void A_folder_that_cannot_be_read_is_counted_and_does_not_stop_the_scan()
    {
        string secret = Path.Combine(_root, "secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "hidden.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "x");
        Cmd($"icacls \"{secret}\" /deny *S-1-1-0:(RX)");
        try
        {
            var svc = Indexed();
            Assert.Equal(1, svc.Status.AccessDenied);
            Assert.Single(Find("visible.txt"));
            Assert.Empty(Find("hidden.txt"));
        }
        finally { Cmd($"icacls \"{secret}\" /remove:d *S-1-1-0"); }
    }

    [Fact]
    public void Exclusions_by_path_and_name_apply_and_can_be_undone()
    {
        foreach (var d in new[] { "keep", "node_modules", @"keep\node_modules", "private" }) Directory.CreateDirectory(Path.Combine(_root, d));
        File.WriteAllText(Path.Combine(_root, "keep", "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "node_modules", "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "keep", "node_modules", "c.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "private", "d.txt"), "x");
        var svc = Indexed();
        Assert.Equal(4, svc.Status.IndexedFiles);

        int before = svc.CompletedScans;
        svc.SetExclusions(["node_modules", Path.Combine(_root, "private")]);
        WaitFor(() => svc.CompletedScans > before);
        Assert.Equal(1, svc.Status.IndexedFiles);
        Assert.Single(Find("a.txt"));
        Assert.True(File.Exists(Path.Combine(_root, "private", "d.txt"))); // only the index changes

        before = svc.CompletedScans;
        svc.SetExclusions([]);
        WaitFor(() => svc.CompletedScans > before);
        Assert.Equal(4, svc.Status.IndexedFiles);
    }

    [Fact]
    public void A_tree_changing_during_the_scan_does_not_break_it()
    {
        for (int i = 0; i < 300; i++)
        {
            string d = Path.Combine(_root, "d" + i);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "f.txt"), "x");
        }
        var svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = false });
        _svc = svc;
        svc.Start();
        svc.AddRoot(_root);
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                string d = Path.Combine(_root, "d" + (i++ % 300));
                try { Directory.Delete(d, true); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "f.txt"), "y"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
        WaitFor(() => svc.CompletedScans >= 1);
        stop.Cancel();
        churn.GetAwaiter().GetResult();
        Rescan(svc);
        Assert.NotEqual(Nexus.Core.Indexing.IndexerState.Stopped, svc.Status.State);
        Assert.True(svc.Status.IndexedFiles > 0);
    }

    // ---- content search ----

    private void DocX(string path, string text)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        w.Write($"<w:document><w:t>{text}</w:t></w:document>");
    }

    private ContentRunResult Content(ContentIndexer c, params string[] folders) =>
        c.Run(new ContentOptions { Folders = folders.Length == 0 ? [_root] : folders, Throttle = false }, CancellationToken.None);

    [Fact]
    public void Content_of_text_and_office_files_is_searchable()
    {
        File.WriteAllText(Path.Combine(_root, "n.txt"), "le budget prévisionnel");
        File.WriteAllText(Path.Combine(_root, "old.txt"), "café latin", Encoding.Latin1);
        DocX(Path.Combine(_root, "r.docx"), "synthese annuelle &amp; budget");
        File.WriteAllBytes(Path.Combine(_root, "bin.txt"), [1, 0, 2, 0, 65, 66]);
        File.WriteAllText(Path.Combine(_root, "ignored.exe"), "budget");
        Indexed();
        using var c = new ContentIndexer(_db);
        var r = Content(c);
        Assert.Equal(3, r.Indexed);
        Assert.Equal(1, r.Unreadable); // bin.txt
        Assert.Equal(["n.txt", "r.docx"], Find("content:budget").Select(e => e.Name).OrderBy(n => n).ToList());
        Assert.Equal("r.docx", Find("content:annuelle").Single().Name);
        Assert.Single(Find("content:previsionnel")); // accents ignored
        Assert.Single(Find("content:café"));
        Assert.Equal("n.txt", Find("content:budget ext:txt").Single().Name);
        Assert.Single(Find("content:\"budget prévisionnel\""));
        Assert.Empty(Find("content:\"prévisionnel budget\""));
    }

    [Fact]
    public void Only_changed_files_are_read_again_and_stale_text_is_not_found()
    {
        string p = Path.Combine(_root, "n.txt");
        File.WriteAllText(p, "ancien");
        var svc = Indexed();
        using var c = new ContentIndexer(_db);
        Assert.Equal(1, Content(c).Indexed);
        Assert.Equal(0, Content(c).Indexed);

        File.WriteAllText(p, "nouveau texte plus long");
        Rescan(svc, deep: true);
        Assert.Empty(Find("content:ancien"));  // the index has moved on: old text no longer counts, before any re-read
        Assert.Equal(1, Content(c).Indexed);
        Assert.Empty(Find("content:ancien"));
        Assert.Single(Find("content:nouveau"));
    }

    [Fact]
    public void Deleted_files_and_removed_folders_leave_the_content_index()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x"));
        File.WriteAllText(Path.Combine(_root, "x", "a.txt"), "motunique");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "autremot");
        var svc = Indexed();
        using var c = new ContentIndexer(_db);
        Content(c);
        Assert.Equal(2, c.CountIndexed());

        Assert.Equal(1, Content(c, Path.Combine(_root, "x")).Removed); // b.txt left the chosen folders
        File.Delete(Path.Combine(_root, "x", "a.txt"));
        Rescan(svc);
        Content(c, Path.Combine(_root, "x"));
        Assert.Equal(0, c.CountIndexed());
        Assert.Empty(Find("content:motunique"));
    }

    [Fact]
    public void Corrupt_office_file_is_skipped()
    {
        File.WriteAllText(Path.Combine(_root, "bad.docx"), "not a zip");
        Indexed();
        using var c = new ContentIndexer(_db);
        Assert.Equal(1, Content(c).Unreadable);
    }

    [Fact]
    public void Settings_round_trip_and_damaged_settings_fall_back_to_defaults()
    {
        var store = new SettingsStore(_db);
        store.Save(new NexusSettings { ThrottleIndexer = false, Exclusions = ["node_modules"] });
        var s = store.Load();
        Assert.False(s.ThrottleIndexer);
        Assert.Equal(["node_modules"], s.Exclusions);
        Assert.True(s.WatchChanges);

        _db.SetMeta("settings", "{not json");
        Assert.True(store.Load().ThrottleIndexer);
    }

    public void Dispose()
    {
        _svc?.Dispose();
        NexusDatabase.ReleaseAll();
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(_base, true); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
