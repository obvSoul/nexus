using System.Windows.Controls;
using System.Windows.Input;
using Nexus.App.ViewModels;
using Nexus.Core;

namespace Nexus.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => Vm?.Widget.SuspendHotkey();
    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Vm?.Widget.ResumeHotkey();

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= HotkeyModifiers.Windows;

        Vm?.ApplyWidgetHotkey(new Hotkey(mods, key.ToString()).ToString());
        Keyboard.ClearFocus();
    }
}
