using System.Diagnostics;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Search;

// Dev tool: "Nexus.Benchmarks seed <dbPath> <rootFolder>" fills a database with the real files under a folder
// (temporary stand-in until the real indexer exists in Phase 4).
if (args.Length == 3 && args[0] == "seed")
{
    var sdb = new NexusDatabase(args[1]);
    sdb.Initialize();
    IEnumerable<FileEntry> Real()
    {
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var info in new DirectoryInfo(args[2]).EnumerateFileSystemInfos("*", opt))
        {
            bool dir = info is DirectoryInfo;
            string parent = (info is FileInfo f ? f.DirectoryName : ((DirectoryInfo)info).Parent?.FullName) ?? "";
            yield return new FileEntry(0, parent.TrimEnd('\\'), info.Name, dir ? "" : FileEntry.ExtensionOf(info.Name),
                info is FileInfo fi ? fi.Length : 0, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.LastAccessTimeUtc.Ticks, dir);
        }
    }
    int seeded = new FileRepository(sdb).UpsertBatch(Real());
    Console.WriteLine($"Seeded {seeded:N0} entries into {args[1]}");
    NexusDatabase.ReleaseAll();
    return;
}

// Dev tool: "Nexus.Benchmarks inspect <dbPath>" prints the state of an index, read-only (diagnostics).
if (args.Length == 2 && args[0] == "inspect")
{
    using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={args[1]};Mode=ReadOnly;Pooling=False");
    con.Open();
    void Q(string title, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        Console.WriteLine("--- " + title);
        while (r.Read()) Console.WriteLine("  " + string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i)))));
    }
    Q("version", "PRAGMA user_version");
    Q("roots", "SELECT path, datetime((added-621355968000000000)/10000000,'unixepoch','localtime') FROM index_roots");
    Q("meta", "SELECT key, substr(value,1,200) FROM meta");
    Q("counts", "SELECT (SELECT COUNT(*) FROM files WHERE is_dir=0) AS files, (SELECT COUNT(*) FROM files WHERE is_dir<>0) AS folders, (SELECT COUNT(*) FROM dirs) AS dirs_listed, (SELECT COUNT(*) FROM dir_stats) AS dir_stats");
    Q("dirs listed under the drive root", "SELECT path, mtime FROM dirs WHERE path='C:'");
    Q("children of C: in files", "SELECT name, is_dir FROM files WHERE dir='C:' ORDER BY name LIMIT 40");
    Q("top-level spread of files", "SELECT substr(dir,1,instr(substr(dir,4),'\\')+2) AS top, COUNT(*) FROM files WHERE is_dir=0 GROUP BY top ORDER BY 2 DESC LIMIT 15");
    Q("subfolders of C: not listed yet", "SELECT f.name FROM files f LEFT JOIN dirs d ON d.path = f.dir||'\\'||f.name WHERE f.dir='C:' AND f.is_dir=1 AND d.path IS NULL");
    return;
}

// Dev tool: "Nexus.Benchmarks content <dbPath> <word>" reads the default content folders, then searches a word (own index + Windows Search).
if (args.Length == 3 && args[0] == "content")
{
    var cdb = new NexusDatabase(args[1]);
    cdb.Initialize();
    using var indexer = new Nexus.Indexer.ContentIndexer(cdb);
    for (int pass = 1; pass <= 2; pass++)
    {
        var sw2 = Stopwatch.StartNew();
        var r = indexer.Run(new Nexus.Indexer.ContentOptions { Folders = Nexus.Core.NexusSettings.DefaultContentFolders }, default);
        Console.WriteLine($"Pass {pass}: {sw2.Elapsed.TotalSeconds:F1} s | read {r.Indexed:N0} | unreadable {r.Unreadable:N0} | removed {r.Removed:N0}");
    }
    var ws = new WindowsSearchProvider();
    var sw3 = Stopwatch.StartNew();
    var wsHits = ws.FindContent([args[2]], default);
    Console.WriteLine($"Windows Search: {wsHits.Count:N0} hits in {sw3.ElapsedMilliseconds} ms");
    foreach (var (name, eng) in new (string, ISearchEngine)[] { ("own index", new SqliteSearchEngine(cdb)), ("own + Windows", new SqliteSearchEngine(cdb, ws)) })
    {
        var q = QueryParser.Parse("content:" + args[2]);
        var sw4 = Stopwatch.StartNew();
        var page = eng.SearchAsync(q, 0, 200, default).Result;
        Console.WriteLine($"{name}: first page {sw4.Elapsed.TotalMilliseconds:F1} ms, {eng.CountAsync(q, default).Result:N0} matches");
    }
    Console.WriteLine($"Files with text indexed: {indexer.CountIndexed():N0}, db {new FileInfo(cdb.FilePath).Length / 1048576} MB, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
    NexusDatabase.ReleaseAll();
    return;
}

// Dev tool: "Nexus.Benchmarks insert <rows> <cacheMb> <walPages>" times index writes with given SQLite settings.
if (args.Length == 4 && args[0] == "insert")
{
    int rows = int.Parse(args[1]), cacheMb = int.Parse(args[2]), walPages = int.Parse(args[3]);
    string idir = Path.Combine(Path.GetTempPath(), "nexus-ins-" + Guid.NewGuid().ToString("N"));
    var idb = new NexusDatabase(Path.Combine(idir, "i.db"));
    idb.Initialize();
    string[] w = ["report", "invoice", "photo", "holiday", "backup", "project", "design", "notes", "budget", "meeting"];
    var irnd = new Random(1);
    var t0 = Stopwatch.StartNew();
    using (var store = new IndexStore(idb))
    {
        store.Tune(cacheMb, walPages);
        for (int i = 0; i < rows; i++)
        {
            string nm = $"{w[irnd.Next(10)]}_{w[irnd.Next(10)]}_{i}.txt";
            store.Upsert(new FileEntry(0, $@"C:\bench\{w[irnd.Next(10)]}\{irnd.Next(2000)}", nm, "txt", irnd.NextInt64(50_000_000), i, irnd.NextInt64(1L << 60), i, false));
            if (store.PendingOps >= 5000) store.Commit();
        }
        store.Commit();
    }
    Console.WriteLine($"cache {cacheMb,4} MB, wal {walPages,6} pages: {rows / t0.Elapsed.TotalSeconds,8:N0} rows/s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
    NexusDatabase.ReleaseAll();
    try { Directory.Delete(idir, true); } catch (IOException) { }
    return;
}

// Dev tool: "Nexus.Benchmarks dups <dbPath> [runs]" runs the duplicate search on an existing index (read-only on the files).
if (args.Length >= 2 && args[0] == "dups")
{
    var ddb = new NexusDatabase(args[1]);
    ddb.Initialize();
    var finder = new Nexus.Duplicates.DuplicateFinder(ddb);
    string G(long v) => Nexus.Core.Formatting.ByteSize.Format(v);
    int runs = args.Length > 2 ? int.Parse(args[2]) : 2;
    for (int run = 1; run <= runs; run++)
    {
        var res = finder.FindAsync(new Nexus.Duplicates.DuplicateOptions(), null, default).GetAwaiter().GetResult();
        Console.WriteLine($"Run {run}: {res.Elapsed.TotalSeconds:F1} s | groups {res.Groups.Count:N0} | recoverable {G(res.Groups.Sum(x => x.Wasted))} | candidates {res.Candidates:N0} | quick {res.QuickHashed:N0} | sha {res.FullHashed:N0} | cache hits {res.CacheHits:N0} | read {G(res.BytesRead)} | skipped {res.Skipped:N0} | hard links {res.HardLinksIgnored}");
        if (run == runs)
            foreach (var g in res.Groups.Take(5)) Console.WriteLine($"  {G(g.Size),9} x{g.Files.Count}  {g.Files[0].Name}");
    }
    Console.WriteLine($"Peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
    NexusDatabase.ReleaseAll();
    return;
}

// Dev tool: "Nexus.Benchmarks apps <dbPath>" runs the application analysis on an existing index and prints a report.
if (args.Length == 2 && args[0] == "apps")
{
    var adb = new NexusDatabase(args[1]);
    adb.Initialize();
    var st = new Nexus.Storage.DirectoryStatsBuilder(adb);
    if (!st.GetInfo().Exists) st.Rebuild();
    var ts = Stopwatch.StartNew();
    var detected = Nexus.AppAnalyzer.AppAnalyzerService.DetectAll();
    Console.WriteLine($"Detect: {detected.Count} entries in {ts.ElapsedMilliseconds} ms");
    foreach (var g in detected.GroupBy(d => d.Source)) Console.WriteLine($"  {g.Key}: {g.Count()} ({g.Count(x => x.IsSystem)} system)");
    ts.Restart();
    var sizer = new Nexus.AppAnalyzer.AppSizer(new Nexus.Storage.StorageQueries(adb), new Nexus.Storage.TempCacheAnalyzer(adb));
    var infos = sizer.Build(detected);
    Console.WriteLine($"Sizing: {ts.ElapsedMilliseconds} ms");
    foreach (var g in infos.Where(i => !i.IsSystem).GroupBy(i => i.ProgramKind)) Console.WriteLine($"  program size {g.Key}: {g.Count()}");
    string F(long v) => Nexus.Core.Formatting.ByteSize.Format(v);
    foreach (var a in infos.Where(i => !i.IsSystem).OrderByDescending(i => i.TotalEstimated).Take(15))
        Console.WriteLine($"{F(a.TotalEstimated),9} | prog {F(a.ProgramSize),9} {a.ProgramKind,-9} | data {F(a.DataSize),9} | cache {F(a.CacheSize),9} | {a.Source,-12} | {a.Name}");
    Console.WriteLine("Store sample:");
    foreach (var a in infos.Where(i => i.Source == Nexus.AppAnalyzer.AppSource.Store && !i.IsSystem).Take(5))
        Console.WriteLine($"  {a.Name} | {a.Publisher} | {a.Version} | data {F(a.DataSize)} | prog {a.ProgramKind}");
    NexusDatabase.ReleaseAll();
    return;
}

// Dev tool: "Nexus.Benchmarks stats <dbPath>" times the folder-size computation and the storage queries on an existing index.
if (args.Length == 2 && args[0] == "stats")
{
    var tdb = new NexusDatabase(args[1]);
    tdb.Initialize();
    var builder = new Nexus.Storage.DirectoryStatsBuilder(tdb);
    var tw = Stopwatch.StartNew();
    long folders = builder.Rebuild();
    Console.WriteLine($"Rebuild dir_stats: {folders:N0} folders in {tw.Elapsed.TotalSeconds:F1} s, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
    var sq = new Nexus.Storage.StorageQueries(tdb);
    var tmp = new Nexus.Storage.TempCacheAnalyzer(tdb);
    void Time(string name, Action a) { a(); var s = Stopwatch.StartNew(); a(); Console.WriteLine($"{name,-34} {s.Elapsed.TotalMilliseconds,8:F1} ms"); }
    Time("child folders of C:\\Users", () => sq.GetChildFolders(@"C:\Users"));
    Time("top 200 files (C:)", () => sq.TopFiles("C:", 200));
    Time("top 200 files (C:\\Windows)", () => sq.TopFiles(@"C:\Windows", 200));
    Time("top 200 folders by direct size (C:)", () => sq.TopFolders("C:", 200));
    Time("temp files by extension", () => tmp.TempFilesByExtension());
    Time("cache folders", () => tmp.FindCacheFolders(60));
    Time("known temp locations", () => tmp.MeasureKnownLocations());
    foreach (var c in tmp.FindCacheFolders(5)) Console.WriteLine($"  cache: {Nexus.Core.Formatting.ByteSize.Format(c.Size),9}  {c.Path}");
    NexusDatabase.ReleaseAll();
    return;
}

// Dev tool: "Nexus.Benchmarks scan <dbPath> <root> [nothrottle]" runs the real indexer on a real folder/drive twice
// (full first scan, then incremental) and prints timings and memory.
if (args.Length >= 3 && args[0] == "scan")
{
    var sdb = new NexusDatabase(args[1]);
    sdb.Initialize();
    var opts = new Nexus.Indexer.IndexerOptions { AutoScanOnStart = false, Watch = false, Throttle = args.Length < 4 };
    using var svc = new Nexus.Indexer.IndexerService(sdb, null, opts);
    svc.Start();
    long peak = 0;
    for (int round = 1; round <= 2; round++)
    {
        int before = svc.CompletedScans;
        var t = Stopwatch.StartNew();
        if (round == 1) svc.AddRoot(args[2]); else svc.RequestScan();
        while (svc.CompletedScans == before)
        {
            Thread.Sleep(500);
            peak = Math.Max(peak, Process.GetCurrentProcess().WorkingSet64);
            if (round == 1 && t.Elapsed.TotalSeconds % 10 < 0.5) Console.WriteLine($"  ...{svc.Status.IndexedFiles:N0} files, {svc.Status.DirsScanned:N0} dirs, {svc.Status.CurrentPath}");
        }
        var st = svc.Status;
        Console.WriteLine($"Scan {round}: {t.Elapsed.TotalSeconds:F1} s | files {st.IndexedFiles:N0} | dirs scanned {st.DirsScanned:N0} (skipped unchanged {st.DirsSkipped:N0}) | access denied {st.AccessDenied:N0}");
    }
    Console.WriteLine($"Peak working set {peak / 1048576} MB, db {new FileInfo(sdb.FilePath).Length / 1048576} MB");
    svc.Stop();
    NexusDatabase.ReleaseAll();
    return;
}

// Search benchmark on a synthetic index. Usage: Nexus.Benchmarks [fileCount]  (default 1,000,000)
int count = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 1_000_000;
string dir = Path.Combine(Path.GetTempPath(), "nexus-bench-" + Guid.NewGuid().ToString("N"));
var db = new NexusDatabase(Path.Combine(dir, "bench.db"));
db.Initialize();

string[] words = ["report", "invoice", "photo", "holiday", "backup", "project", "design", "notes", "budget", "meeting", "draft", "final", "archive", "image", "movie", "song", "setup", "readme", "config", "data"];
string[] exts = ["txt", "pdf", "png", "jpg", "docx", "xlsx", "mp4", "mp3", "zip", "cs", "json", "log", "dll", "exe", "iso"];
var rnd = new Random(42);
long now = DateTime.UtcNow.Ticks;

IEnumerable<FileEntry> Generate()
{
    for (int i = 0; i < count; i++)
    {
        string name = $"{words[rnd.Next(words.Length)]}_{words[rnd.Next(words.Length)]}_{i}.{exts[rnd.Next(exts.Length)]}";
        string d = $@"C:\Users\bench\{words[rnd.Next(words.Length)]}\{rnd.Next(2000)}";
        long size = rnd.NextInt64(0, 50_000_000);
        if (i % 100_000 == 0) size = 2_000_000_000;
        long t = now - rnd.NextInt64(0, TimeSpan.TicksPerDay * 900L);
        yield return new FileEntry(0, d, name, FileEntry.ExtensionOf(name), size, t, t, t, false);
    }
}

var repo = new FileRepository(db);
var sw = Stopwatch.StartNew();
long total = 0;
foreach (var chunk in Generate().Chunk(50_000)) total += repo.UpsertBatch(chunk);
Console.WriteLine($"Insert {total:N0} rows: {sw.Elapsed.TotalSeconds:F1} s ({total / sw.Elapsed.TotalSeconds:N0} rows/s), db {new FileInfo(db.FilePath).Length / 1048576.0:F0} MB");

var engine = new SqliteSearchEngine(db);
string[] queries =
[
    "report", "rep", "holiday_backup", "*.png", "size:>1gb", "dm:week ext:pdf", "ext:log path:backup",
    "type:image sort:-size", "regex:^photo_.*_9\\d{3}\\.", "backup_zzzz", "reprot", "invoice -draft ext:xlsx",
];
Console.WriteLine($"{"query",-30} {"first page (200)",18} {"count",14}");
foreach (var text in queries)
{
    var q = QueryParser.Parse(text);
    engine.SearchAsync(q, 0, 200, default).Wait(); // warm-up
    var times = new List<double>();
    SearchPage? page = null;
    for (int i = 0; i < 5; i++)
    {
        var s = Stopwatch.StartNew();
        page = engine.SearchAsync(q, 0, 200, default).Result;
        times.Add(s.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    var cs = Stopwatch.StartNew();
    long matches = engine.CountAsync(q, default).Result;
    Console.WriteLine($"{text,-30} {times[2],12:F1} ms{(page!.UsedFuzzy ? "~" : " ")} {matches,8:N0} in {cs.Elapsed.TotalMilliseconds,6:F0} ms");
}
Console.WriteLine($"Process working set: {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");

NexusDatabase.ReleaseAll();
try { Directory.Delete(dir, true); } catch (IOException) { }
