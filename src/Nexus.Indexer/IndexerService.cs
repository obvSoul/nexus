using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Nexus.Core.Indexing;
using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Infrastructure;

namespace Nexus.Indexer;

public sealed class IndexerOptions
{
    /// <summary>Wait before the automatic scan that follows start-up (keeps app start-up fast).</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(3);
    public bool AutoScanOnStart { get; init; } = true;
    public bool Watch { get; init; } = true;
    /// <summary>Sleep between folders when other processes are using the CPU.</summary>
    public bool Throttle { get; init; } = true;
    public int BatchSize { get; init; } = 5000;
    /// <summary>Folder names never indexed (system-protected or pure noise).</summary>
    public HashSet<string> ExcludedNames { get; init; } = new(StringComparer.OrdinalIgnoreCase)
        { "$Recycle.Bin", "System Volume Information", "Config.Msi", "$WinREAgent", "$SysReset", "Recovery" };
    /// <summary>Full folder paths never indexed (e.g. the index's own folder, to avoid feedback loops).</summary>
    public List<string> ExcludedPaths { get; init; } = [];
    public TimeSpan BackupEvery { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Keeps the SQLite index in sync with the disk. One background thread (low CPU/I/O priority) does everything:
/// incremental scans (folders whose modification time is unchanged are not re-listed), real-time changes from
/// FileSystemWatcher (coalesced), pause/resume, and CPU-aware pacing. Index writes are batched in transactions.
/// </summary>
public sealed class IndexerService : IDisposable
{
    private readonly record struct ScanEntry(string Name, long Size, long Created, long Modified, long Accessed, bool IsDir, bool IsReparse);

    private static readonly EnumerationOptions EnumOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,      // we want the exception so access errors can be counted
        AttributesToSkip = 0,            // include hidden and system entries
        ReturnSpecialDirectories = false,
    };

    private readonly NexusDatabase _db;
    private readonly INexusLog _log;
    private readonly IndexerOptions _opt;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _running = new(true);
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action<IndexStore>> _commands = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Old, string New)> _renames = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _roots = [];
    private readonly object _statusGate = new();
    private readonly LinkedList<ActivityItem> _activity = new();
    private readonly CpuMonitor _cpu = new();
    private Thread? _thread;
    private IndexerStatus _status = IndexerStatus.Initial;
    private volatile int _scanRequest; // 0 none, 1 quick, 2 deep
    private const long CountEveryMs = 120_000;
    private const long ChangeNoteEveryMs = 60_000;
    private long _lastEventTick, _firstEventTick, _lastPublish, _lastCpuSample, _lastCountTick, _lastChangeNote;
    private long _changesSinceNote;
    private int _paceDelayMs;
    private bool _countDirty, _writeTuned;
    private long _lastCount;
    private int _scanCount;

    // scan counters (worker thread only; published through _status)
    private long _indexed, _dirsScanned, _dirsSkipped, _denied, _added, _updated, _removed;

    public IndexerService(NexusDatabase db, INexusLog? log = null, IndexerOptions? options = null)
    {
        _db = db;
        _log = log ?? NullLog.Instance;
        _opt = options ?? new IndexerOptions();
        // Our own files change whenever we work: watching them would feed the indexer with its own activity.
        _opt.ExcludedPaths.Add(Path.GetDirectoryName(Path.GetFullPath(db.FilePath)) ?? "");
        _opt.ExcludedPaths.Add(FileLog.DefaultDirectory);
    }

    public IndexerStatus Status { get { lock (_statusGate) return _status; } }

    /// <summary>Number of completed scans since start (lets callers/tests await a scan).</summary>
    public int CompletedScans => Volatile.Read(ref _scanCount);

    public event Action<IndexerStatus>? StatusChanged;
    public event Action<ActivityItem>? ActivityAdded;
    /// <summary>Raised on the indexer thread after each completed scan.</summary>
    public event Action? ScanFinished;

    public IReadOnlyList<ActivityItem> RecentActivity { get { lock (_activity) return _activity.ToList(); } }
    public IReadOnlyList<string> Roots { get { lock (_roots) return _roots.ToList(); } }

    // ------------------------------------------------------------------ public control

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { Name = "NexusIndexer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void AddRoot(string path)
    {
        path = NormalizeRoot(path);
        Post(store =>
        {
            store.AddRoot(path);
            store.Commit();
            lock (_roots) if (!_roots.Contains(path, StringComparer.OrdinalIgnoreCase)) _roots.Add(path);
            StartWatcher(path);
            Note($"Dossier ajouté à l'index : {path}");
            _scanRequest = Math.Max(_scanRequest, 1);
        });
    }

    /// <summary>Stops indexing a folder and forgets its entries (the real files are never touched).</summary>
    public void RemoveRoot(string path)
    {
        path = NormalizeRoot(path);
        Post(store =>
        {
            StopWatcher(path);
            lock (_roots) _roots.RemoveAll(r => r.Equals(path, StringComparison.OrdinalIgnoreCase));
            store.RemoveRoot(path);
            store.Commit();
            _countDirty = true;
            Note($"Dossier retiré de l'index : {path}");
        });
    }

    /// <summary>Queue a scan. Quick = skip folders whose modification time is unchanged; deep = compare everything.</summary>
    public void RequestScan(bool deep = false)
    {
        _scanRequest = Math.Max(_scanRequest, deep ? 2 : 1);
        _wake.Set();
    }

    /// <summary>Runs an incremental scan now and completes when the index matches the disk.</summary>
    public Task SyncWithDiskAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(store =>
        {
            try
            {
                ScanAll(store, deep: false, _cts.Token);
                done.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                throw;
            }
        });
        return done.Task;
    }

    public void Pause()
    {
        _running.Reset();
        SetStatus(s => s with { State = IndexerState.Paused, Message = "En pause" }, force: true);
        Note("Indexation mise en pause.");
    }

    public void Resume()
    {
        _running.Set();
        _wake.Set();
        Note("Indexation reprise.");
    }

    public void Stop()
    {
        _cts.Cancel();
        _running.Set();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        Stop();
        _running.Dispose();
        _wake.Dispose();
        _cts.Dispose();
    }

    private void Post(Action<IndexStore> command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    /// <summary>
    /// Canonical form of a root. "C:" means the whole drive: Windows would otherwise read it as "the current folder on drive C:",
    /// which is C:\Windows\System32 when the program is started by the system.
    /// </summary>
    public static string NormalizeRoot(string path)
    {
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':') return char.ToUpperInvariant(path[0]) + ":";
        return Path.GetFullPath(path).TrimEnd('\\');
    }

    /// <summary>Version 1.0.0 could save System32 instead of the drive the user had ticked. Done once per index.</summary>
    private void RepairDriveRoot(IndexStore store)
    {
        if (store.GetMeta("roots_repaired") is not null) return;
        string system = Environment.SystemDirectory.TrimEnd('\\');
        if (store.GetRoots().Contains(system, StringComparer.OrdinalIgnoreCase))
        {
            store.RemoveRoot(system);
            store.AddRoot(system[..2]);
            _log.Warn($"Index root repaired: {system} replaced by {system[..2]}");
        }
        store.SetMeta("roots_repaired", "1");
        store.Commit();
    }

    /// <summary>
    /// Versions up to 1.0.1 stored changes reported under an 8.3 path as separate entries that no scan ever visits.
    /// Done once per index. A real name that merely looks like an 8.3 name is kept.
    /// </summary>
    private void RepairShortPaths(IndexStore store)
    {
        if (store.GetMeta("shortpaths_repaired") is not null) return;
        var phantoms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in store.GetPathsWithTilde())
            if (ShortPaths.FirstShortPrefix(path) is { } prefix) phantoms.Add(prefix);

        int removed = 0;
        foreach (string prefix in phantoms)
        {
            bool real = (Directory.Exists(prefix) || File.Exists(prefix)) && prefix.Equals(ShortPaths.ToLong(prefix), StringComparison.OrdinalIgnoreCase);
            var (dir, name) = Split(prefix);
            if (real || dir is null) continue;
            store.DeleteTree(dir, name);
            removed++;
        }
        store.SetMeta("shortpaths_repaired", "1");
        store.Commit();
        if (removed > 0) _log.Warn($"Index repaired: {removed} short-path location(s) removed");
    }

    // ------------------------------------------------------------------ worker

    private void Run()
    {
        BackgroundMode.BeginOnCurrentThread();
        var ct = _cts.Token;
        try
        {
            using var store = new IndexStore(_db);
            store.SetMeta("clean_shutdown", "0");
            store.Commit();
            RepairDriveRoot(store);
            RepairShortPaths(store);
            lock (_roots) { _roots.Clear(); _roots.AddRange(store.GetRoots()); }
            _indexed = _lastCount = store.CountFiles();
            DateTime? lastScan = long.TryParse(store.GetMeta("last_scan_utc"), out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;
            SetStatus(s => s with { State = IndexerState.Idle, IndexedFiles = _indexed, LastScanUtc = lastScan, Message = "Prêt" }, force: true);
            _log.Info($"Indexer started: {_indexed:N0} files known, {Roots.Count} root(s)");

            try
            {
                if (_opt.Watch) foreach (var r in Roots) StartWatcher(r);
                if (_opt.AutoScanOnStart && Roots.Count > 0)
                {
                    ct.WaitHandle.WaitOne(_opt.InitialDelay);
                    ct.ThrowIfCancellationRequested();
                    _scanRequest = 1;
                }

                while (!ct.IsCancellationRequested)
                {
                    WaitIfPaused(store, ct);
                    while (_commands.TryDequeue(out var cmd)) cmd(store);

                    int req = Interlocked.Exchange(ref _scanRequest, 0);
                    if (req != 0) { ScanAll(store, deep: req == 2, ct); continue; }

                    if (PendingDue()) { ProcessChanges(store, ct); continue; }

                    // Counting a million rows is not free: while the disk is busy the count may lag by up to two minutes.
                    long sinceCount = Environment.TickCount64 - _lastCountTick;
                    if (_countDirty && sinceCount >= CountEveryMs)
                    {
                        _indexed = _lastCount = store.CountFiles();
                        _lastCountTick = Environment.TickCount64;
                        _countDirty = false;
                        SetStatus(s => s with { IndexedFiles = _indexed }, force: true);
                    }
                    int wait = !_pending.IsEmpty ? 250 : _countDirty ? (int)Math.Max(250, CountEveryMs - sinceCount) : Timeout.Infinite;
                    _wake.WaitOne(wait);
                }
            }
            catch (OperationCanceledException) { } // a normal stop, even in the middle of a scan
            store.SetMeta("clean_shutdown", "1"); // only reached when the indexer stops by itself, not after a crash
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error("Indexer crashed", ex);
            SetStatus(s => s with { State = IndexerState.Stopped, Message = "Erreur : " + ex.Message }, force: true);
        }
        finally
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            SetStatus(s => s with { State = IndexerState.Stopped, Watching = false, Message = "Arrêté" }, force: true);
        }
    }

    private void WaitIfPaused(IndexStore store, CancellationToken ct)
    {
        if (_running.IsSet) return;
        if (store.PendingOps > 0) store.Commit(); // never sit on uncommitted work
        SetStatus(s => s with { State = IndexerState.Paused, Message = "En pause" }, force: true);
        _running.Wait(ct);
        SetStatus(s => s with { State = IndexerState.Idle, Message = "Prêt" }, force: true);
    }

    // ------------------------------------------------------------------ scanning

    private void ScanAll(IndexStore store, bool deep, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _dirsScanned = _dirsSkipped = _denied = _added = _updated = _removed = 0;
        SetStatus(s => s with { State = IndexerState.Scanning, DirsScanned = 0, DirsSkipped = 0, AccessDenied = 0, Message = deep ? "Analyse complète…" : "Analyse…" }, force: true);
        Note(deep ? "Analyse complète démarrée." : "Analyse incrémentale démarrée.");
        bool completed = false;
        try
        {
            foreach (var root in Roots)
            {
                if (!Directory.Exists(root + "\\")) { Note($"Dossier introuvable, ignoré : {root}"); continue; }
                ScanTree(store, root, deep, ct);
            }
            completed = true;
        }
        finally
        {
            store.Commit();
            if (_writeTuned)
            {
                store.Tune(4, 1000); // give the memory back
                _writeTuned = false;
            }
        }
        if (!completed) return;

        _indexed = _lastCount = store.CountFiles();
        _countDirty = false;
        store.SetMeta("last_scan_utc", DateTime.UtcNow.Ticks.ToString());
        store.Commit();
        Interlocked.Increment(ref _scanCount);
        ScanFinished?.Invoke();
        SetStatus(s => s with
        {
            State = IndexerState.Idle, IndexedFiles = _indexed, CurrentPath = "", LastScanUtc = DateTime.UtcNow, Message = "À jour",
            DirsScanned = _dirsScanned, DirsSkipped = _dirsSkipped, AccessDenied = _denied,
        }, force: true);
        string summary = $"Analyse terminée en {sw.Elapsed.TotalSeconds:F1} s : {_added:N0} nouveaux, {_updated:N0} modifiés, {_removed:N0} supprimés ({_dirsSkipped:N0} dossiers inchangés ignorés).";
        Note(summary);
        _log.Info(summary + $" denied={_denied}");
        try { if (DatabaseMaintenance.BackupIfDue(_db, _opt.BackupEvery)) Note("Sauvegarde de l'index créée."); }
        catch (Exception ex) { _log.Error("Index backup failed", ex); }
    }

    private void ScanTree(IndexStore store, string root, bool deep, CancellationToken ct)
    {
        var stack = new Stack<(string Path, long Mtime)>();
        stack.Push((root, 0));
        long lastFlush = Environment.TickCount64;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            WaitIfPausedMidScan(store, ct);
            var (dir, knownMtime) = stack.Pop();
            long mtime = knownMtime != 0 ? knownMtime : SafeMtime(dir);
            if (mtime == 0) { HandleVanishedDir(store, dir); continue; }

            Pace(ct);
            long? stored = deep ? null : store.GetDirMtime(dir);
            if (stored == mtime)
            {
                // Unchanged folder: its file list is not read again, only descend into known sub-folders.
                _dirsSkipped++;
                foreach (var sub in store.GetSubdirNames(dir)) stack.Push((dir + "\\" + sub, 0));
            }
            else if (!ScanDirectory(store, dir, mtime, stack)) { /* denied/vanished: counted inside */ }

            _dirsScanned++;
            if (!_writeTuned && _added + _updated > 5000)
            {
                // A long run of writes (typically the first scan): more cache for its duration only.
                store.Tune(64, 10_000);
                _writeTuned = true;
            }
            if (store.PendingOps >= _opt.BatchSize || Environment.TickCount64 - lastFlush > 1500)
            {
                store.Commit();
                lastFlush = Environment.TickCount64;
            }
            PublishProgress(dir);
        }
    }

    /// <summary>Lists one folder, diffs it against the index, queues sub-folders. Returns false if it could not be read.</summary>
    private bool ScanDirectory(IndexStore store, string dir, long mtime, Stack<(string, long)> stack)
    {
        List<ScanEntry> fs;
        try { fs = Enumerate(dir); }
        catch (UnauthorizedAccessException) { _denied++; return false; }
        catch (DirectoryNotFoundException) { HandleVanishedDir(store, dir); return false; }
        catch (IOException ex) { _denied++; _log.Warn($"Cannot read {dir}: {ex.Message}"); return false; }

        var known = store.GetEntries(dir);
        foreach (var e in fs)
        {
            if (e.IsDir && IsExcluded(dir, e.Name)) continue;
            if (known.Remove(e.Name, out var old))
            {
                bool same = old.Size == e.Size && old.Modified == e.Modified && (old.IsDir != 0) == e.IsDir;
                if (!same) { store.Upsert(ToEntry(dir, e)); _updated++; }
            }
            else
            {
                store.Upsert(ToEntry(dir, e));
                if (!e.IsDir) _added++;
            }
            // Time 0 = read it from the folder itself: the copy in the parent's listing can lag behind (NTFS updates it lazily).
            if (e.IsDir && !e.IsReparse) stack.Push((dir + "\\" + e.Name, 0));
        }
        foreach (var (name, gone) in known) // in the index but no longer on disk
        {
            store.DeleteTree(dir, name);
            if (gone.IsDir == 0) _removed++;
        }
        store.SetDir(dir, mtime);
        return true;
    }

    private void HandleVanishedDir(IndexStore store, string dir)
    {
        int i = dir.LastIndexOf('\\');
        if (i > 0) store.DeleteTree(dir[..i], dir[(i + 1)..]);
        _removed++;
    }

    private static List<ScanEntry> Enumerate(string dir)
    {
        string path = dir.Length == 2 && dir[1] == ':' ? dir + "\\" : dir;
        var en = new FileSystemEnumerable<ScanEntry>(path,
            static (ref FileSystemEntry e) => new ScanEntry(
                e.FileName.ToString(), e.IsDirectory ? 0 : e.Length,
                e.CreationTimeUtc.UtcTicks, e.LastWriteTimeUtc.UtcTicks, e.LastAccessTimeUtc.UtcTicks,
                e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), EnumOpts);
        return [.. en];
    }

    private static long SafeMtime(string dir)
    {
        try
        {
            string path = dir.Length == 2 && dir[1] == ':' ? dir + "\\" : dir;
            var info = new DirectoryInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static FileEntry ToEntry(string dir, ScanEntry e) =>
        new(0, dir, e.Name, e.IsDir ? "" : FileEntry.ExtensionOf(e.Name), e.Size, e.Created, e.Modified, e.Accessed, e.IsDir, e.IsReparse);

    /// <summary>
    /// Replaces the user's exclusions (a full path, or a bare folder name). A deep scan is queued so that newly excluded
    /// folders leave the index and newly allowed ones enter it.
    /// </summary>
    public void SetExclusions(IEnumerable<string> exclusions, bool rescan = true)
    {
        var list = exclusions.Select(e => e.Trim().TrimEnd('\\')).Where(e => e.Length > 0).ToList();
        _userExcludedNames = new HashSet<string>(list.Where(e => !e.Contains('\\')), StringComparer.OrdinalIgnoreCase);
        _userExcludedPaths = list.Where(e => e.Contains('\\')).ToArray();
        if (rescan) RequestScan(deep: true);
    }

    /// <summary>Turns the back-off under CPU load on or off, immediately.</summary>
    public void SetThrottle(bool enabled) => _throttle = enabled;

    private volatile HashSet<string> _userExcludedNames = new(StringComparer.OrdinalIgnoreCase);
    private volatile string[] _userExcludedPaths = [];
    private volatile bool _throttle = true;

    private bool IsExcluded(string parentDir, string name)
    {
        if (_opt.ExcludedNames.Contains(name) || _userExcludedNames.Contains(name)) return true;
        string full = parentDir + "\\" + name;
        foreach (var p in _opt.ExcludedPaths.Concat(_userExcludedPaths))
            if (p.Length > 0 && full.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool IsPathExcluded(string fullPath)
    {
        var names = _userExcludedNames;
        foreach (var seg in fullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (_opt.ExcludedNames.Contains(seg) || names.Contains(seg)) return true;
        foreach (var p in _opt.ExcludedPaths.Concat(_userExcludedPaths))
        {
            string x = p.TrimEnd('\\');
            if (x.Length > 0 && (fullPath.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                                 fullPath.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private void WaitIfPausedMidScan(IndexStore store, CancellationToken ct)
    {
        if (_running.IsSet) return;
        store.Commit();
        SetStatus(s => s with { State = IndexerState.Paused, Message = "En pause" }, force: true);
        _running.Wait(ct);
        SetStatus(s => s with { State = IndexerState.Scanning, Message = "Analyse…" }, force: true);
    }

    /// <summary>Back off while other programs are busy: sleeps grow with their CPU use.</summary>
    private void Pace(CancellationToken ct)
    {
        if (!_opt.Throttle || !_throttle) return;
        long now = Environment.TickCount64;
        if (now - _lastCpuSample > 500)
        {
            double other = _cpu.SampleOtherLoad();
            _paceDelayMs = other > 0.85 ? 400 : other > 0.60 ? 120 : 0;
            _lastCpuSample = now;
        }
        if (_paceDelayMs > 0) ct.WaitHandle.WaitOne(_paceDelayMs);
    }

    private void PublishProgress(string current)
    {
        long now = Environment.TickCount64;
        if (now - _lastPublish < 250) return;
        long filesNow = _lastCount + _added - _removed;
        SetStatus(s => s with { DirsScanned = _dirsScanned, DirsSkipped = _dirsSkipped, AccessDenied = _denied, IndexedFiles = Math.Max(0, filesNow), CurrentPath = current });
    }

    // ------------------------------------------------------------------ real-time changes

    private void StartWatcher(string root)
    {
        if (!_opt.Watch || !Directory.Exists(root + "\\")) return;
        if (_watchers.Any(w => w.Path.Equals(root + "\\", StringComparison.OrdinalIgnoreCase) || w.Path.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            var w = new FileSystemWatcher(root + "\\")
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            w.Created += (_, e) => Enqueue(e.FullPath);
            w.Changed += (_, e) => Enqueue(e.FullPath);
            w.Deleted += (_, e) => Enqueue(e.FullPath);
            w.Renamed += (_, e) =>
            {
                if (IsPathExcluded(e.FullPath) && IsPathExcluded(e.OldFullPath)) return;
                _renames.Enqueue((e.OldFullPath, e.FullPath));
                Enqueue(e.FullPath);
            };
            w.Error += (_, e) =>
            {
                // Buffer overflow: events were lost, so fall back to an incremental scan of everything.
                _log.Warn("FileSystemWatcher error: " + e.GetException().Message);
                _scanRequest = Math.Max(_scanRequest, 1);
                _wake.Set();
            };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
            SetStatus(s => s with { Watching = true }, force: true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _log.Warn($"Cannot watch {root}: {ex.Message}");
        }
    }

    private void StopWatcher(string root)
    {
        foreach (var w in _watchers.Where(w => w.Path.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            w.Dispose();
            _watchers.Remove(w);
        }
        SetStatus(s => s with { Watching = _watchers.Count > 0 }, force: true);
    }

    internal void Enqueue(string fullPath)
    {
        if (IsPathExcluded(fullPath)) return;
        long now = Environment.TickCount64;
        if (_pending.IsEmpty) Volatile.Write(ref _firstEventTick, now);
        Volatile.Write(ref _lastEventTick, now);
        if (_pending.Count > 200_000) { _pending.Clear(); _scanRequest = Math.Max(_scanRequest, 1); } // flood: rescan instead
        _pending[fullPath] = 0;
        _wake.Set();
    }

    /// <summary>Changes are applied once the disk has been quiet for 750 ms (or after 5 s at most).</summary>
    private bool PendingDue()
    {
        if (_pending.IsEmpty && _renames.IsEmpty) return false;
        long now = Environment.TickCount64;
        return now - Volatile.Read(ref _lastEventTick) > 750 || now - Volatile.Read(ref _firstEventTick) > 5000;
    }

    private void ProcessChanges(IndexStore store, CancellationToken ct)
    {
        int ops = 0;
        while (_renames.TryDequeue(out var r))
        {
            ct.ThrowIfCancellationRequested();
            ApplyRename(store, r.Old, r.New);
            ops++;
        }
        foreach (var path in _pending.Keys.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            _pending.TryRemove(path, out _);
            Recheck(store, path, ct);
            ops++;
        }
        store.Commit();
        if (ops > 0)
        {
            _countDirty = true;
            _changesSinceNote += ops;
            // Windows changes files all the time: one summary per minute, not one line per change.
            long now = Environment.TickCount64;
            if (now - _lastChangeNote >= ChangeNoteEveryMs)
            {
                Note($"{_changesSinceNote:N0} changement(s) appliqué(s) depuis le disque.");                _changesSinceNote = 0;
                _lastChangeNote = now;
            }
        }
    }

    private void ApplyRename(IndexStore store, string oldPath, string newPath)
    {
        oldPath = ShortPaths.ToLong(oldPath);
        newPath = ShortPaths.ToLong(newPath);
        if (IsPathExcluded(newPath)) { SplitDelete(store, oldPath); return; }
        var (od, on) = Split(oldPath);
        var (nd, nn) = Split(newPath);
        if (od is null || nd is null) return;
        // Keeps the row id (and the whole subtree for folders) instead of delete + insert.
        if (!store.Move(od, on, nd, nn, FileEntry.ExtensionOf(nn))) return;
        Recheck(store, newPath, CancellationToken.None, scanNewFolder: false);
    }

    private void Recheck(IndexStore store, string path, CancellationToken ct, bool scanNewFolder = true)
    {
        path = ShortPaths.ToLong(path);
        if (IsPathExcluded(path)) return;
        var (dir, name) = Split(path);
        if (dir is null || !UnderAnyRoot(path)) return;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) { store.DeleteTree(dir, name); return; }

            bool isDir = info is DirectoryInfo;
            bool reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            long size = info is FileInfo fi ? fi.Length : 0;
            // Many events change nothing we store (attributes, access): no write for those.
            if (store.GetEntry(dir, name) is { } old && old.Size == size && old.Modified == info.LastWriteTimeUtc.Ticks && (old.IsDir != 0) == isDir) return;
            store.Upsert(new FileEntry(0, dir, name, isDir ? "" : FileEntry.ExtensionOf(name), size,
                info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.LastAccessTimeUtc.Ticks, isDir, reparse));

            // A folder we have never listed (copied or moved in): index its content too.
            if (isDir && !reparse && scanNewFolder && store.GetDirMtime(path) is null)
                ScanTree(store, path, deep: false, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or protected right now: skipped; the folder's modification time will make the next scan catch it.
            _log.Warn($"Change skipped ({ex.GetType().Name}): {path}");
        }
    }

    private void SplitDelete(IndexStore store, string path)
    {
        var (dir, name) = Split(path);
        if (dir is not null) store.DeleteTree(dir, name);
    }

    private bool UnderAnyRoot(string path)
    {
        foreach (var r in Roots)
            if (path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static (string? Dir, string Name) Split(string path)
    {
        path = path.TrimEnd('\\');
        int i = path.LastIndexOf('\\');
        return i <= 0 ? (null, path) : (path[..i], path[(i + 1)..]);
    }

    // ------------------------------------------------------------------ status & activity

    private void SetStatus(Func<IndexerStatus, IndexerStatus> change, bool force = false)
    {
        IndexerStatus snapshot;
        long now = Environment.TickCount64;
        lock (_statusGate)
        {
            _status = change(_status);
            if (!force && now - _lastPublish < 250) return;
            _lastPublish = now;
            snapshot = _status;
        }
        StatusChanged?.Invoke(snapshot);
    }

    private void Note(string text)
    {
        var item = new ActivityItem(DateTime.UtcNow, text);
        lock (_activity)
        {
            _activity.AddFirst(item);
            while (_activity.Count > 100) _activity.RemoveLast();
        }
        ActivityAdded?.Invoke(item);
        _log.Info(text);
    }
}
