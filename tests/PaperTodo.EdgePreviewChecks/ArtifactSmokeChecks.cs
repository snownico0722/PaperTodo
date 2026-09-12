using System.Windows;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    // Shared by follow-up artifact parity checks. Keeping construction here makes the production
    // smoke path compile against the real immutable artifact/surface contract rather than source shape.
    private static MarkdownPreviewArtifact ArtifactFixture(double width = 200, double height = 40)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        drawing.Freeze();
        return new MarkdownPreviewArtifact(
            drawing,
            width,
            height,
            false,
            Array.Empty<MarkdownPreviewArtifactLink>());
    }
}
