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
        Check("plain-inline-reuse", CheckPlainInlineReuse);
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
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic,
            MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
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

    private static void CheckPlainInlineReuse()
    {
        var text = string.Concat(Enumerable.Repeat("普通文字 English e\u0301 العربية 😀 ", 20));
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic,
            MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            var pieces = MarkdownEdgeCapsulePreviewRenderer.InlinePieces(text, mode).ToArray();
            Require(pieces.Length == 1 && pieces[0].Style == MarkdownEdgeCapsulePreviewRenderer.InlineStyle.None &&
                pieces[0].Link == null && ReferenceEquals(pieces[0].Text, text),
                "semantic plain text reuses the original string instead of per-character reconstruction");
        }
        var styled = MarkdownEdgeCapsulePreviewRenderer.InlinePieces("**正文** " + text, MarkdownRenderModes.Full).ToArray();
        Require(styled.Any(p => (p.Style & MarkdownEdgeCapsulePreviewRenderer.InlineStyle.Strong) != 0) &&
            string.Concat(styled.Select(p => p.Text)) == "正文 " + text, "styled input still uses the shared recognizer");
    }

    private static void ProfilePlainInlineAllocation()
    {
        foreach (var length in new[] { 300, 6000 })
        foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
        {
            var text = new string('文', length);
            long Sample()
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var pieces = MarkdownEdgeCapsulePreviewRenderer.InlinePieces(text, mode).ToArray();
                GC.KeepAlive(pieces);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (var i = 0; i < 5; i++) Sample();
            var bytes = Enumerable.Range(0, 21).Select(_ => Sample()).Order().ToArray();
            Console.WriteLine($"INLINE_ALLOC length={length} mode={mode} medianBytes={bytes[bytes.Length/2]}");
        }
    }
}
