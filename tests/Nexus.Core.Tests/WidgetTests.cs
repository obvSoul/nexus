using Nexus.Core;
using Xunit;

namespace Nexus.Core.Tests;

public class WidgetTests
{
    private static readonly ScreenArea Main = new(@"\\.\DISPLAY1", true, 0, 0, 1920, 1032, 1.0);
    private static readonly ScreenArea Left = new(@"\\.\DISPLAY2", false, -1600, 2, -320, 2002, 1.5);
    private static readonly ScreenArea[] Screens = [Main, Left];

    [Theory]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, "Space")]
    [InlineData("ctrl + shift + F2", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F2")]
    [InlineData("Win+Alt+K", HotkeyModifiers.Windows | HotkeyModifiers.Alt, "K")]
    public void Shortcuts_are_parsed(string text, HotkeyModifiers mods, string key)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(mods, h.Modifiers);
        Assert.Equal(key, h.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Space")]
    [InlineData("Alt")]
    [InlineData("Alt+A+B")]
    [InlineData("Alt+")]
    public void Unusable_shortcuts_are_refused(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void A_shortcut_survives_a_round_trip()
    {
        Assert.True(Hotkey.TryParse("shift+alt+ctrl+Space", out var h));
        Assert.Equal("Ctrl+Alt+Shift+Space", h.ToString());
        Assert.True(Hotkey.TryParse(h.ToString(), out var again));
        Assert.Equal(h, again);
    }

    [Fact]
    public void Top_centre_opens_on_the_screen_under_the_cursor_and_respects_its_scale()
    {
        var place = WidgetPlacement.Compute(Screens, new WidgetSettings(), -1000, 500, 700);
        Assert.Equal(Left, place.Screen);
        Assert.Equal(-1600 + (1280 - 1050) / 2, place.X);
        Assert.Equal(2 + (int)(2000 * 0.16), place.Y);
    }

    [Theory]
    [InlineData(WidgetSize.Small, 560)]
    [InlineData(WidgetSize.Normal, 700)]
    [InlineData(WidgetSize.Large, 875)]
    [InlineData(WidgetSize.ExtraLarge, 1050)]
    public void Each_size_has_its_width(WidgetSize size, double width)
    {
        var place = WidgetPlacement.Compute(Screens, new WidgetSettings { Size = size }, 10, 10, WidgetSizes.Width(size));
        Assert.Equal(width, place.WidthDip);
        Assert.Equal((1920 - (int)width) / 2, place.X);
    }

    [Fact]
    public void A_wide_widget_is_narrowed_to_fit_a_scaled_screen()
    {
        var place = WidgetPlacement.Compute(Screens, new WidgetSettings { Screen = Left.Device }, 10, 10, 1050);
        Assert.Equal(1280 / 1.5 - 32, place.WidthDip, 3);
        Assert.True(place.X >= Left.Left);
    }

    [Fact]
    public void Middle_position_is_lower_than_top_position()
    {
        var top = WidgetPlacement.Compute(Screens, new WidgetSettings(), 10, 10, 700);
        var middle = WidgetPlacement.Compute(Screens, new WidgetSettings { Position = WidgetPosition.MiddleCenter }, 10, 10, 700);
        Assert.Equal(top.X, middle.X);
        Assert.True(middle.Y > top.Y);
    }

    [Fact]
    public void A_chosen_screen_wins_over_the_cursor_and_falls_back_when_unplugged()
    {
        var s = new WidgetSettings { Screen = @"\\.\DISPLAY2" };
        Assert.Equal(Left, WidgetPlacement.Compute(Screens, s, 10, 10, 700).Screen);
        Assert.Equal(Main, WidgetPlacement.Compute([Main], s, 10, 10, 700).Screen);
        Assert.Equal(Main, WidgetPlacement.Compute(Screens, new WidgetSettings { Screen = WidgetSettings.PrimaryScreen }, -1000, 500, 700).Screen);
    }

    [Fact]
    public void A_custom_position_is_kept_and_stays_inside_its_screen()
    {
        var kept = WidgetPlacement.Compute(Screens, new WidgetSettings { Position = WidgetPosition.Custom, CustomX = 100, CustomY = 200 }, 0, 0, 700);
        Assert.Equal((100, 200), (kept.X, kept.Y));
        var clamped = WidgetPlacement.Compute(Screens, new WidgetSettings { Position = WidgetPosition.Custom, CustomX = 1900, CustomY = 200 }, 0, 0, 700);
        Assert.Equal(1920 - 700, clamped.X);
    }

    [Fact]
    public void A_custom_position_on_a_missing_screen_falls_back_to_a_visible_one()
    {
        var place = WidgetPlacement.Compute(Screens, new WidgetSettings { Position = WidgetPosition.Custom, CustomX = 9000, CustomY = 9000 }, 10, 10, 700);
        Assert.Equal(Main, place.Screen);
        Assert.True(Main.Contains(place.X, place.Y));
    }
}
