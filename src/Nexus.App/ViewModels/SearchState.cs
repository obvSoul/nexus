using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

/// <summary>The text of the global search bar, shared by the bar and the Search page.</summary>
public sealed partial class SearchState : ObservableObject
{
    [ObservableProperty] private string _text = "";
}
