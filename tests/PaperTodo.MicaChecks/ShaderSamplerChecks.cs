using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

// A generated pattern, not a desktop screenshot. Exercise WPF's actual sampler
// realization path so correct C# crop constants cannot hide a resized input grid.
internal static class ShaderSamplerChecks
{
    private const int SourceWidth = 768, SourceHeight = 512, SourceX = 128, SourceY = 96;
    internal static void Run()
    {
        var pixels = new byte[SourceWidth * SourceHeight * 4];
        for (var y = 0; y < SourceHeight; y++) for (var x = 0; x < SourceWidth; x++)
        {
            var i = (y * SourceWidth + x) * 4;
            pixels[i] = (byte)((x / 3 % 2 == 0) ? 35 : 215);
            pixels[i + 1] = (byte)((y / 5 % 2 == 0) ? 65 : 185);
            pixels[i + 2] = (byte)((x / 7 + y / 7) % 2 == 0 ? 25 : 225);
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(SourceWidth, SourceHeight, 96, 96, PixelFormats.Bgra32, null, pixels, SourceWidth * 4);
        source.Freeze();
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            var stable = new LiquidRefractionEffect { Scene = LiquidRefractionEffect.CreateSampler(source), Scattering = new(0, 0, 1, 0) };
            var legacy = new LiquidRefractionEffect { Scene = new ImageBrush(source) { Stretch = Stretch.Fill }, Scattering = new(0, 0, 1, 0) };
            var stableReference = Render(stable, 240, 180, dpi);
            var legacyReference = Render(legacy, 240, 180, dpi);
            var stableDifference = 0; var legacyDifference = 0; var sourceError = 0;
            for (var n = 1; n <= 24; n++)
            {
                var width = 240 + n; var height = 180 + n / 2;
                var stableFrame = Render(stable, width, height, dpi);
                var legacyFrame = Render(legacy, width, height, dpi);
                for (var y = 40; y < 130; y += 3) for (var x = 40; x < 180; x += 2)
                {
                    var a = (y * 240 + x) * 4; var b = (y * width + x) * 4;
                    Program.Assert(stableFrame[b + 3] == 255, "cached sampler must render real opaque pixels");
                    for (var c = 0; c < 3; c++)
                    {
                        stableDifference = Math.Max(stableDifference, Math.Abs(stableFrame[b + c] - stableReference[a + c]));
                        legacyDifference = Math.Max(legacyDifference, Math.Abs(legacyFrame[b + c] - legacyReference[a + c]));
                        sourceError = Math.Max(sourceError, Math.Abs(stableFrame[b + c] - pixels[((y + SourceY) * SourceWidth + x + SourceX) * 4 + c]));
                    }
                }
            }
            Console.WriteLine($"SAMPLER dpi={dpi}: legacy resize drift={legacyDifference}/255; fixed-grid drift={stableDifference}/255; source error={sourceError}/255.");
            Program.Assert(legacyDifference >= 4, "legacy ImageBrush control must reproduce resize-dependent resampling");
            Program.Assert(stableDifference <= 1 && sourceError <= 1, "fixed sampler pixels remain screen-anchored across resize and DPI");
            var profile = (BitmapCacheBrush)stable.Profile;
            Program.Assert(VisualTreeHelper.GetContentBounds(profile.Target) == new Rect(0, 0, 512, 1) && profile.BitmapCache.RenderAtScale == 1,
                "RG16 profile remains 512x1, not resized to the panel");
        }
        var mutable = new WriteableBitmap(source);
        var effect = new LiquidRefractionEffect { Scene = LiquidRefractionEffect.CreateSampler(mutable), Scattering = new(0, 0, 1, 0) };
        var before = Render(effect, 240, 180, 1);
        var sampler = (BitmapCacheBrush)effect.Scene; var target = sampler.Target;
        var changed = (byte[])pixels.Clone();
        for (var i = 0; i < changed.Length; i += 4) for (var c = 0; c < 3; c++) changed[i + c] = (byte)(255 - changed[i + c]);
        mutable.WritePixels(new Int32Rect(0, 0, SourceWidth, SourceHeight), changed, SourceWidth * 4, 0);
        var after = Render(effect, 240, 180, 1);
        var sample = (70 * 240 + 70) * 4;
        Program.Assert(Math.Abs(after[sample] - (255 - before[sample])) <= 1 && ReferenceEquals(target, sampler.Target),
            "new background pixels invalidate the existing sampler cache without resizing its grid");
        Console.WriteLine("SAMPLER: mutable background update observed; cached target reused; no screenshot files created.");
    }
    private static byte[] Render(LiquidRefractionEffect effect, int pixelWidth, int pixelHeight, double dpi)
    {
        var w = pixelWidth / dpi; var h = pixelHeight / dpi;
        effect.Crop = new((double)pixelWidth / SourceWidth, (double)pixelHeight / SourceHeight, (double)SourceX / SourceWidth, (double)SourceY / SourceHeight);
        effect.Extent = new(w, h, 1d / 12, 1); effect.Radii = new(12, 12, 12, 12);
        var visual = new DrawingVisual { Effect = effect };
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var result = new byte[pixelWidth * pixelHeight * 4]; bitmap.CopyPixels(result, pixelWidth * 4, 0);
        visual.Effect = null;
        return result;
    }
}
