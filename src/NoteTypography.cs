using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperTodo;

public static class NoteTypography
{
    private static string _size = VisualTextSizes.Medium;
    private static bool _bold;

    // Keep the global factor unrounded here so MarkdownTextBox can apply the per-note zoom last
    // and round only the final composed size.
    public static double FontSize =>
        (14 + VisualTextSizes.Correction(_size)) * AppTypography.ScaleFactor;
    public static double CodeFontSize =>
        (13 + VisualTextSizes.Correction(_size)) * AppTypography.ScaleFactor;
    public static double Heading1FontSize =>
        (17 + VisualTextSizes.Correction(_size)) * AppTypography.ScaleFactor;
    public static double Heading2FontSize =>
        (15 + VisualTextSizes.Correction(_size)) * AppTypography.ScaleFactor;
    public static double Heading3FontSize =>
        (14 + VisualTextSizes.Correction(_size)) * AppTypography.ScaleFactor;

    public static FontFamily FontFamily => AppTypography.FontFamilyFor(content: true, bold: _bold);
    public static FontFamily CodeFontFamily => AppTypography.CodeFontFamily;
    public static FontStyle FontStyle => FontStyles.Normal;
    public static FontWeight FontWeight => AppTypography.FontWeightFor(_bold);
    public static FontWeight HeadingFontWeight => AppTypography.HeadingFontWeightFor(_bold);
    public static FontStretch FontStretch => FontStretches.Normal;
    public static XmlLanguage Language => AppTypography.Language;
    public static Thickness ContentPadding => new(13, 8, 6, 8);

    public static void Configure(string? size, bool bold)
    {
        _size = VisualTextSizes.Normalize(size);
        _bold = bold;
    }

    public static void ApplyTextRendering(DependencyObject target)
    {
        AppTypography.ApplyTextRendering(target);
    }
}
