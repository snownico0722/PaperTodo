using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class OpticalProfileChecks
{
    internal static void Run()
    {
        // Decode the actual uploaded bytes, including low-byte wrap boundaries. Testing
        // only the analytic quintic missed both quantisation and RGB-channel foldover.
        var bitmap = (BitmapSource)((ImageBrush)LensDisplacement.ProfileBrush).ImageSource;
        var bytes = new byte[bitmap.PixelWidth * 4];
        bitmap.CopyPixels(bytes, bytes.Length, 0);
        var values = new double[bitmap.PixelWidth];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (bytes[i * 4 + 2] * 256 + bytes[i * 4 + 1]) / 65535d;
            var expected = LensDisplacement.ProfileAt(i / (double)(values.Length - 1)).Shift;
            Program.Assert(bytes[i * 4 + 3] == 255 && Math.Abs(values[i] - expected) <= .5 / 65535 + 1e-12,
                "uploaded profile retains 16-bit precision without premultiplied data");
        }
        Program.Assert(values[0] == 1 && values[^1] == 0, "packed profile joins exact endpoints");
        double Profile(double t)
        {
            var at = Math.Clamp(t, 0, 1) * (values.Length - 1);
            var i = Math.Min(values.Length - 2, (int)at);
            return values[i] + (values[i + 1] - values[i]) * (at - i);
        }
        var minimum = 1d;
        foreach (var size in new[] { new Size(180, 36), new Size(36, 180), new Size(360, 100),
                     new Size(430, 350), new Size(1000, 800), new Size(2400, 1200) })
        foreach (var strength in new[] { .4, 1d })
        foreach (var channel in new[] { 1 - GlassMetrics.ChromaticSpread, 1, 1 + GlassMetrics.ChromaticSpread })
        {
            var metrics = GlassMetrics.For(size, false);
            const double zoom = 1; // flat body; only the shoulder displaces background samples
            var amount = metrics.Displacement * strength * channel;
            for (var i = 0; i < values.Length - 1; i++)
            {
                var step = metrics.Bezel / (values.Length - 1);
                var derivative = zoom + amount * (values[i + 1] - values[i]) / step;
                minimum = Math.Min(minimum, derivative);
                Program.Assert(derivative > .08, "every RGB channel keeps ordered background samples in both strength modes");
            }
            // A radius narrower than the shoulder creates a focus inside its active
            // region. Verify tangential order as well as the straight-edge derivative.
            var radius = metrics.OpticalRadius(8);
            for (var i = 0; i < 512; i++)
            {
                var depth = metrics.Bezel * i / 512;
                var tangential = zoom - amount * Profile(depth / metrics.Bezel) / (radius - depth);
                Program.Assert(tangential > .3, "rounded optical shoulder does not cross its corner centre");
            }
        }
        CheckRenderedChannels();
        Console.WriteLine($"OPTICAL PROFILE: packed RG16, minimum RGB sample derivative {minimum:F4}; corner order preserved.");
    }
    private static void CheckRenderedChannels()
    {
        const int width = 430, height = 120;
        var ramp = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            var value = (byte)Math.Round(x * 255d / (width - 1));
            ramp[at] = ramp[at + 1] = ramp[at + 2] = value; ramp[at + 3] = 255;
        }
        var scene = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, ramp, width * 4);
        scene.Freeze();
        var metrics = GlassMetrics.For(new Size(width, height), false);
        var radius = metrics.OpticalRadius(8);
        var effect = new LiquidRefractionEffect
        {
            Scene = new ImageBrush(scene),
            Shift = new Point(metrics.Displacement / width, metrics.Displacement / height),
            Extent = new(width, height, 1 / metrics.Bezel, 1),
            Radii = new(radius, radius, radius, radius), Scattering = new(0, 0, 1, 0)
        };
        var visual = new DrawingVisual { Effect = effect };
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); output.Render(visual);
        var pixels = new byte[ramp.Length]; output.CopyPixels(pixels, width * 4, 0);
        for (var x = 2; x < width - 2; x++)
        {
            var i = (height / 2 * width + x) * 4;
            Program.Assert(pixels[i + 3] == 255, "compiled optical shader produces an opaque sampled scene");
            for (var channel = 0; channel < 3; channel++)
                Program.Assert(pixels[i + channel] + 1 >= pixels[i - 4 + channel],
                    "actual compiled RGB shader retains gradient order across the shoulder");
        }
        var center = (height / 2 * width + width / 2) * 4;
        Program.Assert(Math.Abs(pixels[center] - 128) < 3, "shader control reads the source rather than black fallback");
    }

}
