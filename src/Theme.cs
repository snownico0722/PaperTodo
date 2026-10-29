using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaperTodo;

public static class ColorSchemes
{
    public const string Warm = "warm";
    public const string Ink = "ink";
    public const string Forest = "forest";
    public const string Rose = "rose";
    // Keep the stored ID so older Mica settings retain their original neutral palette.
    public const string Neutral = "mica";
    public const string Mica = Neutral;

    public static readonly string[] All = { Warm, Ink, Forest, Rose, Mica };

    public static bool IsValid(string? id) => id is Warm or Ink or Forest or Rose or Mica;

    public static string Normalize(string? id) => IsValid(id) ? id! : Warm;
}

public static class MicaBackdropTypes
{
    public const string Mica = "mica";
    public const string Acrylic = "acrylic";
    public const string ClearAcrylic = "clearAcrylic";

    public static readonly string[] All = { Mica, Acrylic, ClearAcrylic };

    public static bool IsValid(string? id) => id is Mica or Acrylic or ClearAcrylic;

    // Retired "micaAlt" selections migrate to standard Mica, as do unknown values.
    public static string Normalize(string? id) => IsValid(id) ? id! : Mica;

    public static int ToDwmBackdrop(string? id) => Normalize(id) switch
    {
        Acrylic => DwmMicaApi.TransientWindow,
        ClearAcrylic => DwmMicaApi.None, // Custom accent policy must not overlap a system backdrop.
        _ => DwmMicaApi.MainWindow
    };
}

public static class Theme
{
    private static ThemePalette? _global;
    private static bool? _isDark;
    internal static ThemePalette Global => _global ??= new(
        ColorSchemes.Normalize(AppController.Current?.State.ColorScheme), IsDark);
    public static void Invalidate() { _global = null; _isDark = null; }
    public static bool IsDark => _isDark ??= ResolveDark();
    private static bool ResolveDark()
    {
        var mode = AppController.Current?.State.Theme;
        if (mode != "system") return mode == "dark";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }
    public static string Skin => PaperSkins.Resolve(AppController.Current?.State);
    public static bool UsesNativeBackdrop => PaperSkins.UsesNativeBackdrop(Skin);
    public static bool IsPixelSkin => Skin == PaperSkins.Pixel && !SystemParameters.HighContrast;
    internal static MaterialPalette MaterialColors => Global.MaterialColors;
    internal static Brush NativeMaterialTint => Global.NativeMaterialTint;
    internal static Brush TitleBarBrush(bool opaque) => Global.TitleBarBrush(opaque);
    public static Brush Tint(byte alpha) => Global.Tint(alpha);
    public static Brush Danger(byte alpha) => Global.Danger(alpha);
    public static Brush ResizeGripBrush(string? mode) => Global.ResizeGripBrush(mode);
    public static DrawingBrush ResizeGripTileBrush(Brush dots) => ThemePalette.ResizeGripTileBrush(dots);
    public static Brush BrushWithAlpha(Brush source, double alpha) => ThemePalette.BrushWithAlpha(source, alpha);
    public static Brush PaperBrush => Global.PaperBrush;
    public static Brush PaperBorderBrush => Global.PaperBorderBrush;
    public static Brush TextBrush => Global.TextBrush;
    public static Brush WeakTextBrush => Global.WeakTextBrush;
    public static Brush BrightWeakTextBrush => Global.BrightWeakTextBrush;
    public static Brush ActiveBrush => Global.ActiveBrush;
    public static Brush CodeBrush => Global.CodeBrush;
    public static Brush QuoteBorderBrush => Global.QuoteBorderBrush;
    public static Brush LinkBrush => Global.LinkBrush;
    public static Brush CheckBoxBorderBrush => Global.CheckBoxBorderBrush;
    public static Brush DangerBrush => Global.DangerBrush;
    public static Brush ScrollThumbBrush => Global.ScrollThumbBrush;
    public static Brush ScrollThumbHoverBrush => Global.ScrollThumbHoverBrush;
    public static Brush HoverBrush => Global.HoverBrush;
    public static Brush CapsuleFocusBorderBrush => Global.CapsuleFocusBorderBrush;
    public static Brush DangerHoverBrush => Global.DangerHoverBrush;
    public static Brush SyntaxFadeBrush => Global.SyntaxFadeBrush;
    public static Brush CheckBoxHoverBorderBrush => Global.CheckBoxHoverBorderBrush;
    public static Brush CheckBoxActiveHoverBrush => Global.CheckBoxActiveHoverBrush;
    public static Brush CheckBoxUncheckedHoverBgBrush => Global.CheckBoxUncheckedHoverBgBrush;
}
