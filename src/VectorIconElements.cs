using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace PaperTodo;

// A one-stroke collapse icon aligned to physical pixels at the current DPI.
internal sealed class CollapseMinusIconElement : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(CollapseMinusIconElement),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public CollapseMinusIconElement()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 ||
            ActualHeight <= 0 ||
            Foreground == null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelX = 1.0 / Math.Max(0.01, dpi.DpiScaleX);
        var pixelY = 1.0 / Math.Max(0.01, dpi.DpiScaleY);
        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var deviceOrigin = DevicePixelOrigin();

        DrawMinus(drawingContext, centerX, centerY, pixelX, pixelY, deviceOrigin.X, deviceOrigin.Y);
    }

    private void DrawMinus(
        DrawingContext drawingContext,
        double centerX,
        double centerY,
        double pixelX,
        double pixelY,
        double originPixelX,
        double originPixelY)
    {
        var halfWidth = ActualWidth * 0.31;
        var thickness = AxisThickness(pixelY);
        var centerLineY = AxisCenter(centerY, pixelY, thickness, originPixelY);
        DrawCenteredRectangle(
            drawingContext,
            centerX,
            centerLineY,
            halfWidth * 2,
            thickness,
            pixelX,
            pixelY,
            originPixelX,
            originPixelY);
    }

    private void DrawCenteredRectangle(
        DrawingContext drawingContext,
        double centerX,
        double centerY,
        double width,
        double height,
        double pixelX,
        double pixelY,
        double originPixelX,
        double originPixelY)
    {
        var absoluteCenterPixelsX = originPixelX + (centerX / pixelX);
        var absoluteCenterPixelsY = originPixelY + (centerY / pixelY);
        var widthPixels = PixelLengthForCenter(
            width / pixelX,
            absoluteCenterPixelsX);
        var heightPixels = PixelLengthForCenter(
            height / pixelY,
            absoluteCenterPixelsY);
        var leftPixels = Math.Round(
            absoluteCenterPixelsX - (widthPixels / 2.0),
            MidpointRounding.AwayFromZero);
        var topPixels = Math.Round(
            absoluteCenterPixelsY - (heightPixels / 2.0),
            MidpointRounding.AwayFromZero);

        drawingContext.DrawRectangle(
            Foreground,
            null,
            new Rect(
                (leftPixels - originPixelX) * pixelX,
                (topPixels - originPixelY) * pixelY,
                widthPixels * pixelX,
                heightPixels * pixelY));
    }

    private static int PixelLengthForCenter(
        double desiredPixels,
        double centerPixels)
    {
        var rounded = Math.Max(
            1,
            (int)Math.Round(
                desiredPixels,
                MidpointRounding.AwayFromZero));
        var centerFraction = Math.Abs(
            centerPixels - Math.Round(
                centerPixels,
                MidpointRounding.AwayFromZero));
        var requiresOddLength = centerFraction > 0.25;
        if (((rounded & 1) == 1) == requiresOddLength)
        {
            return rounded;
        }

        var lower = rounded > 1 ? rounded - 1 : int.MaxValue;
        var upper = rounded + 1;
        if (lower != int.MaxValue &&
            Math.Abs(desiredPixels - lower) < Math.Abs(upper - desiredPixels))
        {
            return lower;
        }

        return upper;
    }

    private static double AxisCenter(
        double centerDip,
        double pixelDip,
        double thicknessDip,
        double originPixels)
    {
        var centerPixels = originPixels + (centerDip / pixelDip);
        var thicknessPixels = Math.Max(
            1,
            (int)Math.Round(
                thicknessDip / pixelDip,
                MidpointRounding.AwayFromZero));

        var alignedCenterPixels = (thicknessPixels % 2) == 0
            ? Math.Round(centerPixels, MidpointRounding.AwayFromZero)
            : Math.Floor(centerPixels) + 0.5;
        return (alignedCenterPixels - originPixels) * pixelDip;
    }

    private Point DevicePixelOrigin()
    {
        try
        {
            return PointToScreen(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return new Point(0, 0);
        }
    }

    private static double AxisThickness(double pixelDip)
    {
        if (pixelDip <= 0 ||
            double.IsNaN(pixelDip) ||
            double.IsInfinity(pixelDip))
        {
            return 1.0;
        }

        return StrokePixelsForDpi(1.0 / pixelDip) * pixelDip;
    }

    private static int StrokePixelsForDpi(double dpiScale)
    {
        if (double.IsNaN(dpiScale) ||
            double.IsInfinity(dpiScale) ||
            dpiScale <= 0)
        {
            return 1;
        }

        // 100–150%: 1 px; 175–250%: 2 px; 275–350%: 3 px;
        // 375–450%: 4 px, and so on. This keeps the primitive icons
        // visually proportional at future ultra-high-DPI scale factors.
        return Math.Max(
            1,
            (int)Math.Floor(dpiScale + 0.25));
    }
}

/// <summary>
/// Converts a system-font glyph to a fill geometry while keeping the original text
/// layout box. This preserves the old glyph's proportions, baseline and advance width.
/// </summary>
internal sealed class VectorGlyphElement : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(VectorGlyphElement),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly string _text;
    private readonly FontFamily _fontFamily;
    private readonly double _fontSize;
    private readonly FontWeight _fontWeight;
    private readonly Size _desiredSize;

    public VectorGlyphElement(
        string text,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight)
    {
        _text = text ?? "";
        _fontFamily = fontFamily;
        _fontSize = fontSize;
        _fontWeight = fontWeight;

        var formatted = CreateFormattedText(
            pixelsPerDip: 1.0);
        _desiredSize = new Size(
            Math.Max(
                0.1,
                formatted.WidthIncludingTrailingWhitespace),
            Math.Max(
                0.1,
                formatted.Height));

        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return _desiredSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Foreground == null ||
            string.IsNullOrEmpty(_text) ||
            ActualWidth <= 0 ||
            ActualHeight <= 0)
        {
            return;
        }

        var formatted = CreateFormattedText(
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawGeometry(
            Foreground,
            null,
            formatted.BuildGeometry(new Point(0, 0)));
    }

    private FormattedText CreateFormattedText(double pixelsPerDip)
    {
        return new FormattedText(
            _text,
            UiLanguages.EffectiveUiCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                _fontFamily,
                FontStyles.Normal,
                _fontWeight,
                FontStretches.Normal),
            _fontSize,
            Brushes.Black,
            null,
            AppTypography.TextFormattingMode,
            pixelsPerDip);
    }
}
