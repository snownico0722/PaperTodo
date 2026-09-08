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

        Check("WPF collapse restores link and emphasis widths after editing", () =>
        {
            foreach (var syntax in new[] {
                "[b](https://example.com/a\\*b)",
                "[b](https://example.com \"a\\*b\")",
                "**abc**"
            })
            {
                var source = syntax + "\n\nplain";
                using var editor = new Editor(source);
                var box = editor.Box;
                box.SetPreviewMode(true);
                Pump();
                var initialWidth = FirstLineWidth(box);
                box.BeginCaretRevealGesture();
                box.SetPreviewMode(false);
                box.CaretOffset = 1;
                Near(initialWidth, FirstLineWidth(box), "entry gesture freezes layout");
                box.EndCaretRevealGesture();
                Pump();
                var revealedWidth = FirstLineWidth(box);
                Require(revealedWidth > initialWidth, $"editing {syntax} reveals syntax: {initialWidth:F2} -> {revealedWidth:F2}, caret {box.CaretOffset}");
                box.CaretOffset = source.Length;
                Pump();
                Near(initialWidth, FirstLineWidth(box), "leaving restores initial width");
                box.SetMarkdownRenderMode(MarkdownRenderModes.Enhanced);
                Pump();
                Require(FirstLineWidth(box) > initialWidth, "Enhanced shows source syntax");
                box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
                Pump();
                Near(initialWidth, FirstLineWidth(box), "returning to Full restores width");
                Equal(source, box.Text, "rendering preserves source");
            }
        });

        Check("Link escapes have nonoverlapping collapse runs in both caret directions", () =>
        {
            foreach (var syntax in new[] {
                "[b](https://example.com/a\\*b)",
                "[b](https://example.com \"a\\*b\")",
                "[a\\*b](https://example.com)",
                "[b](https://example.com/a\\*b\n \"title\")",
                "[b][a\\*b]\n\n[a\\*b]: https://example.com",
                "<https://example.com/a\\*b>"
            })
            {
                var source = syntax + "\n\nplain";
                var snapshot = MarkdownSemanticSnapshot.Parse(source);
                var table = MarkdownCollapseTable.Build(snapshot, source, MarkdownCaretReveal.None);
                WalkCaret(source, snapshot, table, 0, source.Length);
            }
        });

        Check("Link label escapes retain their independent collapse cell", () =>
        {
            const string source = "[a\\*b](https://example.com)";
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var escape = source.IndexOf('\\');
            var runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(snapshot, source, MarkdownCaretReveal.None);
            Require(runs.Any(run => run.Start == escape && run.End == escape + 1), "label escape remains collapsed");
            runs = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(snapshot, source, new MarkdownCaretReveal(escape + 1, 0));
            Require(!runs.Any(run => run.Start == escape), "editing label escape reveals its backslash");
        });

        Check("Local rebase keeps link escape ownership after edits", () =>
        {
            var prefix = string.Concat(Enumerable.Repeat("ordinary paragraph\n\n", 100));
            const string syntax = "[b](https://example.com/a\\*b)";
            var source = prefix + syntax + "\n\n" + prefix;
            var snapshot = MarkdownSemanticSnapshot.Parse(source);
            var table = MarkdownCollapseTable.Build(snapshot, source, MarkdownCaretReveal.None);
            var changed = source.Insert(prefix.Length + syntax.IndexOf("/a", StringComparison.Ordinal) + 2, "x");
            Require(MarkdownSemanticSnapshot.TryParseIncrementalLocal(source, snapshot, changed, out var next, out var window), "local parse succeeds");
            Require(window.NewEnd - window.NewStart < changed.Length, "parse uses a partial window");
            table = table.Rebase(next, changed, window, MarkdownCaretReveal.None);
            Require(table != null, "local rebase succeeds");
            WalkCaret(changed, next, table!, prefix.Length - 1, prefix.Length + syntax.Length + 3);
        });

        Console.WriteLine($"Markdown editing checks: {failures} failure(s).");
        return failures == 0 ? 0 : 1;

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
    }

    private static void WalkCaret(string source, MarkdownSemanticSnapshot snapshot, MarkdownCollapseTable table, int start, int end)
    {
        AssertRuns(MarkdownCaretReveal.None);
        for (var offset = start; offset <= end; offset++) MoveTo(offset);
        for (var offset = end; offset >= start; offset--) MoveTo(offset);
        table.SyncTo(MarkdownCaretReveal.None);
        AssertRuns(MarkdownCaretReveal.None);

        void MoveTo(int offset)
        {
            var caret = new MarkdownCaretReveal(offset, MarkdownSemanticCollapseLayout.FindLine(snapshot.LineStarts, offset));
            table.SyncTo(caret);
            AssertRuns(caret);
        }

        void AssertRuns(MarkdownCaretReveal caret)
        {
            var expected = MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(snapshot, source, caret);
            Require(expected.SequenceEqual(table.Runs), $"incremental runs match full calculation at {caret.CaretOffset}");
            for (var index = 1; index < expected.Count; index++)
                Require(expected[index - 1].End <= expected[index].Start, $"collapse cells do not overlap at {caret.CaretOffset}");
        }
    }

    private static double FirstLineWidth(MarkdownTextBox box)
    {
        box.ApplyTemplate();
        box.Measure(new Size(800, 600));
        box.Arrange(new Rect(0, 0, 800, 600));
        box.UpdateLayout();
        var view = box.TextArea.TextView;
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        view.EnsureVisualLines();
        var line = view.GetOrConstructVisualLine(box.Document.GetLineByNumber(1));
        return line.TextLines.Sum(text => text.WidthIncludingTrailingWhitespace);
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
