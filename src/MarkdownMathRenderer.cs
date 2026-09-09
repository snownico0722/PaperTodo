using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PaperTodo;

internal readonly record struct MarkdownMathBitmap(
    BitmapSource Source,
    double Width,
    double Height,
    double Baseline);

/// <summary>
/// Thin managed owner for the RaTeX native bridge. Formula source and the WPF document remain the
/// authority; this class only caches detached, frozen PNG decodes. Any native/load/parse failure
/// returns false so the element generator leaves the original Markdown visible.
/// </summary>
internal static class MarkdownMathRenderer
{
    private const string NativeLibraryName = "papertodo_math.dll";
    private const int NativeSuccess = 0;
    private const int MaximumFormulaBytes = 64 * 1024;
    private const int MaximumPngBytes = 32 * 1024 * 1024;
    private const long MaximumDecodedCacheBytes = 48L * 1024 * 1024;
    private const int MaximumCacheEntries = 64;

    private readonly record struct CacheKey(
        string Formula,
        bool Display,
        int FontSizeTenths,
        uint Argb);

    private sealed record CacheEntry(
        CacheKey Key,
        MarkdownMathBitmap Bitmap,
        long DecodedBytes);

    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static long _decodedCacheBytes;
    private static bool _nativeUnavailable;

    public static bool TryRender(
        string formula,
        bool display,
        double fontSize,
        Color color,
        out MarkdownMathBitmap bitmap)
    {
        bitmap = default;
        if (string.IsNullOrWhiteSpace(formula) ||
            !double.IsFinite(fontSize) ||
            fontSize < 4 ||
            fontSize > 256)
        {
            return false;
        }

        var utf8Length = Encoding.UTF8.GetByteCount(formula);
        if (utf8Length <= 0 || utf8Length > MaximumFormulaBytes)
        {
            return false;
        }

        var fontSizeTenths = (int)Math.Round(fontSize * 10, MidpointRounding.AwayFromZero);
        var key = new CacheKey(
            formula,
            display,
            fontSizeTenths,
            ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Lru.Remove(cached);
                Lru.AddFirst(cached);
                bitmap = cached.Value.Bitmap;
                return true;
            }

            if (_nativeUnavailable)
            {
                return false;
            }
        }

        MarkdownMathBitmap rendered;
        try
        {
            if (!TryRenderNative(
                    formula,
                    display,
                    fontSizeTenths / 10f,
                    color,
                    out rendered))
            {
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            MarkNativeUnavailable();
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            MarkNativeUnavailable();
            return false;
        }
        catch (BadImageFormatException)
        {
            MarkNativeUnavailable();
            return false;
        }
        catch (SEHException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        var decodedBytes = (long)rendered.Source.PixelWidth * rendered.Source.PixelHeight * 4;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var raced))
            {
                Lru.Remove(raced);
                Lru.AddFirst(raced);
                bitmap = raced.Value.Bitmap;
                return true;
            }

            var entry = new CacheEntry(key, rendered, decodedBytes);
            var node = Lru.AddFirst(entry);
            Cache[key] = node;
            _decodedCacheBytes += decodedBytes;
            TrimCacheLocked();
        }

        bitmap = rendered;
        return true;
    }

    private static unsafe bool TryRenderNative(
        string formula,
        bool display,
        float fontSize,
        Color color,
        out MarkdownMathBitmap bitmap)
    {
        bitmap = default;
        var source = Encoding.UTF8.GetBytes(formula);
        IntPtr output = IntPtr.Zero;
        nuint outputLength = 0;
        try
        {
            fixed (byte* sourcePointer = source)
            {
                var status = papertodo_math_render(
                    sourcePointer,
                    (nuint)source.Length,
                    fontSize,
                    display ? (byte)1 : (byte)0,
                    color.R,
                    color.G,
                    color.B,
                    color.A,
                    out output,
                    out outputLength,
                    out var logicalWidth,
                    out var logicalHeight,
                    out var logicalBaseline);
                if (status != NativeSuccess ||
                    output == IntPtr.Zero ||
                    outputLength == 0 ||
                    outputLength > MaximumPngBytes ||
                    !float.IsFinite(logicalWidth) ||
                    !float.IsFinite(logicalHeight) ||
                    !float.IsFinite(logicalBaseline) ||
                    logicalWidth <= 0 ||
                    logicalHeight <= 0 ||
                    logicalWidth > 4096 ||
                    logicalHeight > 4096)
                {
                    return false;
                }

                var length = checked((int)outputLength);
                var png = GC.AllocateUninitializedArray<byte>(length);
                Marshal.Copy(output, png, 0, length);
                using var stream = new MemoryStream(png, writable: false);
                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    return false;
                }

                var frame = decoder.Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                {
                    return false;
                }
                frame.Freeze();
                bitmap = new MarkdownMathBitmap(
                    frame,
                    logicalWidth,
                    logicalHeight,
                    Math.Clamp(logicalBaseline, 0, logicalHeight));
                return true;
            }
        }
        finally
        {
            if (output != IntPtr.Zero && outputLength > 0)
            {
                papertodo_math_free(output, outputLength);
            }
        }
    }

    private static void MarkNativeUnavailable()
    {
        lock (Gate)
        {
            _nativeUnavailable = true;
        }
    }

    private static void TrimCacheLocked()
    {
        while (Lru.Count > MaximumCacheEntries ||
               _decodedCacheBytes > MaximumDecodedCacheBytes)
        {
            var last = Lru.Last;
            if (last == null)
            {
                break;
            }

            Lru.RemoveLast();
            Cache.Remove(last.Value.Key);
            _decodedCacheBytes -= last.Value.DecodedBytes;
        }
    }

    [DllImport(
        NativeLibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern unsafe int papertodo_math_render(
        byte* source,
        nuint sourceLength,
        float fontSize,
        byte display,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        out IntPtr output,
        out nuint outputLength,
        out float logicalWidth,
        out float logicalHeight,
        out float logicalBaseline);

    [DllImport(
        NativeLibraryName,
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true)]
    private static extern void papertodo_math_free(IntPtr data, nuint length);
}
