using System.Text;

namespace PaperTodo;

internal readonly record struct NoteImageSizePrediction(long JpegBytes, long PngBytes);

// A coarse size hint from information already available while composing the note.
// No output rasterization, pixel reads, sampling, encoder, or retained image buffers.
internal sealed class NoteImageSizeEstimate
{
    private double _wideText;
    private double _narrowText;
    private double _kanaText;
    private double _hangulText;
    private double _codeText;
    private int _quotes;
    private int _checkboxes;
    private double _imageJpeg;
    private double _imagePng;

    internal void AddText(ReadOnlySpan<char> text, double fontSize, bool bold = false, bool code = false)
    {
        var scale = Math.Pow(fontSize / 18, 1.6) * (bold ? 1.08 : 1);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            var units = scale * (Rune.IsPunctuation(rune) ? .6 : 1);
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff or >= 0xff66 and <= 0xff9f)
                _kanaText += units;
            else if (value is >= 0xac00 and <= 0xd7a3 or >= 0x1100 and <= 0x11ff or >= 0x3130 and <= 0x318f)
                _hangulText += units;
            else if (value >= 0x2e80) _wideText += units;
            else if (code) _codeText += units;
            else _narrowText += units;
        }
    }

    internal void AddQuote() => _quotes++;
    internal void AddCheckbox() => _checkboxes++;

    internal void AddImage(long encodedBytes, string mime, double sourceWidth, double sourceHeight,
        double outputWidth, double outputHeight, NoteImageJpegInfo jpegInfo = default, NoteImagePngInfo pngInfo = default)
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
        var jpegSource = mime == "image/jpeg";
        var imageBytes = jpegSource && jpegInfo.PayloadBytes > 0 ? jpegInfo.PayloadBytes :
            !jpegSource && pngInfo.PayloadBytes > 0 ? pngInfo.PayloadBytes : encodedBytes;
        var density = Math.Log(Math.Clamp(imageBytes / (sourceWidth * sourceHeight), .001, 4));
        var scale = Math.Log(Math.Clamp(Math.Sqrt(area / (sourceWidth * sourceHeight)), .01, 100));
        var jpegRate = jpegSource
            ? Rate(density, scale, -.835228, .396450, -.098479, 0, 0, .030032, 0, .178976)
            : Rate(density, scale, -2.062611, .555156, .658371, .088997, -.005942, -.176980, -.057669, .121336);
        var pngRate = jpegSource
            ? Rate(density, scale, .609807, -.381840, -.418132, 0, 0, .039313, 0, .162505)
            : Rate(density, scale, -.044796, 1.046804, .022870, -.133692, -.026168, -.038347, .110734, .168795);
        // Almost incompressible RGB PNGs contain information the broad log fit underestimates.
        if (!jpegSource && density > Math.Log(2.8) && scale >= 0)
            jpegRate = Math.Max(jpegRate, .8 / Math.Exp(scale));
        // JPEG quantization can leave small color variations even in very smooth images.
        // A source-size-only fit otherwise severely underestimates their lossless PNG size.
        if (jpegSource && density > Math.Log(.025))
            pngRate = Math.Max(pngRate, Math.Min(.4, 8 * Math.Exp(density)));
        if (jpegSource && density > Math.Log(.5))
            jpegRate = Math.Max(jpegRate, .85 * Math.Exp(density) * Math.Min(1, Math.Exp(scale)));
        if (jpegSource && jpegInfo.PayloadBytes > 0 && jpegInfo.Quantization is >= 2 and <= 60 &&
            jpegInfo.Sampling is >= 1 and <= 4)
        {
            var payload = jpegInfo.PayloadBytes / (sourceWidth * sourceHeight);
            var d = Math.Log(Math.Clamp(payload, .001, 4));
            var q = Math.Log(jpegInfo.Quantization);
            var h = Math.Log(jpegInfo.Sampling);
            jpegRate = HeaderRate(d, scale, q, h, JpegCoefficients);
            // Near a flat JPEG's coding floor, the large-image fit has little evidence.
            // Keep the original PNG estimate there instead of extrapolating it upwards.
            if (payload > 1.5 * (.01171875 + .015625 / jpegInfo.Sampling))
                pngRate = HeaderRate(d, scale, q, h, PngCoefficients);
        }
        if (!jpegSource && pngInfo.PayloadBytes > 0 && !pngInfo.Interlaced && pngInfo.BitDepth <= 8 &&
            (pngInfo.ColorType == 6 || (!pngInfo.HasAlpha && pngInfo.ColorType is 0 or 3)))
        {
            var candidate = Math.Clamp(PngSourceRate(density, scale, pngInfo, PngSourceJpegCoefficients), .015, 2);
            // An alpha channel does not reveal how much of the picture is transparent.
            jpegRate = pngInfo.HasAlpha ? Math.Sqrt(Math.Clamp(jpegRate, .015, 2) * candidate) : candidate;
            pngRate = PngSourceRate(density, scale, pngInfo, PngSourcePngCoefficients);
            if (pngInfo.ColorType == 0 && pngInfo.BitDepth == 1)
                jpegRate = BilevelJpegRate(density, scale);
        }
        _imageJpeg += area * Math.Clamp(jpegRate, .015, 2);
        _imagePng += area * Math.Clamp(pngRate, .001, 3);
    }

    // Offline fits; coefficient order follows HeaderRate. Calibration history: Decisions D-066.
    private static ReadOnlySpan<double> JpegCoefficients =>
        [-1.60994119, 0.88556676, 0.09386431, -0.38267748, -0.07381068, 0.44077617, 0.35345300, 0.00161109, -0.01643236, 0.24064990, 0.15136109, -0.06327877, 0.07220036, 0.00811685, 0.09241747, -0.04516633, -0.01710289, 0.06072375, 0.01422516];
    private static ReadOnlySpan<double> PngCoefficients =>
        [0.37924125, 0.69088820, -0.09768496, -0.34679741, -0.23730641, 0.18765492, 0.14634589, 0.01238187, -0.20349731, 0.10208420, 0.14276487, -0.02092474, 0.03759470, 0.04607198, 0.00047533, 0.05756559, 0.04366954, 0.01147284, 0.01625308];

    private static double HeaderRate(double d, double s, double q, double h, ReadOnlySpan<double> c) =>
        Math.Exp(c[0] + d * (c[1] + d * (c[2] + d * c[13])) + s * (c[3] + s * c[4]) +
            d * s * c[5] + q * (c[6] + q * c[7]) + d * q * c[8] + s * q * c[9] +
            h * (c[10] + d * c[11] + s * c[12]) + d * d * s * c[14] + d * d * q * c[15] +
            d * q * q * c[16] + d * s * q * c[17] + s * s * q * c[18]);

    private static ReadOnlySpan<double> PngSourceJpegCoefficients =>
        [-1.60944610, 0.60368653, 0.11481673, -0.28348935, -0.15656912, 0.21924539, 0.60761837, 0.11351605, 0.30361767, -0.30437676, -0.05421878, -0.09811950, 0.67634605, -0.09697836, 0.32640004, -0.00611967, -0.04777830, -0.47781888, 0.01316901, 0.02177283, -0.04696148];
    private static ReadOnlySpan<double> PngSourcePngCoefficients =>
        [0.01281997, 0.95503125, -0.01497241, -0.15619905, 0.02934515, 0.37029901, 0.14264137, -0.07857718, 0.10608676, -0.20707307, -0.03287594, -0.10505387, 0.70804490, -0.16859696, 0.03928826, 0.03458994, -0.03434166, -0.35769595, -0.00279873, 0.04886133, -0.06660659];

    private static double PngSourceRate(double d, double s, NoteImagePngInfo info, ReadOnlySpan<double> c)
    {
        var gray = info.ColorType is 0 or 4 ? 1d : 0;
        var palette = info.ColorType == 3 ? 1d : 0;
        var alpha = info.HasAlpha ? 1d : 0;
        var colors = palette * Math.Log(Math.Max(1, info.PaletteColors) / 256d);
        var depth = Math.Log(info.BitDepth / 8d);
        return Math.Exp(c[0] + d * (c[1] + d * (c[2] + d * c[18])) + s * (c[3] + s * c[4]) +
            d * s * (c[5] + d * c[19] + s * c[20]) + gray * (c[6] + d * c[7] + s * c[8]) +
            alpha * (c[9] + d * c[10] + s * c[11]) + palette * (c[12] + d * c[13] + s * c[14]) +
            colors * (c[15] + d * c[16]) + depth * c[17]);
    }

    // Both thresholded and dithered 1-bit sources: dense dithering creates costly JPEG edges.
    private static double BilevelJpegRate(double d, double s)
    {
        ReadOnlySpan<double> c = [0.64532969, 0.05081215, -0.22253955, -0.01968963, 2.19297746, -0.66775246, 1.09827830, 0.10454234, -0.13787834];
        return Math.Exp(c[0] + d * (c[1] + d * (c[2] + d * c[3])) + s * (c[4] + s * c[5]) +
            d * s * (c[6] + d * c[7] + s * c[8]));
    }

    private static double Rate(double density, double scale,
        double a, double b, double c, double d, double e, double f, double g, double h) =>
        Math.Exp(a + density * (b + density * (c + density * (d + density * e))) +
            f * scale + g * scale * scale + h * density * scale);

    internal NoteImageSizePrediction Finish(int width, int height)
    {
        var area = (double)width * height;
        // Text coefficients target the fixed export fonts, not arbitrary raster images.
        return new NoteImageSizePrediction(
            (long)Math.Round(3500 + area * .012 + _wideText * 760 + _narrowText * 330 +
                _kanaText * 520 + _hangulText * 600 + _codeText * 350 + _quotes * 2000 + _checkboxes * 950 + _imageJpeg),
            (long)Math.Round(3500 + area * .0015 + _wideText * 550 + _narrowText * 235 +
                _kanaText * 425 + _hangulText * 345 + _codeText * 275 + _quotes * 1700 + _checkboxes * 1400 + _imagePng));
    }
}
