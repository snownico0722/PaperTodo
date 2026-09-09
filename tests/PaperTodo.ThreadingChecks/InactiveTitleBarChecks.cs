using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static void CheckInactiveTitleBarMask()
    {
        // Render a real WPF paper background, border, title, body and shadow. Checking only
        // the mask brush would miss the original bug: a visible ancestor behind the title.
        var host = new Grid { Background = Brushes.Transparent };
        var paper = new Border
        {
            Margin = new Thickness(8), Background = Brushes.White,
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 }
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(31) });
        shell.RowDefinitions.Add(new RowDefinition());
        shell.Children.Add(new Border { Background = Brushes.SteelBlue });
        var body = new Border { Background = Brushes.Bisque };
        Grid.SetRow(body, 1);
        shell.Children.Add(body);
        paper.Child = shell;
        host.Children.Add(paper);
        var mask = new InactiveTitleBarMask();

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var size in new[] { new Size(240, 180), new Size(310, 210) })
        {
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            var bodyPosition = body.TranslatePoint(new Point(), host);
            var bodySize = body.RenderSize;
            var paperPosition = paper.TranslatePoint(new Point(), host);
            var paperBounds = new Rect(paperPosition, paper.RenderSize);
            var cutoff = Math.Round(bodyPosition.Y * scale) / scale;
            var cornerRadius = paper.CornerRadius.TopLeft;
            mask.UpdateBounds(size, cutoff, paperBounds, cornerRadius);
            host.OpacityMask = null;
            var original = Render();
            host.OpacityMask = mask.MaskBrush;
            mask.SetOpacity(1, 0);
            var shown = Render();
            mask.SetOpacity(0, 0);
            var hidden = Render();
            var width = (int)Math.Ceiling(size.Width * scale);
            for (var y = 0; y < (int)Math.Round(cutoff * scale); y++)
            for (var x = 0; x < width; x++)
                Assert(hidden[(y * width + x) * 4 + 3] == 0, "hidden title/background/shadow still has alpha");

            // The hidden body now owns a rounded top aligned to the paper chrome rather than
            // exposing a full-width rectangular cross-section at the title boundary.
            Assert(mask.HeaderBottom == cutoff, "title hit-test boundary moved with rounded mask");
            var cornerX = (int)Math.Round((paperBounds.Left + 1) * scale);
            var topY = (int)Math.Round((cutoff + 1) * scale);
            var centerX = (int)Math.Round(size.Width * scale / 2);
            Assert(hidden[(topY * width + cornerX) * 4 + 3] == 0,
                "hidden title left a square paper corner");
            Assert(hidden[(topY * width + centerX) * 4 + 3] > 0,
                "rounded hidden paper top removed body center pixels");

            // Only the new rounded-corner band is allowed to differ. Below one corner radius,
            // body and side/bottom shadow pixels remain exactly fixed, and layout never moves.
            var stableY = (int)Math.Round((cutoff + cornerRadius + 1) * scale);
            var start = Math.Min(stableY, (int)Math.Ceiling(size.Height * scale) - 1) * width * 4;
            Assert(hidden.AsSpan(start).SequenceEqual(shown.AsSpan(start)), "body pixels changed below rounded title cutout");
            for (var index = start; index < hidden.Length; index++)
            {
                var alpha = original[index - index % 4 + 3];
                var tolerance = alpha == 255 ? 0 : 1;
                Assert(Math.Abs(hidden[index] - original[index]) <= tolerance,
                    $"body rendering changed at scale {scale}, byte {index}");
            }
            Assert(body.TranslatePoint(new Point(), host) == bodyPosition && body.RenderSize == bodySize,
                "title hiding changed body layout");
            mask.SetOpacity(0.5, 0);
            var middle = Render();
            var titlePixel = ((int)(20 * scale) * width + (int)(80 * scale)) * 4 + 3;
            Assert(middle[titlePixel] is >= 126 and <= 129, "whole title strip did not fade uniformly");
            mask.SetOpacity(1, 0, () => host.OpacityMask = null);
            Assert(Render().AsSpan().SequenceEqual(original), "restoring the title changed the paper pixels");

            byte[] Render()
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
                    (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return pixels;
            }
        }

        // Reversing/settling a fade must cancel its old completion callback.
        var staleCompletion = false;
        mask.SetOpacity(0, 0);
        mask.SetOpacity(1, 120, () => staleCompletion = true);
        mask.SetOpacity(0, 0);
        Drain(Task.Delay(180));
        Assert(mask.HeaderOpacity == 0 && !staleCompletion, "cancelled fade restored an invisible title");
    }
}
