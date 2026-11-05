using System.Text;

namespace PaperTodo;

internal readonly record struct NoteImageSizePrediction(long JpegBytes, long PngBytes);

// A coarse size hint from information already available while composing the note.
// No output rasterization, pixel reads, sampling, encoder, or retained image buffers.
internal sealed class NoteImageSizeEstimate
{
    private double _wideText;
    private double _narrowText;
    private double _imageJpeg;
    private double _imagePng;

    internal void AddText(string text, double fontSize, bool bold = false)
    {
        var scale = Math.Pow(fontSize / 18, 1.6) * (bold ? 1.08 : 1);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            var units = scale * (Rune.IsPunctuation(rune) ? .6 : 1);
            if (rune.Value >= 0x2e80) _wideText += units;
            else _narrowText += units;
        }
    }

    internal void AddImage(long encodedBytes, string mime, double sourceWidth, double sourceHeight,
        double outputWidth, double outputHeight)
    {
        var area = outputWidth * outputHeight;
        if (area <= 0) return;
        if (encodedBytes <= 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            (mime != "image/png" && mime != "image/jpeg"))
        {
            // Unsupported/unknown source metadata: a broad mixed-image prior, not a pixel scan.
            _imageJpeg += area * .25;
            _imagePng += area * 1.25;
            return;
        }
        var density = Math.Log(Math.Clamp(encodedBytes / (sourceWidth * sourceHeight), .001, 4));
        var scale = Math.Log(Math.Clamp(Math.Sqrt(area / (sourceWidth * sourceHeight)), .01, 100));
        var jpegSource = mime == "image/jpeg";
        var jpegRate = jpegSource
            ? Rate(density, scale, -.835228, .396450, -.098479, .030032, .178976)
            : Rate(density, scale, -1.526397, .320193, -.008417, -.119611, .121336);
        var pngRate = jpegSource
            ? Rate(density, scale, .609807, -.381840, -.418132, .039313, .162505)
            : Rate(density, scale, .076086, .747182, -.033117, -.148506, .168795);
        // Almost incompressible RGB PNGs contain information the broad log fit underestimates.
        if (!jpegSource && density > Math.Log(2.8) && scale >= 0)
            jpegRate = Math.Max(jpegRate, .8 / Math.Exp(scale));
        _imageJpeg += area * Math.Clamp(jpegRate, .015, 2);
        _imagePng += area * Math.Clamp(pngRate, .001, 3);
    }

    private static double Rate(double density, double scale, double a, double b, double c, double d, double e) =>
        Math.Exp(a + b * density + c * density * density + d * scale + e * density * scale);

    internal NoteImageSizePrediction Finish(int width, int height)
    {
        var area = (double)width * height;
        // Text coefficients target the fixed export fonts, not arbitrary raster images.
        return new NoteImageSizePrediction(
            (long)Math.Round(3500 + area * .012 + _wideText * 760 + _narrowText * 350 + _imageJpeg),
            (long)Math.Round(3500 + area * .0015 + _wideText * 550 + _narrowText * 275 + _imagePng));
    }
}
