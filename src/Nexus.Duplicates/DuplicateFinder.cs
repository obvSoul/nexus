using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using Nexus.Core.Indexing;
using Nexus.Database;
using Nexus.Infrastructure;

namespace Nexus.Duplicates;

public sealed record DuplicateOptions
{
    public long MinSize { get; init; } = 1024 * 1024;
    /// <summary>Only look under this folder (null = everything indexed).</summary>
    public string? Scope { get; init; }
    /// <summary>Copies with different extensions are not compared (saves reading files).</summary>
    public bool SameExtensionOnly { get; init; } = true;
    /// <summary>Also look in application folders. Off by default: their internal copies are needed by the applications.</summary>
    public bool IncludeApplicationFolders { get; init; }
    public bool Throttle { get; init; } = true;

    /// <summary>Never examined, whatever the options.</summary>
    public IReadOnlyList<string> AlwaysExcluded { get; init; } = [Environment.GetFolderPath(Environment.SpecialFolder.Windows)];

    public IReadOnlyList<string> ApplicationFolders { get; init; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
    ];
}

public sealed record DuplicateFile(long Id, string Directory, string Name, string Extension, long Size, long ModifiedTicks)
{
    public string FullPath => Directory + "\\" + Name;
}

public sealed record DuplicateGroup(long Size, string Sha256, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Space that would be recovered by keeping a single copy.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long Done, long Total, int GroupsFound);

public sealed record DuplicateResult(
    IReadOnlyList<DuplicateGroup> Groups,
    long Candidates,
    long QuickHashed,
    long FullHashed,
    long CacheHits,
    long BytesRead,
    long Skipped,
    long HardLinksIgnored,
    TimeSpan Elapsed);

/// <summary>
/// Finds identical files by elimination, so that as little as possible is read:
/// same size (index) → metadata re-checked on disk → fast hash of three 64 KB blocks → SHA-256 of the whole file.
/// Read-only: this class never deletes or changes anything.
/// </summary>
public sealed class DuplicateFinder(NexusDatabase db, INexusLog? log = null)
{
    private const int Block = 64 * 1024;
    private const int FullBuffer = 1024 * 1024;

    // Cloud files that are not on the disk: reading them would trigger a download.
    private const FileAttributes NotLocal = FileAttributes.Offline | (FileAttributes)0x40000 /*RECALL_ON_OPEN*/ | (FileAttributes)0x400000 /*RECALL_ON_DATA_ACCESS*/;

    private readonly INexusLog _log = log ?? NullLog.Instance;

    private sealed class Run
    {
        public long Quick, Full, Hits, Bytes, Skipped, HardLinks;
        public long LastReport, LastSample;
        public int Delay;
        public readonly CpuMonitor Cpu = new();
    }

    public Task<DuplicateResult> FindAsync(DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            BackgroundMode.BeginOnCurrentThread(); // dedicated thread: low CPU and disk priority until it ends
            return Find(options, progress, ct);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private DuplicateResult Find(DuplicateOptions o, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var run = new Run();

        progress?.Report(new DuplicateProgress("Recherche des fichiers de même taille…", 0, 0, 0));
        var groups = LoadCandidates(o, ct);
        long candidates = groups.Sum(g => (long)g.Count);

        groups = CheckMetadata(groups, run, progress, candidates, ct);

        using var cache = new HashCacheStore(db, _log);
        List<DuplicateGroup> confirmed;
        try
        {
            groups = QuickStage(groups, cache, run, o, progress, ct);
            confirmed = FullStage(groups, cache, run, o, progress, ct);
        }
        finally { cache.Flush(); } // keep what was computed, even after a cancellation
        cache.RemoveOrphans();

        var result = RemoveHardLinks(confirmed, run).OrderByDescending(g => g.Wasted).ToList();
        _log.Info($"Duplicates: {result.Count} groups, candidates={candidates}, quick={run.Quick}, full={run.Full}, cache={run.Hits}, read={run.Bytes}, skipped={run.Skipped}, {sw.Elapsed.TotalSeconds:F1}s");
        return new DuplicateResult(result, candidates, run.Quick, run.Full, run.Hits, run.Bytes, run.Skipped, run.HardLinks, sw.Elapsed);
    }

    // ---- 1. same size, from the index: no file is touched ----

    private List<List<DuplicateFile>> LoadCandidates(DuplicateOptions o, CancellationToken ct)
    {
        var excluded = o.AlwaysExcluded.Concat(o.IncludeApplicationFolders ? [] : o.ApplicationFolders)
            .Where(p => p.Length > 0).Select(p => p.TrimEnd('\\')).ToList();
        var result = new List<List<DuplicateFile>>();
        var current = new List<DuplicateFile>();
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal); // one string per folder, whatever the file count
        long size = -1;

        void Close()
        {
            if (current.Count >= 2)
            {
                if (o.SameExtensionOnly)
                    result.AddRange(current.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.ToList()));
                else result.Add(current);
            }
            current = [];
        }

        using var c = db.Open();
        using var cmd = c.CreateCommand();
        string scope = o.Scope is null ? "" : "AND (dir=$p OR (dir>=$lo AND dir<$hi))";
        cmd.CommandText = $"SELECT id,dir,name,ext,size,modified FROM files WHERE is_dir=0 AND size>=$min {scope} ORDER BY size DESC";
        cmd.Parameters.AddWithValue("$min", Math.Max(1, o.MinSize));
        if (o.Scope is not null)
        {
            string s = o.Scope.TrimEnd('\\');
            cmd.Parameters.AddWithValue("$p", s);
            cmd.Parameters.AddWithValue("$lo", s + "\\");
            cmd.Parameters.AddWithValue("$hi", s + "]");
        }
        using var r = cmd.ExecuteReader();
        long n = 0;
        while (r.Read())
        {
            if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            string dir = r.GetString(1);
            if (IsExcluded(dir, excluded)) continue;
            if (!dirs.TryGetValue(dir, out var shared)) dirs[dir] = shared = dir;
            long sz = r.GetInt64(4);
            if (sz != size) { Close(); size = sz; }
            current.Add(new DuplicateFile(r.GetInt64(0), shared, r.GetString(2), r.GetString(3), sz, r.GetInt64(5)));
        }
        Close();
        return result;
    }

    private static bool IsExcluded(string dir, List<string> excluded)
    {
        foreach (var x in excluded)
            if (dir.StartsWith(x, StringComparison.OrdinalIgnoreCase) && (dir.Length == x.Length || dir[x.Length] == '\\')) return true;
        return false;
    }

    // ---- 2. metadata: the index may be out of date, and some files must not be read ----

    private static List<List<DuplicateFile>> CheckMetadata(List<List<DuplicateFile>> groups, Run run, IProgress<DuplicateProgress>? progress, long total, CancellationToken ct)
    {
        var kept = new List<List<DuplicateFile>>(groups.Count);
        long done = 0;
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var ok = new List<DuplicateFile>(g.Count);
            foreach (var f in g)
            {
                done++;
                Report(run, progress, "Vérification des métadonnées…", done, total, 0);
                if (IsReadable(f)) ok.Add(f); else run.Skipped++;
            }
            if (ok.Count >= 2) kept.Add(ok);
        }
        return kept;
    }

    private static bool IsReadable(DuplicateFile f)
    {
        try
        {
            var info = new FileInfo(f.FullPath);
            return info.Exists && info.Length == f.Size && info.LastWriteTimeUtc.Ticks == f.ModifiedTicks
                   && (info.Attributes & (NotLocal | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    // ---- 3. fast hash on three blocks ----

    private List<List<DuplicateFile>> QuickStage(List<List<DuplicateFile>> groups, HashCacheStore cache, Run run, DuplicateOptions o, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var kept = new List<List<DuplicateFile>>();
        long total = groups.Sum(g => (long)g.Count), done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Block);
        try
        {
            foreach (var g in groups)
            {
                var byHash = new Dictionary<long, List<DuplicateFile>>();
                foreach (var f in g)
                {
                    ct.ThrowIfCancellationRequested();
                    Pace(run, o, ct);
                    Report(run, progress, "Comparaison rapide…", ++done, total, 0);
                    long? quick = cache.Get(f.Id, f.Size, f.ModifiedTicks).Quick;
                    if (quick is not null) run.Hits++;
                    else
                    {
                        quick = TryQuickHash(f, buffer, run);
                        if (quick is null) { run.Skipped++; continue; }
                        run.Quick++;
                        cache.Put(f.Id, f.Size, f.ModifiedTicks, quick, cache.Get(f.Id, f.Size, f.ModifiedTicks).Sha);
                    }
                    if (!byHash.TryGetValue(quick.Value, out var list)) byHash[quick.Value] = list = [];
                    list.Add(f);
                }
                kept.AddRange(byHash.Values.Where(l => l.Count >= 2));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return kept;
    }

    private long? TryQuickHash(DuplicateFile f, byte[] buffer, Run run)
    {
        try
        {
            using var h = File.OpenHandle(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            var hash = new XxHash3();
            foreach (long offset in BlockOffsets(f.Size))
            {
                int want = (int)Math.Min(Block, f.Size - offset);
                int got = ReadFully(h, buffer.AsSpan(0, want), offset);
                if (got != want) return null;
                run.Bytes += got;
                hash.Append(buffer.AsSpan(0, got));
            }
            return unchecked((long)hash.GetCurrentHashAsUInt64());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // locked or protected
    }

    /// <summary>Start, middle and end of the file (non-overlapping; small files are simply read whole).</summary>
    public static IEnumerable<long> BlockOffsets(long size)
    {
        if (size <= 3L * Block)
        {
            for (long o = 0; o < size; o += Block) yield return o;
            yield break;
        }
        yield return 0;
        yield return size / 2 - Block / 2;
        yield return size - Block;
    }

    private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle h, Span<byte> target, long offset)
    {
        int total = 0;
        while (total < target.Length)
        {
            int n = RandomAccess.Read(h, target[total..], offset + total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ---- 4. SHA-256 of the whole file: the actual proof ----

    private List<DuplicateGroup> FullStage(List<List<DuplicateFile>> groups, HashCacheStore cache, Run run, DuplicateOptions o, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var confirmed = new List<DuplicateGroup>();
        long total = groups.Sum(g => g.Sum(f => f.Size)), done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FullBuffer);
        try
        {
            foreach (var g in groups)
            {
                var byHash = new Dictionary<string, List<DuplicateFile>>();
                foreach (var f in g)
                {
                    ct.ThrowIfCancellationRequested();
                    var cached = cache.Get(f.Id, f.Size, f.ModifiedTicks);
                    byte[]? sha = cached.Sha;
                    if (sha is not null) run.Hits++;
                    else
                    {
                        long before = done;
                        sha = TryFullHash(f, buffer, run, o, ct, n =>
                        {
                            done += n;
                            Report(run, progress, "Vérification SHA-256…", done, total, confirmed.Count);
                        });
                        done = before + f.Size;
                        if (sha is null) { run.Skipped++; continue; }
                        run.Full++;
                        cache.Put(f.Id, f.Size, f.ModifiedTicks, cached.Quick, sha);
                    }
                    string key = Convert.ToHexString(sha);
                    if (!byHash.TryGetValue(key, out var list)) byHash[key] = list = [];
                    list.Add(f);
                }
                confirmed.AddRange(byHash.Where(kv => kv.Value.Count >= 2).Select(kv => new DuplicateGroup(kv.Value[0].Size, kv.Key, kv.Value)));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return confirmed;
    }

    private byte[]? TryFullHash(DuplicateFile f, byte[] buffer, Run run, DuplicateOptions o, CancellationToken ct, Action<int> onRead)
    {
        try
        {
            using var h = File.OpenHandle(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Pace(run, o, ct);
                int n = RandomAccess.Read(h, buffer.AsSpan(0, FullBuffer), offset);
                if (n == 0) break;
                sha.AppendData(buffer, 0, n);
                offset += n;
                run.Bytes += n;
                onRead(n);
            }
            return offset == f.Size ? sha.GetHashAndReset() : null; // changed while being read
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- 5. hard links are one copy of the data under two names: deleting one frees nothing ----

    private static IEnumerable<DuplicateGroup> RemoveHardLinks(List<DuplicateGroup> groups, Run run)
    {
        foreach (var g in groups)
        {
            var seen = new HashSet<(uint, ulong)>();
            var unique = new List<DuplicateFile>(g.Files.Count);
            foreach (var f in g.Files)
            {
                if (FileIdentity.TryGet(f.FullPath) is { } id && !seen.Add(id)) { run.HardLinks++; continue; }
                unique.Add(f);
            }
            if (unique.Count >= 2) yield return g with { Files = unique };
        }
    }

    // ---- pacing ----

    private static void Pace(Run run, DuplicateOptions o, CancellationToken ct)
    {
        if (!o.Throttle) return;
        long now = Environment.TickCount64;
        if (now - run.LastSample > 500)
        {
            double other = run.Cpu.SampleOtherLoad();
            run.Delay = other > 0.85 ? 300 : other > 0.60 ? 80 : 0;
            run.LastSample = now;
        }
        if (run.Delay > 0) ct.WaitHandle.WaitOne(run.Delay);
    }

    private static void Report(Run run, IProgress<DuplicateProgress>? progress, string stage, long done, long total, int groups)
    {
        if (progress is null) return;
        long now = Environment.TickCount64;
        if (now - run.LastReport < 200) return;
        run.LastReport = now;
        progress.Report(new DuplicateProgress(stage, done, total, groups));
    }
}
