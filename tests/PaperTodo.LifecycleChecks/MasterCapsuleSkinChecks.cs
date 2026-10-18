using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class MasterCapsuleSkinChecks
{
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    internal static void Prepare()
    {
        var directory = AppContext.BaseDirectory;
        Require(MasterCapsuleSkin.LoadFromDirectory(directory) == null,
            "An absent master-capsule image must not replace the default pill");

        // A 40x30 canvas has a 12x14 opaque sticker away from its transparent edges.
        var pixels = new byte[40 * 30 * 4];
        for (var y = 8; y < 22; y++)
        for (var x = 19; x < 31; x++)
        {
            var index = (y * 40 + x) * 4;
            pixels[index] = 220;
            pixels[index + 1] = 120;
            pixels[index + 2] = 25;
            pixels[index + 3] = 255;
        }
        var source = BitmapSource.Create(
            40, 30, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        var pngPath = Path.Combine(directory, "papertodomain.png");
        using (var output = File.Create(pngPath)) png.Save(output);

        // Broken optional WebP must not supersede a valid PNG.
        File.WriteAllText(Path.Combine(directory, "papertodomain.webp"), "broken");
        var loaded = MasterCapsuleSkin.LoadFromDirectory(directory);
        Require(loaded is { PixelWidth: 12, PixelHeight: 14, IsFrozen: true },
            "Transparent image margins were not cropped and frozen");
        Require(MasterCapsuleSkin.DisplayWidthDip(loaded) >= 16 &&
                MasterCapsuleSkin.DisplayWidthDip(loaded) <= 128,
            "Visible master skin width must remain bounded");
    }

    internal static void Verify(AppController controller)
    {
        var master = controller.MasterCapsuleForQueue("", EdgeCapsuleEdge.Right);
        Require(master != null, "The master capsule was not created");
        var pill = (Border)typeof(MasterCapsuleWindow)
            .GetField("_pill", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(master)!;
        Require(pill is not SkinBorder && pill.Child is Grid,
            "An image skin must replace the master chrome without changing its host");
        var grid = (Grid)pill.Child;
        var art = grid.Children.OfType<Image>().SingleOrDefault();
        Require(art?.Source is BitmapSource { PixelWidth: 12, PixelHeight: 14 },
            "Master skin was not bound to the transparent image");
        Require(Math.Abs(master.DesiredDockedWidth - MasterCapsuleSkin.DisplayWidthDip(
                (BitmapSource)art.Source)) < 0.1,
            "Master skin must use the cropped image width");
        Require(pill.ContextMenu != null && pill.Cursor != null,
            "Image skin must keep the master pill's interaction surface");

        Require(controller.TryCreateMasterQueueFloatingDragHostOptions(
                "", EdgeCapsuleEdge.Right, "▾", "5", out var options),
            "Master queue drag options unavailable");
        var floatingOptions = options with
        {
            Shape = options.Shape with
            {
                WindowWidthDip = master.DesiredDockedWidth + EdgeCapsuleLayout.WindowChromeMargin
            },
            MasterSkinSource = (BitmapSource)art.Source,
            MasterSkinEdge = EdgeCapsuleEdge.Right
        };
        var floating = EdgeCapsuleDragWindow.Rent(floatingOptions);
        try
        {
            var surface = (Grid)typeof(EdgeCapsuleDragWindow)
                .GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(floating)!;
            Require(surface.Children.OfType<Image>().Any(image =>
                    image.Visibility == Visibility.Visible &&
                    ReferenceEquals(image.Source, art.Source)),
                "The floating master must reuse the same image");
        }
        finally { floating.ReturnToPool(); }
    }
}
