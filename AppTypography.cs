using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class AppTypography
{
    private const string SymbolFallback = "Segoe UI Symbol, Segoe UI Emoji";
    private const string DefaultCodeFontFamilyName = "Cascadia Mono, Consolas, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji";

    private sealed record CustomFontFace(FontFamily Family, FontWeight Weight);

    private static string _preset = UiFontPresets.Default;
    private static CustomFontFace? _customFontFace;
    private static CustomFontFace? _customBoldFontFace;
    private static bool _customFontEnhancedBold;
    private static string _textRenderingProfile = TextRenderingProfiles.Standard;
    private static double _scale = 1.0;

    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(UiLanguages.EffectiveUiCulture.IetfLanguageTag);

    public static FontFamily UiFontFamily => _customFontFace?.Family ?? ResolveUiFontFamily();

    public static FontFamily ContentFontFamily => _customFontFace?.Family ?? ResolveContentFontFamily();

    public static FontFamily CodeFontFamily => new(DefaultCodeFontFamilyName);

    public static FontFamily SymbolFontFamily { get; } = new(SymbolFallback);

    public static bool UsesCustomTextRendering =>
        _textRenderingProfile != TextRenderingProfiles.Standard;

    // Standard follows WPF defaults. Soft keeps the current layout-stable smoothing path, while
    // Sharp uses the pixel-aligned Display path that was used by the earlier rendering experiment.
    public static TextFormattingMode TextFormattingMode =>
        _textRenderingProfile == TextRenderingProfiles.Sharp
            ? TextFormattingMode.Display
            : TextFormattingMode.Ideal;
    public static TextRenderingMode TextRenderingMode =>
        UsesCustomTextRendering ? TextRenderingMode.Grayscale : TextRenderingMode.Auto;
    public static TextHintingMode TextHintingMode =>
        _textRenderingProfile == TextRenderingProfiles.Soft
            ? TextHintingMode.Animated
            : TextHintingMode.Auto;

    public static bool HasCustomFont => _customFontFace != null;

    public static bool HasCustomBoldFont => _customBoldFontFace != null;

    /// <summary>
    /// Enhanced bold is armed in settings, both custom faces are present, and the caller wants bold.
    /// </summary>
    public static bool UsesCustomBoldFace(bool bold) =>
        bold &&
        _customFontEnhancedBold &&
        _customFontFace != null &&
        _customBoldFontFace != null;

    public static double ScaleFactor => _scale;

    public static double Scale(double fontSize)
    {
        return Math.Round(fontSize * _scale, 1, MidpointRounding.AwayFromZero);
    }

    public static double FitChrome(double normalSize)
    {
        return _scale <= 1.0
            ? normalSize
            : Math.Ceiling(normalSize * _scale);
    }

    public static void Configure(
        string? preset,
        double scale = 1.0,
        bool customFontEnhancedBold = false,
        string? textRenderingProfile = null)
    {
        _preset = UiFontPresets.Normalize(preset);
        _scale = OverallFontScales.Normalize(scale);
        _customFontEnhancedBold = customFontEnhancedBold;
        _textRenderingProfile = TextRenderingProfiles.Normalize(textRenderingProfile);
        _customFontFace = TryLoadCustomFontFaceFromCandidates(CustomRegularFontCandidates());
        _customBoldFontFace = TryLoadCustomFontFaceFromCandidates(CustomBoldFontCandidates());
    }

    /// <summary>
    /// Family for UI chrome or body text. content=true: notes / todos; content=false: titles, capsules, settings chrome.
    /// When enhanced bold is active, bold runs use papertodo_bold.
    /// </summary>
    public static FontFamily FontFamilyFor(bool content, bool bold)
    {
        if (UsesCustomBoldFace(bold))
        {
            return _customBoldFontFace!.Family;
        }

        return content ? ContentFontFamily : UiFontFamily;
    }

    /// <summary>
    /// Paper title face — same as other chrome (capsule labels, etc.).
    /// </summary>
    public static FontFamily FontFamilyForTitle(bool bold) => FontFamilyFor(content: false, bold: bold);

    /// <summary>
    /// Weight for bold runs. Preserve the face's designed weight so WPF selects the real bold
    /// face when regular and bold files share the same internal family name.
    /// </summary>
    public static FontWeight FontWeightFor(bool bold)
    {
        if (UsesCustomBoldFace(bold))
        {
            return _customBoldFontFace!.Weight;
        }

        return bold ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public static FontWeight HeadingFontWeightFor(bool bold)
    {
        if (UsesCustomBoldFace(bold))
        {
            return _customBoldFontFace!.Weight;
        }

        return bold ? FontWeights.Bold : FontWeights.SemiBold;
    }

    public static void ApplyTextRendering(DependencyObject target)
    {
        if (!UsesCustomTextRendering)
        {
            ClearTextRendering(target);
            return;
        }

        TextOptions.SetTextFormattingMode(target, TextFormattingMode);
        TextOptions.SetTextRenderingMode(target, TextRenderingMode);
        TextOptions.SetTextHintingMode(target, TextHintingMode);
        target.ClearValue(RenderOptions.ClearTypeHintProperty);
    }

    private static void ClearTextRendering(DependencyObject target)
    {
        target.ClearValue(TextOptions.TextFormattingModeProperty);
        target.ClearValue(TextOptions.TextRenderingModeProperty);
        target.ClearValue(TextOptions.TextHintingModeProperty);
        target.ClearValue(RenderOptions.ClearTypeHintProperty);
    }

    // YaHei / DengXian: selected face leads all scripts; Segoe is missing-glyph only.
    private const string YaHeiFontFamilyName =
        "Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, Segoe UI, " + SymbolFallback;
    private const string DengXianFontFamilyName =
        "DengXian, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, Segoe UI, " + SymbolFallback;
    // System default chrome (titles, capsules, settings): YaHei UI first.
    private const string DefaultChromeFontFamilyName =
        "Microsoft YaHei UI, Microsoft YaHei, Segoe UI, " + SymbolFallback;

    private static FontFamily ResolveUiFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily(YaHeiFontFamilyName),
            UiFontPresets.DengXian => new FontFamily(DengXianFontFamilyName),
            _ => new FontFamily(DefaultChromeFontFamilyName)
        };
    }

    // Notes and todo items only: under system default keep Segoe-first regional body chains.
    private static FontFamily ResolveContentFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily(YaHeiFontFamilyName),
            UiFontPresets.DengXian => new FontFamily(DengXianFontFamilyName),
            _ => DefaultBodyFontFamily()
        };
    }

    private static FontFamily DefaultBodyFontFamily()
    {
        var cultureName = UiLanguages.EffectiveUiCulture.Name;
        var language = UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName;

        return language switch
        {
            "zh" when cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                      cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
                => new FontFamily($"Segoe UI, Microsoft JhengHei UI, Microsoft JhengHei, Microsoft YaHei UI, Microsoft YaHei, {SymbolFallback}"),
            "zh" => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, {SymbolFallback}"),
            "ja" => new FontFamily($"Segoe UI, Yu Gothic UI, Meiryo, {SymbolFallback}"),
            "ko" => new FontFamily($"Segoe UI, Malgun Gothic, {SymbolFallback}"),
            _ => new FontFamily($"Segoe UI, {SymbolFallback}")
        };
    }

    private static CustomFontFace? TryLoadCustomFontFaceFromCandidates(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var fontUri = new Uri(path, UriKind.Absolute);
                var glyphTypeface = new GlyphTypeface(fontUri);
                var familyName = PreferredFamilyName(glyphTypeface);
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                var baseUri = new Uri(AppendDirectorySeparator(directory), UriKind.Absolute);
                return new CustomFontFace(
                    new FontFamily(baseUri, $"./#{familyName}"),
                    glyphTypeface.Weight);
            }
            catch
            {
                // Invalid or unsupported custom fonts must not affect startup.
            }
        }

        return null;
    }

    private static IEnumerable<string> CustomRegularFontCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.ttf");
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.otf");
    }

    /// <summary>
    /// Same folder as the app: papertodo_bold / PaperTodo_Bold (+ ttf/otf).
    /// </summary>
    private static IEnumerable<string> CustomBoldFontCandidates()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var name in new[]
                 {
                     "papertodo_bold",
                     "papertodo-bold",
                     "PaperTodo_Bold",
                     "PaperTodo-Bold"
                 })
        {
            yield return Path.Combine(dir, name + ".ttf");
            yield return Path.Combine(dir, name + ".otf");
        }
    }

    private static string PreferredFamilyName(GlyphTypeface glyphTypeface)
    {
        var culture = UiLanguages.EffectiveUiCulture;
        if (glyphTypeface.Win32FamilyNames.TryGetValue(culture, out var localized))
        {
            return localized;
        }

        var neutral = culture.TwoLetterISOLanguageName;
        foreach (var pair in glyphTypeface.Win32FamilyNames)
        {
            if (pair.Key.TwoLetterISOLanguageName == neutral)
            {
                return pair.Value;
            }
        }

        if (glyphTypeface.Win32FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-us"), out var english))
        {
            return english;
        }

        return glyphTypeface.Win32FamilyNames.Values.FirstOrDefault() ?? "";
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
