using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    [STAThread]
    private static int Main()
    {
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = 0;
        Check("Full mode preserves quote source and undo history", () =>
        {
            foreach (var source in new[] { "- > a\n  > b", "1. > a\n   > b", "> a\nb", "> a\n> > b\nlazy" })
            {
                foreach (var fullBeforeSemantics in new[] { false, true })
                {
                    using var editor = new Editor(source, fullBeforeSemantics);
                    Pump();
                    Equal(source, editor.Box.Text, "loading Full keeps source");
                    editor.Box.SetPreviewMode(true);
                    editor.Box.SetMarkdownRenderMode(MarkdownRenderModes.Enhanced);
                    editor.Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
                    editor.Box.SetPreviewMode(false);
                    Pump();
                    Equal(source, editor.Box.Text, "presentation changes keep source");
                    Require(!editor.Box.CanUndo, "presentation must not add an undo operation");
                }
            }
        });

        Check("Multiline quote insertion has one undo and redo", () =>
        {
            using var editor = new Editor("");
            const string source = "> a\nb";
            editor.Box.TextArea.PerformTextInput(source);
            Pump();
            Equal(source, editor.Box.Text, "inserted Markdown stays exact");
            editor.Box.Undo();
            Pump();
            Equal("", editor.Box.Text, "one undo removes the insertion");
            Require(!editor.Box.CanUndo, "no extra normalization undo operation");
            editor.Box.Redo();
            Pump();
            Equal(source, editor.Box.Text, "redo restores the exact insertion");
        });

        Check("Explicit Enter still continues a quote in one undo group", () =>
        {
            using var editor = new Editor("> a\n> b");
            editor.Box.CaretOffset = editor.Box.Text.Length;
            Require(editor.Box.TryHandleSemanticEnter(), "Enter handled");
            Pump();
            Equal("> a\n> b" + Environment.NewLine + "> ", editor.Box.Text, "Enter continues quote");
            Equal(editor.Box.Text.Length, editor.Box.CaretOffset, "caret follows prefix");
            editor.Box.Undo();
            Pump();
            Equal("> a\n> b", editor.Box.Text, "one undo removes the newline and prefix");
        });

        Console.WriteLine($"Markdown editing checks: {failures} failure(s).");
        return failures == 0 ? 0 : 1;

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, actual {actual}");

    private static void Near(double expected, double actual, string message) =>
        Require(Math.Abs(expected - actual) < 0.01, $"{message}: expected {expected:F2}, actual {actual:F2}");

    private sealed class Editor : IDisposable
    {
        public MarkdownTextBox Box { get; }
        private readonly MarkdownSemanticDocument _document;
        private readonly MarkdownSemanticPresentation _presentation;
        public MarkdownSemanticPresentation Presentation => _presentation;

        public Editor(string source, bool fullBeforeSemantics = false)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.Document.UndoStack.ClearAll();
            Box.SetMarkdownEditAnimationEnabled(false);
            if (fullBeforeSemantics) Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            _presentation = new MarkdownSemanticPresentation(Box, _document);
            if (!fullBeforeSemantics) Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
        }

        public void Dispose()
        {
            _presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
