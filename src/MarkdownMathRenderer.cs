using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

internal sealed record MarkdownMathDrawing(
    DrawingGroup Drawing,
    double Width,
    double Height,
    double Baseline);

/// <summary>
/// RaTeX owns parsing and layout; WPF owns final drawing. The native bridge returns a bounded
/// DisplayList protocol, then this class maps glyphs to the exact bundled KaTeX TTFs through
/// GlyphRun and maps rules/paths to WPF geometry. Formula source and AvalonEdit remain authoritative.
/// </summary>
internal static class MarkdownMathRenderer
{
    private const string NativeLibraryName = "papertodo_math.dll";
    private const int NativeSuccess = 0;
    private const int WireProtocolVersion = 1;
    private const int MaximumFormulaBytes = 64 * 1024;
    private const int MaximumNativeBytes = 32 * 1024 * 1024;
    private const int MaximumDisplayItems = 100_000;
    private const int MaximumPathCommands = 200_000;
    private const int MaximumCacheEntries = 128;

    private readonly record struct CacheKey(
        string Formula,
        bool Display,
        int FontSizeTenths,
        uint Argb,
        int PixelsPerDipThousandths);

    private sealed record CacheEntry(
        CacheKey Key,
        MarkdownMathDrawing Drawing);

    private static readonly IReadOnlyDictionary<string, string> PackagedFontFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AMS-Regular"] = "KaTeX_AMS-Regular.ttf",
            ["Caligraphic-Regular"] = "KaTeX_Caligraphic-Regular.ttf",
            ["Fraktur-Regular"] = "KaTeX_Fraktur-Regular.ttf",
            ["Fraktur-Bold"] = "KaTeX_Fraktur-Bold.ttf",
            ["Main-Bold"] = "KaTeX_Main-Bold.ttf",
            ["Main-BoldItalic"] = "KaTeX_Main-BoldItalic.ttf",
            ["Main-Italic"] = "KaTeX_Main-Italic.ttf",
            ["Main-Regular"] = "KaTeX_Main-Regular.ttf",
            ["Math-BoldItalic"] = "KaTeX_Math-BoldItalic.ttf",
            ["Math-Italic"] = "KaTeX_Math-Italic.ttf",
            ["SansSerif-Bold"] = "KaTeX_SansSerif-Bold.ttf",
            ["SansSerif-Italic"] = "KaTeX_SansSerif-Italic.ttf",
            ["SansSerif-Regular"] = "KaTeX_SansSerif-Regular.ttf",
            ["Script-Regular"] = "KaTeX_Script-Regular.ttf",
            ["Size1-Regular"] = "KaTeX_Size1-Regular.ttf",
            ["Size2-Regular"] = "KaTeX_Size2-Regular.ttf",
            ["Size3-Regular"] = "KaTeX_Size3-Regular.ttf",
            ["Size4-Regular"] = "KaTeX_Size4-Regular.ttf",
            ["Typewriter-Regular"] = "KaTeX_Typewriter-Regular.ttf"
        };

    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static readonly object FontGate = new();
    private static readonly Dictionary<string, GlyphTypeface?> PackagedFonts =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GlyphTypeface?> SystemFontsCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _nativeUnavailable;

    public static bool TryRender(
        string formula,
        bool display,
        double fontSize,
        Color color,
        double pixelsPerDip,
        out MarkdownMathDrawing drawing)
    {
        drawing = null!;
        if (string.IsNullOrWhiteSpace(formula) ||
            !double.IsFinite(fontSize) ||
            fontSize < 4 ||
            fontSize > 256 ||
            !double.IsFinite(pixelsPerDip))
        {
            return false;
        }

        var utf8Length = Encoding.UTF8.GetByteCount(formula);
        if (utf8Length <= 0 || utf8Length > MaximumFormulaBytes)
        {
            return false;
        }

        pixelsPerDip = Math.Clamp(pixelsPerDip, 0.5, 8.0);
        var fontSizeTenths = (int)Math.Round(fontSize * 10, MidpointRounding.AwayFromZero);
        var key = new CacheKey(
            formula,
            display,
            fontSizeTenths,
            ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
            (int)Math.Round(pixelsPerDip * 1000, MidpointRounding.AwayFromZero));
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Lru.Remove(cached);
                Lru.AddFirst(cached);
                drawing = cached.Value.Drawing;
                return true;
            }

            if (_nativeUnavailable)
            {
                return false;
            }
        }

        MarkdownMathDrawing rendered;
        try
        {
            if (!TryRenderNative(
                    formula,
                    display,
                    fontSizeTenths / 10f,
                    color,
                    (float)pixelsPerDip,
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
        catch (UnauthorizedAccessException)
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
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var raced))
            {
                Lru.Remove(raced);
                Lru.AddFirst(raced);
                drawing = raced.Value.Drawing;
                return true;
            }

            var node = Lru.AddFirst(new CacheEntry(key, rendered));
            Cache[key] = node;
            while (Lru.Count > MaximumCacheEntries)
            {
                var last = Lru.Last;
                if (last == null)
                {
                    break;
                }

                Lru.RemoveLast();
                Cache.Remove(last.Value.Key);
            }
        }

        drawing = rendered;
        return true;
    }

    private static unsafe bool TryRenderNative(
        string formula,
        bool display,
        float fontSize,
        Color color,
        float pixelsPerDip,
        out MarkdownMathDrawing drawing)
    {
        drawing = null!;
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
                    outputLength > MaximumNativeBytes ||
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
                var payload = GC.AllocateUninitializedArray<byte>(length);
                Marshal.Copy(output, payload, 0, length);
                return TryBuildDrawing(
                    payload,
                    display,
                    fontSize,
                    pixelsPerDip,
                    logicalWidth,
                    logicalHeight,
                    logicalBaseline,
                    out drawing);
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

    private static bool TryBuildDrawing(
        byte[] payload,
        bool display,
        double fontSize,
        float pixelsPerDip,
        double logicalWidth,
        double logicalHeight,
        double logicalBaseline,
        out MarkdownMathDrawing drawing)
    {
        drawing = null!;
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var protocolVersion) ||
            protocolVersion != WireProtocolVersion ||
            !root.TryGetProperty("display_list", out var list) ||
            list.ValueKind != JsonValueKind.Object ||
            !TryReadFinite(list, "width", 0, 4096, out var listWidth) ||
            !TryReadFinite(list, "height", 0, 4096, out var listHeight) ||
            !TryReadFinite(list, "depth", 0, 4096, out var listDepth) ||
            !list.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() > MaximumDisplayItems)
        {
            return false;
        }

        var padding = display ? 4.0 : 1.5;
        var expectedWidth = listWidth * fontSize + padding * 2;
        var expectedHeight = (listHeight + listDepth) * fontSize + padding * 2;
        var expectedBaseline = listHeight * fontSize + padding;
        if (Math.Abs(expectedWidth - logicalWidth) > 0.1 ||
            Math.Abs(expectedHeight - logicalHeight) > 0.1 ||
            Math.Abs(expectedBaseline - logicalBaseline) > 0.1)
        {
            return false;
        }

        var group = new DrawingGroup();
        var brushes = new Dictionary<uint, SolidColorBrush>();
        var totalPathCommands = 0;
        using (var context = group.Open())
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !TryReadBrush(item, brushes, out var brush))
                {
                    return false;
                }

                switch (typeElement.GetString())
                {
                    case "GlyphPath":
                        if (!TryDrawGlyph(context, item, brush, fontSize, padding, pixelsPerDip))
                        {
                            return false;
                        }
                        break;

                    case "Line":
                        if (!TryDrawLine(context, item, brush, fontSize, padding))
                        {
                            return false;
                        }
                        break;

                    case "Rect":
                        if (!TryDrawRect(context, item, brush, fontSize, padding))
                        {
                            return false;
                        }
                        break;

                    case "Path":
                        if (!TryDrawPath(
                                context,
                                item,
                                brush,
                                fontSize,
                                padding,
                                ref totalPathCommands))
                        {
                            return false;
                        }
                        break;

                    default:
                        return false;
                }
            }
        }

        if (group.CanFreeze)
        {
            group.Freeze();
        }

        drawing = new MarkdownMathDrawing(
            group,
            logicalWidth,
            logicalHeight,
            Math.Clamp(logicalBaseline, 0, logicalHeight));
        return true;
    }

    private static bool TryDrawGlyph(
        DrawingContext context,
        JsonElement item,
        Brush brush,
        double fontSize,
        double padding,
        float pixelsPerDip)
    {
        if (!TryReadFinite(item, "x", -4096, 4096, out var x) ||
            !TryReadFinite(item, "y", -4096, 4096, out var y) ||
            !TryReadFinite(item, "scale", 0.001, 64, out var scale) ||
            !item.TryGetProperty("font", out var fontElement) ||
            fontElement.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("char_code", out var charElement) ||
            charElement.ValueKind != JsonValueKind.Number ||
            !charElement.TryGetUInt32(out var charCode) ||
            charCode > 0x10FFFF)
        {
            return false;
        }

        var font = fontElement.GetString();
        if (string.IsNullOrWhiteSpace(font) ||
            !TryResolveGlyph(font, charCode, out var glyphTypeface, out var glyphIndex))
        {
            return false;
        }

        var emSize = scale * fontSize;
        if (!double.IsFinite(emSize) || emSize <= 0 || emSize > 4096)
        {
            return false;
        }

        var origin = new Point(
            padding + x * fontSize,
            padding + y * fontSize);
        var advance = glyphTypeface.AdvanceWidths.TryGetValue(glyphIndex, out var advanceEm)
            ? advanceEm * emSize
            : emSize;
        if (!double.IsFinite(advance) || advance < 0)
        {
            return false;
        }

        var glyphRun = new GlyphRun(
            glyphTypeface,
            0,
            false,
            emSize,
            pixelsPerDip,
            new[] { glyphIndex },
            origin,
            new[] { advance },
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        context.DrawGlyphRun(brush, glyphRun);
        return true;
    }

    private static bool TryDrawLine(
        DrawingContext context,
        JsonElement item,
        Brush brush,
        double fontSize,
        double padding)
    {
        if (!TryReadFinite(item, "x", -4096, 4096, out var x) ||
            !TryReadFinite(item, "y", -4096, 4096, out var y) ||
            !TryReadFinite(item, "width", 0, 4096, out var width) ||
            !TryReadFinite(item, "thickness", 0.0001, 4096, out var thickness))
        {
            return false;
        }

        var left = padding + x * fontSize;
        var centerY = padding + y * fontSize;
        var widthDip = width * fontSize;
        var thicknessDip = Math.Max(0.01, thickness * fontSize);
        var dashed = item.TryGetProperty("dashed", out var dashedElement) &&
            dashedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            dashedElement.GetBoolean();

        if (!dashed)
        {
            context.DrawRectangle(
                brush,
                null,
                new Rect(left, centerY - thicknessDip / 2, widthDip, thicknessDip));
            return true;
        }

        var pen = new Pen(brush, thicknessDip)
        {
            DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0)
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }
        context.DrawLine(pen, new Point(left, centerY), new Point(left + widthDip, centerY));
        return true;
    }

    private static bool TryDrawRect(
        DrawingContext context,
        JsonElement item,
        Brush brush,
        double fontSize,
        double padding)
    {
        if (!TryReadFinite(item, "x", -4096, 4096, out var x) ||
            !TryReadFinite(item, "y", -4096, 4096, out var y) ||
            !TryReadFinite(item, "width", 0, 4096, out var width) ||
            !TryReadFinite(item, "height", 0, 4096, out var height))
        {
            return false;
        }

        context.DrawRectangle(
            brush,
            null,
            new Rect(
                padding + x * fontSize,
                padding + y * fontSize,
                width * fontSize,
                height * fontSize));
        return true;
    }

    private static bool TryDrawPath(
        DrawingContext context,
        JsonElement item,
        Brush brush,
        double fontSize,
        double padding,
        ref int totalPathCommands)
    {
        if (!TryReadFinite(item, "x", -4096, 4096, out var originX) ||
            !TryReadFinite(item, "y", -4096, 4096, out var originY) ||
            !item.TryGetProperty("commands", out var commands) ||
            commands.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        totalPathCommands += commands.GetArrayLength();
        if (totalPathCommands > MaximumPathCommands)
        {
            return false;
        }

        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        PathFigure? figure = null;
        foreach (var command in commands.EnumerateArray())
        {
            if (!command.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (typeElement.GetString())
            {
                case "MoveTo":
                    if (!TryReadPoint(command, originX, originY, fontSize, padding, out var move))
                    {
                        return false;
                    }
                    if (figure != null)
                    {
                        geometry.Figures.Add(figure);
                    }
                    figure = new PathFigure { StartPoint = move };
                    break;

                case "LineTo":
                    if (figure == null ||
                        !TryReadPoint(command, originX, originY, fontSize, padding, out var line))
                    {
                        return false;
                    }
                    figure.Segments.Add(new LineSegment(line, true));
                    break;

                case "CubicTo":
                    if (figure == null ||
                        !TryReadControlPoint(command, "x1", "y1", originX, originY, fontSize, padding, out var c1) ||
                        !TryReadControlPoint(command, "x2", "y2", originX, originY, fontSize, padding, out var c2) ||
                        !TryReadPoint(command, originX, originY, fontSize, padding, out var end))
                    {
                        return false;
                    }
                    figure.Segments.Add(new BezierSegment(c1, c2, end, true));
                    break;

                case "QuadTo":
                    if (figure == null ||
                        !TryReadControlPoint(command, "x1", "y1", originX, originY, fontSize, padding, out var control) ||
                        !TryReadPoint(command, originX, originY, fontSize, padding, out var quadEnd))
                    {
                        return false;
                    }
                    figure.Segments.Add(new QuadraticBezierSegment(control, quadEnd, true));
                    break;

                case "Close":
                    if (figure == null)
                    {
                        return false;
                    }
                    figure.IsClosed = true;
                    geometry.Figures.Add(figure);
                    figure = null;
                    break;

                default:
                    return false;
            }
        }

        if (figure != null)
        {
            geometry.Figures.Add(figure);
        }
        if (geometry.Figures.Count == 0)
        {
            return false;
        }
        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        var fill = item.TryGetProperty("fill", out var fillElement) &&
            fillElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            fillElement.GetBoolean();
        if (fill)
        {
            context.DrawGeometry(brush, null, geometry);
        }
        else
        {
            var pen = new Pen(brush, 1.5);
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }
            context.DrawGeometry(null, pen, geometry);
        }
        return true;
    }

    private static bool TryReadPoint(
        JsonElement element,
        double originX,
        double originY,
        double fontSize,
        double padding,
        out Point point) =>
        TryReadControlPoint(
            element,
            "x",
            "y",
            originX,
            originY,
            fontSize,
            padding,
            out point);

    private static bool TryReadControlPoint(
        JsonElement element,
        string xName,
        string yName,
        double originX,
        double originY,
        double fontSize,
        double padding,
        out Point point)
    {
        point = default;
        if (!TryReadFinite(element, xName, -4096, 4096, out var x) ||
            !TryReadFinite(element, yName, -4096, 4096, out var y))
        {
            return false;
        }

        point = new Point(
            padding + (originX + x) * fontSize,
            padding + (originY + y) * fontSize);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static bool TryReadBrush(
        JsonElement item,
        Dictionary<uint, SolidColorBrush> cache,
        out SolidColorBrush brush)
    {
        brush = null!;
        if (!item.TryGetProperty("color", out var color) ||
            color.ValueKind != JsonValueKind.Object ||
            !TryReadFinite(color, "r", 0, 1, out var r) ||
            !TryReadFinite(color, "g", 0, 1, out var g) ||
            !TryReadFinite(color, "b", 0, 1, out var b) ||
            !TryReadFinite(color, "a", 0, 1, out var a))
        {
            return false;
        }

        var red = ToByte(r);
        var green = ToByte(g);
        var blue = ToByte(b);
        var alpha = ToByte(a);
        var key = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
        if (cache.TryGetValue(key, out brush))
        {
            return true;
        }

        brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        cache[key] = brush;
        return true;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(
            (int)Math.Round(value * 255, MidpointRounding.AwayFromZero),
            0,
            255);

    private static bool TryReadFinite(
        JsonElement element,
        string name,
        double minimum,
        double maximum,
        out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) &&
            double.IsFinite(value) &&
            value >= minimum &&
            value <= maximum;
    }

    private static bool TryResolveGlyph(
        string font,
        uint charCode,
        out GlyphTypeface glyphTypeface,
        out ushort glyphIndex)
    {
        glyphTypeface = null!;
        glyphIndex = 0;

        if (TryGetPackagedTypeface(font, out var packaged) &&
            TryGetGlyphIndex(packaged, charCode, out glyphIndex))
        {
            glyphTypeface = packaged;
            return true;
        }

        if (!string.Equals(font, "Main-Regular", StringComparison.Ordinal) &&
            TryGetPackagedTypeface("Main-Regular", out var main) &&
            TryGetGlyphIndex(main, charCode, out glyphIndex))
        {
            glyphTypeface = main;
            return true;
        }

        foreach (var family in SystemFallbackFamilies(font))
        {
            if (TryGetSystemTypeface(family, out var system) &&
                TryGetGlyphIndex(system, charCode, out glyphIndex))
            {
                glyphTypeface = system;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SystemFallbackFamilies(string font)
    {
        if (string.Equals(font, "Emoji-Fallback", StringComparison.Ordinal))
        {
            yield return "Segoe UI Emoji";
            yield return "Segoe UI Symbol";
        }

        yield return SystemFonts.MessageFontFamily.Source;
        yield return "Microsoft YaHei UI";
        yield return "Yu Gothic UI";
        yield return "Malgun Gothic";
        yield return "Segoe UI Symbol";
        yield return "Segoe UI Emoji";
    }

    private static bool TryGetGlyphIndex(
        GlyphTypeface typeface,
        uint charCode,
        out ushort glyphIndex)
    {
        glyphIndex = 0;
        return charCode <= 0x10FFFF &&
            typeface.CharacterToGlyphMap.TryGetValue((int)charCode, out glyphIndex) &&
            glyphIndex != 0;
    }

    private static bool TryGetPackagedTypeface(
        string font,
        out GlyphTypeface typeface)
    {
        typeface = null!;
        if (!PackagedFontFiles.TryGetValue(font, out var fileName))
        {
            return false;
        }

        lock (FontGate)
        {
            if (PackagedFonts.TryGetValue(font, out var cached))
            {
                typeface = cached!;
                return cached != null;
            }

            GlyphTypeface? loaded = null;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "math-fonts", fileName);
                if (File.Exists(path))
                {
                    loaded = new GlyphTypeface(new Uri(Path.GetFullPath(path), UriKind.Absolute));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            PackagedFonts[font] = loaded;
            typeface = loaded!;
            return loaded != null;
        }
    }

    private static bool TryGetSystemTypeface(
        string familyName,
        out GlyphTypeface typeface)
    {
        typeface = null!;
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return false;
        }

        lock (FontGate)
        {
            if (SystemFontsCache.TryGetValue(familyName, out var cached))
            {
                typeface = cached!;
                return cached != null;
            }

            GlyphTypeface? loaded = null;
            try
            {
                var candidate = new Typeface(
                    new FontFamily(familyName),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);
                if (candidate.TryGetGlyphTypeface(out var glyphTypeface))
                {
                    loaded = glyphTypeface;
                }
            }
            catch (ArgumentException)
            {
            }

            SystemFontsCache[familyName] = loaded;
            typeface = loaded!;
            return loaded != null;
        }
    }

    private static void MarkNativeUnavailable()
    {
        lock (Gate)
        {
            _nativeUnavailable = true;
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

internal sealed class MarkdownMathVisual : FrameworkElement
{
    private readonly DrawingGroup _drawing;
    private readonly double _scale;
    private readonly Size _size;

    public MarkdownMathVisual(MarkdownMathDrawing drawing, double scale)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        _drawing = drawing.Drawing;
        _scale = Math.Clamp(scale, 0.01, 1.0);
        _size = new Size(drawing.Width * _scale, drawing.Height * _scale);
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextBlock.SetBaselineOffset(this, drawing.Baseline * _scale);
    }

    protected override Size MeasureOverride(Size availableSize) => _size;

    protected override Size ArrangeOverride(Size finalSize) => _size;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Math.Abs(_scale - 1) > 0.001)
        {
            drawingContext.PushTransform(new ScaleTransform(_scale, _scale));
        }

        drawingContext.DrawDrawing(_drawing);

        if (Math.Abs(_scale - 1) > 0.001)
        {
            drawingContext.Pop();
        }
    }
}
