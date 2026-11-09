using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.Core;
using Nexus.Core.Formatting;
using Nexus.Database;
using Nexus.Indexer;
using Nexus.Infrastructure;
using Nexus.Search;

namespace Nexus.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsStore _store;
    private readonly IndexerService _indexer;
    private readonly ContentIndexer _content;
    private readonly WindowsSearchProvider _windowsSearch;
    private readonly UserDataRepository _user;
    private readonly NexusDatabase _db;
    private readonly FileActions _actions;
    private bool _loading;

    public override string Title => "Settings";
    public override string Glyph => "";

    public ObservableCollection<string> Exclusions { get; } = [];
    public ObservableCollection<string> ContentFolders { get; } = [];

    [ObservableProperty] private bool _throttle;
    [ObservableProperty] private bool _watch;
    [ObservableProperty] private bool _scanAtStartup;
    [ObservableProperty] private bool _contentEnabled;
    [ObservableProperty] private bool _useWindowsSearch;
    [ObservableProperty] private string _newExclusion = "";
    [ObservableProperty] private string _databaseInfo = "";
    [ObservableProperty] private string _notice = "";

    public SettingsViewModel(SettingsStore store, IndexerService indexer, ContentIndexer content, WindowsSearchProvider windowsSearch,
        UserDataRepository user, NexusDatabase db, FileActions actions, Widget.WidgetService widget)
    {
        _store = store; _indexer = indexer; _content = content; _windowsSearch = windowsSearch; _user = user; _db = db; _actions = actions; Widget = widget;
    }

    // ---- Search Widget ----

    public sealed record ScreenChoice(string Label, string Value);

    public Widget.WidgetService Widget { get; }
    public IReadOnlyList<string> WidgetPositions { get; } = ["Centré en haut", "Centré au milieu", "Position personnalisée"];
    public IReadOnlyList<string> WidgetSizeNames { get; } = ["Petite", "Normale", "Grande", "Très grande"];
    public IReadOnlyList<int> WidgetResultCounts { get; } = [5, 8, 9, 10, 15, 20];
    public ObservableCollection<ScreenChoice> WidgetScreens { get; } = [];

    [ObservableProperty] private bool _widgetEnabled;
    [ObservableProperty] private string _widgetHotkey = "";
    [ObservableProperty] private int _widgetPosition;
    [ObservableProperty] private int _widgetSize;
    [ObservableProperty] private string _widgetScreen = "";
    [ObservableProperty] private int _widgetMaxResults;
    [ObservableProperty] private bool _widgetKeepLast;
    [ObservableProperty] private bool _widgetAutostart;

    private void LoadWidget()
    {
        var w = Widget.Settings;
        var screens = Nexus.App.Widget.ScreenListView.Choices();
        WidgetScreens.Clear();
        WidgetScreens.Add(new ScreenChoice("Écran où se trouve la souris", WidgetSettings.ScreenUnderCursor));
        WidgetScreens.Add(new ScreenChoice("Écran principal", WidgetSettings.PrimaryScreen));
        foreach (var (label, device) in screens) WidgetScreens.Add(new ScreenChoice(label, device));
        WidgetEnabled = Widget.Enabled;
        WidgetHotkey = w.Hotkey;
        WidgetPosition = (int)w.Position;
        WidgetSize = (int)w.Size;
        WidgetScreen = WidgetScreens.Any(s => s.Value == w.Screen) ? w.Screen : WidgetSettings.ScreenUnderCursor;
        WidgetMaxResults = w.MaxResults;
        WidgetKeepLast = w.KeepLastSearch;
        WidgetAutostart = Widget.StartWithWindows;
    }

    partial void OnWidgetEnabledChanged(bool value)
    {
        if (_loading) return;
        Widget.SetEnabled(value);
        if (!value) { _loading = true; WidgetAutostart = Widget.StartWithWindows; _loading = false; }
    }

    partial void OnWidgetPositionChanged(int value)
    {
        if (_loading || value < 0) return;
        Widget.Update(s => s with { Position = (WidgetPosition)value });
        if (value == (int)Nexus.Core.WidgetPosition.Custom) Notice = "Ouvrez le widget, puis faites-le glisser par son icône de recherche : sa position sera retenue.";
    }

    partial void OnWidgetSizeChanged(int value) { if (!_loading && value >= 0) Widget.Update(s => s with { Size = (WidgetSize)value }); }
    partial void OnWidgetScreenChanged(string value) { if (!_loading && value is not null) Widget.Update(s => s with { Screen = value }); }
    partial void OnWidgetMaxResultsChanged(int value) { if (!_loading && value > 0) Widget.Update(s => s with { MaxResults = value }); }
    partial void OnWidgetKeepLastChanged(bool value) { if (!_loading) Widget.Update(s => s with { KeepLastSearch = value }); }

    partial void OnWidgetAutostartChanged(bool value)
    {
        if (_loading) return;
        string? error = Widget.SetStartWithWindows(value);
        if (error is null) return;
        Notice = error;
        _loading = true; WidgetAutostart = Widget.StartWithWindows; _loading = false;
    }

    /// <summary>Called by the shortcut field once a complete combination has been typed.</summary>
    public void ApplyWidgetHotkey(string text)
    {
        string? error = Widget.TrySetHotkey(text);
        if (error is null)
        {
            WidgetHotkey = Widget.Settings.Hotkey;
            Notice = $"Raccourci enregistré : {WidgetHotkey}.";
        }
        else Notice = error;
    }

    public override async Task OnNavigatedToAsync()
    {
        _loading = true;
        var s = _store.Load();
        Throttle = s.ThrottleIndexer;
        Watch = s.WatchChanges;
        ScanAtStartup = s.ScanAtStartup;
        ContentEnabled = s.ContentIndexEnabled;
        UseWindowsSearch = s.UseWindowsSearch;
        Exclusions.Clear();
        foreach (var e in s.Exclusions) Exclusions.Add(e);
        ContentFolders.Clear();
        foreach (var f in s.ContentFolders ?? [.. NexusSettings.DefaultContentFolders]) ContentFolders.Add(f);
        LoadWidget();
        _loading = false;

        long content = await Task.Run(_content.CountIndexed);
        long size = new[] { _db.FilePath, _db.FilePath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        DatabaseInfo = $"Base : {ByteSize.Format(size)}  ·  {_indexer.Status.IndexedFiles:N0} fichiers indexés  ·  contenu lu pour {content:N0} fichiers";
    }

    private NexusSettings Current() => new()
    {
        ThrottleIndexer = Throttle, WatchChanges = Watch, ScanAtStartup = ScanAtStartup, ContentIndexEnabled = ContentEnabled,
        UseWindowsSearch = UseWindowsSearch, Exclusions = [.. Exclusions], ContentFolders = [.. ContentFolders], Widget = Widget.Settings,
    };

    private void Save(string? notice = null)
    {
        if (_loading) return;
        _store.Save(Current());
        if (notice is not null) Notice = notice;
    }

    partial void OnThrottleChanged(bool value) { _indexer.SetThrottle(value); Save(); }
    partial void OnWatchChanged(bool value) => Save("Pris en compte au prochain démarrage de NEXUS.");
    partial void OnScanAtStartupChanged(bool value) => Save("Pris en compte au prochain démarrage de NEXUS.");
    partial void OnUseWindowsSearchChanged(bool value) { _windowsSearch.Enabled = value; Save(); }
    partial void OnContentEnabledChanged(bool value) { Save(); RequestContent(); }

    private void RequestContent()
    {
        if (_loading) return;
        _content.Request(() => ContentOptionsFrom(_store.Load()));
    }

    public static ContentOptions? ContentOptionsFrom(NexusSettings s) =>
        s.ContentIndexEnabled ? new ContentOptions { Folders = s.ContentFolders ?? [.. NexusSettings.DefaultContentFolders], Throttle = s.ThrottleIndexer } : null;

    private void ExclusionsChanged()
    {
        Save("Exclusions enregistrées : une analyse complète est lancée pour les appliquer.");
        _indexer.SetExclusions(Exclusions);
    }

    [RelayCommand]
    private void AddExclusionFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à exclure de l'index" };
        if (dlg.ShowDialog() != true) return;
        string path = Path.GetFullPath(dlg.FolderName).TrimEnd('\\');
        if (Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Exclusions.Add(path);
        ExclusionsChanged();
    }

    /// <summary>A bare name such as node_modules applies to every folder with that name.</summary>
    [RelayCommand]
    private void AddExclusionName()
    {
        string name = NewExclusion.Trim().TrimEnd('\\');
        if (name.Length == 0 || Exclusions.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Exclusions.Add(name);
        NewExclusion = "";
        ExclusionsChanged();
    }

    [RelayCommand]
    private void RemoveExclusion(string? value)
    {
        if (value is not null && Exclusions.Remove(value)) ExclusionsChanged();
    }

    [RelayCommand]
    private void AddContentFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier dont le contenu sera indexé" };
        if (dlg.ShowDialog() != true) return;
        string path = Path.GetFullPath(dlg.FolderName).TrimEnd('\\');
        if (ContentFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        ContentFolders.Add(path);
        Save("Lecture du contenu de ce dossier lancée en arrière-plan.");
        RequestContent();
    }

    [RelayCommand]
    private void RemoveContentFolder(string? value)
    {
        if (value is null || !ContentFolders.Remove(value)) return;
        Save("Dossier retiré de l'index de contenu.");
        RequestContent();
    }

    public string VersionText { get; } = "NEXUS " + UpdateInstaller.CurrentVersion.ToString(3);

    [RelayCommand]
    private void InstallUpdate()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Installateur de la nouvelle version", Filter = "Installateur NEXUS|NEXUS-Setup-*.exe" };
        if (dlg.ShowDialog() != true) return;
        var check = UpdateInstaller.Inspect(dlg.FileName);
        if (check.Error is not null) { Notice = check.Error; return; }
        if (!_actions.Confirm($"Installer NEXUS {check.Version!.ToString(3)} à la place de la version {UpdateInstaller.CurrentVersion.ToString(3)} ?\n\n" +
                              "NEXUS va se fermer puis redémarrer. L'index et les réglages sont conservés.", "Mise à jour")) return;
        try { UpdateInstaller.RunAndExit(dlg.FileName); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Notice = "Impossible de lancer l'installateur : " + ex.Message; }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _user.ClearHistory();
        Notice = "Historique des recherches effacé.";
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo(FileLog.DefaultDirectory) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Notice = ex.Message; }
    }
}
