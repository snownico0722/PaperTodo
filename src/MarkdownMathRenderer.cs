using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using XamlMath.Boxes;
using XamlMath.Exceptions;
using XamlMath.Rendering;
using XamlMath.Rendering.Transformations;

namespace PaperTodo;

internal sealed record MarkdownMathDrawing(
    DrawingGroup Drawing,
    double Width,
    double Height,
    double Baseline);

/// <summary>
/// Native WPF formula layout and vector drawing through WpfMath. Results are immutable and cached;
/// malformed or unsupported formulas simply stay as source text in the editor.
/// </summary>
internal static class MarkdownMathRenderer
{
    private const int MaximumCommandCount = 2_048;
    private const int MaximumBraceDepth = 128;
    private const int MaximumCacheEntries = 128;
    private const double MaximumRenderedDimension = 8_192;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> CacheOrder = new();

    private readonly record struct CacheKey(
        string Formula,
        bool Display,
        int FontSizeTenths,
        uint Argb,
        string TextFontName);

    private sealed record CacheEntry(
        CacheKey Key,
        MarkdownMathDrawing? Drawing,
        bool DeterministicFailure);

    public static bool TryRender(
        string formula,
        bool display,
        double fontSize,
        Brush foreground,
        out MarkdownMathDrawing drawing) =>
        TryRender(formula, display, fontSize, foreground, null, out drawing);

    public static bool TryRender(
        string formula,
        bool display,
        double fontSize,
        Brush foreground,
        string? preferredTextFontName,
        out MarkdownMathDrawing drawing)
    {
        drawing = null!;
        var normalized = MarkdownMathCompatibility.Normalize(formula);
        if (!IsSafeFormula(normalized) || !double.IsFinite(fontSize))
        {
            return false;
        }

        var size = Math.Round(Math.Clamp(fontSize, 8, 96), 1);
        var color = ResolveColor(foreground);
        var textFontName = ResolveTextFontName(preferredTextFontName);
        var key = new CacheKey(
            normalized,
            display,
            (int)Math.Round(size * 10),
            ToArgb(color),
            textFontName);

        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                TouchLocked(cached);
                if (cached.Value.Drawing != null)
                {
                    drawing = cached.Value.Drawing;
                    return true;
                }

                return !cached.Value.DeterministicFailure;
            }
        }

        MarkdownMathDrawing? rendered;
        try
        {
            rendered = RenderCore(normalized, display, size, color, textFontName);
        }
        catch (TexException)
        {
            AddToCache(key, null, deterministicFailure: true);
            return false;
        }
        catch (ArgumentException)
        {
            AddToCache(key, null, deterministicFailure: true);
            return false;
        }
        catch (NotSupportedException)
        {
            AddToCache(key, null, deterministicFailure: true);
            return false;
        }
        catch (Exception exception) when (IsRecoverableRenderingFailure(exception))
        {
            // A third-party parser/font/WPF failure must never take down the note editor. Do not
            // negative-cache unexpected failures because a transient font or device state may heal.
            return false;
        }

        if (rendered == null)
        {
            AddToCache(key, null, deterministicFailure: true);
            return false;
        }

        AddToCache(key, rendered, deterministicFailure: false);
        drawing = rendered;
        return true;
    }

    private static MarkdownMathDrawing? RenderCore(
        string formulaText,
        bool display,
        double fontSize,
        Color color,
        string textFontName)
    {
        var formula = WpfTeXFormulaParser.Instance.Parse(formulaText);
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        TexEnvironment environment;
        try
        {
            environment = WpfTeXEnvironment.Create(
                display ? TexStyle.Display : TexStyle.Text,
                fontSize,
                textFontName,
                brush);
        }
        catch (InvalidOperationException) when (!string.Equals(
                   textFontName,
                   "Arial",
                   StringComparison.OrdinalIgnoreCase))
        {
            environment = WpfTeXEnvironment.Create(
                display ? TexStyle.Display : TexStyle.Text,
                fontSize,
                "Arial",
                brush);
        }

        var metrics = new RootBoxMetricsRenderer();
        XamlMath.Rendering.TeXFormulaExtensions.RenderTo(
            formula,
            metrics,
            environment,
            0,
            0);
        var box = metrics.Root;
        if (box == null)
        {
            return null;
        }

        var raw = new DrawingGroup();
        using (var context = raw.Open())
        {
            WpfTeXFormulaExtensions.RenderTo(
                formula,
                context,
                environment,
                fontSize,
                0,
                0);
        }
        raw.Freeze();

        var nominal = new Rect(
            0,
            0,
            Math.Max(0, box.TotalWidth * fontSize),
            Math.Max(0, box.TotalHeight * fontSize));
        var bounds = raw.Bounds;
        if (bounds.IsEmpty)
        {
            bounds = nominal;
        }
        else if (!nominal.IsEmpty)
        {
            bounds.Union(nominal);
        }

        if (!FinitePositive(bounds.Width) ||
            !FinitePositive(bounds.Height) ||
            bounds.Width > MaximumRenderedDimension ||
            bounds.Height > MaximumRenderedDimension)
        {
            return null;
        }

        var padding = display ? 4.0 : 1.0;
        var offsetX = padding - bounds.X;
        var offsetY = padding - bounds.Y;
        var translated = new DrawingGroup
        {
            Transform = new TranslateTransform(offsetX, offsetY)
        };
        translated.Children.Add(raw);
        translated.Freeze();

        var width = bounds.Width + padding * 2;
        var height = bounds.Height + padding * 2;
        var baseline = Math.Clamp(box.Height * fontSize + offsetY, 0, height);
        return new MarkdownMathDrawing(translated, width, height, baseline);
    }

    private static bool FinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool IsRecoverableRenderingFailure(Exception exception) =>
        exception is not OutOfMemoryException and
        not AccessViolationException;

    private static Color ResolveColor(Brush foreground) =>
        foreground is SolidColorBrush solid
            ? solid.Color
            : Colors.Black;

    private static string ResolveTextFontName(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        var system = SystemFonts.MessageFontFamily.Source;
        return string.IsNullOrWhiteSpace(system) ? "Arial" : system;
    }

    private static uint ToArgb(Color color) =>
        ((uint)color.A << 24) |
        ((uint)color.R << 16) |
        ((uint)color.G << 8) |
        color.B;

    private static bool IsSafeFormula(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula) ||
            formula.Length > MarkdownMathScanner.MaximumFormulaContentLength ||
            formula.IndexOf('\0') >= 0)
        {
            return false;
        }

        var depth = 0;
        var commands = 0;
        for (var index = 0; index < formula.Length; index++)
        {
            switch (formula[index])
            {
                case '\\':
                    if (++commands > MaximumCommandCount)
                    {
                        return false;
                    }
                    break;

                case '{':
                    if (++depth > MaximumBraceDepth)
                    {
                        return false;
                    }
                    break;

                case '}':
                    depth--;
                    break;
            }
        }

        return depth <= MaximumBraceDepth;
    }

    private static void AddToCache(
        CacheKey key,
        MarkdownMathDrawing? drawing,
        bool deterministicFailure)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                CacheOrder.Remove(existing);
                Cache.Remove(key);
            }

            var node = CacheOrder.AddLast(new CacheEntry(key, drawing, deterministicFailure));
            Cache[key] = node;
            while (Cache.Count > MaximumCacheEntries && CacheOrder.First != null)
            {
                var oldest = CacheOrder.First;
                CacheOrder.RemoveFirst();
                Cache.Remove(oldest.Value.Key);
            }
        }
    }

    private static void TouchLocked(LinkedListNode<CacheEntry> node)
    {
        CacheOrder.Remove(node);
        CacheOrder.AddLast(node);
    }

    private sealed class RootBoxMetricsRenderer : IElementRenderer
    {
        public Box? Root { get; private set; }

        public void RenderElement(Box box, double x, double y)
        {
            Root ??= box;
        }

        public void RenderCharacter(
            CharInfo info,
            double x,
            double y,
            XamlMath.Rendering.IBrush? foreground)
        {
        }

        public void RenderLine(
            XamlMath.Rendering.Point point0,
            XamlMath.Rendering.Point point1,
            XamlMath.Rendering.IBrush? foreground)
        {
        }

        public void RenderRectangle(
            XamlMath.Rendering.Rectangle rectangle,
            XamlMath.Rendering.IBrush? foreground)
        {
        }

        public void RenderTransformed(
            Box box,
            IEnumerable<Transformation> transforms,
            double x,
            double y)
        {
        }

        public void FinishRendering()
        {
        }
    }
}

internal static class MarkdownMathCompatibility
{
    public static string Normalize(string formula)
    {
        var normalized = formula
            .Replace("\\begin{aligned}", "\\begin{align}", StringComparison.Ordinal)
            .Replace("\\end{aligned}", "\\end{align}", StringComparison.Ordinal);
        normalized = RewriteEnvironment(normalized, "cases", "\\cases{");
        normalized = RewriteEnvironment(normalized, "matrix", "\\matrix{");
        return normalized;
    }

    private static string RewriteEnvironment(string source, string name, string command)
    {
        var opening = $"\\begin{{{name}}}";
        var closing = $"\\end{{{name}}}";
        var first = source.IndexOf(opening, StringComparison.Ordinal);
        if (first < 0)
        {
            return source;
        }

        var builder = new StringBuilder(source.Length + 8);
        var cursor = 0;
        while (first >= 0)
        {
            var contentStart = first + opening.Length;
            var end = source.IndexOf(closing, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                return source;
            }

            builder.Append(source, cursor, first - cursor);
            builder.Append(command);
            builder.Append(source, contentStart, end - contentStart);
            builder.Append('}');
            cursor = end + closing.Length;
            first = source.IndexOf(opening, cursor, StringComparison.Ordinal);
        }

        builder.Append(source, cursor, source.Length - cursor);
        return builder.ToString();
    }
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
