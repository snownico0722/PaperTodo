using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class MaterialPaletteChecks
{
    internal static void Run(AppController controller)
    {
        var saved = (controller.State.Theme, controller.State.PaperSkin, controller.State.ColorScheme,
            controller.State.MatchAuxiliaryMaterialStrength);
        try
        {
            foreach (var dark in new[] { false, true })
            foreach (var scheme in ColorSchemes.All)
            {
                controller.State.Theme = dark ? "dark" : "light"; controller.State.ColorScheme = scheme;
                var surfaces = new HashSet<Color>();
                foreach (var skin in PaperSkins.All)
                {
                    controller.State.PaperSkin = skin; Theme.Invalidate();
                    var palette = Theme.MaterialColors;
                    if (skin is not (PaperSkins.Paper or PaperSkins.Pixel)) surfaces.Add(palette.Surface);
                    Program.Assert(palette.Surface.A == 255 && ((SolidColorBrush)Theme.TextBrush).Color.A == 255,
                        "material palettes preserve opaque semantic colors");
                    if (scheme == ColorSchemes.Neutral) Program.Assert(palette.NativeOverlay.A == 0, "neutral system wash remains unchanged");
                    if (!PaperSkins.UsesNativeBackdrop(skin)) continue;
                    var surface = new SkinBorder { IsCapsule = true, UseLightweightMaterial = true,
                        Background = Theme.PaperBrush, CornerRadius = new CornerRadius(8) };
                    Color? first = null;
                    foreach (var size in new[] { new Size(200, 80), new Size(450, 300), new Size(620, 480) })
                    foreach (var full in new[] { false, true })
                    {
                        controller.State.MatchAuxiliaryMaterialStrength = full; surface.RefreshSkin();
                        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                        image.Render(surface);
                        var pixel = new byte[4]; image.CopyPixels(new Int32Rect((int)size.Width/2, (int)size.Height/2, 1, 1), pixel, 4, 0);
                        var color = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                        first ??= color;
                        Program.Assert(color == first && color.A == (dark ? 248 : 244),
                            $"{skin}/{scheme}: preview hue and alpha fixed across size and full/quiet modes");
                    }
                }
                if (scheme != ColorSchemes.Neutral) Program.Assert(surfaces.Count >= 4,
                    "each color family is tuned per material rather than one shared tint");
            }
        }
        finally
        {
            (controller.State.Theme, controller.State.PaperSkin, controller.State.ColorScheme,
                controller.State.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
    }
}
