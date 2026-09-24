using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        try
        {
            VectorRendererUsesGlyphRuns();
            PreviewingMultilineFormulaDoesNotSkipUncollapsedLines();
            FullModeRevealRestoresAndRecollapsesSource();
            TransientFindRevealRestoresAndRecollapsesSource();
            Console.WriteLine("Markdown math layout checks passed: 4");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VectorRendererUsesGlyphRuns()
    {
        var assembly = typeof(MarkdownMathRenderer).Assembly;
        var resources = assembly.GetManifestResourceNames();
        Require(
            resources.Contains("PaperTodo.Native.papertodo_math.dll", StringComparer.Ordinal),
            "native math bridge must be embedded in the PaperTodo assembly");
        Require(
            resources.Count(name => name.StartsWith("PaperTodo.MathFonts.", StringComparison.Ordinal)) == 19,
            "all 19 fixed KaTeX TTFs must be embedded in the PaperTodo assembly");
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
        using var editor = new Editor(source);
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
        editor.SetTransientFindReveal(matchOffset, 3);
        Pump();
        Layout(box);
        Require(!HasMathElement(box, span),
            "find hit inside rendered formula reveals exact Markdown source");

        editor.SetTransientFindReveal(null, 0);
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

    private static void Layout(MarkdownTextBox box)
    {
        box.ApplyTemplate();
        box.Measure(new Size(800, 600));
        box.Arrange(new Rect(0, 0, 800, 600));
        box.UpdateLayout();
        var view = box.TextArea.TextView;
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        view.EnsureVisualLines();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Editor : IDisposable
    {
        public MarkdownTextBox Box { get; }
        private readonly MarkdownSemanticDocument _document;
        private readonly MarkdownSemanticPresentation _presentation;

        public Editor(string source)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.Document.UndoStack.ClearAll();
            Box.SetMarkdownEditAnimationEnabled(false);
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            _presentation = new MarkdownSemanticPresentation(Box, _document);
        }

        public void SetTransientFindReveal(int? offset, int length) =>
            _presentation.SetTransientFindReveal(offset, length);

        public void Dispose()
        {
            _presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
