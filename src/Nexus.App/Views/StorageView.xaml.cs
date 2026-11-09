using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class StorageView : UserControl
{
    public StorageView() => InitializeComponent();

    private StorageViewModel? Vm => DataContext as StorageViewModel;

    private void Disk_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return; // the "Indexer" button handles itself
        if ((sender as FrameworkElement)?.DataContext is DiskCard card) Vm?.SelectDiskCommand.Execute(card);
    }

    private void IndexDisk_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DiskCard card) Vm?.IndexDiskCommand.Execute(card);
    }

    private void Map_FolderActivated(TreemapNode node) => Vm?.NavigateTo(node.Path);

    private void MapMenu_Opened(object sender, RoutedEventArgs e) =>
        MapOpenLocation.IsEnabled = Map.HoveredNode is { IsOther: false };

    private void MapOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Map.HoveredNode is { IsOther: false } n) Vm?.OpenLocationCommand.Execute(n.Path);
    }

    private void EntryRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StorageRow row) Vm?.OpenEntryCommand.Execute(row);
    }

    private void ListRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StorageRow row) Vm?.OpenLocationCommand.Execute(row.Path);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) Vm?.OpenLocationCommand.Execute(path);
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
