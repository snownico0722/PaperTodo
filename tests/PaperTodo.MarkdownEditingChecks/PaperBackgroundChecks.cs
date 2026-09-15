using System.IO;
using System.Reflection;
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
                layout: PaperBackgroundLayouts.Center);
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

            var bottomLeft = PaperBackground.CreateBrush(
                blendWithTheme: true,
                layout: PaperBackgroundLayouts.BottomLeft);
            Require(bottomLeft != null && bottomLeft.Opacity < 1.0,
                "enabled blending mixes the image with the paper palette");
            Require(bottomLeft.Stretch == Stretch.Uniform &&
                    bottomLeft.AlignmentX == AlignmentX.Left &&
                    bottomLeft.AlignmentY == AlignmentY.Bottom,
                "bottom-left mode preserves aspect ratio and anchors correctly");

            var stretched = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Stretch);
            Require(stretched != null && stretched.Stretch == Stretch.Fill,
                "stretch mode fills the whole paper body");

            var host = new Grid();
            PaperBackground.Apply(host);
            Require(host.Background is ImageBrush,
                "configured paper background applies to an outer content host");

            var todoHost = new ScrollViewer();
            PaperBackground.Apply(todoHost);
            Require(todoHost.Background is ImageBrush,
                "paper background applies to the todo ScrollViewer host");

            // Keep the production Todo wiring covered without constructing a full PaperWindow.
            // If BuildTodoBody stops attaching its ScrollViewer to the paper-background runtime,
            // this check fails even though applying a brush to a standalone ScrollViewer still works.
            var buildTodoBody = typeof(PaperWindow).GetMethod(
                "BuildTodoBody",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var attachTodoBackgroundHost = typeof(PaperWindow).GetMethod(
                "AttachTodoBackgroundHost",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(buildTodoBody != null && attachTodoBackgroundHost != null,
                "todo paper background wiring methods are available");

            var il = buildTodoBody!.GetMethodBody()?.GetILAsByteArray();
            Require(il != null, "BuildTodoBody exposes an IL body for wiring verification");
            var targetToken = BitConverter.GetBytes(attachTodoBackgroundHost!.MetadataToken);
            var attachesBackgroundHost = false;
            for (var i = 1; i <= il!.Length - targetToken.Length; i++)
            {
                if ((il[i - 1] == 0x28 || il[i - 1] == 0x6f) &&
                    il.AsSpan(i, targetToken.Length).SequenceEqual(targetToken))
                {
                    attachesBackgroundHost = true;
                    break;
                }
            }
            Require(attachesBackgroundHost,
                "BuildTodoBody attaches its ScrollViewer to the paper background runtime");

            File.WriteAllText(backgroundPath, "not an image");
            var badImage = PaperBackground.CreateBrush(
                blendWithTheme: false,
                layout: PaperBackgroundLayouts.Center);
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
            File.Delete(backgroundPath);
        }
    }
}
