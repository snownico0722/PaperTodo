using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class MarkerSlotTypographyCacheChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string source = "- ```\n  code\n  ```\n- item\n  continuation";
        using var editor = new SlotEditor(source);
        editor.Layout();

        var itemOffset = source.IndexOf("item", StringComparison.Ordinal);
        var continuationOffset = source.IndexOf("continuation", StringComparison.Ordinal);
        Near(
            XAtOffset(editor.Box, itemOffset),
            XAtOffset(editor.Box, continuationOffset),
            "normal list body stays aligned after a code-styled bullet populated the cache");

        Console.WriteLine("PASS marker slot typography cache");
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
                $"FAIL marker slot typography cache: {message}: {expected:F2} != {actual:F2}");
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
            _presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
