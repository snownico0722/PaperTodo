using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static class TransientSelectionRevealChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckCollapsedSourceRevealIsScoped();
        CheckWrappedSelectionKeepsExactColumn();
        Console.WriteLine("PASS transient find reveal");
    }

    private static void CheckCollapsedSourceRevealIsScoped()
    {
        const string source = "[label](https://needle.example) tail";
        using var editor = new PreviewEditor(source, width: 800, height: 180);
        editor.Layout();

        var tailOffset = source.IndexOf("tail", StringComparison.Ordinal);
        var initialTailX = XAtOffset(editor.Box, tailOffset);

        var labelOffset = source.IndexOf("label", StringComparison.Ordinal);
        editor.Presentation.SetTransientFindReveal(labelOffset, "label".Length);
        editor.Layout();
        Near(
            initialTailX,
            XAtOffset(editor.Box, tailOffset),
            "visible link text does not expand hidden syntax");

        var needleOffset = source.IndexOf("needle", StringComparison.Ordinal);
        editor.Presentation.SetTransientFindReveal(needleOffset, "needle".Length);
        editor.Layout();
        var revealedTailX = XAtOffset(editor.Box, tailOffset);
        if (revealedTailX <= initialTailX + 20)
        {
            throw new InvalidOperationException(
                $"FAIL transient find reveal: hidden link destination did not expand: {initialTailX:F2} -> {revealedTailX:F2}");
        }

        editor.Presentation.SetTransientFindReveal(null, 0);
        editor.Layout();
        Near(
            initialTailX,
            XAtOffset(editor.Box, tailOffset),
            "clearing find reveal restores normal Full preview layout");
    }

    private static void CheckWrappedSelectionKeepsExactColumn()
    {
        var source = string.Join(" ", Enumerable.Repeat("wrapped", 80)) + " needle";
        using var editor = new PreviewEditor(source, width: 180, height: 90);
        editor.Layout();

        var needleOffset = source.LastIndexOf("needle", StringComparison.Ordinal);
        var target = PaperWindow.GetBuiltInFindScrollTarget(editor.Box, needleOffset);
        if (target.Line != 1 || target.Column != needleOffset + 1)
        {
            throw new InvalidOperationException(
                $"FAIL transient find reveal: exact scroll target {target.Line}:{target.Column} != 1:{needleOffset + 1}");
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
                $"FAIL transient find reveal: {message}: {expected:F2} != {actual:F2}");
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

    private sealed class PreviewEditor : IDisposable
    {
        private readonly MarkdownSemanticDocument _document;
        private readonly double _width;
        private readonly double _height;

        public PreviewEditor(string source, double width, double height)
        {
            _width = width;
            _height = height;
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
            Box.Measure(new Size(_width, _height));
            Box.Arrange(new Rect(0, 0, _width, _height));
            Box.UpdateLayout();
            var view = Box.TextArea.TextView;
            view.Measure(new Size(_width, _height));
            view.Arrange(new Rect(0, 0, _width, _height));
            view.EnsureVisualLines();
            Pump();
        }

        public void Dispose()
        {
            Presentation.SetTransientFindReveal(null, 0);
            Presentation.Dispose();
            Box.SetSemanticDocument(null);
            _document.Dispose();
        }
    }
}
