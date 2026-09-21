using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void CheckPaperBackgroundToggle()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "papertodo.png"),
            Path.Combine(AppContext.BaseDirectory, "papertodo.jpg"),
            Path.Combine(AppContext.BaseDirectory, "papertodo.jpeg")
        };
        foreach (var candidate in candidatePaths)
        {
            Require(!File.Exists(candidate), "background fixture must not replace an existing papertodo image");
        }

        var backgroundPath = candidatePaths[0];
        try
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 16, 16));
            }

            var rendered = new RenderTargetBitmap(
                16, 16, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using (var stream = File.Create(backgroundPath))
            {
                encoder.Save(stream);
            }

            Require(PaperBackground.IsAvailable, "papertodo image beside the executable is detected");
            Require(PaperBackground.LoadError == null, "valid background reports no load error");

            var original = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false);
            Require(original != null, "valid image produces an ImageBrush");
            Require(original.ImageSource != null, "valid image brush keeps its image source");
            Require(original.ImageSource is BitmapSource originalBitmap &&
                    originalBitmap.PixelWidth == 16 &&
                    originalBitmap.PixelHeight == 16,
                "small backgrounds keep their native decode size instead of being upscaled");
            Require(original.IsFrozen, "paper background brush is frozen for UI reuse");
            Require(Math.Abs(original.Opacity - 1.0) < 0.001,
                "disabled blending keeps the original image opaque");
            Require(original.Stretch == Stretch.None,
                "small images keep their native size when stretch is disabled");

            var oversized = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false,
                availableWidth: 8,
                availableHeight: 8);
            Require(oversized != null && oversized.Stretch == Stretch.Uniform,
                "oversized images are uniformly scaled down to stay inside the paper");

            var oneAxisOversized = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false,
                availableWidth: 8,
                availableHeight: 32);
            Require(oneAxisOversized != null && oneAxisOversized.Stretch == Stretch.Uniform,
                "exceeding either paper dimension triggers proportional downscaling");

            var roomForNativeSize = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false,
                availableWidth: 32,
                availableHeight: 32);
            Require(roomForNativeSize != null && roomForNativeSize.Stretch == Stretch.None,
                "images smaller than the paper are never enlarged when stretch is disabled");
            Require(original.AlignmentX == AlignmentX.Center &&
                    original.AlignmentY == AlignmentY.Center,
                "center position aligns the image to the center");

            var bottomLeft = PaperBackground.CreateBrush(
                blendWithTheme: true,
                layout: PaperBackgroundLayouts.BottomLeft,
                stretch: false);
            Require(bottomLeft != null && bottomLeft.Opacity < 1.0,
                "enabled blending mixes the image with the paper palette");
            Require(bottomLeft.Stretch == Stretch.None &&
                    bottomLeft.AlignmentX == AlignmentX.Left &&
                    bottomLeft.AlignmentY == AlignmentY.Bottom,
                "bottom-left position keeps native size and anchors correctly");

            var bottomCenter = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.BottomCenter,
                stretch: false);
            Require(bottomCenter != null &&
                    bottomCenter.Stretch == Stretch.None &&
                    bottomCenter.AlignmentX == AlignmentX.Center &&
                    bottomCenter.AlignmentY == AlignmentY.Bottom,
                "bottom-center position keeps native size and anchors correctly");

            var bottomRight = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.BottomRight,
                stretch: false);
            Require(bottomRight != null &&
                    bottomRight.Stretch == Stretch.None &&
                    bottomRight.AlignmentX == AlignmentX.Right &&
                    bottomRight.AlignmentY == AlignmentY.Bottom,
                "bottom-right position keeps native size and anchors correctly");

            var stretched = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: true);
            Require(stretched != null && stretched.Stretch == Stretch.UniformToFill,
                "stretch toggle fills the paper while preserving the source aspect ratio");

            var host = new Grid();
            PaperBackground.Apply(host);
            Require(host.Background is ImageBrush,
                "configured paper background applies to an outer content host");
            Require(
                PaperBackground.NeedsSizeRefresh(host.Background, 8, 8),
                "non-stretched backgrounds refresh when the paper becomes smaller than the image");
            Require(
                !PaperBackground.NeedsSizeRefresh(host.Background, 32, 32),
                "non-stretched backgrounds do not refresh while native-size rendering still fits");

            var todoHost = new ScrollViewer();
            PaperBackground.Apply(todoHost);
            Require(todoHost.Background is ImageBrush,
                "paper background applies to the todo ScrollViewer host");

            File.Delete(backgroundPath);
            WriteJpegFixture(candidatePaths[1], width: 1, height: 5000);
            var tall = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false);
            Require(tall?.ImageSource is BitmapSource tallBitmap &&
                    tallBitmap.PixelWidth <= 4096 &&
                    tallBitmap.PixelHeight == 4096,
                "tall backgrounds cap their longest decoded edge at 4096 pixels");

            File.Delete(candidatePaths[1]);
            WriteJpegFixture(candidatePaths[2], width: 5000, height: 1);
            var wide = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false);
            Require(wide?.ImageSource is BitmapSource wideBitmap &&
                    wideBitmap.PixelWidth == 4096 &&
                    wideBitmap.PixelHeight <= 4096,
                "wide backgrounds cap their longest decoded edge at 4096 pixels");

            File.Delete(candidatePaths[2]);
            File.WriteAllText(backgroundPath, "not an image");
            var badImage = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center,
                stretch: false);
            Require(badImage == null, "bad image falls back instead of throwing");
            Require(!string.IsNullOrWhiteSpace(PaperBackground.LoadError),
                "bad image exposes a diagnostic load error");

            var badHost = new Grid();
            PaperBackground.Apply(badHost);
            Require(ReferenceEquals(badHost.Background, Brushes.Transparent),
                "bad image keeps the plain paper background");

            Require(
                PaperBackgroundLayouts.Normalize("unknown") == PaperBackgroundLayouts.Center,
                "unknown layout falls back to center");
        }
        finally
        {
            foreach (var candidate in candidatePaths)
            {
                File.Delete(candidate);
            }
        }
    }

    private static void WriteJpegFixture(string path, int width, int height)
    {
        var stride = checked(width * 3);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            new byte[checked(stride * height)],
            stride);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
