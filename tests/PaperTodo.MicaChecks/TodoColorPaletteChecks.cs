using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo;

internal static class TodoColorPaletteChecks
{
    internal static void Run()
    {
        // Snapshot representative semantic colors from the released palette tables.
        var expected = new[]
        {
            ("warm", false, "#FFF9EA", "#33291E", "#8C7350"), ("warm", true, "#211F1C", "#E7E0D4", "#A88E6A"),
            ("ink", false, "#F6F7F9", "#262C36", "#5A6C86"), ("ink", true, "#1A1C20", "#DEE3EA", "#849CBC"),
            ("forest", false, "#F3F8F1", "#26322A", "#588260"), ("forest", true, "#1A1E1B", "#DCE4DC", "#7CA886"),
            ("rose", false, "#FDF5F6", "#36262A", "#9E6876"), ("rose", true, "#211C1E", "#E8DCDF", "#BE8694")
        };
        foreach (var (scheme, dark, paper, text, active) in expected)
        {
            var colors = new ThemePalette(scheme, dark, highContrast: false);
            Require(Hex(colors.PaperBrush) == paper && Hex(colors.TextBrush) == text && Hex(colors.ActiveBrush) == active, "preset changed: " + scheme);
        }
        foreach (var seed in new[] { "#000000", "#FFFFFF", "#808080", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF", "#2468AC" })
        foreach (var dark in new[] { false, true })
        {
            var colors = new ThemePalette("warm", dark, seed, highContrast: false);
            var background = Color(colors.PaperBrush);
            foreach (var brush in new[] { colors.TextBrush, colors.WeakTextBrush, colors.BrightWeakTextBrush, colors.ActiveBrush, colors.LinkBrush })
                Require(ThemePalette.Contrast(Color(brush), background) >= 4.5, "custom text contrast: " + seed);
            Require(ThemePalette.Contrast(Color(colors.CheckBoxBorderBrush), background) >= 3, "checkbox contrast");
            Require(ThemePalette.Contrast(Color(colors.ActiveBrush), background) >= 3, "checkmark contrast");
            Require(background.A == 255 && Color(colors.TextBrush).A == 255, "semantic colors became transparent");
        }
        var red = new ThemePalette("warm", false, "#FF0000", false);
        var blue = new ThemePalette("warm", false, "#0000FF", false);
        var first = new StackPanel(); var second = new StackPanel();
        var redSurface = new SkinBorder(); var blueSurface = new SkinBorder();
        ThemePalette.SetScope(first, red); ThemePalette.SetScope(second, blue);
        first.Children.Add(redSurface); second.Children.Add(blueSurface);
        Require(Hex(ThemePalette.For(redSurface).ActiveBrush) == Hex(red.ActiveBrush) && Hex(ThemePalette.For(blueSurface).ActiveBrush) == Hex(blue.ActiveBrush), "inherited palettes leaked");
        Require(red.MaterialColors.NativeOverlay != blue.MaterialColors.NativeOverlay && red.MaterialColors.Surface != blue.MaterialColors.Surface, "custom material tint ignored");
        var global = Hex(Theme.PaperBrush);
        ThemePalette.SetScope(first, blue);
        Require(Hex(Theme.PaperBrush) == global, "local palette mutated global theme");
        var contrast = new ThemePalette("forest", false, "#FF0000", true);
        Require(Color(contrast.PaperBrush) == SystemColors.WindowColor && Color(contrast.ActiveBrush) == SystemColors.HighlightColor, "high contrast did not use system colors");
        var slot = new TodoColorSlot { Name = "draft", PrimaryColor = "#2468AC" };
        var draft = slot.Copy(); draft.PrimaryColor = "#FF0000";
        Require(slot.PrimaryColor == "#2468AC", "editing draft mutated shared slot");
    }

    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;
    private static string Hex(Brush brush) { var c = Color(brush); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
