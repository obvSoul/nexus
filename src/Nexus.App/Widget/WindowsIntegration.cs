using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nexus.Core;

namespace Nexus.App.Widget;

/// <summary>The monitors as they are right now.</summary>
internal static class ScreenList
{
    public static List<ScreenArea> Get()
    {
        var list = new List<ScreenArea>();
        WidgetNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref WidgetNative.RECT _, IntPtr _) =>
        {
            var info = new WidgetNative.MONITORINFOEX { cbSize = Marshal.SizeOf<WidgetNative.MONITORINFOEX>() };
            if (!WidgetNative.GetMonitorInfoW(monitor, ref info)) return true;
            double scale = WidgetNative.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            list.Add(new ScreenArea(info.szDevice, (info.dwFlags & 1) != 0, info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom, scale));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

public static class ScreenListView
{
    /// <summary>The monitors, labelled for a settings list.</summary>
    public static IEnumerable<(string Label, string Device)> Choices() =>
        ScreenList.Get().OrderBy(s => s.Device, StringComparer.Ordinal).Select(s =>
        {
            string number = new(s.Device.Where(char.IsDigit).ToArray());
            return ($"Écran {number} · {s.Width} × {s.Height} · {s.Scale * 100:0} %{(s.IsPrimary ? " · principal" : "")}", s.Device);
        });
}

/// <summary>Start with Windows, for the current user only. The registry is the single source of truth.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Value = "NEXUS";
    public const string BackgroundArgument = "--background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Value) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Value, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
        else key.DeleteValue(Value, throwOnMissingValue: false);
    }
}
