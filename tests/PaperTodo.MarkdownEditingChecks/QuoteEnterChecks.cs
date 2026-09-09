using System.Runtime.CompilerServices;
using PaperTodo;

internal static class QuoteEnterChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("- > a", "  > ", "unordered list + quote first row");
        Check("- > a\n  > b", "  > ", "unordered list + quote continuation row");
        Check("1. > a", "   > ", "ordered list + quote first row");
        Check("1. > a\n   > b", "   > ", "ordered list + quote continuation row");
        Check("10. > a", "    > ", "multi-digit ordered list keeps content indent");
        Check("10. > a\n    > b", "    > ", "wide ordered list continuation keeps content indent");
        Check("> - a", "> - ", "inner list owns Enter inside outer quote");
        Check("> - > a", ">   > ", "inner quote owns Enter after quote/list containers");

        Check("- > - item", "  > - ", "deep unordered list owns Enter");
        Check("1. > 9. item", "   > 10. ", "deep ordered list increments itself");
        Check("> - > - item", ">   > - ", "alternating containers continue innermost list");
        Check("- > - [ ] item", "  > - [ ] ", "nested task list continues at inner level");
        CheckEmpty("- > - ", "- > ", "empty nested list exits only inner marker");
        CheckEmpty("- > - [ ] ", "- > ", "empty nested task exits inner marker and task cell");

        Console.WriteLine("PASS quote/list Enter continuation ownership");
    }

    private static void Check(string source, string continuation, string message)
    {
        using var editor = new EnterEditor(source);
        editor.Box.CaretOffset = editor.Box.Text.Length;
        if (!editor.Box.TryHandleSemanticEnter())
        {
            throw new InvalidOperationException($"FAIL quote/list Enter: {message}: Enter was not handled");
        }

        var expected = source + Environment.NewLine + continuation;
        if (!string.Equals(expected, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote/list Enter: {message}: expected '{Escape(expected)}', actual '{Escape(editor.Box.Text)}'");
        }

        if (editor.Box.CaretOffset != editor.Box.Text.Length)
        {
            throw new InvalidOperationException(
                $"FAIL quote/list Enter: {message}: caret {editor.Box.CaretOffset}, length {editor.Box.Text.Length}");
        }

        editor.Box.Undo();
        if (!string.Equals(source, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote/list Enter: {message}: one undo did not restore the source");
        }
    }

    private static void CheckEmpty(string source, string expected, string message)
    {
        using var editor = new EnterEditor(source);
        editor.Box.CaretOffset = editor.Box.Text.Length;
        if (!editor.Box.TryHandleSemanticEnter())
        {
            throw new InvalidOperationException($"FAIL quote/list Enter: {message}: Enter was not handled");
        }
        if (!string.Equals(expected, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote/list Enter: {message}: expected '{Escape(expected)}', actual '{Escape(editor.Box.Text)}'");
        }

        editor.Box.Undo();
        if (!string.Equals(source, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote/list Enter: {message}: one undo did not restore the source");
        }
    }

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class EnterEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;

        public EnterEditor(string source)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.Document.UndoStack.ClearAll();
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
        }

        public MarkdownTextBox Box { get; }

        public void Dispose()
        {
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
