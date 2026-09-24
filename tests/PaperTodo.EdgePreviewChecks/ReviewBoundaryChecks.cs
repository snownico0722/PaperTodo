using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void ReviewBoundaryChecks()
    {
        var failures = new List<string>();
        void Check(string name, Action check)
        {
            try { check(); Console.WriteLine("PASS review " + name); }
            catch (Exception ex) { failures.Add(name); Console.Error.WriteLine("FAIL review " + name + ": " + ex.Message); }
        }
        Check("borrowed-brushes", CheckBorrowedBrushes);
        Check("unicode-excerpt", CheckUnicodeExcerpt);
        if (failures.Count > 0) throw new InvalidOperationException("Review failures: " + string.Join(", ", failures));
    }

    private static void CheckBorrowedBrushes()
    {
        var root = new StackPanel();
        var foreground = new SolidColorBrush(Colors.DarkRed);
        var background = new SolidColorBrush(Colors.LightGray);
        var link = new SolidColorBrush(Colors.Blue);
        root.Resources["TextBrushKey"] = foreground;
        root.Resources["HoverBrushKey"] = background;
        root.Resources["LinkBrushKey"] = link;
        var window = new Window { Content = root, Width = 420, Height = 260,
            ShowActivated = false, ShowInTaskbar = false };
        var text = string.Concat(Enumerable.Repeat("正文 **粗体** `code` [link](https://example.com) ", 40));
        void Render()
        {
            RenderForCheck(root, text, _ => { },
                MarkdownRenderModes.Full, new Size(390, 200));
            window.UpdateLayout(); Pump();
        }
        bool HasColor(Color color) => root.Children.OfType<MarkdownPreviewArtifactSurface>()
            .SelectMany(p => Glyphs(p.Artifact.Drawing))
            .Any(g => g.ForegroundBrush is SolidColorBrush b && b.Color == color);
        try
        {
            window.Show(); Pump(); Render();
            Require(root.Children.OfType<MarkdownPreviewArtifactSurface>().Any(), "fixture uses prepared paragraph path");
            Require(!foreground.IsFrozen && !background.IsFrozen && !link.IsFrozen,
                "preparing text must not freeze caller-owned color resources");
            Require(HasColor(Colors.DarkRed), "first draw uses the original resource value");
            foreground.Color = Colors.DarkGreen;
            background.Color = Colors.Beige;
            link.Color = Colors.Purple;
            Require(HasColor(Colors.DarkRed), "completed drawing retains its own immutable snapshot");
            Render();
            Require(HasColor(Colors.DarkGreen) && HasColor(Colors.Purple), "rebuild captures the changed resource values");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckUnicodeExcerpt()
    {
        var fixtures = new[] {
            new string('文', 5999) + "😀tail",
            "prefix\n" + new string('文', 5991) + "😀tail",
            new string('文', 5998) + "😀",
            "```\n" + new string('文', 5995) + "😀tail\n```"
        };
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Full })
        foreach (var source in fixtures)
        {
            var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source, mode);
            var excerpt = string.Join('\n', content.Lines.Select(line => line.Text));
            Require(excerpt.Length <= 6000, "valid Unicode never exceeds the existing source budget");
            _ = strictUtf8.GetBytes(excerpt);
            if (source.Length == 6000)
                Require(!content.Truncated && excerpt == source, "a complete pair fitting exactly is retained");
            else Require(content.Truncated, "omission remains reported after moving to a safe boundary");
        }
    }


}
