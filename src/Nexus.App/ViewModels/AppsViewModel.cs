using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.AppAnalyzer;
using Nexus.Core.Formatting;

namespace Nexus.App.ViewModels;

public sealed class DataFolderRow(DataFolder f)
{
    public string Path { get; } = f.Path;
    public string SizeText { get; } = ByteSize.Format(f.Size);
    public string Label { get; } = f.Confidence == Confidence.High ? $"compté · {f.Reason}" : "possible · non compté dans le total";
}

public sealed class AppRow(AppInfo a)
{
    public AppInfo App { get; } = a;
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string DateText => App.InstallDate is { } d ? (App.InstallDateFromFolder ? "≈ " : "") + d.ToString("d") : "—";
    public string ProgramText { get; } = Format(a.ProgramSize, a.ProgramKind);
    public string DataText { get; } = a.DataSize > 0 ? ByteSize.Format(a.DataSize) : "—";
    public string CacheText { get; } = a.CacheSize > 0 ? ByteSize.Format(a.CacheSize) : "—";
    public string TotalText { get; } = a.TotalEstimated > 0 ? "≈ " + ByteSize.Format(a.TotalEstimated) : "—";
    public string SourceText { get; } = a.Source switch
    {
        AppSource.Registry => "Registre",
        AppSource.Store => "Microsoft Store",
        AppSource.Steam => "Steam",
        AppSource.Epic => "Epic Games",
        _ => "Dossier Program Files",
    };

    /// <summary>Measured sizes are plain; anything else carries a visible mark.</summary>
    public static string Format(long size, SizeKind kind) => kind switch
    {
        SizeKind.Measured => ByteSize.Format(size),
        SizeKind.Estimated => "≈ " + ByteSize.Format(size),
        SizeKind.Declared => ByteSize.Format(size) + " (déclaré)",
        _ => "—",
    };
}

public sealed partial class AppsViewModel : PageViewModel
{
    private readonly AppAnalyzerService _service;
    private readonly FileActions _actions;
    private List<AppRow> _all = [];

    public override string Title => "Applications";
    public override string Glyph => "";

    public IReadOnlyList<string> SortModes { get; } = ["Plus lourdes", "Plus légères", "Nom", "Date d'installation"];
    public ObservableCollection<AppRow> Rows { get; } = [];
    public ObservableCollection<DataFolderRow> SelectedFolders { get; } = [];

    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _showSystem;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private AppRow? _selected;
    [ObservableProperty] private string _detailPath = "";
    [ObservableProperty] private string _detailSizes = "";
    [ObservableProperty] private string _detailNotes = "";

    public AppsViewModel(AppAnalyzerService service, FileActions actions)
    {
        _service = service;
        _actions = actions;
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_all.Count == 0) await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        Status = "Détection des applications et calcul des tailles…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var apps = await _service.AnalyzeAsync(refreshStats: true);
            _all = apps.Select(a => new AppRow(a)).ToList();
            Apply();
            Status = $"{apps.Count(a => !a.IsSystem):N0} applications détectées en {sw.Elapsed.TotalSeconds:F1} s.";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Status = "Échec de l'analyse : " + ex.Message;
        }
    }

    partial void OnSortIndexChanged(int value) => Apply();
    partial void OnFilterChanged(string value) => Apply();
    partial void OnShowSystemChanged(bool value) => Apply();

    private void Apply()
    {
        IEnumerable<AppRow> q = _all;
        if (!ShowSystem) q = q.Where(r => !r.App.IsSystem);
        if (!string.IsNullOrWhiteSpace(Filter))
            q = q.Where(r => r.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase) || r.Publisher.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));
        q = SortIndex switch
        {
            0 => q.OrderByDescending(r => r.App.TotalEstimated),
            // Applications whose size is unknown are not "the lightest": they go last.
            1 => q.OrderBy(r => r.App.TotalEstimated == 0 ? 1 : 0).ThenBy(r => r.App.TotalEstimated),
            2 => q.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(r => r.App.InstallDate is null ? 1 : 0).ThenByDescending(r => r.App.InstallDate),
        };
        Rows.Clear();
        foreach (var r in q) Rows.Add(r);
    }

    partial void OnSelectedChanged(AppRow? value)
    {
        SelectedFolders.Clear();
        if (value is null) { DetailPath = DetailSizes = DetailNotes = ""; return; }
        var a = value.App;
        foreach (var f in a.DataFolders) SelectedFolders.Add(new DataFolderRow(f));
        DetailPath = a.InstallPath ?? "Emplacement inconnu";
        string measured = a.ProgramKind switch
        {
            SizeKind.Measured => "Taille mesurée : " + ByteSize.Format(a.ProgramSize),
            SizeKind.Estimated => "Taille estimée : ≈ " + ByteSize.Format(a.ProgramSize),
            _ => "Taille mesurée : non disponible",
        };
        string declared = a.DeclaredSize is { } d ? "Déclarée par l'installateur : " + ByteSize.Format(d) : "Déclarée par l'installateur : non renseignée";
        string possible = a.PossibleDataSize > 0 ? $"   ·   Données possibles non comptées : {ByteSize.Format(a.PossibleDataSize)}" : "";
        DetailSizes = $"{measured}   ·   {declared}{possible}";
        DetailNotes = string.Join("\n", a.Notes);
    }

    [RelayCommand]
    private void OpenInstall()
    {
        if (Selected?.App.InstallPath is { } p) OpenLocation(p);
    }

    [RelayCommand]
    private void OpenLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { _actions.OpenLocation(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Status = ex.Message; }
    }
}
