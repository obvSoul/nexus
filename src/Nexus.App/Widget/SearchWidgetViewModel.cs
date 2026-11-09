using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.App.ViewModels;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Search;

namespace Nexus.App.Widget;

/// <summary>An application in the results. It exposes the same properties as a file row, which the list displays.</summary>
public sealed partial class AppRow(LaunchableApp app, ImageSource? icon) : ObservableObject
{
    public LaunchableApp App { get; } = app;
    public string Name => App.Name;
    public string Directory => "Application";
    public string Extension => "app";
    public string SizeText => "";
    public string ModifiedText => "";
    public string ToolTipText => App.Name;
    [ObservableProperty] private ImageSource? _icon = icon;
}

/// <summary>The widget's search box and results. Files come from the engine of the Search page.</summary>
public sealed partial class SearchWidgetViewModel : ObservableObject
{
    private const int DebounceMs = 30;
    private const int FirstBatch = 4;
    private const int MaxApps = 3;

    private readonly ISearchEngine _engine;
    private readonly UserDataRepository _user;
    private readonly FileActions _actions;
    private readonly IconProvider _icons;
    private readonly AppCatalog _catalog;
    private CancellationTokenSource _cts = new();

    public int MaxResults { get; set; } = 9;
    public ObservableCollection<object> Results { get; } = [];

    [ObservableProperty] private string _text = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenLocationCommand), nameof(CopyPathCommand), nameof(CopyFileCommand), nameof(ShowPropertiesCommand), nameof(ToggleFavoriteCommand))]
    private object? _selected;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _showFooter;

    /// <summary>The argument says whether the application that was in front must get the focus back.</summary>
    public event Action<bool>? HideRequested;
    public event Action<string>? ShowAllRequested;

    public SearchWidgetViewModel(ISearchEngine engine, UserDataRepository user, FileActions actions, IconProvider icons, AppCatalog catalog)
    {
        _engine = engine; _user = user; _actions = actions; _icons = icons; _catalog = catalog;
    }

    partial void OnTextChanged(string value) => _ = RunAsync(value);

    private async Task RunAsync(string text)
    {
        _cts.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) { Empty(""); return; }
            await Task.Delay(DebounceMs, ct);
            var query = QueryParser.Parse(text);
            if (query.Error is not null) { Empty(query.Error); return; }
            if (query.IsEmpty) { Empty(""); return; }

            // A filter (ext:, size:...) is a question about files.
            var apps = query.IsNameOnly ? AppMatcher.Find(_catalog.Apps, query.Terms, Math.Min(MaxApps, MaxResults)) : [];
            int files = MaxResults - apps.Count;
            var page = files > 0 ? await _engine.SearchAsync(query, 0, files, ct) : SearchPage.Empty;
            ct.ThrowIfCancellationRequested();
            await ShowAsync(apps, page, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            Empty("Erreur de recherche : " + ex.Message);
        }
    }

    private async Task ShowAsync(IReadOnlyList<LaunchableApp> apps, SearchPage page, CancellationToken ct)
    {
        // The previous results stay on screen until the new ones are known, so the window does not flicker.
        Results.Clear();
        var rows = new List<Func<object>>(apps.Count + page.Items.Count);
        foreach (var app in apps) rows.Add(() => AppItem(app));
        foreach (var file in page.Items) rows.Add(() => Item(file));

        foreach (var row in rows.Take(FirstBatch)) Results.Add(row());
        Selected = Results.FirstOrDefault();
        HasResults = Results.Count > 0;
        ShowFooter = true;
        Status = rows.Count == 0 ? "Aucun résultat." : Summary(apps.Count, page);

        if (rows.Count > FirstBatch)
        {
            await Dispatcher.Yield(DispatcherPriority.Background); // the first rows are painted before the others are built
            ct.ThrowIfCancellationRequested();
            foreach (var row in rows.Skip(FirstBatch)) Results.Add(row());
        }
    }

    private ResultItem Item(FileEntry entry) => new(entry, _icons.Get(entry.Extension, entry.IsDirectory));

    private AppRow AppItem(LaunchableApp app)
    {
        var row = new AppRow(app, _icons.Get("exe", false));
        _ = LoadIconAsync(row);
        return row;
    }

    private async Task LoadIconAsync(AppRow row)
    {
        if (await _catalog.GetIconAsync(row.App) is { } icon) row.Icon = icon;
    }

    private static string Summary(int apps, SearchPage page)
    {
        string files = page.HasMore ? $"{page.Items.Count}+ fichiers" : $"{page.Items.Count} fichier(s)";
        if (page.UsedFuzzy) files += " approchants";
        return apps == 0 ? files : $"{apps} application(s), {files}";
    }

    private void Empty(string message)
    {
        Results.Clear();
        Selected = null;
        HasResults = false;
        Status = message;
        ShowFooter = message.Length > 0;
    }

    public void Clear() => Text = "";

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        int index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[(index + delta + Results.Count) % Results.Count];
    }

    private bool IsFileSelected() => Selected is ResultItem;

    private void Guarded(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Status = ex.Message;
            ShowFooter = true;
        }
    }

    private void Do(Action<ResultItem> action)
    {
        if (Selected is ResultItem item) Guarded(() => action(item));
    }

    private void Remember()
    {
        string text = Text;
        _ = Task.Run(() => _user.AddHistory(text));
    }

    [RelayCommand]
    private void Open() => Guarded(() =>
    {
        switch (Selected)
        {
            case AppRow app: AppCatalog.Launch(app.App); break;
            case ResultItem file: _actions.Open(file.Entry.FullPath); break;
            default: return;
        }
        Remember();
        HideRequested?.Invoke(false);
    });

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private void OpenLocation() => Do(i =>
    {
        _actions.OpenLocation(i.Entry.FullPath);
        Remember();
        HideRequested?.Invoke(false);
    });

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private void CopyPath() => Do(i => { _actions.CopyPath(i.Entry.FullPath); Status = "Chemin copié."; });

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private void CopyFile() => Do(i => { _actions.CopyFile(i.Entry.FullPath); Status = "Fichier copié dans le presse-papiers."; });

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private void ShowProperties() => Do(i =>
    {
        _actions.ShowProperties(i.Entry.FullPath);
        HideRequested?.Invoke(false);
    });

    [RelayCommand(CanExecute = nameof(IsFileSelected))]
    private void ToggleFavorite() => Do(i => Status = _user.ToggleFavorite(i.Entry.FullPath) ? "Ajouté aux favoris." : "Retiré des favoris.");

    [RelayCommand] private void HideWidget() => HideRequested?.Invoke(true);

    [RelayCommand]
    private void ShowAll()
    {
        if (!string.IsNullOrWhiteSpace(Text)) ShowAllRequested?.Invoke(Text);
    }
}
