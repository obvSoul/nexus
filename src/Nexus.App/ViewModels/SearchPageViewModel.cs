using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.Core;
using Nexus.Core.Formatting;
using Nexus.Core.Models;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Search;

namespace Nexus.App.ViewModels;

public sealed class ResultItem(FileEntry entry, ImageSource? icon)
{
    public FileEntry Entry { get; } = entry;
    public ImageSource? Icon { get; } = icon;
    public string Name => Entry.Name;
    public string Extension => Entry.Extension;
    public string TypeText { get; } = FileTypes.Label(entry.Extension, entry.IsDirectory);
    public string SizeText => Entry.IsDirectory ? "" : ByteSize.Format(Entry.Size);
    public string Directory => Entry.Directory;
    public string ModifiedText => Entry.ModifiedLocal?.ToString("g") ?? "";
    public string ToolTipText => Entry.FullPath;
}

public sealed partial class FilterChip(string label, string token) : ObservableObject
{
    public string Label { get; } = label;
    public string Token { get; } = token;
    [ObservableProperty] private bool _isActive;
}

public sealed partial class SearchPageViewModel : PageViewModel
{
    private const int PageSize = 200;
    private const int MaxLoaded = 20_000; // the list never holds millions of rows, however far the user scrolls

    private readonly ISearchEngine _engine;
    private readonly UserDataRepository _user;
    private readonly FileRepository _files;
    private readonly FileActions _actions;
    private readonly IconProvider _icons;
    private readonly SearchState _state;

    private CancellationTokenSource _cts = new();
    private SearchQuery? _query;
    private bool _hasMore, _loadingMore, _fuzzy;
    private SortField _sort;
    private bool _descending;

    public override string Title => "Search";
    public override string Glyph => "";

    public ObservableCollection<ResultItem> Results { get; } = [];
    public IReadOnlyList<FilterChip> Chips { get; } =
    [
        new("Images", "type:image"), new("Vidéos", "type:video"), new("Audio", "type:audio"),
        new("Documents", "type:document"), new("Archives", "type:archive"), new("> 1 Go", "size:>1gb"),
        new("Aujourd'hui", "dm:today"), new("Cette semaine", "dm:week"), new("Dossiers", "is:folder"), new("Favoris", "is:favorite"),
    ];

    [ObservableProperty] private string _status = "Tapez dans la barre de recherche (Ctrl+K). Syntaxe : ext:png size:>1gb dm:week path:dossier -exclu /regex/";
    [ObservableProperty] private ResultItem? _selected;

    public SearchPageViewModel(ISearchEngine engine, UserDataRepository user, FileRepository files,
        FileActions actions, IconProvider icons, SearchState state)
    {
        _engine = engine; _user = user; _files = files; _actions = actions; _icons = icons; _state = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SearchState.Text)) return;
            UpdateChips();
            _ = RunDebouncedAsync();
        };
    }

    private async Task RunDebouncedAsync()
    {
        _cts.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token); // debounce
            await RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Erreur de recherche : " + ex.Message; }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var q = QueryParser.Parse(_state.Text);
        if (q.Sort == SortField.None) { q.Sort = _sort; q.Descending = _descending; }
        _query = q;

        if (q.Error is not null) { Results.Clear(); Status = q.Error; return; }
        if (q.IsEmpty) { Results.Clear(); Status = "Tapez dans la barre de recherche (Ctrl+K)."; return; }

        var page = await _engine.SearchAsync(q, 0, PageSize, ct);
        ct.ThrowIfCancellationRequested();

        Results.Clear();
        AddItems(page);
        _hasMore = page.HasMore;
        _fuzzy = page.UsedFuzzy;

        string note = page.UsedFuzzy ? " · aucun résultat exact, résultats approchants" : "";
        if (Results.Count == 0) { Status = "Aucun résultat (l'index est peut-être vide ou incomplet)."; return; }
        Status = page.HasMore ? $"{Results.Count}+ résultats · {page.ElapsedMs:F0} ms{note}" : $"{Results.Count} résultat(s) · {page.ElapsedMs:F0} ms{note}";

        if (page.HasMore && !page.UsedFuzzy)
        {
            // Exact total in the background; the first page is already displayed.
            try
            {
                long total = await _engine.CountAsync(q, ct);
                ct.ThrowIfCancellationRequested();
                Status = $"{total:N0} résultats · premiers en {page.ElapsedMs:F0} ms";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* keep the provisional status */ }
        }
    }

    private void AddItems(SearchPage page)
    {
        foreach (var e in page.Items) Results.Add(new ResultItem(e, _icons.Get(e.Extension, e.IsDirectory)));
    }

    /// <summary>Called when the grid is scrolled near its end: appends the next page.</summary>
    public async Task LoadMoreAsync()
    {
        if (!_hasMore || _loadingMore || _query is null || _fuzzy) return;
        if (Results.Count >= MaxLoaded)
        {
            Status = $"Affichage limité aux {MaxLoaded:N0} premiers résultats : précisez la recherche ou triez autrement.";
            return;
        }
        _loadingMore = true;
        var ct = _cts.Token;
        try
        {
            var page = await _engine.SearchAsync(_query, Results.Count, PageSize, ct);
            ct.ThrowIfCancellationRequested();
            AddItems(page);
            _hasMore = page.HasMore;
        }
        catch (OperationCanceledException) { }
        finally { _loadingMore = false; }
    }

    public async Task SortByAsync(SortField field, bool descending)
    {
        _sort = field; _descending = descending;
        _cts.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try { await RunAsync(cts.Token); } catch (OperationCanceledException) { }
    }

    // ---- quick filters ----

    private void UpdateChips()
    {
        var tokens = Tokens(_state.Text);
        foreach (var c in Chips) c.IsActive = tokens.Contains(c.Token, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    [RelayCommand]
    private void ToggleFilter(FilterChip chip)
    {
        var tokens = Tokens(_state.Text);
        int removed = tokens.RemoveAll(t => t.Equals(chip.Token, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) tokens.Add(chip.Token);
        _state.Text = string.Join(' ', tokens);
    }

    // ---- actions on the selected result ----

    private void Do(Action<ResultItem> action)
    {
        if (Selected is not { } item) return;
        try { action(item); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void Open() => Do(i =>
    {
        _actions.Open(i.Entry.FullPath);
        string text = _state.Text;
        _ = Task.Run(() => _user.AddHistory(text));
    });

    [RelayCommand] private void OpenLocation() => Do(i => _actions.OpenLocation(i.Entry.FullPath));
    [RelayCommand] private void CopyPath() => Do(i => { _actions.CopyPath(i.Entry.FullPath); Status = "Chemin copié."; });
    [RelayCommand] private void CopyFile() => Do(i => { _actions.CopyFile(i.Entry.FullPath); Status = "Fichier copié dans le presse-papiers."; });
    [RelayCommand] private void ShowProperties() => Do(i => _actions.ShowProperties(i.Entry.FullPath));

    [RelayCommand]
    private void Delete() => Do(i =>
    {
        if (!_actions.ConfirmAndDelete(i.Entry.FullPath, i.Entry.IsDirectory)) return;
        _files.DeleteByPath(i.Entry.Directory, i.Entry.Name);
        Results.Remove(i);
        Status = "Envoyé à la Corbeille.";
    });

    [RelayCommand]
    private void ToggleFavorite() => Do(i =>
    {
        bool now = _user.ToggleFavorite(i.Entry.FullPath);
        Status = now ? "Ajouté aux favoris (filtre « Favoris »)." : "Retiré des favoris.";
    });
}
