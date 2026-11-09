using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Nexus.App.Services;
using Nexus.Core;

namespace Nexus.App.Widget;

public partial class SearchWidgetWindow : Window
{
    private readonly SearchWidgetViewModel _vm;
    private readonly WidgetService _service;

    internal SearchWidgetWindow(SearchWidgetViewModel vm, WidgetService service)
    {
        _vm = vm; _service = service;
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyChrome();
        Deactivated += (_, _) =>
        {
            if (ResultsList.ContextMenu?.IsOpen != true) _service.Hide(restoreFocus: false);
        };
        PreviewKeyDown += OnKey;
        vm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Rounded corners, border and shadow come from the desktop compositor: WPF keeps its hardware rendering.</summary>
    private void ApplyChrome()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long style = WidgetNative.GetWindowLongPtr(hwnd, WidgetNative.GWL_EXSTYLE).ToInt64();
        WidgetNative.SetWindowLongPtr(hwnd, WidgetNative.GWL_EXSTYLE, new IntPtr(style | WidgetNative.WS_EX_TOOLWINDOW)); // not in Alt+Tab

        int dark = 1, corners = WidgetNative.DWMWCP_ROUND, border = 0x002B2626 /* COLORREF of the theme's #26262B */;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, WidgetNative.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var margins = new WidgetNative.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        WidgetNative.DwmExtendFrameIntoClientArea(hwnd, ref margins); // gives a borderless window its shadow
    }

    internal void Present(WidgetPlace place)
    {
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        ApplySize(place.WidthDip);
        MaxHeight = Math.Max(160, (place.Screen.Bottom - place.Y) / place.Screen.Scale - 12);
        WidgetNative.SetWindowPos(hwnd, WidgetNative.HWND_TOPMOST, place.X, place.Y, 0, 0, WidgetNative.SWP_NOSIZE | WidgetNative.SWP_NOACTIVATE);
        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        FocusBox();
        SearchBox.SelectAll();
    }

    /// <summary>Everything grows together: text, rows and margins keep their proportions at every size.</summary>
    private void ApplySize(double width)
    {
        if (Math.Abs(width - Width) < 0.5) return;
        Width = width;
        double scale = width / WidgetSizes.BaseWidth;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        Root.LayoutTransform = transform;
    }

    private void FocusBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchWidgetViewModel.Selected) && _vm.Selected is { } item) ResultsList.ScrollIntoView(item);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape: _service.Hide(restoreFocus: true); break;
            case Key.Down: _vm.MoveSelection(1); break;
            case Key.Up: _vm.MoveSelection(-1); break;
            case Key.Enter when ctrl: _vm.OpenLocationCommand.Execute(null); break;
            case Key.Enter: _vm.OpenCommand.Execute(null); break;
            case Key.Tab: break; // the focus stays in the search box
            default: return;
        }
        e.Handled = true;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_service.Settings.Position != WidgetPosition.Custom) return;
        DragMove();
        if (WidgetNative.GetWindowRect(new WindowInteropHelper(this).Handle, out var r)) _service.CustomPositionMoved(r.Left, r.Top);
    }

    private static object? ItemAt(MouseButtonEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext is { } item and (ViewModels.ResultItem or AppRow) ? item : null;

    private void Results_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is { } item) _vm.Selected = item;
        else e.Handled = true; // no menu on the empty area or the scroll bar
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is not null) _vm.OpenCommand.Execute(null);
    }

    private void Results_LeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is { } item) _vm.Selected = item;
        FocusBox();
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        if (IsVisible) FocusBox();
    }
}
