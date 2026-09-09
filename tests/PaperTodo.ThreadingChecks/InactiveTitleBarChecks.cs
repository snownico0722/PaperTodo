using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static void CheckInactiveTitleBarMask()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var size in new[] { new Size(240, 180), new Size(310, 210) })
        {
            // A measured title row ends on a device pixel when layout rounding is enabled.
            var extent = Math.Round(31 * scale) / scale;
            var chrome = new PaperChromeBorder();
            var (host, body) = Build(chrome, extent, hasTitle: true);
            var (normal, _) = Build(new Border(), extent, hasTitle: true);
            var (shortPaper, _) = Build(new Border(), extent, hasTitle: false);
            var original = Render(host);
            Compare(original, Render(normal), "normal chrome changed");
            var bodyPosition = body.TranslatePoint(new Point(), host);
            var bodySize = body.RenderSize;
            var bodyArrangeCount = body.ArrangeCount;

            chrome.SetHeaderExtent(extent);
            chrome.SetHeaderOpacity(0, 0);
            var hidden = Render(host);
            // Compare against an ordinary shorter Border, including all its corners,
            // stroke and shadow. A flat crop cannot satisfy this reference image.
            if (scale == 1.25 && size.Width == 240)
            {
                Preview(host, "ACTUAL");
                Preview(shortPaper, "EXPECTED");
            }
            Compare(hidden, Render(shortPaper), "hidden chrome differs from a complete rounded paper");
            var width = (int)Math.Ceiling(size.Width * scale);
            for (var y = 0; y < (int)((8 + extent - 16) * scale); y++)
            for (var x = 0; x < width; x++)
                Assert(hidden[(y * width + x) * 4 + 3] == 0, "hidden title still occupies transparent pixels");
            AssertBodyStable();

            chrome.SetHeaderOpacity(0.5, 0);
            var middle = Render(host);
            var start = (int)Math.Ceiling((bodyPosition.Y + 24) * scale) * width * 4;
            Compare(middle[start..], original[start..], "body pixels changed during the transition");
            AssertBodyStable();

            chrome.SetHeaderOpacity(1, 0);
            chrome.SetHeaderExtent(0);
            Compare(Render(host), original, "restoring the title changed the paper");
            Assert(chrome.Child.Clip == null, "restoring left a content clip behind");
            AssertBodyStable();

            // Theme resources must remain live while the short background is showing.
            chrome.SetHeaderExtent(extent);
            chrome.SetHeaderOpacity(0, 0);
            chrome.Background = Brushes.LemonChiffon;
            chrome.BorderBrush = Brushes.DarkGoldenrod;
            var expectedChrome = (Border)shortPaper.Children[0];
            expectedChrome.Background = chrome.Background;
            expectedChrome.BorderBrush = chrome.BorderBrush;
            Compare(Render(host), Render(shortPaper), "hidden chrome retained old theme brushes");

            if (scale == 1 && size.Width == 310)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Bitmap(host)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                Console.WriteLine("TITLEBAR_PREVIEW:" + Convert.ToBase64String(stream.ToArray()));
            }

            void Preview(Grid root, string label)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Bitmap(root)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                Console.WriteLine("TITLEBAR_" + label + ":" + Convert.ToBase64String(stream.ToArray()));
            }

            void AssertBodyStable()
            {
                Assert(body.TranslatePoint(new Point(), host) == bodyPosition && body.RenderSize == bodySize,
                    "title hiding moved or resized the body");
                Assert(body.ArrangeCount == bodyArrangeCount, "title hiding rearranged the body");
            }

            void Compare(byte[] actual, byte[] expected, string reason)
            {
                Assert(actual.Length == expected.Length, reason);
                for (var i = 0; i < actual.Length; i++)
                {
                    var alpha = expected[i - i % 4 + 3];
                    var tolerance = alpha == 255 ? 0 : 1;
                    Assert(Math.Abs(actual[i] - expected[i]) <= tolerance,
                        $"{reason}: scale={scale} size={size} byte={i} actual={actual[i]} expected={expected[i]}");
                }
            }

            RenderTargetBitmap Bitmap(Grid root)
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
                    (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                return bitmap;
            }

            byte[] Render(Grid root)
            {
                var bitmap = Bitmap(root);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return pixels;
            }
        }

        var fading = new PaperChromeBorder();
        var staleCompletion = false;
        fading.SetHeaderOpacity(0, 0);
        fading.SetHeaderOpacity(1, 120, () => staleCompletion = true);
        fading.SetHeaderOpacity(0, 0);
        Drain(Task.Delay(180));
        Assert(fading.HeaderOpacity == 0 && !staleCompletion, "cancelled fade restored an invisible title");

        static (Grid Host, TitleBarBodyProbe Body) Build(Border chrome, double extent, bool hasTitle)
        {
            var host = new Grid { Background = Brushes.Transparent };
            chrome.Margin = new Thickness(8, 8 + (hasTitle ? 0 : extent), 8, 8);
            chrome.Background = Brushes.White;
            chrome.BorderBrush = Brushes.Gray;
            chrome.BorderThickness = new Thickness(1);
            chrome.CornerRadius = new CornerRadius(20);
            chrome.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.22 };
            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(hasTitle ? extent : 0) });
            shell.RowDefinitions.Add(new RowDefinition());
            if (hasTitle)
            {
                var title = new Border { Margin = new Thickness(20, 4, 20, 4), Background = Brushes.SteelBlue };
                if (chrome is PaperChromeBorder)
                    title.SetBinding(UIElement.OpacityProperty, new Binding
                    {
                        Source = chrome, Path = new PropertyPath(PaperChromeBorder.HeaderOpacityProperty)
                    });
                shell.Children.Add(title);
            }
            var body = new TitleBarBodyProbe();
            body.Children.Add(new Border
            {
                Background = Brushes.Bisque, Height = 24, Margin = new Thickness(16, 12, 16, 0),
                VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(5)
            });
            Grid.SetRow(body, 1);
            shell.Children.Add(body);
            chrome.Child = shell;
            host.Children.Add(chrome);
            return (host, body);
        }
    }

    private sealed class TitleBarBodyProbe : Grid
    {
        internal int ArrangeCount { get; private set; }
        protected override Size ArrangeOverride(Size arrangeSize)
        {
            ArrangeCount++;
            return base.ArrangeOverride(arrangeSize);
        }
    }
}
