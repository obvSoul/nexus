using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Nexus.Core.Indexing;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Indexer;
using Nexus.Search;
using Xunit;

namespace Nexus.Indexer.Tests;

public sealed class IndexerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "nexus-idx-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly NexusDatabase _db;
    private readonly SqliteSearchEngine _search;
    private IndexerService? _svc;

    public IndexerTests()
    {
        _root = Path.Combine(_base, "data");
        Directory.CreateDirectory(_root);
        _db = new NexusDatabase(Path.Combine(_base, "db", "index.db"));
        _db.Initialize();
        _search = new SqliteSearchEngine(_db);
    }

    private IndexerService Service(bool watch = false)
    {
        _svc = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = false, Watch = watch, Throttle = false });
        _svc.Start();
        return _svc;
    }

    private void W(string rel, string content = "x")
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            Thread.Sleep(50);
        }
    }

    private void ScanAndWait(IndexerService svc, bool deep = false)
    {
        int before = svc.CompletedScans;
        svc.RequestScan(deep);
        WaitFor(() => svc.CompletedScans > before);
    }

    private IndexerService Indexed(bool watch = false)
    {
        var svc = Service(watch);
        int before = svc.CompletedScans;
        svc.AddRoot(_root); // queues a first scan
        WaitFor(() => svc.CompletedScans > before);
        return svc;
    }

    /// <summary>Exact matches only: the typo-tolerant fallback is excluded so "not found" assertions are meaningful.</summary>
    private List<FileEntry> Find(string text)
    {
        var page = _search.SearchAsync(QueryParser.Parse(text), 0, 1000, CancellationToken.None).Result;
        return page.UsedFuzzy ? [] : page.Items.ToList();
    }

    [Fact]
    public void Initial_scan_indexes_files_and_folders()
    {
        W("a.txt"); W(@"sub\b.png", "12345"); W(@"sub\deep\c.log");
        var svc = Indexed();
        Assert.Equal(3, svc.Status.IndexedFiles);
        Assert.Single(Find("b.png"), e => e.Size == 5 && e.Directory.EndsWith(@"data\sub"));
        Assert.Equal(2, Find("is:folder").Count); // sub, deep
        Assert.Empty(svc.Status.CurrentPath);
    }

    [Fact]
    public void Rescan_detects_new_changed_and_removed_files()
    {
        W("keep.txt"); W("gone.txt"); W(@"dir\inner.txt", "1");
        var svc = Indexed();

        W("new.txt");
        File.Delete(Path.Combine(_root, "gone.txt"));
        Directory.Delete(Path.Combine(_root, "dir"), true);
        ScanAndWait(svc);

        Assert.Single(Find("new.txt"));
        Assert.Empty(Find("gone.txt"));
        Assert.Empty(Find("inner.txt"));
        Assert.Empty(Find("is:folder"));
        Assert.Equal(2, svc.Status.IndexedFiles);
    }

    [Fact]
    public void Unchanged_folders_are_not_listed_again()
    {
        for (int i = 0; i < 5; i++) W($@"d{i}\f.txt");
        var svc = Indexed();
        ScanAndWait(svc);
        Assert.True(svc.Status.DirsSkipped >= 5, $"skipped={svc.Status.DirsSkipped}");
    }

    [Fact]
    public void Deep_scan_finds_in_place_edits_that_do_not_touch_the_folder_time()
    {
        W(@"d\f.txt", "1");
        var svc = Indexed();
        File.AppendAllText(Path.Combine(_root, "d", "f.txt"), "22222");
        ScanAndWait(svc, deep: true);
        Assert.Equal(6, Find("f.txt").Single().Size);
    }

    [Fact]
    public void Junctions_are_indexed_but_never_followed()
    {
        W(@"real\file.txt");
        string link = Path.Combine(_root, "link");
        var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{Path.Combine(_root, "real")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction could not be created");

        Indexed();
        Assert.Single(Find("file.txt"));              // not duplicated through the junction
        Assert.Single(Find("link"), e => e.IsDirectory);
    }

    [Fact]
    public void Protected_system_folders_are_skipped()
    {
        W(@"$Recycle.Bin\secret.txt"); W("ok.txt");
        Indexed();
        Assert.Empty(Find("secret.txt"));
        Assert.Single(Find("ok.txt"));
    }

    [Fact]
    public void Watcher_applies_create_rename_and_delete()
    {
        W("seed.txt");
        var svc = Indexed(watch: true);

        W("live.txt");
        WaitFor(() => Find("live.txt").Count == 1);
        long id = Find("live.txt").Single().Id;

        File.Move(Path.Combine(_root, "live.txt"), Path.Combine(_root, "renamed.txt"));
        WaitFor(() => Find("renamed.txt").Count == 1 && Find("live.txt").Count == 0);
        Assert.Equal(id, Find("renamed.txt").Single().Id); // moved, not re-created

        File.Delete(Path.Combine(_root, "renamed.txt"));
        WaitFor(() => Find("renamed.txt").Count == 0);
        Assert.True(svc.Status.Watching);
    }

    [Fact]
    public void Watcher_follows_a_renamed_folder_and_indexes_a_copied_in_tree()
    {
        W(@"old\x.txt");
        Indexed(watch: true);

        Directory.Move(Path.Combine(_root, "old"), Path.Combine(_root, "newname"));
        WaitFor(() => Find("x.txt").Count == 1 && Find("x.txt")[0].Directory.EndsWith("newname"));

        string staged = Path.Combine(_base, "staged");
        Directory.CreateDirectory(Path.Combine(staged, "inner"));
        File.WriteAllText(Path.Combine(staged, "inner", "copied.txt"), "z");
        Directory.Move(staged, Path.Combine(_root, "arrived"));
        WaitFor(() => Find("copied.txt").Count == 1);
    }

    [Fact]
    public void Pause_stops_work_and_resume_completes_it()
    {
        W("a.txt");
        var svc = Service();
        svc.AddRoot(_root);
        svc.Pause();
        svc.RequestScan();
        Thread.Sleep(700);
        Assert.Equal(IndexerState.Paused, svc.Status.State);
        Assert.Equal(0, svc.CompletedScans);

        svc.Resume();
        WaitFor(() => svc.CompletedScans >= 1);
        Assert.Single(Find("a.txt"));
    }

    [Fact]
    public void An_idle_indexer_does_not_hold_the_write_lock()
    {
        W("a.txt");
        Indexed(watch: true);
        var user = new UserDataRepository(_db);
        var sw = Stopwatch.StartNew();
        user.AddHistory("pendant que l'indexeur attend");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"write took {sw.ElapsedMilliseconds} ms");
        Assert.Single(user.GetHistory("pendant", 5));
    }

    [Fact]
    public void A_write_from_elsewhere_gets_through_while_the_store_works_in_batches()
    {
        using var store = new IndexStore(_db);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                store.Upsert(new FileEntry(0, @"C:\busy", $"f{n++}.txt", "txt", 1, 1, 1, 1, false));
                if (n % 200 == 0) store.Commit();
            }
            store.Commit();
        });
        Thread.Sleep(100);
        var sw = Stopwatch.StartNew();
        new UserDataRepository(_db).AddHistory("écriture concurrente");
        long elapsed = sw.ElapsedMilliseconds;
        stop.Cancel();
        writer.Wait();
        Assert.True(elapsed < 2000, $"write took {elapsed} ms");
    }

    [Fact]
    public void Removing_a_root_forgets_its_entries_but_not_the_files()
    {
        W("a.txt");
        var svc = Indexed();
        svc.RemoveRoot(_root);
        WaitFor(() => Find("a.txt").Count == 0);
        Assert.True(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Empty(svc.Roots);
    }

    [Fact]
    public void Restart_resumes_from_the_existing_index_without_relisting_everything()
    {
        for (int i = 0; i < 4; i++) W($@"d{i}\f.txt");
        Indexed().Stop();

        var again = new IndexerService(_db, null, new IndexerOptions { AutoScanOnStart = true, InitialDelay = TimeSpan.Zero, Watch = false, Throttle = false });
        _svc = again;
        again.Start();
        WaitFor(() => again.CompletedScans >= 1);
        Assert.Equal(4, again.Status.IndexedFiles);
        Assert.True(again.Status.DirsSkipped >= 4);
    }

    [Fact]
    public void Corrupt_database_is_restored_from_backup()
    {
        W("precious.txt");
        Indexed().Stop();
        DatabaseMaintenance.Backup(_db);
        NexusDatabase.ReleaseAll();
        File.WriteAllBytes(_db.FilePath, new byte[4096].Select((_, i) => (byte)(i * 31)).ToArray());

        var (db, notice) = DatabaseMaintenance.OpenWithRecovery(_db.FilePath);
        Assert.Contains("restauré", notice);
        var hits = new SqliteSearchEngine(db).SearchAsync(QueryParser.Parse("precious"), 0, 10, CancellationToken.None).Result.Items;
        Assert.Single(hits);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_db.FilePath)!, "*.corrupt-*")); // original kept aside
    }

    [Fact]
    public void Corrupt_database_without_backup_starts_a_fresh_index()
    {
        string path = Path.Combine(_base, "other", "x.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a database");
        var (db, notice) = DatabaseMaintenance.OpenWithRecovery(path);
        Assert.Contains("nouvel index", notice);
        Assert.Equal(0, new FileRepository(db).Count());
    }

    [Fact]
    public void Old_v1_database_is_migrated_and_keeps_user_data()
    {
        string path = Path.Combine(_base, "v1", "old.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files(id INTEGER PRIMARY KEY, dir TEXT NOT NULL, name TEXT NOT NULL, ext TEXT NOT NULL DEFAULT '', size INTEGER NOT NULL DEFAULT 0,
                    created INTEGER NOT NULL DEFAULT 0, modified INTEGER NOT NULL DEFAULT 0, accessed INTEGER NOT NULL DEFAULT 0, is_dir INTEGER NOT NULL DEFAULT 0);
                CREATE VIRTUAL TABLE files_fts USING fts5(name, content='files', content_rowid='id', tokenize='trigram');
                CREATE TABLE search_history(query TEXT PRIMARY KEY COLLATE NOCASE, last_used INTEGER NOT NULL, uses INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE favorites(path TEXT PRIMARY KEY COLLATE NOCASE, added INTEGER NOT NULL);
                INSERT INTO search_history VALUES('keepme', 1, 1);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        var db = new NexusDatabase(path);
        db.Initialize();
        Assert.Equal(["keepme"], new UserDataRepository(db).GetHistory("", 5));
        new FileRepository(db).UpsertBatch([new FileEntry(0, @"C:\a", "x.txt", "txt", 1, 1, 1, 1, false)]);
        Assert.Equal(1, new FileRepository(db).Count());
    }

    public void Dispose()
    {
        _svc?.Dispose();
        NexusDatabase.ReleaseAll();
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(_base, true); break; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
