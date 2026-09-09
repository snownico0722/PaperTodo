using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class MarkerSlotEdgeChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckListContainedQuoteAlignment("-");
        CheckListContainedQuoteAlignment("*");
        CheckListContainedQuoteAlignment("+");
        CheckTaskMarkerCaretStops();
        CheckMetricKeyTracksTextFormattingMode();
        Console.WriteLine("PASS marker slot edge cases");
    }

    private static void CheckListContainedQuoteAlignment(string marker)
    {
        var source = $"{marker} > a\n  > b";
        using var editor = new EdgeEditor(source);
        editor.Layout();
        var first = source.IndexOf('a');
        var second = source.IndexOf('b');
        Near(
            XAtOffset(editor.Box, first),
            XAtOffset(editor.Box, second),
            $"{marker} list-contained quote keeps physical continuation content aligned");
    }

    private static void CheckTaskMarkerCaretStops()
    {
        const string source = "- [x] task";
        using var editor = new EdgeEditor(source);
        editor.Layout();
        var box = editor.Box;
        var line = box.Document.GetLineByNumber(1);
        var visual = box.TextArea.TextView.GetOrConstructVisualLine(line);

        var forwardOffsets = new List<int>();
        var column = -1;
        var prefixEndColumn = -1;
        for (var guard = 0; guard < 32; guard++)
        {
            column = visual.GetNextCaretPosition(
                column,
                LogicalDirection.Forward,
                CaretPositioningMode.Normal,
                allowVirtualSpace: false);
            if (column < 0)
            {
                break;
            }

            var offset = visual.GetRelativeOffset(column);
            if (offset <= 6)
            {
                forwardOffsets.Add(offset);
            }
            if (offset == 6)
            {
                prefixEndColumn = column;
                break;
            }
        }

        EqualOffsets(
            forwardOffsets,
            [0, 1, 2, 3, 4, 5, 6],
            "task/list marker cells expose every forward caret boundary");
        if (prefixEndColumn < 0)
        {
            throw new InvalidOperationException(
                "FAIL marker slot caret: could not resolve prefix end visual column");
        }

        var backwardOffsets = new List<int>();
        column = prefixEndColumn + 1;
        for (var guard = 0; guard < 32; guard++)
        {
            column = visual.GetNextCaretPosition(
                column,
                LogicalDirection.Backward,
                CaretPositioningMode.Normal,
                allowVirtualSpace: false);
            if (column < 0)
            {
                break;
            }

            var offset = visual.GetRelativeOffset(column);
            if (offset <= 6)
            {
                backwardOffsets.Add(offset);
            }
            if (offset == 0)
            {
                break;
            }
        }

        EqualOffsets(
            backwardOffsets,
            [6, 5, 4, 3, 2, 1, 0],
            "task/list marker cells expose every backward caret boundary");
    }

    private static void CheckMetricKeyTracksTextFormattingMode()
    {
        using var editor = new EdgeEditor("+ item");
        editor.Layout();
        var field = typeof(MarkdownSemanticPresentation).GetField(
            "_markerSlotMetricKey",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "FAIL marker slot metric key: cache field missing");
        var key = field.GetValue(editor.Presentation)
            ?? throw new InvalidOperationException(
                "FAIL marker slot metric key: metrics were not populated");
        var property = key.GetType().GetProperty(
            "TextFormattingMode",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "FAIL marker slot metric key: TextFormattingMode is not part of the cache key");
        var actual = property.GetValue(key);
        if (actual is not TextFormattingMode mode || mode != AppTypography.TextFormattingMode)
        {
            throw new InvalidOperationException(
                $"FAIL marker slot metric key: formatting mode {actual} != {AppTypography.TextFormattingMode}");
        }
    }

    private static double XAtOffset(MarkdownTextBox box, int offset)
    {
        var view = box.TextArea.TextView;
        var line = box.Document.GetLineByOffset(offset);
        var indexInLine = Math.Clamp(offset - line.Offset, 0, line.Length);
        var point = view.GetVisualPosition(
            new TextViewPosition(line.LineNumber, indexInLine + 1),
            VisualYPosition.TextMiddle);
        return point.X - view.HorizontalOffset;
    }

    private static void Near(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.35)
        {
            throw new InvalidOperationException(
                $"FAIL marker slot edge: {message}: {expected:F2} != {actual:F2}");
        }
    }

    private static void EqualOffsets(
        IReadOnlyList<int> actual,
        IReadOnlyList<int> expected,
        string message)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"FAIL marker slot caret: {message}: [{string.Join(",", actual)}]");
        }
        for (var index = 0; index < expected.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                throw new InvalidOperationException(
                    $"FAIL marker slot caret: {message}: [{string.Join(",", actual)}]");
            }
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class EdgeEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;

        public EdgeEditor(string source)
        {
            Box = new MarkdownTextBox { Text = source };
            Box.SetMarkdownEditAnimationEnabled(false);
            _document = new MarkdownSemanticDocument(Box.Document);
            Box.SetSemanticDocument(_document);
            Presentation = new MarkdownSemanticPresentation(Box, _document);
            Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            Box.SetPreviewMode(true);
        }

        public MarkdownTextBox Box { get; }
        public MarkdownSemanticPresentation Presentation { get; }

        public void Layout()
        {
            Box.ApplyTemplate();
            Box.Measure(new Size(800, 400));
            Box.Arrange(new Rect(0, 0, 800, 400));
            Box.UpdateLayout();
            var view = Box.TextArea.TextView;
            view.Measure(new Size(800, 400));
            view.Arrange(new Rect(0, 0, 800, 400));
            view.EnsureVisualLines();
            Pump();
        }

        public void Dispose()
        {
            Presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
