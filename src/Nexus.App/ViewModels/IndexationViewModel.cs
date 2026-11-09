using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Core.Indexing;
using Nexus.Database;
using Nexus.Indexer;

namespace Nexus.App.ViewModels;

public sealed partial class RootItem : ObservableObject
{
    private readonly Action<RootItem, bool> _changed;
    private bool _silent;

    public RootItem(string path, bool enabled, Action<RootItem, bool> changed)
    {
        Path = path;
        _isEnabled = enabled;
        _changed = changed;
    }

    public string Path { get; }
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_silent) _changed(this, value);
    }
}

public sealed partial class IndexationViewModel : PageViewModel
{
    private readonly IndexerService _indexer;
    private readonly NexusDatabase _db;

    public override string Title => "Indexation";
    public override string Glyph => "";

    public ObservableCollection<RootItem> Roots { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];

    [ObservableProperty] private string _stateText = "—";
    [ObservableProperty] private string _filesText = "0";
    [ObservableProperty] private string _foldersText = "0";
    [ObservableProperty] private string _skippedText = "0";
    [ObservableProperty] private string _deniedText = "0";
    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _lastScanText = "jamais";
    [ObservableProperty] private string _pauseLabel = "Pause";
    [ObservableProperty] private string _watchText = "";
    [ObservableProperty] private string _notice = "";

    public IndexationViewModel(IndexerService indexer, NexusDatabase db)
    {
        _indexer = indexer;
        _db = db;
        BuildRoots();
        foreach (var a in indexer.RecentActivity.Reverse()) Activity.Insert(0, Format(a));
        Apply(indexer.Status);
        indexer.StatusChanged += s => OnUi(() => Apply(s));
        indexer.ActivityAdded += a => OnUi(() =>
        {
            Activity.Insert(0, Format(a));
            while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    // The indexer loads its folder list on its own thread after start-up, so the check boxes are built when the page opens.
    public override Task OnNavigatedToAsync()
    {
        BuildRoots();
        Apply(_indexer.Status);
        return Task.CompletedTask;
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static string Format(ActivityItem a) => $"{a.TimeUtc.ToLocalTime():HH:mm:ss}  {a.Text}";

    private void BuildRoots()
    {
        Roots.Clear();
        var active = _indexer.Roots;
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                string root = d.Name.TrimEnd('\\');
                shown.Add(root);
                Roots.Add(new RootItem(root, active.Contains(root, StringComparer.OrdinalIgnoreCase), OnRootToggled));
            }
            catch (IOException) { }
        }
        foreach (var r in active.Where(r => !shown.Contains(r)))
            Roots.Add(new RootItem(r, true, OnRootToggled));
    }

    private void OnRootToggled(RootItem item, bool enabled)
    {
        if (enabled) _indexer.AddRoot(item.Path);
        else _indexer.RemoveRoot(item.Path); // forgets index entries only; files on disk are untouched
    }

    private void Apply(IndexerStatus s)
    {
        StateText = s.State switch
        {
            IndexerState.Scanning => "Indexation en cours",
            IndexerState.Paused => "En pause",
            IndexerState.Idle => s.Watching ? "À jour — surveillance en temps réel active" : (Roots.Any(r => r.IsEnabled) ? "À jour" : "Aucun dossier sélectionné"),
            _ => "Arrêté",
        };
        FilesText = s.IndexedFiles.ToString("N0");
        FoldersText = s.DirsScanned.ToString("N0");
        SkippedText = s.DirsSkipped.ToString("N0");
        DeniedText = s.AccessDenied.ToString("N0");
        CurrentPath = s.State == IndexerState.Scanning ? s.CurrentPath : "";
        LastScanText = s.LastScanUtc is { } t ? t.ToLocalTime().ToString("g") : "jamais";
        PauseLabel = s.State == IndexerState.Paused ? "Reprendre" : "Pause";
    }

    [RelayCommand] private void ScanNow() => _indexer.RequestScan(deep: false);
    [RelayCommand] private void DeepScan() => _indexer.RequestScan(deep: true);

    [RelayCommand]
    private void TogglePause()
    {
        if (_indexer.Status.State == IndexerState.Paused) _indexer.Resume(); else _indexer.Pause();
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à indexer" };
        if (dlg.ShowDialog() != true) return;
        string path = Path.GetFullPath(dlg.FolderName).TrimEnd('\\');
        if (Roots.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Roots.Add(new RootItem(path, true, OnRootToggled));
        _indexer.AddRoot(path);
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        try
        {
            await Task.Run(() => DatabaseMaintenance.Backup(_db));
            Notice = $"Sauvegarde créée : {DatabaseMaintenance.BackupPath(_db)}";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Notice = "Échec de la sauvegarde : " + ex.Message;
        }
    }
}
