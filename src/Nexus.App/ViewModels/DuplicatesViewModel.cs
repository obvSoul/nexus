using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus.Core.Formatting;
using Nexus.Database;
using Nexus.Duplicates;

namespace Nexus.App.ViewModels;

public sealed partial class DuplicateFileRow(DuplicateFile file, Action changed) : ObservableObject
{
    public DuplicateFile File { get; } = file;
    public string Name => File.Name;
    public string Directory => File.Directory;
    public string FullPath => File.FullPath;
    public string ModifiedText => File.ModifiedTicks > 0 ? new DateTime(File.ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("g") : "";

    /// <summary>Ticked by the user only: nothing in the application ever ticks this box.</summary>
    [ObservableProperty] private bool _isMarked;

    partial void OnIsMarkedChanged(bool value) => changed();
}

public sealed partial class DuplicateGroupRow : ObservableObject
{
    public DuplicateGroupRow(DuplicateGroup group, Action changed)
    {
        Size = group.Size;
        Files = new ObservableCollection<DuplicateFileRow>(group.Files.Select(f => new DuplicateFileRow(f, changed)));
        Name = group.Files[0].Name;
        SizeText = ByteSize.Format(group.Size);
        Refresh();
    }

    public long Size { get; }
    public string Name { get; }
    public string SizeText { get; }
    public ObservableCollection<DuplicateFileRow> Files { get; }
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _wastedText = "";

    public void Refresh()
    {
        CountText = $"{Files.Count} copies";
        WastedText = ByteSize.Format(Size * (Files.Count - 1));
    }
}

public sealed partial class DuplicatesViewModel : PageViewModel
{
    private static readonly long[] MinSizes = [100 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024];

    private readonly DuplicateFinder _finder;
    private readonly FileActions _actions;
    private readonly FileRepository _files;
    private CancellationTokenSource? _cts;

    public override string Title => "Duplicates";
    public override string Glyph => "";

    public IReadOnlyList<string> MinSizeLabels { get; } = ["≥ 100 Ko", "≥ 1 Mo", "≥ 10 Mo", "≥ 100 Mo"];
    public ObservableCollection<DuplicateGroupRow> Groups { get; } = [];

    [ObservableProperty] private int _minSizeIndex = 1;
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private bool _sameExtension = true;
    [ObservableProperty] private bool _includeApps;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Lancez une recherche. Seuls les fichiers de même taille sont lus, et rien n'est jamais supprimé sans votre choix.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _selectionText = "Aucun fichier sélectionné.";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private DuplicateGroupRow? _selectedGroup;

    public string ScopeLabel => string.IsNullOrEmpty(Scope) ? "Tous les dossiers indexés" : Scope;
    partial void OnScopeChanged(string value) => OnPropertyChanged(nameof(ScopeLabel));

    public DuplicatesViewModel(DuplicateFinder finder, FileActions actions, FileRepository files)
    {
        _finder = finder;
        _actions = actions;
        _files = files;
    }

    [RelayCommand]
    private void ChooseScope()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à analyser" };
        if (dlg.ShowDialog() == true) Scope = Path.GetFullPath(dlg.FolderName).TrimEnd('\\');
    }

    [RelayCommand] private void ClearScope() => Scope = "";

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        Progress = 0;
        Groups.Clear();
        SelectedGroup = null;
        UpdateSelection();
        Summary = "";
        _cts = new CancellationTokenSource();
        var options = new DuplicateOptions
        {
            MinSize = MinSizes[Math.Clamp(MinSizeIndex, 0, MinSizes.Length - 1)],
            Scope = string.IsNullOrEmpty(Scope) ? null : Scope,
            SameExtensionOnly = SameExtension,
            IncludeApplicationFolders = IncludeApps,
        };
        var progress = new Progress<DuplicateProgress>(p =>
        {
            Status = p.Total > 0 ? $"{p.Stage} {100.0 * p.Done / p.Total:0} %" : p.Stage;
            Progress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });
        try
        {
            var r = await _finder.FindAsync(options, progress, _cts.Token);
            foreach (var g in r.Groups) Groups.Add(new DuplicateGroupRow(g, UpdateSelection));
            long wasted = r.Groups.Sum(g => g.Wasted);
            Status = r.Groups.Count == 0 ? "Aucun doublon trouvé." : "Cochez les copies à supprimer dans la liste de droite.";
            Summary = $"{r.Groups.Count:N0} groupes · {ByteSize.Format(wasted)} récupérables en gardant une copie de chaque · " +
                      $"{r.Candidates:N0} fichiers de même taille, {r.FullHashed:N0} vérifiés en SHA-256, {ByteSize.Format(r.BytesRead)} lus, " +
                      $"{r.CacheHits:N0} hash réutilisés, {r.Skipped:N0} ignorés (modifiés, verrouillés ou hors ligne), {r.Elapsed.TotalSeconds:F1} s";
            Progress = 100;
        }
        catch (OperationCanceledException) { Status = "Recherche annulée."; Progress = 0; }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { Status = "Erreur : " + ex.Message; }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { _actions.OpenLocation(path); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Status = ex.Message; }
    }

    private IEnumerable<DuplicateFileRow> Marked => Groups.SelectMany(g => g.Files).Where(f => f.IsMarked);

    private void UpdateSelection()
    {
        var marked = Marked.ToList();
        HasSelection = marked.Count > 0;
        SelectionText = marked.Count == 0 ? "Aucun fichier sélectionné." : $"{marked.Count:N0} fichier(s) sélectionné(s) · {ByteSize.Format(marked.Sum(f => f.File.Size))}";
    }

    [RelayCommand]
    private async Task DeleteMarkedAsync()
    {
        var targets = Groups.Where(g => g.Files.Any(f => f.IsMarked)).ToList();
        if (targets.Count == 0) return;

        var allMarked = targets.FirstOrDefault(g => g.Files.All(f => f.IsMarked));
        if (allMarked is not null)
        {
            SelectedGroup = allMarked;
            Status = $"Rien n'a été supprimé : toutes les copies de « {allMarked.Name} » sont cochées. Gardez-en au moins une.";
            return;
        }

        long count = targets.Sum(g => g.Files.Count(f => f.IsMarked));
        long bytes = targets.Sum(g => g.Files.Where(f => f.IsMarked).Sum(f => f.File.Size));
        if (!_actions.Confirm($"Envoyer {count:N0} fichier(s) ({ByteSize.Format(bytes)}) à la Corbeille ?\n\nAu moins une copie de chaque groupe est conservée. " +
                              "L'espace n'est libéré qu'une fois la Corbeille vidée.", "Supprimer les doublons")) return;

        IsRunning = true;
        Status = "Envoi à la Corbeille…";
        int done = 0, failed = 0;
        foreach (var g in targets)
        {
            var marked = g.Files.Where(f => f.IsMarked).ToList();
            var keepers = g.Files.Where(f => !f.IsMarked).ToList();

            // The results may be old: never remove a copy if the one that should remain has since changed or vanished.
            bool safe = await Task.Run(() => keepers.Any(k => Unchanged(k.File)));
            if (!safe) { failed += marked.Count; continue; }

            foreach (var f in marked)
            {
                try
                {
                    if (!await Task.Run(() => Unchanged(f.File))) { failed++; continue; }
                    await Task.Run(() =>
                    {
                        _actions.SendFileToRecycleBin(f.FullPath);
                        _files.DeleteByPath(f.Directory, f.Name);
                    });
                    g.Files.Remove(f);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { failed++; }
            }
            if (g.Files.Count < 2) Groups.Remove(g); else g.Refresh();
        }
        IsRunning = false;
        UpdateSelection();
        Status = failed == 0 ? $"{done:N0} fichier(s) envoyé(s) à la Corbeille."
                             : $"{done:N0} fichier(s) envoyé(s) à la Corbeille, {failed:N0} laissé(s) en place (modifiés depuis l'analyse, disparus ou verrouillés).";
    }

    private static bool Unchanged(DuplicateFile f)
    {
        try
        {
            var i = new FileInfo(f.FullPath);
            return i.Exists && i.Length == f.Size && i.LastWriteTimeUtc.Ticks == f.ModifiedTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
