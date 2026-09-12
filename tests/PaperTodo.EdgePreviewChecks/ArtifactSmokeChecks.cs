using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    // This runs before the ordinary Edge checks and exercises the actual WPF hit-test contract:
    // one zero-child surface is selectable only inside link rectangles, so background clicks keep
    // flowing to the existing preview gesture instead of turning the whole drawing into a control.
    private static readonly bool ArtifactSurfaceContractChecked = CheckArtifactSurfaceContract();

    private static bool CheckArtifactSurfaceContract()
    {
        var artifact = ArtifactFixture(links: new[]
        {
            new MarkdownPreviewArtifactLink(new Rect(12, 7, 64, 18), "https://example.com/a", 0),
            new MarkdownPreviewArtifactLink(new Rect(90, 7, 64, 18), "https://example.com/b", 1)
        });
        var surface = new MarkdownPreviewArtifactSurface(artifact, _ => { });
        surface.Measure(new Size(200, 40));
        surface.Arrange(new Rect(0, 0, 200, 40));
        Require(surface is StackPanel && surface.Children.Count == 0,
            "artifact demand product is the directly mounted surface, not a wrapper panel/tree");
        Require(surface.DesiredSize == new Size(200, 40), "artifact surface keeps prepared geometry");
        Require(ReferenceEquals(surface.InputHitTest(new Point(20, 12)), surface) &&
                ReferenceEquals(surface.InputHitTest(new Point(100, 12)), surface),
            "each artifact link rectangle participates in hit testing");
        Require(surface.InputHitTest(new Point(82, 12)) == null &&
                surface.InputHitTest(new Point(150, 30)) == null,
            "gaps and artifact background remain transparent to the existing paper-open gesture");
        Require(artifact.Drawing.IsFrozen, "whole-preview drawing is immutable across demand mounts");
        return true;
    }

    private static MarkdownPreviewArtifact ArtifactFixture(
        double width = 200,
        double height = 40,
        IReadOnlyList<MarkdownPreviewArtifactLink>? links = null)
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
            links ?? Array.Empty<MarkdownPreviewArtifactLink>());
    }
}
