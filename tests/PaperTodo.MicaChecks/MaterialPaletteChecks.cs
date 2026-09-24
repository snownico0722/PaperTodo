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
            controller.State.MatchAuxiliaryMaterialStrength, controller.State.HideSurfaceOutline,
            controller.State.MaterialTransparency);
        try
        {
            controller.State.MaterialTransparency = MaterialTransparencyLevels.Medium;
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
                    if (skin == PaperSkins.Aero)
                        Program.Assert(palette.TransmissionAlpha == (dark ? 102 : 58), "Aero medium uses four steps more cover than the previous baseline");
                    if (skin == PaperSkins.TracingPaper)
                        Program.Assert(palette.TransmissionAlpha == (dark ? 163 : 152), "tracing paper medium uses one step less cover than the previous baseline");
                    if (!PaperSkins.UsesNativeBackdrop(skin)) continue;
                    var surface = new SkinBorder { IsCapsule = true, UseLightweightMaterial = true,
                        Background = Theme.PaperBrush, CornerRadius = new CornerRadius(8) };
                    Color? firstFull = null;
                    foreach (var size in new[] { new Size(200, 80), new Size(450, 300), new Size(620, 480) })
                    foreach (var full in new[] { false, true })
                    {
                        controller.State.MatchAuxiliaryMaterialStrength = full; surface.RefreshSkin();
                        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                        image.Render(surface);
                        var pixel = new byte[4]; image.CopyPixels(new Int32Rect((int)size.Width/2, (int)size.Height/2, 1, 1), pixel, 4, 0);
                        var color = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                        if (full)
                        {
                            firstFull ??= color;
                            Program.Assert(color == firstFull && color.A == (dark ? 248 : 244),
                                $"{skin}/{scheme}: full preview hue and alpha stay stable across size");
                        }
                        else
                        {
                            Program.Assert(color.A == 255,
                                $"{skin}/{scheme}: full-material OFF makes the preview opaque");
                        }
                    }
                }
                if (scheme != ColorSchemes.Neutral) Program.Assert(surfaces.Count >= 4,
                    "each color family is tuned per material rather than one shared tint");
            }
            // The five saved levels stay unchanged. Tracing Paper and Aero shift their optical
            // baselines, while every material still moves monotonically across those five levels.
            controller.State.Theme = "light";
            controller.State.ColorScheme = ColorSchemes.Warm;
            foreach (var skin in new[]
            {
                PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic,
                PaperSkins.TracingPaper, PaperSkins.Aero
            })
            {
                controller.State.PaperSkin = skin;
                var alphas = new List<byte>();
                foreach (var level in MaterialTransparencyLevels.All)
                {
                    controller.State.MaterialTransparency = level;
                    Theme.Invalidate();
                    alphas.Add(Theme.MaterialColors.TransmissionAlpha);
                }
                Program.Assert(alphas.Zip(alphas.Skip(1), (a, b) => a > b).All(value => value),
                    $"{skin}: five transparency levels monotonically reduce material cover");
                if (skin == PaperSkins.TracingPaper)
                    Program.Assert(alphas.SequenceEqual(new byte[] { 206, 179, 152, 125, 98 }),
                        "tracing paper shifts every level one transparency step lighter");
                if (skin == PaperSkins.Aero)
                    Program.Assert(alphas.SequenceEqual(new byte[] { 90, 74, 58, 42, 26 }),
                        "Aero keeps Medium and spreads the five transparency levels farther apart");
            }
            controller.State.MaterialTransparency = MaterialTransparencyLevels.Medium;
            Theme.Invalidate();

            if (!SystemParameters.HighContrast)
            {
                controller.State.PaperSkin = PaperSkins.Paper; controller.State.Theme = "light"; Theme.Invalidate();
                var surface = new SkinBorder { Width = 48, Height = 36, Background = Brushes.White,
                    BorderBrush = Brushes.Red, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
                Color Edge()
                {
                    surface.Measure(new Size(48, 36)); surface.Arrange(new Rect(0, 0, 48, 36)); surface.UpdateLayout();
                    var image = new RenderTargetBitmap(48, 36, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
                    var pixel = new byte[4]; image.CopyPixels(new Int32Rect(0, 18, 1, 1), pixel, 4, 0);
                    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                }
                controller.State.HideSurfaceOutline = false; surface.RefreshSkin(); var outlined = Edge();
                controller.State.HideSurfaceOutline = true; surface.RefreshSkin(); var clean = Edge();
                Program.Assert(outlined.R > 180 && outlined.G < 100 && clean.R > 220 && clean.G > 220 && clean.B > 220,
                    "outer-border preference removes only the visible stroke while retaining the surface fill");

                Color Center(SkinBorder target)
                {
                    target.Measure(new Size(target.Width, target.Height));
                    target.Arrange(new Rect(0, 0, target.Width, target.Height));
                    target.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)target.Width, (int)target.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(target);
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)target.Width / 2, (int)target.Height / 2, 1, 1), pixel, 4, 0);
                    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                }

                controller.State.PaperSkin = PaperSkins.Pixel;
                controller.State.ColorScheme = ColorSchemes.Ink;
                controller.State.MatchAuxiliaryMaterialStrength = false;
                Theme.Invalidate();
                var quietPixel = new SkinBorder
                {
                    IsCapsule = true, IsEdgeActiveMaterial = true,
                    Width = 80, Height = 40, Background = Brushes.Transparent,
                    BorderThickness = new Thickness(), CornerRadius = new CornerRadius(8)
                };
                Program.Assert(Center(quietPixel).A == 255,
                    "Pixel quiet active capsule keeps an opaque surface instead of fading the bitmap alpha");

                foreach (var materialSkin in new[]
                {
                    PaperSkins.Mica, PaperSkins.Acrylic, PaperSkins.ClearAcrylic,
                    PaperSkins.TracingPaper, PaperSkins.Aero
                })
                {
                    controller.State.PaperSkin = materialSkin;
                    controller.State.MatchAuxiliaryMaterialStrength = false;
                    Theme.Invalidate();
                    var activeMaterial = new SkinBorder
                    {
                        IsCapsule = true, Width = 80, Height = 40,
                        Background = Brushes.Transparent, BorderThickness = new Thickness(),
                        CornerRadius = new CornerRadius(8)
                    };
                    activeMaterial.IsEdgeActiveMaterial = false;
                    var opaqueResting = Center(activeMaterial).A;
                    activeMaterial.IsEdgeActiveMaterial = true;
                    var opaqueActive = Center(activeMaterial).A;
                    Program.Assert(opaqueResting == 255 && opaqueActive == 255,
                        $"{materialSkin}: full-material OFF has no transmission or active alpha compensation");

                    controller.State.MatchAuxiliaryMaterialStrength = true;
                    activeMaterial.RefreshSkin();
                    activeMaterial.IsEdgeActiveMaterial = false;
                    var restingAlpha = Center(activeMaterial).A;
                    activeMaterial.IsEdgeActiveMaterial = true;
                    var activeAlpha = Center(activeMaterial).A;
                    Program.Assert(activeAlpha > restingAlpha,
                        $"{materialSkin}: full-material DockedActive receives only its contextual alpha compensation");
                }
            }
        }
        finally
        {
            (controller.State.Theme, controller.State.PaperSkin, controller.State.ColorScheme,
                controller.State.MatchAuxiliaryMaterialStrength, controller.State.HideSurfaceOutline,
                controller.State.MaterialTransparency) = saved;
            Theme.Invalidate();
        }
    }
}
