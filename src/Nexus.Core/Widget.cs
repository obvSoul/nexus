namespace Nexus.Core;

public enum WidgetPosition { TopCenter, MiddleCenter, Custom }

public enum WidgetSize { Small, Normal, Large, ExtraLarge }

public static class WidgetSizes
{
    public const double BaseWidth = 700;

    public static double Width(WidgetSize size) => BaseWidth * size switch
    {
        WidgetSize.Small => 0.8,
        WidgetSize.Large => 1.25,
        WidgetSize.ExtraLarge => 1.5,
        _ => 1.0,
    };
}

/// <summary>Preferences of the floating search widget.</summary>
public sealed record WidgetSettings
{
    public const string ScreenUnderCursor = "";
    public const string PrimaryScreen = "primary";

    public bool Enabled { get; init; } = true;
    public string Hotkey { get; init; } = "Alt+Space";
    public WidgetPosition Position { get; init; } = WidgetPosition.TopCenter;
    public WidgetSize Size { get; init; } = WidgetSize.Normal;
    /// <summary><see cref="ScreenUnderCursor"/>, <see cref="PrimaryScreen"/> or a device name such as \\.\DISPLAY2.</summary>
    public string Screen { get; init; } = ScreenUnderCursor;
    public int MaxResults { get; init; } = 9;
    public bool KeepLastSearch { get; init; }
    public int? CustomX { get; init; }
    public int? CustomY { get; init; }
    public bool BackgroundNoticeShown { get; init; }
}

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

/// <summary>A keyboard shortcut in text form ("Ctrl+Alt+Space"). The key name is the one used by WPF.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "ctrl" or "control": mods |= HotkeyModifiers.Control; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows": mods |= HotkeyModifiers.Windows; break;
                case "": return false;
                default:
                    if (key is not null) return false;
                    key = raw;
                    break;
            }
        }
        if (mods == HotkeyModifiers.None || key is null) return false; // a bare key would be captured everywhere
        hotkey = new Hotkey(mods, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>A monitor's usable area in physical pixels, with its scale (1.5 = 150 %).</summary>
public readonly record struct ScreenArea(string Device, bool IsPrimary, int Left, int Top, int Right, int Bottom, double Scale)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct WidgetPlace(int X, int Y, ScreenArea Screen, double WidthDip);

/// <summary>Decides where the widget opens. Everything is in physical pixels of the virtual desktop.</summary>
public static class WidgetPlacement
{
    private const double TopFraction = 0.16, MiddleFraction = 0.30, ScreenMargin = 32;

    public static ScreenArea ChooseScreen(IReadOnlyList<ScreenArea> screens, string requested, int cursorX, int cursorY)
    {
        var primary = screens.FirstOrDefault(s => s.IsPrimary);
        if (primary == default) primary = screens[0];
        if (requested == WidgetSettings.ScreenUnderCursor)
        {
            foreach (var s in screens)
                if (s.Contains(cursorX, cursorY)) return s;
            return primary;
        }
        foreach (var s in screens)
            if (s.Device.Equals(requested, StringComparison.OrdinalIgnoreCase)) return s;
        return primary; // the chosen monitor has been unplugged
    }

    public static WidgetPlace Compute(IReadOnlyList<ScreenArea> screens, WidgetSettings settings, int cursorX, int cursorY, double widthDip)
    {
        if (settings is { Position: WidgetPosition.Custom, CustomX: { } x, CustomY: { } y })
        {
            foreach (var s in screens)
            {
                if (!s.Contains(x, y)) continue;
                double fitted = Fit(widthDip, s);
                int width = (int)Math.Ceiling(fitted * s.Scale);
                return new WidgetPlace(Math.Max(s.Left, Math.Min(x, s.Right - width)), y, s, fitted);
            }
            // The saved position is on a monitor that is no longer there: fall through.
        }

        var screen = ChooseScreen(screens, settings.Screen, cursorX, cursorY);
        double fraction = settings.Position == WidgetPosition.MiddleCenter ? MiddleFraction : TopFraction;
        double fit = Fit(widthDip, screen);
        int w = (int)Math.Ceiling(fit * screen.Scale);
        return new WidgetPlace(screen.Left + Math.Max(0, (screen.Width - w) / 2), screen.Top + (int)(screen.Height * fraction), screen, fit);
    }

    /// <summary>A large widget must still fit on a small or scaled monitor.</summary>
    private static double Fit(double widthDip, ScreenArea screen) => Math.Min(widthDip, screen.Width / screen.Scale - ScreenMargin);
}
