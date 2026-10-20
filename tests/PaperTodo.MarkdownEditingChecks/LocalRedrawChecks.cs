using System.IO;
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
            var local = CapturePixels();
            var widths = View.VisualLines.SelectMany(line => line.TextLines)
                .Select(line => line.WidthIncludingTrailingWhitespace).ToArray();
            View.Redraw(DispatcherPriority.Render);
            Flush();
            var full = CapturePixels();
            var matches = local.Width == full.Width && local.Height == full.Height &&
                local.Pixels.SequenceEqual(full.Pixels);
            if (!matches)
            {
                // Keep the strict assertion. A diagnostic capture must never turn a
                // stale/incomplete render into a passing check via an extra dispatcher pump.
                try { SaveRedrawMismatch(context, local, full, widths); }
                catch (Exception error) { Console.Error.WriteLine("Markdown redraw evidence failed: " + error); }
            }
            Require(matches, $"{context}: local pixels match fresh full redraw");
            Require(widths.SequenceEqual(View.VisualLines.SelectMany(line => line.TextLines)
                .Select(line => line.WidthIncludingTrailingWhitespace)),
                $"{context}: local widths match fresh full redraw");
        }

        private sealed record PixelCapture(int Width, int Height, byte[] Pixels);

        private PixelCapture CapturePixels()
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(View.ActualWidth), (int)Math.Ceiling(View.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(View);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }

        private byte[] RenderPixels() => CapturePixels().Pixels;

        private void SaveRedrawMismatch(string context, PixelCapture local, PixelCapture full, double[] widths)
        {
            var root = Environment.GetEnvironmentVariable("PAPER_MARKDOWN_REDRAW_CAPTURE");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Path.GetTempPath(), "PaperTodo-MarkdownRedraw");
            var label = new string(context.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var folder = Path.Combine(root, label + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") +
                "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);

            var width = Math.Max(local.Width, full.Width);
            var height = Math.Max(local.Height, full.Height);
            var delta = new byte[checked(width * height * 4)];
            var changed = 0;
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            var maxChannelDelta = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var isDifferent = false;
                for (var c = 0; c < 4; c++)
                {
                    var a = x < local.Width && y < local.Height
                        ? local.Pixels[(y * local.Width + x) * 4 + c] : (byte)0;
                    var b = x < full.Width && y < full.Height
                        ? full.Pixels[(y * full.Width + x) * 4 + c] : (byte)0;
                    var difference = Math.Abs(a - b);
                    maxChannelDelta = Math.Max(maxChannelDelta, difference);
                    if (difference != 0) isDifferent = true;
                }
                if (!isDifferent) continue;
                changed++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                var offset = (y * width + x) * 4;
                delta[offset + 2] = 255; // Opaque red highlights every changed pixel.
                delta[offset + 3] = 255;
            }

            SavePng(local, Path.Combine(folder, "local.png"));
            SavePng(full, Path.Combine(folder, "full.png"));
            SavePng(new PixelCapture(width, height, delta), Path.Combine(folder, "difference.png"));

            // This is evidence only, after the original failure was already determined.
            // A later settled image distinguishes a transient pending render from a
            // repeatable pixel discrepancy; it cannot make the original assertion pass.
            Flush();
            var settled = CapturePixels();
            SavePng(settled, Path.Combine(folder, "after-extra-flush.png"));
            var settledMatchesFull = settled.Width == full.Width && settled.Height == full.Height &&
                settled.Pixels.SequenceEqual(full.Pixels);
            var currentWidths = View.VisualLines.SelectMany(line => line.TextLines)
                .Select(line => line.WidthIncludingTrailingWhitespace).ToArray();
            var bounds = changed == 0 ? "none" : $"({minX},{minY})-({maxX},{maxY})";
            var details = $"""
                Context: {context}
                Mode: {Box.MarkdownRenderMode}
                Preview: {Box.IsPreviewMode}
                CaretOffset: {Box.CaretOffset}
                LocalSize: {local.Width}x{local.Height}
                FullSize: {full.Width}x{full.Height}
                ChangedPixels: {changed}
                Bounds: {bounds}
                MaxChannelDifference: {maxChannelDelta}
                FullEqualsAfterExtraFlush: {settledMatchesFull}
                VisualLines: {View.VisualLines.Count}
                LineWidthsEqual: {widths.SequenceEqual(currentWidths)}
                OriginalLineWidths: {string.Join(", ", widths)}
                CurrentLineWidths: {string.Join(", ", currentWidths)}
                """;
            File.WriteAllText(Path.Combine(folder, "diagnostics.txt"), details);
            Console.Error.WriteLine("Markdown local/full redraw mismatch:\n" + details +
                "\nEvidence: " + folder);
        }

        private static void SavePng(PixelCapture frame, string path)
        {
            var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
                PixelFormats.Pbgra32, null, frame.Pixels, frame.Width * 4);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            png.Save(file);
        }

        public void Dispose()
        {
            _editor.Dispose();
            _host.Dispose();
            Pump();
        }
    }
}
