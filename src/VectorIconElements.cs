using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace PaperTodo;

internal enum VectorPrimitiveIconKind
{
    Plus,
    Minus,
    AssociationIdle,
    AssociationActive,
    Settings,
    Close,
    Check,
    ChevronRight,
    ChevronDown,
    ArrowUp,
    ArrowDown,
    DragGrip,
    SortGrip,
    Trash,
    Note,
    Script,
    Link,
    Clock,
    Reset,
    Info,
    Target,
    TargetLocked,
    CheckBox,
    CheckBoxChecked,
    Image,
    ExternalLink,
    Circle,
    CircleFilled,
    Diamond
}

/// <summary>
/// Small vector primitives whose horizontal and vertical strokes are snapped to the
/// current device-pixel grid. Curved and diagonal segments keep normal antialiasing.
/// </summary>
internal sealed class VectorPrimitiveIconElement : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(
            typeof(VectorPrimitiveIconElement),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(VectorPrimitiveIconKind),
            typeof(VectorPrimitiveIconElement), new FrameworkPropertyMetadata(
                VectorPrimitiveIconKind.Plus, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double),
            typeof(VectorPrimitiveIconElement), new FrameworkPropertyMetadata(
                16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly double _verticalOffset;

    public VectorPrimitiveIconElement() : this(VectorPrimitiveIconKind.Plus) { }

    public VectorPrimitiveIconElement(
        VectorPrimitiveIconKind kind,
        double verticalOffset = 0)
    {
        Kind = kind;
        _verticalOffset = verticalOffset;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public VectorPrimitiveIconKind Kind
    {
        get => (VectorPrimitiveIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    // Some controls alternate between an operation icon and a real label (e.g. delete/cancel,
    // linked-paper icon/name). Keep the text surface and its gestures, using geometry only for
    // the icon state. The icon follows the same foreground and typography updates as the label.
    internal static void SetInlineIcon(TextBlock label, VectorPrimitiveIconKind kind, string? text = null)
    {
        label.Inlines.Clear();
        var icon = new VectorPrimitiveIconElement(kind);
        icon.SetBinding(IconSizeProperty, new Binding(nameof(TextBlock.FontSize)) { Source = label });
        label.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
        if (text != null)
        {
            label.Inlines.Add(new Run(" " + text));
        }
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

        switch (Kind)
        {
            case VectorPrimitiveIconKind.Plus:
                DrawPlus(drawingContext, centerX, centerY, pixelX, pixelY, deviceOrigin.X, deviceOrigin.Y);
                break;
            case VectorPrimitiveIconKind.Minus:
                DrawMinus(drawingContext, centerX, centerY, pixelX, pixelY, deviceOrigin.X, deviceOrigin.Y);
                break;
            case VectorPrimitiveIconKind.AssociationIdle:
                DrawAssociation(
                    drawingContext,
                    centerX,
                    centerY,
                    pixelX,
                    pixelY,
                    deviceOrigin.X,
                    deviceOrigin.Y,
                    active: false);
                break;
            case VectorPrimitiveIconKind.AssociationActive:
                DrawAssociation(
                    drawingContext,
                    centerX,
                    centerY,
                    pixelX,
                    pixelY,
                    deviceOrigin.X,
                    deviceOrigin.Y,
                    active: true);
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
            default:
                DrawStandardIcon(drawingContext);
                break;
        }
    }

    // Fixed 16-DIP drawings, independent of UI fonts. Freeze before publishing so first use
    // from a different UI thread cannot leave shared dispatcher-bound geometry behind.
    private static readonly IReadOnlyDictionary<VectorPrimitiveIconKind, Geometry> StandardGeometry =
        CreateStandardGeometry();

    private static IReadOnlyDictionary<VectorPrimitiveIconKind, Geometry> CreateStandardGeometry()
    {
        var paths = new Dictionary<VectorPrimitiveIconKind, string>
        {
            [VectorPrimitiveIconKind.Close] = "M4,4 L12,12 M12,4 L4,12",
            [VectorPrimitiveIconKind.Check] = "M3,8 L6.5,11.5 L13,4.5",
            [VectorPrimitiveIconKind.ChevronRight] = "M6,3 L11,8 L6,13",
            [VectorPrimitiveIconKind.ChevronDown] = "M3,6 L8,11 L13,6",
            [VectorPrimitiveIconKind.ArrowUp] = "M8,13 V3 M4,7 L8,3 L12,7",
            [VectorPrimitiveIconKind.ArrowDown] = "M8,3 V13 M4,9 L8,13 L12,9",
            [VectorPrimitiveIconKind.SortGrip] = "M3,4 H13 M3,8 H13 M3,12 H13",
            [VectorPrimitiveIconKind.Trash] = "M2.5,4 H13.5 M6,4 V2 H10 V4 M4,4 L5,14 H11 L12,4 M7,6.5 V11.5 M9,6.5 V11.5",
            [VectorPrimitiveIconKind.Note] = "M3,10.5 L10.5,3 L13,5.5 L5.5,13 L2.5,13.5 Z M9,4.5 L11.5,7",
            [VectorPrimitiveIconKind.Script] = "M9,1.5 L3.5,9 H7 L6,14.5 L12.5,6.5 H9 Z",
            [VectorPrimitiveIconKind.Link] = "M6.5,10 L5.5,11 A2.5,2.5 0 0 1 2,7.5 L5,4.5 A2.5,2.5 0 0 1 8.5,4.5 M9.5,6 L10.5,5 A2.5,2.5 0 0 1 14,8.5 L11,11.5 A2.5,2.5 0 0 1 7.5,11.5 M5.5,10.5 L10.5,5.5",
            [VectorPrimitiveIconKind.Clock] = "M8,2 A6,6 0 1 1 7.999,2 M8,4.5 V8 L10.5,9.5",
            [VectorPrimitiveIconKind.Reset] = "M3,6 A5.5,5.5 0 1 1 3.5,11 M3,2.5 V6.5 H7",
            [VectorPrimitiveIconKind.Info] = "M8,2 A6,6 0 1 1 7.999,2 M8,7 V11 M8,4.8 V4.9",
            [VectorPrimitiveIconKind.Target] = "M8,3 A5,5 0 1 1 7.999,3 M8,6 A2,2 0 1 1 7.999,6",
            [VectorPrimitiveIconKind.TargetLocked] = "M8,3 A5,5 0 1 1 7.999,3 M8,1 V5 M8,11 V15 M1,8 H5 M11,8 H15",
            [VectorPrimitiveIconKind.CheckBox] = "M3,3 H13 V13 H3 Z",
            [VectorPrimitiveIconKind.CheckBoxChecked] = "M3,3 H13 V13 H3 Z M5,8 L7,10 L11,5.5",
            [VectorPrimitiveIconKind.Image] = "M2,3 H14 V13 H2 Z M3,11 L6.5,7.5 L9,10 L11,8 L14,11 M10.5,5.5 H10.6",
            [VectorPrimitiveIconKind.ExternalLink] = "M9,2.5 H13.5 V7 M13,3 L7,9 M7,3 H3 V13 H13 V9",
            [VectorPrimitiveIconKind.Circle] = "M8,3 A5,5 0 1 1 7.999,3 Z",
            [VectorPrimitiveIconKind.CircleFilled] = "M8,3 A5,5 0 1 1 7.999,3 Z",
            [VectorPrimitiveIconKind.Diamond] = "M8,2 L14,8 L8,14 L2,8 Z"
        };
        var result = new Dictionary<VectorPrimitiveIconKind, Geometry>();
        foreach (var (kind, path) in paths)
        {
            var geometry = Geometry.Parse(path);
            geometry.Freeze();
            result.Add(kind, geometry);
        }
        return result;
    }

    private void DrawStandardIcon(DrawingContext context)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 16.0;
        context.PushTransform(new TranslateTransform(
            (ActualWidth - 16 * scale) / 2, (ActualHeight - 16 * scale) / 2 + _verticalOffset));
        context.PushTransform(new ScaleTransform(scale, scale));
        if (Kind == VectorPrimitiveIconKind.DragGrip)
        {
            context.DrawRoundedRectangle(Foreground, null, new Rect(7, 2.5, 2, 11), 1, 1);
        }
        else if (StandardGeometry.TryGetValue(Kind, out var geometry))
        {
            var pen = new Pen(Foreground, 1.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            context.DrawGeometry(Kind == VectorPrimitiveIconKind.CircleFilled ? Foreground : null,
                Kind == VectorPrimitiveIconKind.CircleFilled ? null : pen, geometry);
        }
        context.Pop();
        context.Pop();
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

    private void DrawAssociation(
        DrawingContext drawingContext,
        double centerX,
        double centerY,
        double pixelX,
        double pixelY,
        double originPixelX,
        double originPixelY,
        bool active)
    {
        var minimum = Math.Min(ActualWidth, ActualHeight);
        var radius = Math.Max(
            Math.Min(pixelX, pixelY) * 2,
            minimum * 0.198);
        var thicknessX = AxisThickness(pixelX);
        var thicknessY = AxisThickness(pixelY);
        var circleStroke = Math.Min(thicknessX, thicknessY);
        var centerLineX = AxisCenter(centerX, pixelX, thicknessX, originPixelX);
        var centerLineY = AxisCenter(centerY, pixelY, thicknessY, originPixelY);
        var circlePen = CreateRoundPen(circleStroke);
        drawingContext.DrawEllipse(
            null,
            circlePen,
            new Point(centerLineX, centerLineY),
            radius,
            radius);

        if (active)
        {
            var innerRadius = Math.Max(
                Math.Min(pixelX, pixelY),
                radius - (circleStroke / 2.0));
            var dotRadius = innerRadius * 0.50;
            drawingContext.DrawEllipse(
                Foreground,
                null,
                new Point(centerLineX, centerLineY),
                dotRadius,
                dotRadius);
            return;
        }

        var arm = radius + (circleStroke / 2.0) + (Math.Min(pixelX, pixelY) * 1.25);
        DrawCenteredRectangle(
            drawingContext,
            centerLineX,
            centerLineY,
            arm * 2,
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
            arm * 2,
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
