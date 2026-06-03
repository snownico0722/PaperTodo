using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class NoteTypography
{
    public const string FontFamilyName = "Microsoft YaHei UI, Segoe UI, Microsoft YaHei, Segoe UI Symbol, Segoe UI Emoji";
    public const string CodeFontFamilyName = "Cascadia Mono, Consolas, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji";
    public const double FontSize = 15;
    public const double CodeFontSize = 14;

    public static FontFamily FontFamily { get; } = new(FontFamilyName);
    public static FontFamily CodeFontFamily { get; } = new(CodeFontFamilyName);
    public static FontStyle FontStyle => FontStyles.Normal;
    public static FontWeight FontWeight => FontWeights.Normal;
    public static FontWeight HeadingFontWeight => FontWeights.SemiBold;
    public static FontStretch FontStretch => FontStretches.Normal;
    public static TextFormattingMode TextFormattingMode => TextFormattingMode.Display;
    public static TextRenderingMode TextRenderingMode => TextRenderingMode.ClearType;
    public static TextHintingMode TextHintingMode => TextHintingMode.Fixed;
    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
    public static Thickness ContentPadding => new(19, 8, 8, 8);

    public static void ApplyTextRendering(DependencyObject target)
    {
        TextOptions.SetTextFormattingMode(target, TextFormattingMode);
        TextOptions.SetTextRenderingMode(target, TextRenderingMode);
        TextOptions.SetTextHintingMode(target, TextHintingMode);
    }
}
