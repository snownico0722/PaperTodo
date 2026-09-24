using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using PaperTodo;

internal static partial class Program
{
    private static void RunLocalRedrawChecks(Action<string, Action> check)
    {
        const string blocks = "plain\n\nprefix **one** suffix\n\n# heading\n\n[b](https://example.com/a\\*b)\n\n> quote\n\n";
        var source = blocks + string.Concat(Enumerable.Repeat("unaffected text\n", 30));
        check("Caret changes and pending deletion/undo match a full redraw", () =>
        {
            using var viewport = new Viewport(source);
            foreach (var label in new[] { "one", "heading", "https" })
            {
                viewport.Box.CaretOffset = source.IndexOf(label, StringComparison.Ordinal);
                viewport.Flush(); viewport.AssertFullRender(label);
            }
            viewport.Box.CaretOffset = source.IndexOf("one", StringComparison.Ordinal);
            viewport.Box.CaretOffset = source.IndexOf("quote", StringComparison.Ordinal);
            viewport.Box.Document.Remove(0, blocks.Length);
            viewport.Flush(); viewport.AssertFullRender("deletion with pending redraw");
            viewport.Box.Undo(); viewport.Flush();
            Equal(source, viewport.Box.Text, "undo restores text after pending redraw");
            viewport.AssertFullRender("undo");
        });
        check("Single and multiline fades preserve current pixels", () =>
        {
            foreach (var syntax in new[] { "**one**", "**first\nsecond**", "[first\nsecond](https://example.com)" })
            {
                using var viewport = new Viewport("plain\n\n" + syntax + "\n\n" + source);
                viewport.Box.CaretOffset = 9; viewport.Flush();
                viewport.Box.SetMarkdownEditAnimationEnabled(true); viewport.Flush();
                var first = viewport.DrawFadeFrame(0.2); viewport.AssertFullRender("fade start");
                var last = viewport.DrawFadeFrame(1.0); viewport.AssertFullRender("fade end");
                Require(!first.SequenceEqual(last), "fade changes visible pixels");
                viewport.Box.SetMarkdownEditAnimationEnabled(false);
                viewport.Box.CaretOffset = 0; viewport.Flush(); viewport.AssertFullRender("fade exit");
            }
        });
        check("Pending redraw survives wrapped text and mode changes", () =>
        {
            var wrapped = "prefix **" + string.Concat(Enumerable.Repeat("long words ", 50)) + "** suffix\n\n" + source;
            using var viewport = new Viewport(wrapped);
            viewport.Box.CaretOffset = 12; viewport.Flush(); viewport.AssertFullRender("wrapped");
            viewport.Box.CaretOffset = 0; viewport.Box.SetPreviewMode(true);
            viewport.Flush(); viewport.AssertFullRender("preview");
            viewport.Box.SetPreviewMode(false); viewport.Box.CaretOffset = 12;
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Basic);
            viewport.Flush(); viewport.AssertFullRender("Basic");
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            viewport.Flush(); viewport.AssertFullRender("Full");
            viewport.Box.CaretOffset = 0;
        });
        Pump();
    }

    private sealed class Viewport : IDisposable
    {
        private readonly Editor _editor;
        private readonly HwndSource _host;
        public MarkdownTextBox Box => _editor.Box;
        public TextView View => Box.TextArea.TextView;

        public Viewport(string source)
        {
            _editor = new Editor(source);
            Box.WordWrap = true;
            _host = new HwndSource(new HwndSourceParameters("PaperTodo editing checks")
            {
                Width = 800, Height = 600, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000)
            });
            _host.RootVisual = Box;
            Flush();
            Require(View.IsVisible && View.VisualLines.Count > 1, "live WPF viewport is available");
        }

        public void Flush()
        {
            Pump();
            Box.ApplyTemplate();
            Box.Measure(new Size(800, 600));
            Box.Arrange(new Rect(0, 0, 800, 600));
            Box.UpdateLayout();
            View.EnsureVisualLines();
            Pump();
        }

        public byte[] DrawFadeFrame(double alpha)
        {
            // Drive fixed alpha values through the production redraw path without timer timing noise.
            var type = typeof(MarkdownSemanticPresentation);
            ((DispatcherTimer?)type.GetField("_fadeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_editor.Presentation))?.Stop();
            type.GetField("_fadeAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_editor.Presentation, alpha);
            type.GetMethod("RedrawRevealRange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action>(_editor.Presentation)();
            Flush();
            return RenderPixels();
        }

        public void AssertFullRender(string context)
        {
            var pixels = RenderPixels();
            var widths = View.VisualLines.SelectMany(line => line.TextLines).Select(line => line.WidthIncludingTrailingWhitespace).ToArray();
            View.Redraw(DispatcherPriority.Render);
            Flush();
            Require(pixels.SequenceEqual(RenderPixels()), $"{context}: local pixels match fresh full redraw");
            Require(widths.SequenceEqual(View.VisualLines.SelectMany(line => line.TextLines).Select(line => line.WidthIncludingTrailingWhitespace)),
                $"{context}: local widths match fresh full redraw");
        }

        private byte[] RenderPixels()
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(View.ActualWidth), (int)Math.Ceiling(View.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(View);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        public void Dispose()
        {
            _editor.Dispose();
            _host.Dispose();
            Pump();
        }
    }
}
