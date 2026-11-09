using Nexus.Storage;

namespace Nexus.AppAnalyzer;

/// <summary>
/// Attaches sizes to detected applications, from the index only (dir_stats). User data is attributed conservatively:
/// a folder counts only if its name identifies the application without ambiguity, and is never counted for two apps.
/// Weaker name resemblances are reported as "possible" and stay out of the totals.
/// </summary>
public sealed class AppSizer(StorageQueries q, TempCacheAnalyzer caches)
{
    public IReadOnlyList<string> DataRoots { get; init; } = [KnownPaths.Roaming, KnownPaths.Local, KnownPaths.LocalLow, KnownPaths.ProgramData];
    public IReadOnlyList<string> ProgramRoots { get; init; } = [KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.UserPrograms];
    public string PackagesRoot { get; init; } = Path.Combine(KnownPaths.Local, "Packages");

    private sealed record Candidate(FolderStat Folder, string Key);
    private sealed record Claim(int App, string Reason, bool ExactName);

    private sealed class Keys
    {
        public string Name = "", Publisher = "", Product = "";
        public HashSet<string> All = [];
    }

    public List<AppInfo> Build(IReadOnlyList<DetectedApp> apps, CancellationToken ct = default)
    {
        var keys = apps.Select(KeysOf).ToList();
        var dataChildren = DataRoots.Where(r => r.Length > 0).SelectMany(r => Children(r)).ToList();
        var programChildren = ProgramRoots.Where(r => r.Length > 0).SelectMany(r => Children(r)).ToList();
        var grandchildren = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);

        // Resolve program folders first: data folders located inside them must not be counted twice.
        var locations = new string?[apps.Count];
        var guessed = new bool[apps.Count];
        for (int i = 0; i < apps.Count; i++)
        {
            locations[i] = apps[i].InstallLocation is { Length: > 0 } l ? KnownPaths.Clean(l) : null;
            if (locations[i] is null && apps[i].Source == AppSource.Registry)
            {
                var hits = FindByKeys(programChildren, keys[i], grandchildren);
                if (hits.Count > 0) { locations[i] = hits[0].Folder.Path; guessed[i] = true; }
            }
        }
        var locationUse = locations.Where(l => l is not null).GroupBy(l => l!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Claims on data folders.
        var claims = new Dictionary<string, List<Claim>>(StringComparer.OrdinalIgnoreCase);
        var stats = new Dictionary<string, FolderStat>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < apps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (apps[i].Source == AppSource.Store)
            {
                if (apps[i].StoreFamily is { } family && q.GetFolder(Path.Combine(PackagesRoot, family)) is { } pkg)
                    AddClaim(claims, stats, pkg, new Claim(i, "dossier du paquet Store", true));
                continue; // a package identity is exact; name resemblance is not needed
            }
            foreach (var (folder, reason) in FindByKeys(dataChildren, keys[i], grandchildren))
            {
                if (IsInside(folder.Path, locations[i])) continue;
                AddClaim(claims, stats, folder, new Claim(i, reason, NameNormalizer.Normalize(folder.Name) == keys[i].Name));
            }
        }

        // A folder claimed by several applications goes to the one matching its exact name, otherwise to nobody.
        var owned = new List<DataFolder>[apps.Count];
        for (int i = 0; i < apps.Count; i++) owned[i] = [];
        var attributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedNotes = new List<string>?[apps.Count];
        foreach (var (path, list) in claims)
        {
            var winners = list.Count == 1 ? list : list.Where(c => c.ExactName).ToList();
            if (winners.Count == 1)
            {
                owned[winners[0].App].Add(new DataFolder(path, stats[path].Size, Confidence.High, winners[0].Reason));
                attributed.Add(path);
            }
            else
            {
                foreach (var c in list)
                    (sharedNotes[c.App] ??= []).Add($"Dossier non compté car revendiqué par plusieurs applications : {path}");
            }
        }

        var result = new List<AppInfo>(apps.Count);
        for (int i = 0; i < apps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(Finish(apps[i], keys[i], locations[i], guessed[i], locationUse, owned[i], sharedNotes[i], dataChildren, attributed));
        }
        return result;
    }

    private AppInfo Finish(DetectedApp app, Keys k, string? loc, bool guessed, Dictionary<string, int> locationUse,
        List<DataFolder> data, List<string>? shared, List<Candidate> dataChildren, HashSet<string> attributed)
    {
        var notes = new List<string>();
        if (shared is not null) notes.AddRange(shared);
        long program = 0;
        SizeKind kind = SizeKind.Unknown;

        var stat = loc is null ? null : q.GetFolder(loc);
        if (stat is not null && stat.Size > 0)
        {
            program = stat.Size;
            kind = SizeKind.Measured;
            // Applications installed inside this folder (Steam and its games…) are counted on their own line.
            var nested = OutermostInside(loc!, locationUse.Keys);
            if (nested.Count > 0)
            {
                long inner = nested.Sum(p => q.GetFolder(p)?.Size ?? 0);
                program = Math.Max(0, program - inner);
                notes.Add($"{nested.Count} application(s) installée(s) dans ce dossier sont exclues de sa taille ({Nexus.Core.Formatting.ByteSize.Format(inner)}).");
            }
            if (guessed) { kind = SizeKind.Estimated; notes.Add("Dossier déduit du nom de l'application (non déclaré par l'installateur)."); }
            if (app.LocationInferred) { kind = SizeKind.Estimated; notes.Add("Dossier déduit de l'emplacement de l'exécutable."); }
            if (locationUse.TryGetValue(loc!, out int n) && n > 1) { kind = SizeKind.Estimated; notes.Add($"Dossier partagé par {n} applications : taille comptée pour chacune."); }
        }
        else if (app.DeclaredSize is { } d)
        {
            program = d;
            kind = SizeKind.Declared;
            notes.Add(loc is null ? "Emplacement inconnu : seule la taille déclarée est disponible." : "Dossier absent de l'index : seule la taille déclarée est disponible.");
        }
        else if (app.Source == AppSource.Store)
            notes.Add("Dossier WindowsApps protégé par Windows : taille du programme non mesurable sans droits supplémentaires.");
        else
            notes.Add(loc is null ? "Emplacement d'installation inconnu." : "Dossier absent de l'index (disque non indexé ou accès refusé).");

        // Weak resemblances: shown, never added to the total.
        var folders = new List<DataFolder>(data);
        long possible = 0;
        if (app.Source != AppSource.Store)
            foreach (var c in dataChildren)
            {
                if (attributed.Contains(c.Folder.Path) || c.Key.Length < 5 || c.Key == k.Publisher || NameNormalizer.Generic.Contains(c.Key) || IsInside(c.Folder.Path, loc)) continue;
                if (k.All.Any(key => key.Length >= 5 && (c.Key.Contains(key, StringComparison.Ordinal) || key.Contains(c.Key, StringComparison.Ordinal))))
                {
                    folders.Add(new DataFolder(c.Folder.Path, c.Folder.Size, Confidence.Possible, "nom proche"));
                    possible += c.Folder.Size;
                }
            }

        long dataSize = data.Sum(f => f.Size);
        long cache = data.Sum(f => caches.FindCacheFolders(int.MaxValue, f.Path).Sum(x => x.Size));

        DateTime? date = app.InstallDate;
        bool fromFolder = false;
        if (date is null && loc is not null && q.GetCreatedTicks(loc) is { } t)
        {
            date = new DateTime(t, DateTimeKind.Utc).ToLocalTime();
            fromFolder = true;
        }

        return new AppInfo(app.Id, app.Name, app.Publisher, app.Version, date, fromFolder, loc, app.Source, app.IsSystem,
            app.DeclaredSize, program, kind, folders, dataSize, cache, possible, program + dataSize, notes);
    }

    /// <summary>Other install folders below <paramref name="location"/>, keeping only the top-most ones (so nothing is subtracted twice).</summary>
    private static List<string> OutermostInside(string location, IEnumerable<string> all)
    {
        var inside = all.Where(p => p.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        return inside.Where(p => !inside.Any(o => p.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static void AddClaim(Dictionary<string, List<Claim>> claims, Dictionary<string, FolderStat> stats, FolderStat f, Claim c)
    {
        stats[f.Path] = f;
        if (!claims.TryGetValue(f.Path, out var list)) claims[f.Path] = list = [];
        if (!list.Any(x => x.App == c.App)) list.Add(c);
    }

    /// <summary>Folders whose name identifies the app: directly under a root, or Publisher\Product.</summary>
    private IEnumerable<(FolderStat Folder, string Reason)> FindByKeysCore(List<Candidate> roots, Keys k, Dictionary<string, List<Candidate>> cache)
    {
        foreach (var c in roots)
        {
            if (k.All.Contains(c.Key) && !NameNormalizer.Generic.Contains(c.Key))
                yield return (c.Folder, "nom identique à l'application");
            else if (k.Publisher.Length >= 3 && c.Key == k.Publisher)
            {
                if (!cache.TryGetValue(c.Folder.Path, out var kids)) cache[c.Folder.Path] = kids = Children(c.Folder.Path).ToList();
                foreach (var g in kids)
                    if (k.All.Contains(g.Key) && !NameNormalizer.Generic.Contains(g.Key))
                        yield return (g.Folder, "dossier Éditeur\\Produit");
            }
        }
    }

    private List<(FolderStat Folder, string Reason)> FindByKeys(List<Candidate> roots, Keys k, Dictionary<string, List<Candidate>> cache) =>
        FindByKeysCore(roots, k, cache).ToList();

    private IEnumerable<Candidate> Children(string root) =>
        q.GetChildFolders(KnownPaths.Clean(root), 20000).Select(f => new Candidate(f, NameNormalizer.Normalize(f.Name)));

    private static bool IsInside(string path, string? location) =>
        location is not null && (path.Equals(location, StringComparison.OrdinalIgnoreCase) || path.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase));

    private static Keys KeysOf(DetectedApp a)
    {
        var k = new Keys
        {
            Name = NameNormalizer.Normalize(a.Name),
            Publisher = NameNormalizer.NormalizePublisher(a.Publisher),
        };
        k.Product = NameNormalizer.Product(k.Name, k.Publisher);
        var candidates = new[]
        {
            k.Name, k.Product,
            a.InstallLocation is { Length: > 0 } l ? NameNormalizer.Normalize(Path.GetFileName(KnownPaths.Clean(l))) : "",
            a.ExePath is { Length: > 0 } e && !IsInstallerExe(e) ? NameNormalizer.Normalize(Path.GetFileNameWithoutExtension(e)) : "",
        };
        foreach (var c in candidates)
            if (c.Length >= 4) k.All.Add(c);
        return k;
    }

    private static bool IsInstallerExe(string exe)
    {
        string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        return n.StartsWith("unins") || n.StartsWith("setup") || n.StartsWith("install") || n is "msiexec" or "update" or "helper" or "maintenancetool";
    }
}

public sealed class AppAnalyzerService(DirectoryStatsBuilder stats, AppSizer sizer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Result of the last analysis (null before the first one).</summary>
    public IReadOnlyList<AppInfo>? Latest { get; private set; }

    /// <summary>Detects applications and computes sizes; folder statistics are refreshed first if the index changed.</summary>
    public async Task<IReadOnlyList<AppInfo>> AnalyzeAsync(bool refreshStats, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run<IReadOnlyList<AppInfo>>(() =>
            {
                var info = stats.GetInfo();
                if (!info.Exists || (refreshStats && info.Stale)) stats.Rebuild(ct);
                var list = sizer.Build(DetectAll(), ct);
                Latest = list;
                return list;
            }, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AppInfo>> GetOrAnalyzeAsync(CancellationToken ct = default) =>
        Latest ?? await AnalyzeAsync(refreshStats: false, ct);

    public static List<DetectedApp> DetectAll()
    {
        var merged = Merge(RegistryAppDetector.Detect(), StoreAppDetector.Detect(), SteamDetector.Detect(), EpicDetector.Detect());
        var claimed = merged.Where(a => a.InstallLocation is not null).Select(a => a.InstallLocation!);
        merged.AddRange(ProgramFilesDetector.Detect(claimed));
        return merged;
    }

    /// <summary>Removes duplicates: launcher entries win over the registry; the same product registered twice is kept once.</summary>
    public static List<DetectedApp> Merge(IEnumerable<DetectedApp> registry, IEnumerable<DetectedApp> store, IEnumerable<DetectedApp> steam, IEnumerable<DetectedApp> epic)
    {
        var result = new List<DetectedApp>(steam);
        result.AddRange(epic);
        var launcherPaths = new HashSet<string>(result.Where(a => a.InstallLocation is not null).Select(a => KnownPaths.Clean(a.InstallLocation)), StringComparer.OrdinalIgnoreCase);

        var unique = registry
            .Where(a => a.InstallLocation is null || !launcherPaths.Contains(KnownPaths.Clean(a.InstallLocation)))
            .GroupBy(a => (NameNormalizer.Normalize(a.Name), a.Version))
            .Select(g => g.OrderBy(a => a.InstallLocation is null ? 1 : 0).ThenBy(a => a.LocationInferred ? 1 : 0).First());
        result.AddRange(unique);
        result.AddRange(store.GroupBy(a => a.StoreFamily).Select(g => g.First()));
        return result;
    }
}
