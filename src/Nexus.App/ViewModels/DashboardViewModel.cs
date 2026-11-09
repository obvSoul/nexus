using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Core.Formatting;
using Nexus.Core.Indexing;
using Nexus.Indexer;

namespace Nexus.App.ViewModels;

public sealed record DiskItem(string Name, string Format, string Kind, long Total, long Free)
{
    public long Used => Total - Free;
    public double Percent => Total == 0 ? 0 : 100.0 * Used / Total;
    public string UsedText => ByteSize.Format(Used);
    public string FreeText => ByteSize.Format(Free);
    public string TotalText => ByteSize.Format(Total);
}

public sealed partial class DashboardViewModel : PageViewModel
{
    public override string Title => "Dashboard";
    public override string Glyph => "";

    public ObservableCollection<DiskItem> Disks { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    [ObservableProperty] private string _totalUsed = "—";
    [ObservableProperty] private string _totalFree = "—";
    [ObservableProperty] private int _diskCount;
    [ObservableProperty] private string _indexedFiles = "0";
    [ObservableProperty] private string _indexState = "";

    public ObservableCollection<string> TopFolders { get; } = [];
    [ObservableProperty] private string _topFoldersNote = "";
    private readonly Nexus.Storage.StorageQueries _storage;
    private readonly Nexus.Storage.DirectoryStatsBuilder _stats;

    public ObservableCollection<string> TopApps { get; } = [];
    [ObservableProperty] private string _appCount = "—";
    [ObservableProperty] private string _topAppsNote = "";
    private readonly Nexus.AppAnalyzer.AppAnalyzerService _apps;

    public DashboardViewModel(IndexerService indexer, Nexus.Storage.StorageQueries storage, Nexus.Storage.DirectoryStatsBuilder stats,
        Nexus.AppAnalyzer.AppAnalyzerService apps)
    {
        _storage = storage; _stats = stats; _apps = apps;
        foreach (var a in indexer.RecentActivity.Take(8).Reverse()) Activity.Insert(0, Format(a));
        Apply(indexer.Status);
        indexer.StatusChanged += s => OnUi(() => Apply(s));
        indexer.ActivityAdded += a => OnUi(() =>
        {
            Activity.Insert(0, Format(a));
            while (Activity.Count > 8) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);
    private static string Format(ActivityItem a) => $"{a.TimeUtc.ToLocalTime():HH:mm:ss}  {a.Text}";

    private void Apply(IndexerStatus s)
    {
        IndexedFiles = s.IndexedFiles.ToString("N0");
        IndexState = s.State switch
        {
            IndexerState.Scanning => "indexation en cours",
            IndexerState.Paused => "en pause",
            IndexerState.Idle => s.Watching ? "à jour" : "",
            _ => "",
        };
    }

    public override async Task OnNavigatedToAsync()
    {
        var disks = await Task.Run(ReadDisks);
        Disks.Clear();
        foreach (var d in disks) Disks.Add(d);
        DiskCount = disks.Count;
        TotalUsed = ByteSize.Format(disks.Sum(d => d.Used));
        TotalFree = ByteSize.Format(disks.Sum(d => d.Free));

        var (top, exists) = await Task.Run(() => (_stats.GetInfo().Exists ? _storage.TopFolders(null, 5) : [], _stats.GetInfo().Exists));
        TopFolders.Clear();
        foreach (var f in top) TopFolders.Add($"{ByteSize.Format(f.OwnSize),9}   {f.Path}");
        TopFoldersNote = exists ? "Données contenues directement dans le dossier (sans sous-dossiers), d'après l'index."
                                : "Disponible après l'indexation d'un disque et l'ouverture de la page Storage.";

        if (!exists)
        {
            TopAppsNote = "Disponible après l'indexation d'un disque.";
            return;
        }
        TopAppsNote = "Analyse des applications…";
        try
        {
            var apps = (await _apps.GetOrAnalyzeAsync()).Where(a => !a.IsSystem).ToList();
            AppCount = apps.Count.ToString("N0");
            TopApps.Clear();
            foreach (var a in apps.OrderByDescending(a => a.TotalEstimated).Take(5))
                TopApps.Add($"≈{ByteSize.Format(a.TotalEstimated),9}   {a.Name}");
            TopAppsNote = "Tailles estimées (programme + données attribuées), détail dans la page Applications.";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            TopAppsNote = "Analyse impossible : " + ex.Message;
        }
    }

    private static List<DiskItem> ReadDisks()
    {
        var list = new List<DiskItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(new DiskItem(d.Name.TrimEnd('\\'), d.DriveFormat, d.DriveType.ToString(), d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}
