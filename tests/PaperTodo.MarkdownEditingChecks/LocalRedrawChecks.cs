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

        check("Lazy quote continuation uses presentation-only indentation", () =>
        {
            foreach (var test in new[]
            {
                (Source: "> a\nb", ExplicitContent: 2, LazyContent: 4),
                (Source: "> > a\nlazy", ExplicitContent: 4, LazyContent: 6),
                (Source: "> **a**\n**b**", ExplicitContent: 4, LazyContent: 10)
            })
            {
                using var viewport = new Viewport(test.Source);
                viewport.Box.SetPreviewMode(true);
                viewport.Flush();
                var first = viewport.Box.Document.GetLineByNumber(1);
                var explicitX = viewport.XAtOffset(first.Offset + test.ExplicitContent);
                var lazyX = viewport.XAtOffset(test.LazyContent);
                Near(explicitX, lazyX, $"{test.Source}: lazy quote content aligns with explicit marker content");
                Equal(test.Source, viewport.Box.Text, "quote indentation preserves source");
                Require(!viewport.Box.CanUndo, "quote indentation does not create undo history");

                viewport.Box.SetPreviewMode(false);
                viewport.Box.CaretOffset = test.LazyContent;
                viewport.Flush();
                Equal(test.Source, viewport.Box.Text, "editing keeps quote source exact");
                Require(!viewport.Box.CanUndo, "editing presentation does not create undo history");
            }
        });

        check("Single-line caret redraw preserves unrelated visual lines and matches full rendering", () =>
        {
            using var viewport = new Viewport(source);
            foreach (var offset in new[] {
                source.IndexOf("one", StringComparison.Ordinal), 0,
                source.IndexOf("heading", StringComparison.Ordinal),
                source.IndexOf("https", StringComparison.Ordinal),
                source.IndexOf("quote", StringComparison.Ordinal), 0
            })
            {
                var unaffected = viewport.View.VisualLines.First(line => line.FirstDocumentLine.LineNumber >= 18);
                var beforeLines = viewport.View.VisualLines.ToArray();
                viewport.Box.CaretOffset = offset;
                viewport.Flush();
                Require(viewport.View.VisualLines.Contains(unaffected), $"distant line stays cached after caret move to {offset}, line {unaffected.FirstDocumentLine.LineNumber}, reused {viewport.View.VisualLines.Count(line => beforeLines.Contains(line))}/{viewport.View.VisualLines.Count}");
                viewport.AssertFullRender($"caret {offset}");
            }

            var before = viewport.View.VisualLines.ToArray();
            viewport.Box.CaretOffset = 1;
            viewport.Flush();
            Require(before.SequenceEqual(viewport.View.VisualLines), "plain-text caret movement needs no line rebuild");
        });

        check("Far-apart single-line caret redraw preserves middle visual lines", () =>
        {
            var farSource =
                "**top**\n" +
                string.Concat(Enumerable.Repeat("middle text\n", 12)) +
                "# bottom";
            using var viewport = new Viewport(farSource);
            viewport.Box.CaretOffset = farSource.IndexOf("top", StringComparison.Ordinal);
            viewport.Flush();
            var middle = viewport.View.VisualLines.First(
                line => line.FirstDocumentLine.LineNumber == 7);

            viewport.Box.CaretOffset = farSource.IndexOf("bottom", StringComparison.Ordinal);
            viewport.Flush();
            Require(
                viewport.View.VisualLines.Contains(middle),
                "distant old/new caret lines do not invalidate the visual lines between them");
            viewport.AssertFullRender("far-apart caret jump");
        });

        check("Queued caret moves coalesce and text edits supersede old redraw offsets", () =>
        {
            using var viewport = new Viewport(source);
            var unaffected = viewport.View.VisualLines.First(line => line.FirstDocumentLine.LineNumber >= 18);
            viewport.Box.CaretOffset = source.IndexOf("one", StringComparison.Ordinal);
            viewport.Box.CaretOffset = source.IndexOf("heading", StringComparison.Ordinal);
            viewport.Box.CaretOffset = source.IndexOf("https", StringComparison.Ordinal);
            viewport.Flush();
            Require(viewport.View.VisualLines.Contains(unaffected), "coalesced local requests preserve distant line");
            viewport.AssertFullRender("rapid caret moves");

            viewport.Box.CaretOffset = source.IndexOf("one", StringComparison.Ordinal);
            viewport.Box.Document.Remove(0, blocks.Length);
            viewport.Flush();
            viewport.AssertFullRender("deletion with a local redraw pending");
            viewport.Box.Undo();
            viewport.Flush();
            Equal(source, viewport.Box.Text, "undo restores text after queued redraw");
            viewport.AssertFullRender("undo");
        });

        check("Single-line fade frames match full rendering without rebuilding distant lines", () =>
        {
            foreach (var syntax in new[] { "**one**", "[b](https://example.com/a\\*b)", "# heading", "> quote", "- [ ] task", "---" })
            {
                using var viewport = new Viewport("plain\n\n" + syntax + "\n\n" + source);
                viewport.Box.CaretOffset = 9;
                viewport.Flush();
                viewport.Box.SetMarkdownEditAnimationEnabled(true);
                viewport.Flush();
                byte[]? previousPixels = null;
                foreach (var alpha in new[] { 0.2, 0.7, 1.0 })
                {
                    var unaffected = viewport.View.VisualLines.First(line => line.FirstDocumentLine.LineNumber >= 18);
                    var pixels = viewport.DrawFadeFrame(alpha);
                    Require(previousPixels == null || !previousPixels.SequenceEqual(pixels), "fade advances the rendered pixels");
                    previousPixels = pixels;
                    Require(viewport.View.VisualLines.Contains(unaffected), "single-line fade retains distant line");
                    viewport.AssertFullRender($"{syntax} alpha {alpha}");
                }
            }
        });

        check("Cross-line syntax retains full redraw and both fade endpoints stay current", () =>
        {
            foreach (var syntax in new[] { "**first\nsecond**", "~~first\nsecond~~", "[first\nsecond](https://example.com)" })
            {
                using var viewport = new Viewport("plain\n\n" + syntax + "\n\n" + source);
                var before = viewport.View.VisualLines.ToArray();
                viewport.Box.CaretOffset = 7 + syntax.IndexOf("first", StringComparison.Ordinal) + 1;
                viewport.Flush();
                Require(viewport.View.VisualLines.All(line => !before.Contains(line)), $"{syntax}: cross-line entry uses full fallback, rebuilt {viewport.View.VisualLines.Count(line => !before.Contains(line))}/{viewport.View.VisualLines.Count}, caret {viewport.Box.CaretOffset}");
                viewport.AssertFullRender("cross-line entry");
                viewport.Box.SetMarkdownEditAnimationEnabled(true);
                viewport.Flush();
                foreach (var alpha in new[] { 0.2, 0.7, 1.0 })
                {
                    before = viewport.View.VisualLines.ToArray();
                    viewport.DrawFadeFrame(alpha);
                    Require(viewport.View.VisualLines.All(line => !before.Contains(line)), "cross-line fade uses full fallback");
                    viewport.AssertFullRender($"cross-line alpha {alpha}");
                }
                viewport.Box.SetMarkdownEditAnimationEnabled(false);
                viewport.Box.CaretOffset = 0;
                viewport.Flush();
                viewport.AssertFullRender("cross-line exit");
            }
        });

        check("Pending local redraw survives preview, mode and wrapped-line changes", () =>
        {
            var wrapped = "prefix **" + string.Concat(Enumerable.Repeat("long words ", 50)) + "** suffix\n\n" + source;
            using var viewport = new Viewport(wrapped);
            viewport.Box.CaretOffset = 12;
            viewport.Flush();
            viewport.AssertFullRender("wrapped source line");
            viewport.Box.CaretOffset = 0;
            viewport.Box.SetPreviewMode(true);
            viewport.Flush();
            viewport.AssertFullRender("preview with pending redraw");
            viewport.Box.SetPreviewMode(false);
            viewport.Box.CaretOffset = 12;
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Enhanced);
            viewport.Flush();
            viewport.AssertFullRender("mode switch with pending redraw");
            viewport.Box.SetMarkdownRenderMode(MarkdownRenderModes.Full);
            viewport.Flush();
            viewport.AssertFullRender("return to Full");
            viewport.Box.CaretOffset = 0;
            viewport.Flush();
            viewport.Box.BeginCaretRevealGesture();
            viewport.Box.CaretOffset = 12;
            viewport.Flush();
            viewport.AssertFullRender("frozen gesture");
            viewport.Box.EndCaretRevealGesture();
            viewport.Flush();
            viewport.AssertFullRender("gesture release");
            viewport.Box.CaretOffset = 0;
            // Disposal must safely cancel the pending presentation callback.
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
        }

        public double XAtOffset(int offset)
        {
            var clamped = Math.Clamp(offset, 0, Box.Document.TextLength);
            var line = Box.Document.GetLineByOffset(clamped);
            var point = View.GetVisualPosition(
                new TextViewPosition(
                    line.LineNumber,
                    clamped - line.Offset + 1),
                VisualYPosition.TextTop);
            return point.X - View.HorizontalOffset;
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
