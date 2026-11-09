using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Nexus.Core;

namespace Nexus.App.Widget;

/// <summary>A system-wide shortcut, delivered on the UI thread through a hidden window.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312, Id = 0x4E58, ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;

    public bool IsRegistered { get; private set; }
    public event Action? Pressed;

    public GlobalHotkey(HwndSource source)
    {
        _source = source;
        _source.AddHook(Hook);
    }

    /// <summary>Replaces the current shortcut. Returns the reason on failure; no shortcut is then active.</summary>
    public string? Register(Hotkey hotkey)
    {
        Unregister();
        if (!Enum.TryParse<Key>(hotkey.Key, ignoreCase: true, out var key)) return $"Touche inconnue : {hotkey.Key}.";
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return $"Touche inutilisable : {hotkey.Key}.";

        if (WidgetNative.RegisterHotKey(_source.Handle, Id, (uint)hotkey.Modifiers | MOD_NOREPEAT, vk))
        {
            IsRegistered = true;
            return null;
        }
        int error = Marshal.GetLastWin32Error();
        return error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Le raccourci {hotkey} est déjà utilisé par Windows ou une autre application."
            : $"Impossible d'enregistrer {hotkey} : {new Win32Exception(error).Message}";
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        WidgetNative.UnregisterHotKey(_source.Handle, Id);
        IsRegistered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
    }
}
