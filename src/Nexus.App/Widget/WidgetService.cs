using System.IO;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Services;
using Nexus.Core;
using Nexus.Core.Indexing;
using Nexus.Database;

namespace Nexus.App.Widget;

/// <summary>
/// Owns everything that lives outside the main window: the shortcut, the notification icon and the floating window, which
/// is only created the first time it is opened. Hiding it stops nothing else.
/// </summary>
public sealed partial class WidgetService : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly SearchWidgetViewModel _vm;
    private readonly INexusLog _log;
    private readonly AppCatalog _apps;
    private HwndSource? _host;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;
    private SearchWidgetWindow? _window;
    private IntPtr _previousForeground;

    public WidgetSettings Settings { get; private set; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _hotkeyError = "";

    public event Action? ShowMainRequested, QuitRequested, Hidden;
    public event Action<string>? ShowAllRequested;

    public WidgetService(SettingsStore store, SearchWidgetViewModel vm, INexusLog log, AppCatalog apps)
    {
        _store = store; _vm = vm; _log = log; _apps = apps;
        vm.HideRequested += restoreFocus => Hide(restoreFocus);
        vm.ShowAllRequested += text =>
        {
            Hide(restoreFocus: false);
            ShowAllRequested?.Invoke(text);
        };
    }

    public bool IsVisible => _window?.IsVisible == true;

    public void Start()
    {
        Settings = _store.Load().Widget;
        Enabled = Settings.Enabled;
        if (Enabled) Attach();
    }

    private void Attach()
    {
        if (_host is not null) return;
        _host = new HwndSource(new HwndSourceParameters("NexusBackground") { Width = 0, Height = 0, WindowStyle = 0 });
        _host.AddHook(HostHook);
        _hotkey = new GlobalHotkey(_host);
        _hotkey.Pressed += Toggle;
        _tray = new TrayIcon(_host);
        _tray.OpenRequested += () => ShowMainRequested?.Invoke();
        _tray.SearchRequested += Show;
        _tray.QuitRequested += () => QuitRequested?.Invoke();
        RegisterCurrentHotkey();
        _apps.RefreshIfStale();
    }

    /// <summary>With no visible window, this is the one an installer or a session end asks to close.</summary>
    private IntPtr HostHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLOSE = 0x10, WM_ENDSESSION = 0x16;
        if (msg == WM_CLOSE || (msg == WM_ENDSESSION && wParam != IntPtr.Zero))
        {
            handled = true;
            QuitRequested?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void Detach()
    {
        Hide(restoreFocus: false);
        _hotkey?.Dispose();
        _tray?.Dispose();
        _host?.RemoveHook(HostHook);
        _host?.Dispose();
        _hotkey = null; _tray = null; _host = null;
        HotkeyError = "";
    }

    private void RegisterCurrentHotkey()
    {
        if (_hotkey is null) return;
        string? error = Hotkey.TryParse(Settings.Hotkey, out var hotkey) ? _hotkey.Register(hotkey) : $"Raccourci invalide : {Settings.Hotkey}.";
        if (error is not null) _log.Warn("Widget shortcut: " + error);
        HotkeyError = error ?? "";
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray is null) return;
        _tray.SearchLabel = $"Recherche rapide\t{Settings.Hotkey}";
        _tray.SetTooltip(HotkeyError.Length == 0 ? $"NEXUS · recherche rapide : {Settings.Hotkey}" : "NEXUS · raccourci indisponible (voir Paramètres)");
    }

    // ------------------------------------------------------------ showing and hiding

    public void Toggle()
    {
        if (IsVisible) Hide(restoreFocus: true);
        else Show();
    }

    public void Show()
    {
        if (!Enabled) return;
        _window ??= new SearchWidgetWindow(_vm, this);
        if (_window.IsVisible) { _window.Activate(); return; }
        _previousForeground = WidgetNative.GetForegroundWindow();
        _vm.MaxResults = Settings.MaxResults;
        _apps.RefreshIfStale(); // an application installed since last time; the list on hand is used meanwhile
        WidgetNative.GetCursorPos(out var cursor);
        var place = WidgetPlacement.Compute(ScreenList.Get(), Settings, cursor.X, cursor.Y, WidgetSizes.Width(Settings.Size));
        _window.Present(place);
    }

    public void Hide(bool restoreFocus)
    {
        if (_window is null || !_window.IsVisible) return;
        _window.Hide();
        if (!Settings.KeepLastSearch) _vm.Clear();
        if (restoreFocus && WidgetNative.IsWindow(_previousForeground)) Native.SetForegroundWindow(_previousForeground);
        Hidden?.Invoke();
    }

    /// <summary>Tells the user once that closing the window does not stop NEXUS.</summary>
    public void NoticeRunningInBackground()
    {
        if (Settings.BackgroundNoticeShown) return;
        Update(s => s with { BackgroundNoticeShown = true });
        _tray?.Notify("NEXUS reste disponible", $"La recherche rapide fonctionne toujours ({Settings.Hotkey}). Pour quitter : clic droit sur cette icône.");
    }

    internal void CustomPositionMoved(int x, int y) => Update(s => s with { CustomX = x, CustomY = y });

    // ------------------------------------------------------------ settings

    public void Update(Func<WidgetSettings, WidgetSettings> change)
    {
        Settings = change(Settings);
        _store.Save(_store.Load() with { Widget = Settings });
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return;
        Enabled = enabled;
        Update(s => s with { Enabled = enabled });
        if (enabled) Attach();
        else
        {
            Detach();
            SetStartWithWindows(false); // no window, no icon: NEXUS would start invisibly
        }
    }

    /// <summary>Applies and saves a new shortcut. Returns the reason on failure; the previous one stays active.</summary>
    public string? TrySetHotkey(string text)
    {
        if (!Hotkey.TryParse(text, out var hotkey)) return $"Raccourci invalide : {text}. Il faut au moins une touche Ctrl, Alt, Shift ou Win.";
        if (_hotkey is null)
        {
            Update(s => s with { Hotkey = hotkey.ToString() });
            return null;
        }
        string? error = _hotkey.Register(hotkey);
        if (error is not null)
        {
            RegisterCurrentHotkey();
            return error;
        }
        Update(s => s with { Hotkey = hotkey.ToString() });
        HotkeyError = "";
        RefreshTray();
        return null;
    }

    /// <summary>While a new shortcut is being typed, the current one must not fire.</summary>
    public void SuspendHotkey() => _hotkey?.Unregister();

    public void ResumeHotkey()
    {
        if (_hotkey is { IsRegistered: false }) RegisterCurrentHotkey();
    }

    public bool StartWithWindows => Autostart.IsEnabled;

    /// <summary>Returns the reason on failure.</summary>
    public string? SetStartWithWindows(bool enabled)
    {
        try { Autostart.Set(enabled); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warn("Autostart: " + ex.Message);
            return "Impossible de modifier le démarrage avec Windows : " + ex.Message;
        }
    }

    public void Dispose() => Detach();
}
