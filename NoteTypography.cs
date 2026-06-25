using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class NoteTypography
{
    public const double FontSize = 14;
    public const double CodeFontSize = 13;

    public static FontFamily FontFamily => AppTypography.ContentFontFamily;
    public static FontFamily CodeFontFamily => AppTypography.CodeFontFamily;
    public static FontStyle FontStyle => FontStyles.Normal;
    public static FontWeight FontWeight => FontWeights.Normal;
    public static FontWeight HeadingFontWeight => FontWeights.SemiBold;
    public static FontStretch FontStretch => FontStretches.Normal;
    public static XmlLanguage Language => AppTypography.Language;
    public static Thickness ContentPadding => new(13, 8, 6, 8);

    public static void ApplyTextRendering(DependencyObject target)
    {
        target.ClearValue(TextOptions.TextFormattingModeProperty);
        target.ClearValue(TextOptions.TextRenderingModeProperty);
        target.ClearValue(TextOptions.TextHintingModeProperty);
        target.ClearValue(RenderOptions.ClearTypeHintProperty);
    }
}
