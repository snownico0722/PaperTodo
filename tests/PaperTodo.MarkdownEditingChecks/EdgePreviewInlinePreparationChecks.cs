using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void RunEdgePreviewInlinePreparationChecks(Action<string, Action> check)
    {
        check("Preview width, height and both renderers reuse one inline preparation", () =>
        {
            foreach (var mode in new[] { MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            foreach (var repeats in new[] { 1, 24 })
            {
                var source = string.Concat(Enumerable.Repeat("**bold** `code` [a *mixed* label](https://example.com) 文 ", repeats)).TrimEnd();
                var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source, mode);
                _ = MarkdownEdgeCapsulePreviewRenderer.MeasureText(content);
                _ = MarkdownEdgeCapsulePreviewRenderer.MeasureContentHeight(content, 360, 1);
                var expected = content.Inlines.Get(source, mode);
                var entries = content.Inlines.Count;
                var small = new StackPanel();
                foreach (var step in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(small, content, _ => { })) { }
                var longText = new StackPanel();
                foreach (var step in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(longText, content, _ => { }, new Size(360, 200))) { }
                Equal(entries, content.Inlines.Count, "publication does not compile already measured text again");
                Require(ReferenceEquals(expected, content.Inlines.Get(source, mode)), "the captured excerpt owns one immutable prepared value");
                Require(EdgePreviewText(small).StartsWith(expected.VisibleText, StringComparison.Ordinal), "TextBlock consumes the prepared visible text");
                if (repeats > 1)
                {
                    var paragraph = EdgePreviewElements(longText).OfType<MarkdownEdgePreviewParagraph>().Single();
                    Require(expected.VisibleText.StartsWith(paragraph.VisibleText, StringComparison.Ordinal), "TextFormatter consumes the same prepared text");
                }
                var replacement = MarkdownEdgeCapsulePreviewRenderer.CaptureContent("replacement", mode);
                Require(!ReferenceEquals(expected, replacement.Inlines.Get("replacement", mode)), "a new excerpt cannot inherit old content");
            }
        });

        check("Prepared inline labels keep distinct links, safe schemes and live resources", () =>
        {
            var panel = new StackPanel();
            MarkdownEdgeCapsulePreviewRenderer.RenderInto(panel,
                "[a **bold** *label*](https://example.com)[second](https://example.com) [unsafe](javascript:no)", _ => { });
            var block = panel.Children.OfType<TextBlock>().Single();
            var links = block.Inlines.OfType<Hyperlink>().ToArray();
            Equal(2, links.Length, "mixed styles share a label, not adjacent equal-URL links");
            Equal("a bold label", InlineText(links[0].Inlines), "styled label text is preserved");
            Equal("second", InlineText(links[1].Inlines), "second link is a separate target");
            Require(InlineText(block.Inlines).EndsWith("unsafe", StringComparison.Ordinal), "unsafe links remain plain labels");
            var firstBrush = new SolidColorBrush(Colors.DarkBlue);
            var secondBrush = new SolidColorBrush(Colors.DarkRed);
            var window = new Window { Content = panel, Width = 450, Height = 180, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Resources["LinkBrushKey"] = firstBrush;
                window.Show(); Pump();
                Require(ReferenceEquals(firstBrush, links[0].Foreground), "link resolves host resources, not cached WPF objects");
                window.Resources["LinkBrushKey"] = secondBrush;
                Pump();
                Require(ReferenceEquals(secondBrush, links[0].Foreground), "resource replacement still refreshes prepared link controls");
            }
            finally { window.Close(); Pump(); }
        });
    }
}
