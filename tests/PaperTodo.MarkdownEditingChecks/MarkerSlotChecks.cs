using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class MarkerSlotChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var bulletXs = new[]
        {
            ContentX("- item", "item"),
            ContentX("* item", "item"),
            ContentX("+ item", "item")
        };
        Near(bulletXs[0], bulletXs[1], "unordered-list marker variants share one slot");
        Near(bulletXs[0], bulletXs[2], "unordered-list plus shares the bullet slot");

        var taskXs = new[]
        {
            ContentX("- [ ] task", "task"),
            ContentX("- [x] task", "task"),
            ContentX("- [X] task", "task")
        };
        Near(taskXs[0], taskXs[1], "unchecked and checked tasks share one slot");
        Near(taskXs[0], taskXs[2], "uppercase checked task shares the same slot");

        CheckRevealStability("- item", "item", "unordered-list slot survives active-block reveal");
        CheckRevealStability("> quote", "quote", "quote gutter survives active-block reveal");
        CheckRevealStability("- [x] task", "task", "task slot survives active-block reveal");

        Console.WriteLine("PASS fixed semantic marker slots");
    }

    private static double ContentX(string source, string content)
    {
        using var editor = new SlotEditor(source);
        Pump();
        return XAtOffset(editor.Box, source.IndexOf(content, StringComparison.Ordinal));
    }

    private static void CheckRevealStability(string source, string content, string message)
    {
        using var editor = new SlotEditor(source);
        var box = editor.Box;
        Pump();
        var contentOffset = source.IndexOf(content, StringComparison.Ordinal);
        var previewX = XAtOffset(box, contentOffset);

        box.SetPreviewMode(false);
        box.CaretOffset = contentOffset + Math.Min(1, content.Length);
        Pump();
        var activeX = XAtOffset(box, contentOffset);
        Near(previewX, activeX, message);
        if (!string.Equals(source, box.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FAIL marker slot: {message}: presentation changed source");
        }
        if (box.CanUndo)
        {
            throw new InvalidOperationException($"FAIL marker slot: {message}: presentation added undo history");
        }
    }

    private static double XAtOffset(MarkdownTextBox box, int offset)
    {
        box.ApplyTemplate();
        box.Measure(new Size(800, 400));
        box.Arrange(new Rect(0, 0, 800, 400));
        box.UpdateLayout();
        var view = box.TextArea.TextView;
        view.Measure(new Size(800, 400));
        view.Arrange(new Rect(0, 0, 800, 400));
        view.EnsureVisualLines();

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
                $"FAIL marker slot: {message}: {expected:F2} != {actual:F2}");
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

    private sealed class SlotEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;
        private readonly MarkdownSemanticPresentation _presentation;

        public SlotEditor(string source)
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
