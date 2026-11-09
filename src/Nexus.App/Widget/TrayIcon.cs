using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Nexus.App.Widget;

/// <summary>Notification-area icon. Native calls only: no Windows Forms to load for one icon.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 77, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_NULL = 0;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80;
    private const uint CmdOpen = 1, CmdSearch = 2, CmdQuit = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated;
    private string _tip = "NEXUS";

    public string SearchLabel { get; set; } = "Recherche rapide";
    public event Action? OpenRequested, SearchRequested, QuitRequested;

    public TrayIcon(HwndSource source)
    {
        _source = source;
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe) ExtractIconExW(exe, 0, null, small, 1);
        _icon = small[0];
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _source.AddHook(Hook);
        Send(NIM_ADD);
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Send(uint message)
    {
        var d = Data();
        Shell_NotifyIconW(message, ref d);
    }

    public void SetTooltip(string text)
    {
        _tip = text.Length < 128 ? text : text[..127];
        Send(NIM_MODIFY);
    }

    public void Notify(string title, string text)
    {
        var d = Data();
        d.uFlags |= NIF_INFO;
        d.szInfoTitle = title;
        d.szInfo = text;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated) Send(NIM_ADD); // Explorer has restarted
        else if (msg == WM_APP_TRAY)
        {
            int mouse = (int)lParam & 0xFFFF;
            if (mouse == WM_LBUTTONUP) OpenRequested?.Invoke();
            else if (mouse == WM_RBUTTONUP) ShowMenu();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdOpen, "Ouvrir NEXUS");
        AppendMenuW(menu, MF_STRING, CmdSearch, SearchLabel);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdQuit, "Quitter NEXUS");
        WidgetNative.GetCursorPos(out var p);
        Services.Native.SetForegroundWindow(_source.Handle); // otherwise the menu does not close when clicking elsewhere
        uint command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, p.X, p.Y, 0, _source.Handle, IntPtr.Zero);
        PostMessageW(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        switch (command)
        {
            case CmdOpen: OpenRequested?.Invoke(); break;
            case CmdSearch: SearchRequested?.Invoke(); break;
            case CmdQuit: QuitRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        Send(NIM_DELETE);
        _source.RemoveHook(Hook);
        if (_icon != IntPtr.Zero) Services.Native.DestroyIcon(_icon);
    }
}
