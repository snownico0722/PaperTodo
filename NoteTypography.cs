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
    public const double DefaultLineHeight = 18;
    private static double _lineHeight = DefaultLineHeight;

    public static FontFamily FontFamily { get; } = new(FontFamilyName);
    public static FontFamily CodeFontFamily { get; } = new(CodeFontFamilyName);
    public static FontStyle FontStyle => FontStyles.Normal;
    public static FontWeight FontWeight => FontWeights.Normal;
    public static FontWeight HeadingFontWeight => FontWeights.SemiBold;
    public static FontStretch FontStretch => FontStretches.Normal;
    public static double LineHeight => _lineHeight;
    public static TextFormattingMode TextFormattingMode => TextFormattingMode.Display;
    public static TextRenderingMode TextRenderingMode => TextRenderingMode.ClearType;
    public static TextHintingMode TextHintingMode => TextHintingMode.Fixed;
    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
    public static Thickness PreviewContentPadding => new(14, 8, 8, 8);
    public static Thickness EditorContentPadding => new(19, 8, 8, 8);
    public static Thickness ParagraphMargin => new(0);
    public static Thickness HeadingMargin => new(0, 4, 0, 4);
    public static Thickness QuoteMargin => new(2, 3, 0, 6);
    public static Thickness QuotePadding => new(8, 0, 0, 0);
    public static Thickness ListMargin => new(14, 1, 0, 4);
    public static Thickness ListPadding => new(10, 0, 0, 0);
    public static Thickness CodeBlockPadding => new(8);
    public static Thickness CodeBlockMargin => new(0, 4, 0, 6);
    public static Thickness ThematicBreakMargin => new(0, 6, 0, 6);

    public static void SetMeasuredLineHeight(double lineHeight)
    {
        if (!double.IsFinite(lineHeight) || lineHeight <= 0)
        {
            return;
        }

        _lineHeight = Math.Round(lineHeight, 2);
    }

    public static void ApplyTextRendering(DependencyObject target)
    {
        TextOptions.SetTextFormattingMode(target, TextFormattingMode);
        TextOptions.SetTextRenderingMode(target, TextRenderingMode);
        TextOptions.SetTextHintingMode(target, TextHintingMode);
    }
}
