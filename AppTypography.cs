using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class AppTypography
{
    private const string SymbolFallback = "Segoe UI Symbol, Segoe UI Emoji";
    private const string DefaultContentFontFamilyName = "Microsoft YaHei UI, Segoe UI, Microsoft YaHei, Segoe UI Symbol, Segoe UI Emoji";
    private const string DefaultCodeFontFamilyName = "Cascadia Mono, Consolas, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji";

    private static string _preset = UiFontPresets.Default;
    private static FontFamily? _customFontFamily;
    private static double _scale = 1.0;

    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);

    public static FontFamily UiFontFamily => _customFontFamily ?? ResolveUiFontFamily();

    public static FontFamily ContentFontFamily => _customFontFamily ?? ResolveContentFontFamily();

    public static FontFamily CodeFontFamily => new(DefaultCodeFontFamilyName);

    public static FontFamily SymbolFontFamily { get; } = new(SymbolFallback);

    public static TextFormattingMode TextFormattingMode => TextFormattingMode.Display;
    public static TextRenderingMode TextRenderingMode => TextRenderingMode.ClearType;
    public static TextHintingMode TextHintingMode => TextHintingMode.Fixed;

    public static bool HasCustomFont => _customFontFamily != null;

    public static double ScaleFactor => _scale;

    public static double Scale(double fontSize)
    {
        return Math.Round(fontSize * _scale, 1, MidpointRounding.AwayFromZero);
    }

    public static void Configure(string? preset, double scale = 1.0)
    {
        _preset = UiFontPresets.Normalize(preset);
        _scale = OverallFontScales.Normalize(scale);
        _customFontFamily = TryLoadCustomFontFamily();
    }

    public static void ApplyTextRendering(DependencyObject target)
    {
        TextOptions.SetTextFormattingMode(target, TextFormattingMode);
        TextOptions.SetTextRenderingMode(target, TextRenderingMode);
        TextOptions.SetTextHintingMode(target, TextHintingMode);
    }

    private static FontFamily ResolveUiFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            UiFontPresets.DengXian => new FontFamily($"Segoe UI, DengXian, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            _ => DefaultUiFontFamily()
        };
    }

    private static FontFamily ResolveContentFontFamily()
    {
        return _preset switch
        {
            UiFontPresets.YaHei => new FontFamily($"Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            UiFontPresets.DengXian => new FontFamily($"Segoe UI, DengXian, Microsoft YaHei UI, Microsoft YaHei, Microsoft JhengHei UI, Microsoft JhengHei, Yu Gothic UI, Malgun Gothic, Meiryo, {SymbolFallback}"),
            _ => new FontFamily(DefaultContentFontFamilyName)
        };
    }

    private static FontFamily DefaultUiFontFamily()
    {
        var cultureName = CultureInfo.CurrentUICulture.Name;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

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

    private static FontFamily? TryLoadCustomFontFamily()
    {
        foreach (var path in CustomFontCandidates())
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
                return new FontFamily(baseUri, $"./#{familyName}");
            }
            catch
            {
                // Invalid or unsupported custom fonts must not affect startup.
            }
        }

        return null;
    }

    private static IEnumerable<string> CustomFontCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.ttf");
        yield return Path.Combine(AppContext.BaseDirectory, "papertodo.otf");
    }

    private static string PreferredFamilyName(GlyphTypeface glyphTypeface)
    {
        var culture = CultureInfo.CurrentUICulture;
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
