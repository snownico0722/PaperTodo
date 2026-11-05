using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

// Offscreen WPF raster checks for the seams between the eight shadow tiles.
// These use synthetic DPI, not monitor DPI or unstable DWM desktop screenshots.
internal static class NineSliceShadowSeamChecks
{
    private const double Gutter = EdgeCapsuleLayout.WindowChromeMargin;
    private const int MaxSeamAlphaDifference = 8;
    private const int MaxOuterEdgeAlpha = 10;

    private readonly record struct Case(
        string Name, double Width, double Height,
        double Blur, double Depth, double Opacity, double Radius, bool Capsule);

    internal static void Run()
    {
        Case[] cases =
        [
            new("paper", 360, 280, 14, 2, .22, 16, false),
            new("paper-fractional-size", 361, 281, 14, 2, .22, 16, false),
            new("paper-minimum-width", 96, 120, 14, 2, .22, 16, false),
            new("capsule", 92, 46, 8, 1, .12, 12, true),
            new("capsule-stretched-side", 92, 160, 8, 1, .12, 12, true)
        ];

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (var scenario in cases)
                Check(scenario, scale);
    }

    private static void Check(Case scenario, double scale)
    {
        var pixelWidth = (int)Math.Ceiling(scenario.Width * scale);
        var pixelHeight = (int)Math.Ceiling(scenario.Height * scale);
        var shadow = NineSlicePaperShadow.Get(
            scenario.Blur, scenario.Depth, scenario.Opacity, scenario.Radius,
            new DpiScale(scale, scale), scenario.Capsule);

        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            shadow.Draw(dc, new Rect(
                Gutter, Gutter,
                scenario.Width - Gutter * 2,
                scenario.Height - Gutter * 2));

        var raster = new RenderTargetBitmap(
            pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        raster.Render(drawing);
        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        raster.CopyPixels(pixels, stride, 0);

        byte At(int x, int y) =>
            pixels[(Math.Clamp(y, 0, pixelHeight - 1) * pixelWidth +
                    Math.Clamp(x, 0, pixelWidth - 1)) * 4 + 3];
        int Px(double dip) => (int)Math.Floor(dip * scale);
        byte Alpha(double x, double y) => At(Px(x), Px(y));
        var context = scenario.Name + " " + (scale * 100).ToString("0") + "% DPI";

        // Mirror the cut positions, not a screenshot's apparent boundaries.
        // Fractional DPI can put the far seam between physical pixel columns.
        var sideX = (int)Math.Ceiling((Gutter + scenario.Radius + scenario.Blur) * scale);
        var sideY = (int)Math.Ceiling((Gutter + scenario.Radius + scenario.Blur) * scale);
        var middleY = Math.Max(4, (int)Math.Ceiling(4 * scale));
        var bitmapHeight = sideY * 2 + middleY;
        if (scenario.Capsule)
            bitmapHeight = Math.Min(bitmapHeight, Math.Max(middleY + 2,
                (int)Math.Round(PaperLayoutDefaults.CapsuleHeight * scale)));
        bitmapHeight -= (bitmapHeight - middleY) % 2;

        var cornerWidth = sideX / scale;
        var cornerHeight = (bitmapHeight - middleY) / (2 * scale);
        var xCuts = new[] { cornerWidth, scenario.Width - cornerWidth };
        var yCuts = new[] { cornerHeight, scenario.Height - cornerHeight };

        // Along the straight perimeter, the blur should be constant across each
        // corner/side join. Compare with the same row/column away from the join.
        // Ignore sub-8/255 raster rounding; reject a clearly visible dark/light seam.
        foreach (var inset in new[] { 7.0, 8.0, 10.0 })
        {
            foreach (var y in new[] { inset, scenario.Height - inset })
            {
                var reference = Alpha(scenario.Width / 2, y);
                Program.Assert(reference >= 6, context + ": top/bottom seam probe has shadow signal");
                foreach (var cut in xCuts)
                {
                    var seamPixel = Px(cut);
                    for (var d = -2; d <= 2; d++)
                    {
                        var actual = At(seamPixel + d, Px(y));
                        Program.Assert(Math.Abs(actual - reference) <= MaxSeamAlphaDifference,
                            context + ": horizontal tile seam at x=" + cut +
                            ", y=" + y + ", offset=" + d +
                            " (alpha " + actual + " vs " + reference + ")");
                    }
                }
            }

            foreach (var x in new[] { inset, scenario.Width - inset })
            {
                var reference = Alpha(x, scenario.Height / 2);
                Program.Assert(reference >= 6, context + ": left/right seam probe has shadow signal");
                foreach (var cut in yCuts)
                {
                    var seamPixel = Px(cut);
                    for (var d = -2; d <= 2; d++)
                    {
                        var actual = At(Px(x), seamPixel + d);
                        Program.Assert(Math.Abs(actual - reference) <= MaxSeamAlphaDifference,
                            context + ": vertical tile seam at y=" + cut +
                            ", x=" + x + ", offset=" + d +
                            " (alpha " + actual + " vs " + reference + ")");
                    }
                }
            }
        }

        // The 8-DIP gutter intentionally clips a faint Gaussian tail. Keep the
        // border alpha small, without incorrectly requiring exact transparency.
        var edgeAlpha = new[]
        {
            Alpha(0, scenario.Height / 2),
            Alpha(scenario.Width - 1 / scale, scenario.Height / 2),
            Alpha(scenario.Width / 2, 0),
            Alpha(scenario.Width / 2, scenario.Height - 1 / scale)
        };
        foreach (var alpha in edgeAlpha)
            Program.Assert(alpha <= MaxOuterEdgeAlpha,
                context + ": outside shadow alpha " + alpha + " exceeds gutter tolerance");
    }
}
