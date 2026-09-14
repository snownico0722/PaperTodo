using System;
using System.Windows.Media;

namespace PaperTodo;

// Surface color is NOT the semantic text palette. Each material has its own tint,
// transmission and diffusion, while text/icons and persisted color IDs stay unchanged.
internal readonly record struct MaterialPalette(Color Surface, Color NativeOverlay, Color Preview,
    byte TransmissionAlpha, double Diffusion)
{
    internal static MaterialPalette For(string skin, string scheme, bool dark, Color paper)
    {
        var neutral = scheme == ColorSchemes.Neutral;
        var hue = scheme switch
        {
            ColorSchemes.Warm => Color.FromRgb(197, 164, 111),
            ColorSchemes.Ink => Color.FromRgb(115, 153, 201),
            ColorSchemes.Forest => Color.FromRgb(121, 169, 142),
            ColorSchemes.Rose => Color.FromRgb(195, 136, 158),
            _ => Color.FromRgb(139, 168, 192)
        };
        var baseColor = dark ? Color.FromRgb(28, 30, 34) : Color.FromRgb(246, 247, 249);
        var (chroma, alpha, wash, blur) = skin switch
        {
            PaperSkins.Mica => (.065, dark ? 226 : 222, dark ? 25 : 20, 38d),
            PaperSkins.Acrylic => (.12, dark ? 165 : 145, dark ? 30 : 26, 26d),
            PaperSkins.ClearAcrylic => (.18, dark ? 95 : 75, dark ? 31 : 28, 12d),
            PaperSkins.LiquidGlass => (.09, dark ? 56 : 22, 0, 0d),
            PaperSkins.Aero => (.72, dark ? 54 : 27, 0, 0d),
            PaperSkins.TracingPaper => (.04, dark ? 226 : 211, 0, 18d),
            _ => (0d, 255, 0, 0d)
        };
        var surface = skin is PaperSkins.Paper or PaperSkins.Pixel ? paper :
            Mix(baseColor, hue, neutral && skin != PaperSkins.Aero ? 0 : chroma * (dark ? .62 : 1));
        // Preserve the neutral system skin exactly; Aero's neutral remains cool clear glass.
        if (neutral && PaperSkins.IsSystemMaterial(skin)) surface = paper;
        var overlay = Color.FromArgb((byte)(neutral ? 0 : wash), hue.R, hue.G, hue.B);
        // Preview color/alpha is stable across geometry and full/quiet modes. It does
        // not inherit the live scene's readiness or a size-dependent optical tint.
        var previewColor = skin is PaperSkins.Paper or PaperSkins.Pixel ? paper : Mix(paper, surface, .22);
        var preview = Color.FromArgb((byte)(dark ? 248 : 244), previewColor.R, previewColor.G, previewColor.B);
        return new(surface, overlay, preview, (byte)alpha, blur);
    }
    private static Color Mix(Color a, Color b, double weight) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R-a.R)*weight), (byte)Math.Round(a.G + (b.G-a.G)*weight),
        (byte)Math.Round(a.B + (b.B-a.B)*weight));
}
