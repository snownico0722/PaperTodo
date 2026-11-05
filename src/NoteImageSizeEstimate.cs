namespace PaperTodo;

internal readonly record struct NoteImageSizePrediction(long JpegBytes, long PngBytes);

// Content statistics calibrated for the note export raster. No DCT/quantization, LZ matching, encoder or output stream.
// Every pixel participates; the eight-row window stays below PNG's 32 KiB history at 1200 px.
internal sealed class NoteImageSizeEstimate(int width)
{
    private readonly int[] _histogram = new int[256];
    private readonly HashSet<uint> _triplets = [];
    private readonly HashSet<ulong> _pairs = [];
    private readonly double[][] _planes = [new double[width * 8], new double[width * 8], new double[width * 8]];
    private readonly int[] _blockColors = new int[(width + 7) / 8];
    private readonly bool[] _activeBlocks = new bool[(width + 7) / 8];
    private readonly double[] _features = new double[19];

    // Feature slots: pixels; Sub-byte entropy; distinct RGB residuals/pairs; nonzero/
    // chromatic residual pixels; [log/sqrt/active] luma and chroma block differences;
    // nonzero/mask entropy; pair/triplet entropy interactions; 2x2/4x4 mean differences;
    // checker convolution. See tools/analysis/note-image-size for reproducible calibration.
    // Calibration is separate from prediction; coefficients target JPG quality 90 and RGB/Sub PNG.
    private static readonly double[] JpegWeights = [0.02620894134, 0, 0, 0, 0, 0, -16.02240855, 2.953482557, 10.40373536, 1.892996141, -1.023628099, -6.112995095, 0, 0, 0, 0, 12.33110438, 0.4453119676, 14.30932592];
    private static readonly double[] PngWeights = [0.007179149902, 0, -1.836534969, 1.836375021, -0.3257814573, 0.3874675017, 0, 0, 0, 0, 0, 0, 0.2761414786, 0.2478138984, 1.205872879, 0.9085711291, 0, 0, 0];
    internal long JpegBytes => Predict(JpegWeights, 0.015625, 1024);
    internal long PngBytes => Predict(PngWeights, 0.003, 128);

    private long Predict(double[] weights, double minimumPerPixel, int overhead)
    {
        double value = 0;
        for (var i = 0; i < weights.Length; i++) value += weights[i] * _features[i];
        return checked((long)Math.Ceiling(Math.Max(_features[0] * minimumPerPixel, value) + overhead));
    }

    internal void AddBgrRows(byte[] pixels, int rows)
    {
        Span<double> low = stackalloc double[16];
        for (var top = 0; top < rows; top += 8)
        {
            var count = Math.Min(8, rows - top);
            Array.Clear(_histogram);
            Array.Clear(_activeBlocks);
            _triplets.Clear(); _pairs.Clear();
            for (var y = 0; y < count; y++)
            {
                var previousR = 0; var previousG = 0; var previousB = 0;
                uint previousCode = 0;
                ulong previousPair = 0;
                for (var x = 0; x < width; x++)
                {
                    var offset = ((top + y) * width + x) * 3;
                    var b = pixels[offset]; var g = pixels[offset + 1]; var r = pixels[offset + 2];
                    var dr = (r - previousR) & 255; var dg = (g - previousG) & 255; var db = (b - previousB) & 255;
                    previousR = r; previousG = g; previousB = b;
                    if (dr == dg && dg == db) _histogram[dr] += 3;
                    else { _histogram[dr]++; _histogram[dg]++; _histogram[db]++; }
                    var code = (uint)(dr | (dg << 8) | (db << 16));
                    // Consecutive duplicates are already present; this changes no statistics.
                    if (x == 0 || code != previousCode) _triplets.Add(code);
                    if (x > 0)
                    {
                        var pair = code | ((ulong)previousCode << 24);
                        if (x == 1 || pair != previousPair) _pairs.Add(pair);
                        previousPair = pair;
                    }
                    previousCode = code;
                    if (code != 0) _features[4]++;
                    if (dr != dg || dg != db) _features[5]++;
                    var block = x / 8;
                    var color = b | (g << 8) | (r << 16);
                    if (y == 0 && x % 8 == 0) _blockColors[block] = color;
                    else if (color != _blockColors[block]) _activeBlocks[block] = true;
                    var luma = (77 * r + 150 * g + 29 * b) / 256.0;
                    var index = y * width + x;
                    _planes[0][index] = luma;
                    _planes[1][index] = b - luma;
                    _planes[2][index] = r - luma;
                }
            }
            var bytes = count * width * 3;
            double entropy = 0;
            foreach (var frequency in _histogram)
                if (frequency > 0) entropy += frequency * Math.Log2((double)bytes / frequency) / 8;
            var zero = _histogram[0]; var nonzero = bytes - zero;
            var mask = zero == 0 || nonzero == 0 ? 0 :
                (zero * Math.Log2((double)bytes / zero) + nonzero * Math.Log2((double)bytes / nonzero)) / 8;
            _features[0] += count * width;
            _features[1] += entropy;
            _features[2] += _triplets.Count;
            _features[3] += _pairs.Count;
            _features[12] += entropy - mask;
            _features[13] += mask;
            _features[14] += _pairs.Count * entropy / bytes;
            _features[15] += _triplets.Count * entropy / bytes;
            // Repeat only the final row for complete spatial blocks, then weight by real rows.
            foreach (var plane in _planes)
                for (var y = count; y < 8; y++) Array.Copy(plane, (count - 1) * width, plane, y * width, width);
            for (var x = 0; x < width; x += 8)
            {
                // All pixels were inspected above. A constant block contributes exactly zero
                // to every spatial-difference feature, so no transform arithmetic is needed.
                if (!_activeBlocks[x / 8]) continue;
                for (var channel = 0; channel < 3; channel++)
                {
                    var plane = _planes[channel];
                    double difference = 0;
                    for (var y = 0; y < 8; y++)
                        for (var dx = 0; dx < 8; dx++)
                        {
                            var index = y * width + Math.Min(x + dx, width - 1);
                            if (dx < 7) difference += Math.Abs(plane[index] - plane[y * width + Math.Min(x + dx + 1, width - 1)]);
                            if (y < 7) difference += Math.Abs(plane[index] - plane[index + width]);
                        }
                    difference /= 112;
                    var offset = channel == 0 ? 6 : 9;
                    _features[offset] += Math.Log2(1 + difference) * count / 8;
                    _features[offset + 1] += Math.Sqrt(difference) * count / 8;
                    if (difference > 0) _features[offset + 2] += count / 8.0;
                }
                var luminance = _planes[0];
                for (var scale = 2; scale <= 4; scale *= 2)
                {
                    var side = 8 / scale;
                    for (var y = 0; y < side; y++)
                        for (var dx = 0; dx < side; dx++)
                        {
                            double total = 0;
                            for (var yy = 0; yy < scale; yy++)
                                for (var xx = 0; xx < scale; xx++)
                                    total += luminance[(y * scale + yy) * width + Math.Min(x + dx * scale + xx, width - 1)];
                            low[y * side + dx] = total / (scale * scale);
                        }
                    double difference = 0;
                    for (var y = 0; y < side; y++)
                        for (var dx = 0; dx < side; dx++)
                        {
                            if (dx + 1 < side) difference += Math.Abs(low[y * side + dx] - low[y * side + dx + 1]);
                            if (y + 1 < side) difference += Math.Abs(low[y * side + dx] - low[(y + 1) * side + dx]);
                        }
                    _features[scale == 2 ? 16 : 17] += Math.Log2(1 + difference / (2 * side * (side - 1))) * count / 8;
                }
                double checker = 0;
                for (var y = 0; y < 8; y += 2)
                    for (var dx = 0; dx < 8; dx += 2)
                    {
                        var a = y * width + Math.Min(x + dx, width - 1);
                        var b = y * width + Math.Min(x + dx + 1, width - 1);
                        checker += Math.Abs(luminance[a] - luminance[b] - luminance[a + width] + luminance[b + width]);
                    }
                _features[18] += Math.Log2(1 + checker / 64) * count / 8;
            }
        }
    }
}
