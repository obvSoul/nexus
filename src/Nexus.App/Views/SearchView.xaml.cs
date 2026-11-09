using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexus.App.ViewModels;
using Nexus.Core.Search;

namespace Nexus.App.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        // Progressive loading: append the next page when the scroll reaches the end.
        Grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    private SearchPageViewModel? Vm => DataContext as SearchPageViewModel;

    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || Vm is null) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 20) await Vm.LoadMoreAsync();
    }

    private async void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // sorting is done by the database, not on the loaded rows
        if (Vm is null || !Enum.TryParse<SortField>(e.Column.SortMemberPath, out var field)) return;
        bool desc = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending;
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = desc ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
        await Vm.SortByAsync(field, desc);
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e) => Vm?.OpenCommand.Execute(null);

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.OpenCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete) { Vm?.DeleteCommand.Execute(null); e.Handled = true; }
    }
}
