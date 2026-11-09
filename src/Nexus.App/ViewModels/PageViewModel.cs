using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

/// <summary>Base class of every navigable page.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public abstract string Glyph { get; }
    /// <summary>Called each time the page becomes visible.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}

/// <summary>Page whose feature is not built yet; states so explicitly (no fake data).</summary>
public sealed class PendingPageViewModel(string title, string glyph, int phase, string description) : PageViewModel
{
    public override string Title { get; } = title;
    public override string Glyph { get; } = glyph;
    public string Description { get; } = description;
    public string Status { get; } = $"Fonctionnalité non encore implémentée (Phase {phase}).";
}
