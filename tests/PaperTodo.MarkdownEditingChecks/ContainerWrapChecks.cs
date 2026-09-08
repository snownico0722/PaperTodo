using System.Runtime.CompilerServices;
using System.Windows;
using PaperTodo;

internal static class ContainerWrapChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("> ", 2, "quote");
        Check("- ", 2, "unordered list");
        Check("1. ", 3, "ordered list");
        Check("- > ", 4, "list then quote");
        Check("> - ", 4, "quote then list");
        Check("10. > ", 6, "wide ordered list then quote");
        Check("- [ ] ", 6, "task list");
        Console.WriteLine("PASS container prefixes participate in soft-wrap indentation");
    }

    private static void Check(string prefix, int expectedLength, string message)
    {
        var source = prefix + string.Concat(Enumerable.Repeat("long content ", 20));
        using var editor = new WrapEditor(source);
        var box = editor.Box;
        box.ApplyTemplate();
        box.Measure(new Size(150, 600));
        box.Arrange(new Rect(0, 0, 150, 600));
        box.UpdateLayout();
        var view = box.TextArea.TextView;
        view.Measure(new Size(150, 600));
        view.Arrange(new Rect(0, 0, 150, 600));
        view.EnsureVisualLines();
        var visual = view.GetOrConstructVisualLine(box.Document.GetLineByNumber(1));
        if (visual.TextLines.Count < 2)
        {
            throw new InvalidOperationException($"FAIL container wrap: {message}: text did not wrap");
        }

        var element = visual.Elements.FirstOrDefault(candidate =>
            candidate.RelativeTextOffset == 0 &&
            candidate.DocumentLength == expectedLength);
        if (element == null)
        {
            throw new InvalidOperationException(
                $"FAIL container wrap: {message}: no {expectedLength}-character prefix element");
        }

        for (var column = element.VisualColumn;
             column < element.VisualColumn + element.VisualLength;
             column++)
        {
            if (!element.IsWhitespace(column))
            {
                throw new InvalidOperationException(
                    $"FAIL container wrap: {message}: prefix column {column} is not indentation");
            }
        }
    }

    private sealed class WrapEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;
        private readonly MarkdownSemanticPresentation _presentation;

        public WrapEditor(string source)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.SetMarkdownEditAnimationEnabled(false);
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            _presentation = new MarkdownSemanticPresentation(Box, _document);
            Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            Box.SetPreviewMode(true);
        }

        public MarkdownTextBox Box { get; }

        public void Dispose()
        {
            _presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
