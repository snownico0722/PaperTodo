using System.Windows;
using System.Windows.Controls;
using PaperTodo;
using Renderer = PaperTodo.MarkdownEdgeCapsulePreviewRenderer;
using Style = PaperTodo.MarkdownEdgeCapsulePreviewRenderer.InlineStyle;

internal static class SharedPreviewSemanticChecks
{
    internal static void Run()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        Renderer.InlinePiece[] Full(string source) => Renderer.InlinePieces(source, MarkdownRenderModes.Full).ToArray();
        string Visible(string source) => string.Concat(Full(source).Select(p => p.Text));
        const string ordinary = "**bold** *italic* ~~gone~~ `a\\*b` [**label**](https://example.com)";
        Require(Visible(ordinary) == "bold italic gone a\\*b label", "shared inline syntax retains code literals and hides markup");
        Require(Visible("snake_case_word") == "snake_case_word", "intraword underscore is not emphasis");
        Require(Full("snake_case_word").All(p => p.Style == Style.None), "literal underscores have no italic style");
        var nested = Full("**a *nested* b**");
        Require(string.Concat(nested.Select(p => p.Text)) == "a nested b", "nested emphasis has exact visible text");
        Require(nested.Any(p => p.Text.Contains("nested") && (p.Style & (Style.Strong | Style.Italic)) == (Style.Strong | Style.Italic)),
            "nested emphasis composes both styles");
        Require(Visible(@"\*literal\* \[not link\]") == "*literal* [not link]", "escaped punctuation stays literal");
        Require(Visible("``a ` b``") == "a ` b", "multiple-backtick inline code follows shared grammar");
        var adjacent = Full("[a **bold**](https://example.com)[second](https://example.com)");
        var label = adjacent.Where(p => p.Link != null && !p.Text.Contains("second")).ToArray();
        var second = adjacent.Single(p => p.Text == "second");
        Require(label.Length >= 2 && label.All(p => ReferenceEquals(p.Link, label[0].Link)), "styles inside one link share identity");
        Require(second.Link != null && !ReferenceEquals(second.Link, label[0].Link), "adjacent equal URLs remain independent");
        var parenthesis = Full("[label](https://example.com/a_(b))");
        Require(string.Concat(parenthesis.Select(p => p.Text)) == "label" &&
            parenthesis.Any(p => p.Link?.AbsoluteUri.EndsWith("a_(b)") == true), "balanced parentheses belong to the link destination");
        Require(Full("`https://example.com`").All(p => p.Link == null), "code URLs never become links");
        Require(Visible("[unsafe](javascript:no)") == "unsafe" && Full("[unsafe](javascript:no)").All(p => p.Link == null),
            "unsafe destinations remain noninteractive labels");
        var html = Full("<strong>a <em>b</em></strong> <u>c</u>");
        Require(string.Concat(html.Select(p => p.Text)) == "a b c", "supported HTML uses the existing semantic recognizer");
        Require(html.Any(p => p.Text == "c" && (p.Style & Style.Underline) != 0), "shared underline semantics survive adaptation");
        Require(Visible("![photo](i:asset)") == "▧ photo", "images stay lightweight placeholders");
        foreach (var mode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced })
            Require(string.Concat(Renderer.InlinePieces(ordinary, mode).Select(p => p.Text)) == ordinary,
                "non-Full modes retain the exact source: " + mode);
        var cache = new Renderer.PreviewInlineCache();
        var prepared = cache.Get(ordinary, MarkdownRenderModes.Full);
        Require(ReferenceEquals(prepared, cache.Get(ordinary, MarkdownRenderModes.Full)) && cache.Count == 1,
            "same excerpt reuses pure inline values");
        var body = new StackPanel();
        var content = Renderer.CaptureContent(string.Concat(Enumerable.Repeat(ordinary + " ", 70)), MarkdownRenderModes.Full);
        var lastLines = 0;
        var omitted = false;
        foreach (var step in Renderer.RenderSteps(body, content, _ => { }, new Size(180, 120)))
        {
            omitted = step;
            var count = body.Children.OfType<MarkdownEdgePreviewParagraph>().Sum(p => p.FormattedLines);
            Require(count - lastLines <= 1, "one step formats no more than one visible wrap");
            lastLines = count;
        }
        Require(omitted && lastLines is > 0 and < 30, "dense source stays bounded by the visible viewport");
        var paragraph = body.Children.OfType<MarkdownEdgePreviewParagraph>().Single();
        var lines = paragraph.FormattedLines;
        body.Measure(new Size(180, 120)); body.Arrange(new Rect(0, 0, 180, 120));
        paragraph.InvalidateMeasure(); paragraph.InvalidateVisual(); body.UpdateLayout();
        Require(paragraph.FormattedLines == lines, "publication and repaint reuse prepared layout");
        Console.WriteLine("PASS shared semantic grammar, nested styles, escapes, code, HTML, images, link identity, caching and bounded publication");
    }
}
