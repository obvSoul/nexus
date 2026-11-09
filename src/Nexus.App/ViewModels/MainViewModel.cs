using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Core.Indexing;
using Nexus.Database;
using Nexus.Indexer;

namespace Nexus.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SearchPageViewModel _search;
    private readonly UserDataRepository _user;
    private bool _searchFocused;

    public SearchState State { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private bool _suggestionsOpen;

    [ObservableProperty] private string _indexSummary = "";

    public Widget.WidgetService Widget { get; }

    public MainViewModel(SearchState state, DashboardViewModel dashboard, SearchPageViewModel search,
        IndexationViewModel indexation, StorageViewModel storage, AppsViewModel apps, DuplicatesViewModel duplicates, SettingsViewModel settings, UserDataRepository user, IndexerService indexer, Widget.WidgetService widget)
    {
        State = state;
        Widget = widget;
        _search = search;
        _user = user;
        UpdateSummary(indexer.Status);
        indexer.StatusChanged += s => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateSummary(s));
        Pages =
        [
            dashboard,
            search,
            storage,
            apps,
            duplicates,
            indexation,
            settings,
        ];
        _currentPage = Pages[0];

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SearchState.Text)) return;
            if (!string.IsNullOrWhiteSpace(state.Text) && CurrentPage != _search) CurrentPage = _search;
            RefreshSuggestions();
        };
    }

    private void UpdateSummary(IndexerStatus s)
    {
        string state = s.State switch
        {
            IndexerState.Scanning => "indexation…",
            IndexerState.Paused => "en pause",
            IndexerState.Idle => s.IndexedFiles == 0 && !s.Watching ? "aucun dossier indexé" : "à jour",
            _ => "arrêté",
        };
        IndexSummary = $"{s.IndexedFiles:N0} fichiers · {state}";
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        if (value is not null) _ = value.OnNavigatedToAsync();
    }

    public Task InitializeAsync() => CurrentPage.OnNavigatedToAsync();

    public void SetSearchFocus(bool focused)
    {
        _searchFocused = focused;
        RefreshSuggestions();
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (_searchFocused)
        {
            foreach (var s in _user.GetHistory(State.Text, 8))
                if (!s.Equals(State.Text.Trim(), StringComparison.OrdinalIgnoreCase)) Suggestions.Add(s);
        }
        SuggestionsOpen = _searchFocused && Suggestions.Count > 0;
    }

    public void PickSuggestion(string text)
    {
        State.Text = text;
        SubmitSearch();
    }

    [RelayCommand] private void ClearSearch() => State.Text = "";

    [RelayCommand] private void OpenWidget() => Widget.Show();

    /// <summary>"Voir tous les résultats" of the widget.</summary>
    public void OpenSearch(string text)
    {
        State.Text = text;
        CurrentPage = _search;
    }

    [RelayCommand]
    private void SubmitSearch()
    {
        if (string.IsNullOrWhiteSpace(State.Text)) return;
        string text = State.Text;
        _ = Task.Run(() => _user.AddHistory(text)); // never wait for a database write on the UI thread
        CurrentPage = _search;
        SuggestionsOpen = false;
    }
}
