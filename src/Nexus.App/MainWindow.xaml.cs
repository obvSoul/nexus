using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.ViewModels;

namespace Nexus.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public IRelayCommand FocusSearchCommand { get; }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        FocusSearchCommand = new RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); });
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += async (_, _) => await vm.InitializeAsync();
    }

    /// <summary>Dark title bar matching the theme (the caption colours need Windows 11; older systems simply ignore them).</summary>
    private void DarkTitleBar()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1, background = 0x00090707 /* COLORREF of #070709 */, text = 0x00F5F3F2;
        Services.Native.DwmSetWindowAttribute(hwnd, Services.Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        Services.Native.DwmSetWindowAttribute(hwnd, Services.Native.DWMWA_BORDER_COLOR, ref background, sizeof(int));
        Services.Native.DwmSetWindowAttribute(hwnd, Services.Native.DWMWA_CAPTION_COLOR, ref background, sizeof(int));
        Services.Native.DwmSetWindowAttribute(hwnd, Services.Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm.SetSearchFocus(true);
    private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm.SetSearchFocus(false);

    // PreviewMouseDown fires before the text box loses focus, so the click is not swallowed by the popup closing.
    private void Suggestions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is string text)
        {
            _vm.PickSuggestion(text);
            e.Handled = true;
        }
    }
}
