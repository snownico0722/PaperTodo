using System.Runtime.CompilerServices;
using PaperTodo;

internal static class MixedNestedListEnterChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check(
            "- outer\n  1. inner",
            "  2. ",
            "multiline unordered parent with ordered child");
        Check(
            "1. outer\n   - inner",
            "   - ",
            "multiline ordered parent with unordered child");
        Check(
            "1. outer\n   - [ ] inner",
            "   - [ ] ",
            "multiline ordered parent with task-list child");
        Check(
            "- outer\n  - inner",
            "  - ",
            "multiline unordered parent with unordered child");
        Check(
            "1. outer\n   1. inner",
            "   2. ",
            "multiline ordered parent with ordered child");

        Console.WriteLine("PASS multiline nested list Enter ownership");
    }

    private static void Check(string source, string continuation, string message)
    {
        using var editor = new EnterEditor(source);
        editor.Box.CaretOffset = editor.Box.Text.Length;
        if (!editor.Box.TryHandleSemanticEnter())
        {
            throw new InvalidOperationException(
                $"FAIL multiline nested list Enter: {message}: Enter was not handled");
        }

        var expected = source + Environment.NewLine + continuation;
        if (!string.Equals(expected, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL multiline nested list Enter: {message}: expected '{Escape(expected)}', actual '{Escape(editor.Box.Text)}'");
        }

        if (editor.Box.CaretOffset != editor.Box.Text.Length)
        {
            throw new InvalidOperationException(
                $"FAIL multiline nested list Enter: {message}: caret {editor.Box.CaretOffset}, length {editor.Box.Text.Length}");
        }

        editor.Box.Undo();
        if (!string.Equals(source, editor.Box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL multiline nested list Enter: {message}: one undo did not restore the source");
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
