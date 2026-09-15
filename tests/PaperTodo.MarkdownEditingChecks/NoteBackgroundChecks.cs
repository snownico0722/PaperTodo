using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static partial class Program
{
    private static void CheckNoteBackgroundToggle()
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

            Require(NoteBackground.IsAvailable, "papertodo image beside the executable is detected");

            var original = NoteBackground.CreateBrush(
                blendWithTheme: false,
                PaperBackgroundLayouts.Center);
            Require(original != null, "valid image produces an ImageBrush");
            Require(original.ImageSource != null, "valid image brush keeps its image source");
            Require(original.IsFrozen, "paper background brush is frozen for UI reuse");
            Require(Math.Abs(original.Opacity - 1.0) < 0.001,
                "disabled blending keeps the original image opaque");
            Require(original.Stretch == Stretch.Uniform,
                "center mode preserves aspect ratio without cropping");
            Require(original.AlignmentX == AlignmentX.Center &&
                    original.AlignmentY == AlignmentY.Center,
                "center mode aligns the image to the center");

            var bottomLeft = NoteBackground.CreateBrush(
                blendWithTheme: true,
                PaperBackgroundLayouts.BottomLeft);
            Require(bottomLeft != null && bottomLeft.Opacity < 1.0,
                "enabled blending mixes the image with the paper palette");
            Require(bottomLeft.Stretch == Stretch.Uniform &&
                    bottomLeft.AlignmentX == AlignmentX.Left &&
                    bottomLeft.AlignmentY == AlignmentY.Bottom,
                "bottom-left mode preserves aspect ratio and anchors correctly");

            var stretched = NoteBackground.CreateBrush(
                blendWithTheme: false,
                PaperBackgroundLayouts.Stretch);
            Require(stretched != null && stretched.Stretch == Stretch.Fill,
                "stretch mode fills the whole paper body");

            var host = new Grid();
            NoteBackground.Apply(host);
            Require(host.Background is ImageBrush,
                "configured paper background applies to an outer content host");

            File.WriteAllText(backgroundPath, "not an image");
            var badImage = NoteBackground.CreateBrush(
                blendWithTheme: false,
                PaperBackgroundLayouts.Center);
            Require(badImage == null, "bad image falls back instead of throwing");

            var badHost = new Grid();
            NoteBackground.Apply(badHost);
            Require(ReferenceEquals(badHost.Background, Brushes.Transparent),
                "bad image keeps the plain paper background");

            Require(
                PaperBackgroundLayouts.Normalize("unknown") == PaperBackgroundLayouts.Center,
                "unknown layout falls back to center");
        }
        finally
        {
            File.Delete(backgroundPath);
        }
    }
}
