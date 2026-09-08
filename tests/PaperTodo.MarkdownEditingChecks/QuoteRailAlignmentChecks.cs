using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class QuoteRailAlignmentChecks
{
    private static readonly double[] FontScales =
    {
        0.5,
        0.75,
        1.0,
        1.1,
        1.25,
        1.45,
        1.5
    };

    [ModuleInitializer]
    internal static void Run()
    {
        CheckLogicalPrefix();
        foreach (var fontScale in FontScales)
        {
            Check(
                "- > a\n  > b",
                fontScale,
                $"unordered list continuation at {fontScale:0.##}x font");
            Check(
                "10. > a\n    > b",
                fontScale,
                $"wide ordered list continuation at {fontScale:0.##}x font");
        }

        Console.WriteLine("PASS list-contained quote rails share logical X across physical lines and font scales");
    }

    private static void CheckLogicalPrefix()
    {
        const string source = "10. > a\n    > b";
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var first = ParseLine(source, snapshot, 0);
        var second = ParseLine(source, snapshot, 1);
        var firstQuote = first.Tokens.Single(token => token.IsQuote);
        var secondQuote = second.Tokens.Single(token => token.IsQuote);
        var firstPrefix = MarkdownContainerPrefix.BuildLogicalVisualPrefix(
            "10. > a",
            first,
            firstQuote.MarkerStart);
        var secondPrefix = MarkdownContainerPrefix.BuildLogicalVisualPrefix(
            "    > b",
            second,
            secondQuote.MarkerStart);
        if (!string.Equals(firstPrefix, "    ", StringComparison.Ordinal) ||
            !string.Equals(secondPrefix, "    ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote rail alignment: logical prefixes '{firstPrefix}'/'{secondPrefix}'");
        }
    }

    private static MarkdownContainerPrefixInfo ParseLine(
        string source,
        MarkdownSemanticSnapshot snapshot,
        int lineZero)
    {
        var start = snapshot.LineStarts[lineZero];
        var end = lineZero + 1 < snapshot.LineStarts.Length
            ? snapshot.LineStarts[lineZero + 1]
            : source.Length;
        while (end > start && source[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return MarkdownContainerPrefix.Parse(
            source[start..end],
            snapshot,
            start,
            end);
    }

    private static void Check(string source, double fontScale, string message)
    {
        using var editor = new RailEditor(source, fontScale);
        var box = editor.Box;
        var view = box.TextArea.TextView;
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var first = box.Document.GetLineByNumber(1);
        var second = box.Document.GetLineByNumber(2);
        var firstRails = GetRails(editor.Presentation, view, box.Document, snapshot, first);
        var secondRails = GetRails(editor.Presentation, view, box.Document, snapshot, second);

        if (firstRails.Length != 1 || secondRails.Length != 1)
        {
            throw new InvalidOperationException(
                $"FAIL quote rail alignment: {message}: rail counts {firstRails.Length}/{secondRails.Length}");
        }

        var delta = Math.Abs(firstRails[0] - secondRails[0]);
        if (delta > 0.25)
        {
            throw new InvalidOperationException(
                $"FAIL quote rail alignment: {message}: X {firstRails[0]:F3}/{secondRails[0]:F3}, delta {delta:F3}px");
        }
    }

    private static double[] GetRails(
        MarkdownSemanticPresentation presentation,
        TextView view,
        IDocument document,
        MarkdownSemanticSnapshot snapshot,
        DocumentLine line)
    {
        var field = typeof(MarkdownSemanticPresentation).GetField(
            "_backgroundRenderer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FAIL quote rail alignment: background renderer field missing");
        var renderer = field.GetValue(presentation)
            ?? throw new InvalidOperationException("FAIL quote rail alignment: background renderer missing");
        var method = renderer.GetType().GetMethod(
            "GetQuoteRailXs",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FAIL quote rail alignment: GetQuoteRailXs missing");
        var result = method.Invoke(
            renderer,
            new object[] { view, document, snapshot, line, presentation.ZoomFactor(), 1.0 });
        return result as double[]
            ?? throw new InvalidOperationException("FAIL quote rail alignment: unexpected rail result");
    }

    private sealed class RailEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;

        public RailEditor(string source, double fontScale)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.FontSize = Math.Max(1, Box.FontSize * fontScale);
            Box.SetMarkdownEditAnimationEnabled(false);
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            Presentation = new MarkdownSemanticPresentation(Box, _document);
            Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            Box.SetPreviewMode(true);

            Box.ApplyTemplate();
            Box.Measure(new Size(480, 240));
            Box.Arrange(new Rect(0, 0, 480, 240));
            Box.UpdateLayout();
            var view = Box.TextArea.TextView;
            view.Measure(new Size(480, 240));
            view.Arrange(new Rect(0, 0, 480, 240));
            view.EnsureVisualLines();
        }

        public MarkdownTextBox Box { get; }
        public MarkdownSemanticPresentation Presentation { get; }

        public void Dispose()
        {
            Presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
