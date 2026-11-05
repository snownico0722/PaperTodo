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
        double outputWidth, double outputHeight, NoteImageJpegInfo jpegInfo = default)
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
        var imageBytes = jpegSource && jpegInfo.PayloadBytes > 0 ? jpegInfo.PayloadBytes : encodedBytes;
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
        _imageJpeg += area * Math.Clamp(jpegRate, .015, 2);
        _imagePng += area * Math.Clamp(pngRate, .001, 3);
    }

    // Offline fits; coefficient order matches HeaderRate and jpeg_calibrate.py.
    private static ReadOnlySpan<double> JpegCoefficients =>
        [-1.60994119, 0.88556676, 0.09386431, -0.38267748, -0.07381068, 0.44077617, 0.35345300, 0.00161109, -0.01643236, 0.24064990, 0.15136109, -0.06327877, 0.07220036, 0.00811685, 0.09241747, -0.04516633, -0.01710289, 0.06072375, 0.01422516];
    private static ReadOnlySpan<double> PngCoefficients =>
        [0.37924125, 0.69088820, -0.09768496, -0.34679741, -0.23730641, 0.18765492, 0.14634589, 0.01238187, -0.20349731, 0.10208420, 0.14276487, -0.02092474, 0.03759470, 0.04607198, 0.00047533, 0.05756559, 0.04366954, 0.01147284, 0.01625308];

    private static double HeaderRate(double d, double s, double q, double h, ReadOnlySpan<double> c) =>
        Math.Exp(c[0] + d * (c[1] + d * (c[2] + d * c[13])) + s * (c[3] + s * c[4]) +
            d * s * c[5] + q * (c[6] + q * c[7]) + d * q * c[8] + s * q * c[9] +
            h * (c[10] + d * c[11] + s * c[12]) + d * d * s * c[14] + d * d * q * c[15] +
            d * q * q * c[16] + d * s * q * c[17] + s * s * q * c[18]);

    private static double Rate(double density, double scale,
        double a, double b, double c, double d, double e, double f, double g, double h) =>
        Math.Exp(a + density * (b + density * (c + density * (d + density * e))) +
            f * scale + g * scale * scale + h * density * scale);

    internal NoteImageSizePrediction Finish(int width, int height)
    {
        var area = (double)width * height;
        // Text coefficients target the fixed export fonts, not arbitrary raster images.
        return new NoteImageSizePrediction(
            (long)Math.Round(3500 + area * .012 + _wideText * 760 + _narrowText * 350 + _imageJpeg),
            (long)Math.Round(3500 + area * .0015 + _wideText * 550 + _narrowText * 275 + _imagePng));
    }
}
