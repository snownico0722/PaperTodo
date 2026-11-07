using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace PaperTodo;

internal enum VectorPrimitiveIconKind
{
    Plus,
    Minus,
    Settings
}

/// <summary>
/// Small vector primitives whose horizontal and vertical strokes are snapped to the
/// current device-pixel grid. Curved and diagonal segments keep normal antialiasing.
/// </summary>
internal sealed class VectorPrimitiveIconElement : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(VectorPrimitiveIconElement),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly VectorPrimitiveIconKind _kind;
    private readonly double _verticalOffset;

    public VectorPrimitiveIconElement(
        VectorPrimitiveIconKind kind,
        double verticalOffset = 0)
    {
        _kind = kind;
        _verticalOffset = verticalOffset;
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
        var centerY = (ActualHeight / 2) + _verticalOffset;
        var deviceOrigin = DevicePixelOrigin();

        switch (_kind)
        {
            case VectorPrimitiveIconKind.Plus:
                DrawPlus(drawingContext, centerX, centerY, pixelX, pixelY, deviceOrigin.X, deviceOrigin.Y);
                break;
            case VectorPrimitiveIconKind.Minus:
                DrawMinus(drawingContext, centerX, centerY, pixelX, pixelY, deviceOrigin.X, deviceOrigin.Y);
                break;
            case VectorPrimitiveIconKind.Settings:
                DrawSettings(
                    drawingContext,
                    centerX,
                    centerY,
                    pixelX,
                    pixelY,
                    deviceOrigin.X,
                    deviceOrigin.Y);
                break;
        }
    }

    private void DrawPlus(
        DrawingContext drawingContext,
        double centerX,
        double centerY,
        double pixelX,
        double pixelY,
        double originPixelX,
        double originPixelY)
    {
        var extent = Math.Min(ActualWidth, ActualHeight) * 0.31;
        var thicknessX = AxisThickness(pixelX);
        var thicknessY = AxisThickness(pixelY);
        var centerLineX = AxisCenter(centerX, pixelX, thicknessX, originPixelX);
        var centerLineY = AxisCenter(centerY, pixelY, thicknessY, originPixelY);
        DrawCenteredRectangle(
            drawingContext,
            centerLineX,
            centerLineY,
            extent * 2,
            thicknessY,
            pixelX,
            pixelY,
            originPixelX,
            originPixelY);
        DrawCenteredRectangle(
            drawingContext,
            centerLineX,
            centerLineY,
            thicknessX,
            extent * 2,
            pixelX,
            pixelY,
            originPixelX,
            originPixelY);
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

    private void DrawSettings(
        DrawingContext drawingContext,
        double centerX,
        double centerY,
        double pixelX,
        double pixelY,
        double originPixelX,
        double originPixelY)
    {
        var minimum = Math.Min(ActualWidth, ActualHeight);
        var thicknessX = AxisThickness(pixelX);
        var thicknessY = AxisThickness(pixelY);
        var stroke = Math.Min(thicknessX, thicknessY);
        var centerLineX = AxisCenter(centerX, pixelX, thicknessX, originPixelX);
        var centerLineY = AxisCenter(centerY, pixelY, thicknessY, originPixelY);
        var ringRadius = Math.Max(minimum * 0.255, stroke * 2.4);
        var toothLength = Math.Max(stroke * 1.6, minimum * 0.09);

        for (var i = 0; i < 6; i++)
        {
            var angle = (Math.PI / 3.0) * i;
            var toothCenterRadius = ringRadius + (toothLength / 2.0) - (stroke * 0.15);
            var toothCenterX = centerLineX + (Math.Cos(angle) * toothCenterRadius);
            var toothCenterY = centerLineY + (Math.Sin(angle) * toothCenterRadius);

            drawingContext.PushTransform(
                new RotateTransform(
                    angle * 180.0 / Math.PI,
                    toothCenterX,
                    toothCenterY));
            DrawCenteredRectangle(
                drawingContext,
                toothCenterX,
                toothCenterY,
                toothLength,
                stroke,
                pixelX,
                pixelY,
                originPixelX,
                originPixelY);
            drawingContext.Pop();
        }

        drawingContext.DrawEllipse(
            null,
            CreateRoundPen(stroke),
            new Point(centerLineX, centerLineY),
            ringRadius,
            ringRadius);
    }

    private Pen CreateRoundPen(double thickness)
    {
        return new Pen(Foreground, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
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
