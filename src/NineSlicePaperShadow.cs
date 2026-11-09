using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

// One small, Gaussian-blurred rounded-rectangle alpha mask per shadow recipe / DPI / radius.
// The eight visible nine-slice tiles are shared by every paper with the same recipe. Resizing
// only stretches the straight sides; the corners and blur are never recomputed for the size.
internal sealed class NineSlicePaperShadow
{
    private const double Gutter = EdgeCapsuleLayout.WindowChromeMargin;
    private const int MiddlePixels = 4;
    private const int MaxCachedRecipes = 32;

    private readonly record struct Key(
        double Blur, double Depth, double Opacity, double Radius,
        double ScaleX, double ScaleY, bool CapsuleShape);

    private static readonly Dictionary<Key, NineSlicePaperShadow> Cache = new();
    private static readonly Queue<Key> CacheOrder = new();

    private readonly BitmapSource[] _tiles;
    private readonly double _cornerWidth;
    private readonly double _cornerHeight;

    private NineSlicePaperShadow(Key key)
    {
        var sideX = (int)Math.Ceiling((Gutter + key.Radius + key.Blur) * key.ScaleX);
        var sideY = (int)Math.Ceiling((Gutter + key.Radius + key.Blur) * key.ScaleY);
        var middleX = Math.Max(MiddlePixels, (int)Math.Ceiling(MiddlePixels * key.ScaleX));
        var middleY = Math.Max(MiddlePixels, (int)Math.Ceiling(MiddlePixels * key.ScaleY));

        var width = sideX * 2 + middleX;
        var fullHeight = sideY * 2 + middleY;
        // The 46-DIP capsule has only 30 DIP of actual surface between its gutters.
        // Capture its real short silhouette instead of shrinking a tall rounded template.
        var height = key.CapsuleShape
            ? Math.Min(fullHeight, Math.Max(middleY + 2,
                (int)Math.Round(PaperLayoutDefaults.CapsuleHeight * key.ScaleY)))
            : fullHeight;
        height -= (height - middleY) % 2; // Keep top and bottom cut rows symmetric at fractional DPI.
        var cutY = (height - middleY) / 2;

        var bitmap = MakeBlurredMask(width, height, key);
        _cornerWidth = sideX / key.ScaleX;
        _cornerHeight = cutY / key.ScaleY;

        _tiles = new BitmapSource[8];
        _tiles[0] = Crop(bitmap, 0, 0, sideX, cutY);                      // top-left
        _tiles[1] = Crop(bitmap, sideX, 0, middleX, cutY);                // top
        _tiles[2] = Crop(bitmap, sideX + middleX, 0, sideX, cutY);       // top-right
        _tiles[3] = Crop(bitmap, 0, cutY, sideX, middleY);               // left
        _tiles[4] = Crop(bitmap, sideX + middleX, cutY, sideX, middleY); // right
        _tiles[5] = Crop(bitmap, 0, cutY + middleY, sideX, cutY);        // bottom-left
        _tiles[6] = Crop(bitmap, sideX, cutY + middleY, middleX, cutY);  // bottom
        _tiles[7] = Crop(bitmap, sideX + middleX, cutY + middleY, sideX, cutY);
    }

    internal static NineSlicePaperShadow Get(
        double blur, double depth, double opacity, double radius, DpiScale dpi, bool capsuleShape)
    {
        // Keep the WPF chrome's corner animation smooth, but reuse only three
        // shadow masks between the paper (16 DIP) and capsule (12 DIP) endpoints.
        // Cold 200% DPI sweeps showed meaningful repeated-mask creation costs.
        var roundedRadius = radius < 13 ? 12 : radius < 15 ? 14 : 16;
        var key = new Key(blur, depth, opacity, roundedRadius,
            dpi.DpiScaleX, dpi.DpiScaleY, capsuleShape);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var result = new NineSlicePaperShadow(key);
        if (Cache.Count == MaxCachedRecipes) Cache.Remove(CacheOrder.Dequeue());
        Cache.Add(key, result);
        CacheOrder.Enqueue(key);
        return result;
    }

    internal void Draw(DrawingContext dc, Rect surface)
    {
        // OnRender is allowed to paint into the owner's existing 8-DIP transparent HWND
        // gutter. The bitmap includes the old default 315-degree down/right depth.
        var x0 = surface.Left - Gutter;
        var y0 = surface.Top - Gutter;
        var x3 = surface.Right + Gutter;
        var y3 = surface.Bottom + Gutter;
        var xWidth = x3 - x0;
        var yHeight = y3 - y0;
        if (xWidth <= 0 || yHeight <= 0) return;

        var width = Math.Min(_cornerWidth, xWidth / 2);
        var height = Math.Min(_cornerHeight, yHeight / 2);
        var x1 = x0 + width;
        var x2 = x3 - width;
        var y1 = y0 + height;
        var y2 = y3 - height;

        dc.DrawImage(_tiles[0], new Rect(x0, y0, width, height));
        if (x2 > x1) dc.DrawImage(_tiles[1], new Rect(x1, y0, x2 - x1, height));
        dc.DrawImage(_tiles[2], new Rect(x2, y0, width, height));
        if (y2 > y1)
        {
            dc.DrawImage(_tiles[3], new Rect(x0, y1, width, y2 - y1));
            dc.DrawImage(_tiles[4], new Rect(x2, y1, width, y2 - y1));
        }
        dc.DrawImage(_tiles[5], new Rect(x0, y2, width, height));
        if (x2 > x1) dc.DrawImage(_tiles[6], new Rect(x1, y2, x2 - x1, height));
        dc.DrawImage(_tiles[7], new Rect(x2, y2, width, height));
        // No center tile: the paper owns its fill, even when its outline is hidden.
    }

    private static BitmapSource Crop(BitmapSource source, int x, int y, int width, int height)
    {
        var tile = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        tile.Freeze();
        return tile;
    }

    private static BitmapSource MakeBlurredMask(int width, int height, Key key)
    {
        var scaleX = key.ScaleX;
        var scaleY = key.ScaleY;
        var halfWidth = width / (2 * scaleX) - Gutter;
        var halfHeight = height / (2 * scaleY) - Gutter;
        var radius = Math.Min(key.Radius, Math.Min(halfWidth, halfHeight));
        var centerX = width / (2 * scaleX);
        var centerY = height / (2 * scaleY);
        // DropShadowEffect's default direction is 315 degrees (down and right).
        var shift = key.Depth / Math.Sqrt(2);
        var pixelDip = 1 / Math.Max(scaleX, scaleY);
        var mask = new double[width * height];

        // Pixel-coverage rounded rectangle. Only the silhouette is needed: the original
        // DropShadowEffect was attached to the whole paper, although the shadow outside
        // its opaque surface is determined by the rounded outer silhouette.
        for (var y = 0; y < height; y++)
        {
            var dy = Math.Abs((y + 0.5) / scaleY - shift - centerY) - (halfHeight - radius);
            for (var x = 0; x < width; x++)
            {
                var dx = Math.Abs((x + 0.5) / scaleX - shift - centerX) - (halfWidth - radius);
                var ax = Math.Max(dx, 0);
                var ay = Math.Max(dy, 0);
                var distance = Math.Min(Math.Max(dx, dy), 0) +
                    Math.Sqrt(ax * ax + ay * ay) - radius;
                mask[y * width + x] = Math.Clamp(0.5 - distance / pixelDip, 0, 1);
            }
        }

        // Separable Gaussian: work is done once per cached recipe, not per paper resize.
        // Three standard deviations fit inside BlurRadius and give a smooth, finite tail.
        var horizontalKernel = Kernel(Math.Max(0.5, key.Blur * scaleX / 3));
        var verticalKernel = Kernel(Math.Max(0.5, key.Blur * scaleY / 3));
        var temp = new double[mask.Length];
        var xRadius = horizontalKernel.Length / 2;
        var yRadius = verticalKernel.Length / 2;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var k = -xRadius; k <= xRadius; k++)
                    if (x + k >= 0 && x + k < width)
                        sum += mask[row + x + k] * horizontalKernel[k + xRadius];
                temp[row + x] = sum;
            }
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var k = -yRadius; k <= yRadius; k++)
                    if (y + k >= 0 && y + k < height)
                        sum += temp[(y + k) * width + x] * verticalKernel[k + yRadius];
                // Layered HWNDs pass input through zero-alpha pixels before WM_NCHITTEST.
                // Keep the expanded paper's existing resize gutter reachable, including
                // the rounded corners. Capsules keep their original transparent shape.
                var alpha = (byte)Math.Clamp(Math.Round(sum * key.Opacity * 255), 0, 255);
                pixels[(y * width + x) * 4 + 3] =
                    !key.CapsuleShape && alpha == 0 ? (byte)1 : alpha;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96 * scaleX, 96 * scaleY,
            PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static double[] Kernel(double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var weights = new double[radius * 2 + 1];
        var sum = 0.0;
        for (var i = -radius; i <= radius; i++)
        {
            var weight = Math.Exp(-i * i / (2 * sigma * sigma));
            weights[i + radius] = weight;
            sum += weight;
        }
        for (var i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    }
}
