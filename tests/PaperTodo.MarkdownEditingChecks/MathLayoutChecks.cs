using System.IO;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static partial class Program
{
    private static void RunMathLayoutChecks(Action<string, Action> check)
    {
        check("Embedded math renderer produces vector glyphs", VectorRendererUsesGlyphRuns);
        check("Multiline formula preview owns all source lines", PreviewingMultilineFormulaDoesNotSkipUncollapsedLines);
        check("Formula caret reveal restores and recollapses source", FullModeRevealRestoresAndRecollapsesSource);
        check("Formula find reveal restores and recollapses source", TransientFindRevealRestoresAndRecollapsesSource);
    }

    private static void VectorRendererUsesGlyphRuns()
    {
        Require(
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "papertodo_math.dll")),
            "math layout test must exercise the embedded native bridge, not a loose sidecar");
        Require(
            !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "math-fonts")),
            "math layout test must exercise embedded fonts, not loose font sidecars");

        Require(
            MarkdownMathRenderer.TryRender(
                @"\frac{a+b}{c}",
                false,
                18,
                Colors.Black,
                1.0,
                out var drawing),
            "RaTeX vector renderer should produce a WPF drawing");

        var flattened = FlattenDrawings(drawing.Drawing).ToArray();
        Require(
            flattened.Any(candidate => candidate is GlyphRunDrawing),
            "ordinary formula characters must render through WPF GlyphRun");
        Require(
            flattened.All(candidate => candidate is not ImageDrawing),
            "210 v2 must not fall back to bitmap formula drawings");
    }

    private static IEnumerable<Drawing> FlattenDrawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is not DrawingGroup group)
        {
            yield break;
        }

        foreach (var child in group.Children)
        {
            foreach (var descendant in FlattenDrawings(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PreviewingMultilineFormulaDoesNotSkipUncollapsedLines()
    {
        const string source = "before\n$$\n\\begin{cases}x=1 \\\\ y=2\\end{cases}\n$$\nafter";
        using var editor = new Editor(source, renderMode: MarkdownRenderModes.Basic);
        var box = editor.Box;
        var span = MathSpan(source);

        // This transition reproduced PaperTodo.crash.log before the fix: RefreshVisualStyle built a
        // visual line whose math element consumed newlines before the continuation lines had been
        // registered as collapsed in AvalonEdit's height tree.
        box.SetPreviewMode(true);
        Pump();
        Layout(box);

        var opening = box.Document.GetLineByOffset(span.Start);
        var visual = box.TextArea.TextView.GetOrConstructVisualLine(opening);
        var element = visual.Elements.OfType<InlineObjectElement>().Single(candidate =>
            candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset);
        Require(element.DocumentLength == span.Length,
            "rendered formula must consume its complete Markdown source range");
        Require(
            visual.LastDocumentLine.LineNumber >=
            box.Document.GetLineByOffset(span.End - 1).LineNumber,
            "collapsed visual line must own every physical formula line");
        Require(box.Text == source, "formula presentation must not rewrite note source");
    }

    private static void FullModeRevealRestoresAndRecollapsesSource()
    {
        const string source = "before\n$$\nx^2+y^2\n$$\nafter";
        using var editor = new Editor(source);
        var box = editor.Box;
        var span = MathSpan(source);

        box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
        box.CaretOffset = source.Length;
        Pump();
        Layout(box);
        Require(HasMathElement(box, span), "Full mode renders formula away from the caret");

        box.CaretOffset = span.Start + span.MarkerLength;
        Pump();
        Layout(box);
        Require(!HasMathElement(box, span), "caret inside formula restores Markdown source");

        box.CaretOffset = source.Length;
        Pump();
        Layout(box);
        Require(HasMathElement(box, span), "leaving formula restores collapsed math element");
    }

    private static void TransientFindRevealRestoresAndRecollapsesSource()
    {
        const string source = "before\n$$\nx^2+y^2\n$$\nafter";
        using var editor = new Editor(source);
        var box = editor.Box;
        var span = MathSpan(source);

        box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
        box.SetPreviewMode(true);
        Pump();
        Layout(box);
        Require(HasMathElement(box, span), "Full preview initially renders formula");

        var matchOffset = source.IndexOf("x^2", StringComparison.Ordinal);
        editor.Presentation.SetTransientFindReveal(matchOffset, 3);
        Pump();
        Layout(box);
        Require(!HasMathElement(box, span),
            "find hit inside rendered formula reveals exact Markdown source");

        editor.Presentation.SetTransientFindReveal(null, 0);
        Pump();
        Layout(box);
        Require(HasMathElement(box, span),
            "clearing find reveal restores folded vector formula");
    }

    private static MarkdownSemanticSpan MathSpan(string source)
    {
        var matches = MarkdownSemanticSnapshot.Parse(source)
            .Spans
            .Where(span => span.Kind == MarkdownSemanticSpanKind.BlockMath)
            .ToArray();
        Require(matches.Length == 1, $"expected one block formula, got {matches.Length}");
        return matches[0];
    }

    private static bool HasMathElement(MarkdownTextBox box, MarkdownSemanticSpan span)
    {
        var line = box.Document.GetLineByOffset(span.Start);
        var visual = box.TextArea.TextView.GetOrConstructVisualLine(line);
        return visual.Elements.OfType<InlineObjectElement>().Any(candidate =>
            candidate.RelativeTextOffset == span.Start - visual.FirstDocumentLine.Offset);
    }
}
