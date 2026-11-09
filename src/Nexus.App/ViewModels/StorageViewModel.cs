using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.Core.Formatting;
using Nexus.Core.Indexing;
using Nexus.Indexer;
using Nexus.Infrastructure;
using Nexus.Storage;

namespace Nexus.App.ViewModels;

public sealed class TreemapNode
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Extension { get; init; } = "";
    public long Size { get; init; }
    public bool IsDir { get; init; }
    /// <summary>Aggregate of the items too small to draw ("autres fichiers").</summary>
    public bool IsOther { get; init; }
    public List<TreemapNode> Children { get; set; } = [];
}

public sealed class StorageRow
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Location { get; init; } = "";
    public string Detail { get; init; } = "";
    public long Size { get; init; }
    public string SizeText => ByteSize.Format(Size);
    public double Percent { get; init; }
    public string PercentText => Percent >= 0.1 ? $"{Percent:0.#} %" : "<0,1 %";
    public string Extra { get; init; } = "";
    public bool IsDir { get; init; }
    public bool IsOther { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed partial class DiskCard(DiskInfoItem info, bool indexed) : ObservableObject
{
    public string Root => info.Root;
    public string Title => string.IsNullOrWhiteSpace(info.Label) ? $"Disque {info.Root}" : $"{info.Label} ({info.Root})";
    public string Kind => info.Kind switch { DiskKind.Ssd => "SSD", DiskKind.Hdd => "HDD", _ => "type inconnu" };
    public string FileSystem => info.FileSystem;
    public double Percent => info.PercentUsed;
    public string Summary => $"{ByteSize.Format(info.Used)} utilisés sur {ByteSize.Format(info.Total)} · {ByteSize.Format(info.Free)} libres";
    public bool IsAlmostFull => info.PercentUsed >= 90;
    [ObservableProperty] private bool _isIndexed = indexed;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class StorageViewModel : PageViewModel
{
    private const int MaxTreemapFolders = 40;

    private readonly StorageQueries _q;
    private readonly DirectoryStatsBuilder _stats;
    private readonly TempCacheAnalyzer _cleanup;
    private readonly IndexerService _indexer;
    private readonly FileActions _actions;
    private readonly IconProvider _icons;
    private CancellationTokenSource _cts = new();
    private bool _computing;
    private long _lastComputeTick;

    public override string Title => "Storage";
    public override string Glyph => "";

    public ObservableCollection<DiskCard> Disks { get; } = [];
    public ObservableCollection<StorageRow> Entries { get; } = [];
    public ObservableCollection<StorageRow> BigFiles { get; } = [];
    public ObservableCollection<StorageRow> BigFolders { get; } = [];
    public ObservableCollection<StorageRow> KnownTemp { get; } = [];
    public ObservableCollection<StorageRow> TempExtensions { get; } = [];
    public ObservableCollection<StorageRow> Caches { get; } = [];
    public IReadOnlyList<string> Tabs { get; } = ["Explorateur", "Plus gros fichiers", "Plus gros dossiers", "Temporaires et caches"];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private DiskCard? _selectedDisk;
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _folderSummary = "";
    [ObservableProperty] private string _cleanupSummary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private TreemapNode? _treemap;

    public StorageViewModel(StorageQueries q, DirectoryStatsBuilder stats, TempCacheAnalyzer cleanup,
        IndexerService indexer, FileActions actions, IconProvider icons)
    {
        _q = q; _stats = stats; _cleanup = cleanup; _indexer = indexer; _actions = actions; _icons = icons;
    }

    // ------------------------------------------------------------ lifecycle

    public override async Task OnNavigatedToAsync()
    {
        if (Disks.Count == 0 || Environment.TickCount64 - _lastComputeTick > 5000) await LoadDisksAsync();
        var info = await Task.Run(_stats.GetInfo);
        bool scanning = _indexer.Status.State == IndexerState.Scanning;
        bool recent = Environment.TickCount64 - _lastComputeTick < 60_000;
        if (!_computing && (!info.Exists || (info.Stale && !scanning && !recent))) await RebuildAsync(checkDisk: false);
        else await ReloadAsync();
    }

    private async Task LoadDisksAsync()
    {
        var disks = await Task.Run(DiskInfoProvider.GetFixedDisks);
        var roots = _indexer.Roots;
        string? keep = SelectedDisk?.Root;
        Disks.Clear();
        foreach (var d in disks)
            Disks.Add(new DiskCard(d, roots.Any(r => r.Equals(d.Root, StringComparison.OrdinalIgnoreCase))));
        var pick = Disks.FirstOrDefault(d => d.Root == keep) ?? Disks.FirstOrDefault(d => d.IsIndexed) ?? Disks.FirstOrDefault();
        if (pick is not null) { SelectedDisk = pick; pick.IsSelected = true; }
    }

    partial void OnSelectedDiskChanged(DiskCard? value)
    {
        foreach (var d in Disks) d.IsSelected = d == value;
        // Refreshing the disk list creates new cards: the folder being viewed must not be lost.
        if (value is not null && !CurrentPath.StartsWith(value.Root, StringComparison.OrdinalIgnoreCase)) CurrentPath = value.Root;
    }

    partial void OnCurrentPathChanged(string value) => _ = ReloadAsync();
    partial void OnTabIndexChanged(int value) => _ = ReloadAsync();

    /// <summary>The "Recalculer" button: what the user expects to see is the disk as it is now.</summary>
    [RelayCommand]
    private Task RecomputeAsync() => RebuildAsync(checkDisk: true);

    private async Task RebuildAsync(bool checkDisk)
    {
        if (_computing) return;
        _computing = true; IsBusy = true;
        try
        {
            if (checkDisk)
            {
                await LoadDisksAsync();
                if (_indexer.Status.State is IndexerState.Idle or IndexerState.Scanning)
                {
                    Status = "Vérification du disque…";
                    await _indexer.SyncWithDiskAsync();
                }
            }
            Status = "Calcul des tailles de dossiers à partir de l'index…";
            var progress = new Progress<string>(m => Status = m);
            long n = await Task.Run(() => _stats.Rebuild(CancellationToken.None, progress));
            _lastComputeTick = Environment.TickCount64;
            Status = $"{n:N0} dossiers analysés à partir de l'index.";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Status = "Échec du calcul : " + ex.Message;
        }
        finally { _computing = false; IsBusy = false; }
        await ReloadAsync();
    }

    // ------------------------------------------------------------ loading the visible tab

    private async Task ReloadAsync()
    {
        if (SelectedDisk is null) return;
        _cts.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        string scope = SelectedDisk.Root, path = string.IsNullOrEmpty(CurrentPath) ? scope : CurrentPath;
        int tab = TabIndex;
        try
        {
            if (!SelectedDisk.IsIndexed)
            {
                Status = "Ce disque n'est pas indexé : cliquez sur « Indexer ce disque » pour analyser ses dossiers.";
                ClearAll();
                return;
            }
            switch (tab)
            {
                case 0: await LoadExplorerAsync(path, ct); break;
                case 1: await LoadFilesAsync(scope, ct); break;
                case 2: await LoadFoldersAsync(scope, ct); break;
                case 3: await LoadCleanupAsync(ct); break;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ClearAll()
    {
        Entries.Clear(); BigFiles.Clear(); BigFolders.Clear(); Treemap = null; FolderSummary = "";
    }

    private async Task LoadExplorerAsync(string path, CancellationToken ct)
    {
        var (root, total, rows) = await Task.Run(() =>
        {
            var node = BuildNode(path, 0, 200, 100);
            ct.ThrowIfCancellationRequested();
            var me = _q.GetFolder(path);
            long sum = me?.Size ?? node.Children.Sum(c => c.Size);
            var list = node.Children.Select(c => ToRow(c, sum)).ToList();
            return (node, me, list);
        }, ct);
        ct.ThrowIfCancellationRequested();

        Entries.Clear();
        foreach (var r in rows) Entries.Add(r);
        Treemap = root;
        FolderSummary = total is null
            ? "Aucune statistique pour ce dossier (calcul en attente ou dossier vide)."
            : $"{ByteSize.Format(total.Size)} · {total.Files:N0} fichiers · {total.Dirs:N0} sous-dossiers directs (tailles logiques d'après l'index)";
        if (!_computing) Status = "";
    }

    /// <summary>Builds a treemap node for a folder; depth 0 also gets the content of its biggest sub-folders.</summary>
    private TreemapNode BuildNode(string path, int depth, int maxFolders, int maxFiles)
    {
        var folders = _q.GetChildFolders(path, maxFolders);
        var (files, fileCount, fileTotal) = _q.GetChildFiles(path, maxFiles);
        var kids = new List<TreemapNode>();
        foreach (var f in folders)
            kids.Add(new TreemapNode { Name = f.Name, Path = f.Path, Size = f.Size, IsDir = true });
        foreach (var f in files)
            kids.Add(new TreemapNode { Name = f.Name, Path = f.FullPath, Size = f.Size, Extension = f.Extension });
        long shown = files.Sum(f => f.Size);
        if (fileCount > files.Count && fileTotal > shown)
            kids.Add(new TreemapNode { Name = $"{fileCount - files.Count:N0} autres fichiers", Path = path, Size = fileTotal - shown, IsOther = true });
        kids = kids.Where(k => k.Size > 0).OrderByDescending(k => k.Size).ToList();

        var node = new TreemapNode { Name = path, Path = path, Size = kids.Sum(k => k.Size), IsDir = true, Children = kids };
        if (depth == 0)
            foreach (var k in kids.Where(k => k.IsDir).Take(MaxTreemapFolders))
                k.Children = BuildNode(k.Path, 1, 60, 40).Children;
        return node;
    }

    private StorageRow ToRow(TreemapNode n, long parentTotal) => new()
    {
        Name = n.Name, Path = n.Path, Size = n.Size, IsDir = n.IsDir && !n.IsOther, IsOther = n.IsOther,
        Percent = parentTotal > 0 ? 100.0 * n.Size / parentTotal : 0,
        Icon = n.IsOther ? null : _icons.Get(n.Extension, n.IsDir),
        Location = n.Path,
    };

    private async Task LoadFilesAsync(string scope, CancellationToken ct)
    {
        var list = await Task.Run(() => _q.TopFiles(scope, 200), ct);
        long total = list.Sum(f => f.Size);
        BigFiles.Clear();
        foreach (var f in list)
            BigFiles.Add(new StorageRow
            {
                Name = f.Name, Path = f.FullPath, Location = f.Directory, Size = f.Size, Icon = _icons.Get(f.Extension, false),
                Extra = new DateTime(f.ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d"),
                Percent = total > 0 ? 100.0 * f.Size / list[0].Size : 0,
            });
        Status = list.Count == 0 ? "Aucun fichier dans l'index pour ce disque." : $"200 plus gros fichiers de {scope} (l'index est la source des tailles).";
    }

    private async Task LoadFoldersAsync(string scope, CancellationToken ct)
    {
        var list = await Task.Run(() => _q.TopFolders(scope, 200), ct);
        long max = list.Count > 0 ? list[0].OwnSize : 0;
        BigFolders.Clear();
        foreach (var f in list)
            BigFolders.Add(new StorageRow
            {
                Name = f.Name, Path = f.Path, Location = f.Path, Size = f.OwnSize, IsDir = true, Icon = _icons.Get("", true),
                Percent = max > 0 ? 100.0 * f.OwnSize / max : 0,
                Extra = ByteSize.Format(f.Size) + " au total · " + f.Files.ToString("N0") + " fichiers",
            });
        Status = list.Count == 0 ? "Aucune statistique de dossier : lancez le calcul." : "Dossiers classés par données qu'ils contiennent directement (sans compter leurs sous-dossiers).";
    }

    private async Task LoadCleanupAsync(CancellationToken ct)
    {
        var (known, exts, caches) = await Task.Run(() =>
            (_cleanup.MeasureKnownLocations(), _cleanup.TempFilesByExtension(), _cleanup.FindCacheFolders(60)), ct);
        KnownTemp.Clear(); TempExtensions.Clear(); Caches.Clear();
        long max = Math.Max(1, new[] { known.Select(k => k.Size).DefaultIfEmpty(0).Max(), caches.Select(c => c.Size).DefaultIfEmpty(0).Max() }.Max());
        foreach (var k in known)
            KnownTemp.Add(new StorageRow { Name = k.Label, Path = k.Path, Location = k.Path, Size = k.Size, Percent = 100.0 * k.Size / max, IsDir = true, Extra = $"{k.Files:N0} fichiers", Icon = _icons.Get("", true) });
        foreach (var e in exts)
            TempExtensions.Add(new StorageRow { Name = "." + e.Extension, Size = e.Size, Extra = $"{e.Count:N0} fichiers" });
        foreach (var c in caches)
            Caches.Add(new StorageRow { Name = c.Name, Path = c.Path, Location = c.Path, Size = c.Size, Percent = 100.0 * c.Size / max, IsDir = true, Extra = $"{c.Files:N0} fichiers", Icon = _icons.Get("", true) });
        CleanupSummary = $"Estimation : {ByteSize.Format(caches.Sum(c => c.Size))} de caches, {ByteSize.Format(known.Sum(k => k.Size))} d'emplacements temporaires connus, " +
                         $"{ByteSize.Format(exts.Sum(e => e.Size))} de fichiers .tmp/.dmp… Les catégories peuvent se chevaucher : ne les additionnez pas.";
        Status = "NEXUS ne supprime jamais rien ici : ouvrez l'emplacement et décidez vous-même (les caches appartiennent aux applications).";
    }

    // ------------------------------------------------------------ commands

    [RelayCommand] private void SelectDisk(DiskCard disk) => SelectedDisk = disk;

    [RelayCommand]
    private void GoUp()
    {
        if (SelectedDisk is null || CurrentPath.Equals(SelectedDisk.Root, StringComparison.OrdinalIgnoreCase)) return;
        int i = CurrentPath.LastIndexOf('\\');
        CurrentPath = i > 0 ? CurrentPath[..i] : SelectedDisk.Root;
    }

    public void NavigateTo(string path)
    {
        if (SelectedDisk is not null && path.StartsWith(SelectedDisk.Root, StringComparison.OrdinalIgnoreCase)) CurrentPath = path;
    }

    [RelayCommand]
    private void OpenEntry(StorageRow? row)
    {
        if (row is null) return;
        if (row.IsDir) NavigateTo(row.Path);
        else if (!row.IsOther) OpenLocation(row.Path);
    }

    [RelayCommand]
    private void OpenLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { _actions.OpenLocation(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Status = ex.Message; }
    }

    [RelayCommand]
    private void IndexDisk(DiskCard? disk)
    {
        if (disk is null) return;
        _indexer.AddRoot(disk.Root);
        disk.IsIndexed = true;
        Status = $"Indexation de {disk.Root} lancée (page Indexation pour suivre). Revenez ici ensuite : les tailles se calculent automatiquement.";
    }
}
