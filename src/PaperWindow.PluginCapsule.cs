using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double PluginCapsuleMinContentWidth = 34;
    private const double PluginCapsuleMaxContentWidth = 320;
    private const double PluginCapsuleComponentGap = 5;
    private const double PluginCapsuleStatusDotSize = 7;
    private const double PluginCapsuleDefaultProgressRingSize = 18;
    private const double PluginCapsuleDefaultProgressBarWidth = 30;
    private const double PluginCapsuleFillProgressBarMinWidth = 18;
    private PaperCapsulePresentation? _pluginCapsulePresentation;
    private Grid? _pluginCapsuleRegularHost;
    private UIElement? _pluginCapsuleRegularDefaultContent;
    private Border? _pluginCapsuleRegularLayer;
    private int _pluginCapsuleCustomViewGeneration = -1;
    private FrameworkElement? _pluginCapsuleRegularCustomView;
    private FrameworkElement? _pluginCapsuleDockedCustomView;
    private bool _pluginCapsuleRegularCustomViewAttempted;
    private bool _pluginCapsuleDockedCustomViewAttempted;
    private double _pluginCapsuleRegularCustomViewWidth = double.NaN;
    private double _pluginCapsuleDockedCustomViewWidth = double.NaN;

    private FrameworkElement BuildPluginCapsuleContentHost(UIElement defaultContent)
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        var layer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed
        };
        host.Children.Add(defaultContent);
        host.Children.Add(layer);
        _pluginCapsuleRegularHost = host;
        _pluginCapsuleRegularDefaultContent = defaultContent;
        _pluginCapsuleRegularLayer = layer;
        RefreshPluginCapsuleRegularContent();
        return host;
    }

    private double? PluginCapsuleRequestedContentWidth(double? pixelsPerDip = null)
    {
        if (_pluginCapsulePresentation == null ||
            IsCurrentBodyProviderMarkdown ||
            (_bodyFailed && !HasPluginRuntimePresentationOwner))
        {
            return null;
        }
        if (_pluginCapsulePresentation.PreferredWidth <=
            PaperCapsulePresentation.AutomaticWidth)
        {
            return MeasurePluginCapsuleTemplateWidth(
                _pluginCapsulePresentation,
                pixelsPerDip);
        }
        return Math.Clamp(
            _pluginCapsulePresentation.PreferredWidth,
            PluginCapsuleMinContentWidth,
            PluginCapsuleMaxContentWidth);
    }

    private void SetPluginCapsulePresentation(PaperCapsulePresentation? presentation)
    {
        var previousRequestedWidth = PluginCapsuleRequestedContentWidth();
        var normalized = NormalizePluginCapsulePresentation(presentation);
        _pluginCapsulePresentation = normalized;
        _paper.BodyCapsuleText = normalized == null
            ? string.Empty
            : CapsulePresentationFallbackText(normalized);
        var requestedWidth = PluginCapsuleRequestedContentWidth();
        var geometryChanged = previousRequestedWidth != requestedWidth;
        if (geometryChanged)
        {
            // PaperCapsuleViewContext carries immutable geometry. Recreate 1.7 views when the
            // presentation resolves to a different width; per-surface DPI checks cover later
            // monitor or typography changes without rebuilding for ordinary state updates.
            ResetPluginCapsuleCustomViews();
        }
        RefreshCapsuleLabel();
        if (geometryChanged)
        {
            ApplyCurrentCollapsedCapsuleWidth();
        }
    }

    internal static PaperCapsulePresentation? NormalizePluginCapsulePresentation(
        PaperCapsulePresentation? presentation)
    {
        if (presentation == null)
        {
            return null;
        }

        var components = (presentation.Components ?? [])
            .Take(3)
            .Select(component => component with
            {
                Text = NormalizeCapsuleComponentText(component.Text),
                Value = double.IsFinite(component.Value)
                    ? Math.Clamp(component.Value, 0, 1)
                    : 0,
                Width = double.IsFinite(component.Width)
                    ? Math.Clamp(component.Width, 0, 160)
                    : 0,
                Color = NormalizeCapsuleColor(component.Color)
            })
            .ToArray();
        if (components.Length == 0)
        {
            return null;
        }

        var width = double.IsFinite(presentation.PreferredWidth)
            ? presentation.PreferredWidth <= PaperCapsulePresentation.AutomaticWidth
                ? PaperCapsulePresentation.AutomaticWidth
                : Math.Clamp(
                    presentation.PreferredWidth,
                    PluginCapsuleMinContentWidth,
                    PluginCapsuleMaxContentWidth)
            : 110;
        return presentation with
        {
            Components = components,
            PreferredWidth = width,
            ToolTip = NormalizePluginDisplayText(presentation.ToolTip),
            PlainText = NormalizePluginDisplayText(presentation.PlainText)
        };
    }

    private static string NormalizeCapsuleComponentText(string? text)
    {
        var normalized = NormalizePluginDisplayText(text);
        return normalized.Length <= 48 ? normalized : normalized[..47] + "…";
    }

    private static string NormalizeCapsuleColor(string? color)
    {
        var value = (color ?? string.Empty).Trim();
        return value.Length <= 16 ? value : string.Empty;
    }

    internal static string CapsulePresentationFallbackText(PaperCapsulePresentation presentation)
    {
        if (!string.IsNullOrWhiteSpace(presentation.PlainText))
        {
            return presentation.PlainText;
        }

        var values = presentation.Components.Select(component => component.Kind switch
        {
            PaperCapsuleComponentKind.Text or PaperCapsuleComponentKind.Glyph => component.Text,
            PaperCapsuleComponentKind.ProgressRing or PaperCapsuleComponentKind.ProgressBar =>
                $"{Math.Round(component.Value * 100, MidpointRounding.AwayFromZero)}%",
            _ => string.Empty
        });
        return NormalizePluginDisplayText(string.Join(" ", values.Where(value => value.Length > 0)));
    }

    private void RefreshPluginCapsuleRegularContent()
    {
        if (_pluginCapsuleRegularHost == null ||
            _pluginCapsuleRegularDefaultContent == null ||
            _pluginCapsuleRegularLayer == null)
        {
            return;
        }

        var presentation = _pluginCapsulePresentation;
        if (presentation == null ||
            IsCurrentBodyProviderMarkdown ||
            (_bodyFailed && !HasPluginRuntimePresentationOwner))
        {
            _pluginCapsuleRegularLayer.Child = null;
            _pluginCapsuleRegularLayer.Visibility = Visibility.Collapsed;
            _pluginCapsuleRegularDefaultContent.Visibility = Visibility.Visible;
            _pluginCapsuleRegularHost.ToolTip = null;
            return;
        }

        _pluginCapsuleRegularDefaultContent.Visibility = Visibility.Collapsed;
        _pluginCapsuleRegularLayer.Child = BuildPluginCapsuleContent(
            presentation,
            PaperCapsuleSurfaceKind.Regular);
        _pluginCapsuleRegularLayer.Visibility = Visibility.Visible;
        _pluginCapsuleRegularHost.ToolTip = string.IsNullOrWhiteSpace(presentation.ToolTip)
            ? _controller.PaperTitleText(_paper)
            : presentation.ToolTip;
    }

    private void RefreshPluginCapsuleDockedContent()
    {
        if (_edgeCapsuleHost == null)
        {
            return;
        }

        var presentation = _pluginCapsulePresentation;
        if (presentation == null ||
            IsCurrentBodyProviderMarkdown ||
            (_bodyFailed && !HasPluginRuntimePresentationOwner))
        {
            _edgeCapsuleHost.SetPluginContent(null, null);
            return;
        }

        _edgeCapsuleHost.SetPluginContent(
            BuildPluginCapsuleContent(
                presentation,
                PaperCapsuleSurfaceKind.Docked),
            string.IsNullOrWhiteSpace(presentation.ToolTip)
                ? _controller.PaperTitleText(_paper)
                : presentation.ToolTip);
    }

    private FrameworkElement BuildPluginCapsuleContent(
        PaperCapsulePresentation presentation,
        PaperCapsuleSurfaceKind surface)
    {
        var customView = TryGetPluginCapsuleCustomView(presentation, surface);
        if (customView != null)
        {
            // Protocol 1.7 owns the complete requested content segment. Do not consume part of
            // PaperCapsuleViewContext.Width with the host template's visual inset.
            return customView;
        }

        // Protocol 1.6 remains host-rendered. Keep PaperTodo's normal visual breathing room
        // inside the requested segment while leaving the 1.7 custom-view contract exact.
        return new Border
        {
            Padding = new Thickness(
                CapsuleLeftPadding,
                0,
                CapsuleRightPadding,
                0),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Child = BuildPluginCapsuleTemplateView(presentation)
        };
    }

    private FrameworkElement? TryGetPluginCapsuleCustomView(
        PaperCapsulePresentation presentation,
        PaperCapsuleSurfaceKind surface)
    {
        if (_bodyDescriptor?.Kind != PaperBodyPluginKind.Native ||
            _paperBodyHost.Current is not IPaperCapsuleViewProvider provider)
        {
            return null;
        }

        EnsurePluginCapsuleCustomViewGeneration();
        ref FrameworkElement? cached = ref (surface == PaperCapsuleSurfaceKind.Docked
            ? ref _pluginCapsuleDockedCustomView
            : ref _pluginCapsuleRegularCustomView);
        ref bool attempted = ref (surface == PaperCapsuleSurfaceKind.Docked
            ? ref _pluginCapsuleDockedCustomViewAttempted
            : ref _pluginCapsuleRegularCustomViewAttempted);
        ref double cachedWidth = ref (surface == PaperCapsuleSurfaceKind.Docked
            ? ref _pluginCapsuleDockedCustomViewWidth
            : ref _pluginCapsuleRegularCustomViewWidth);
        var pixelsPerDip = surface == PaperCapsuleSurfaceKind.Docked
            ? DeepCapsuleSlotDpi().PixelsPerDip
            : (double?)null;
        var width = PluginCapsuleRequestedContentWidth(pixelsPerDip)
            ?? presentation.PreferredWidth;
        if (attempted && Math.Abs(cachedWidth - width) > 0.001)
        {
            // Auto-sized components can resolve to a different DIP width after text, typography,
            // target-monitor DPI or settings change. Recreate only the affected surface so the
            // immutable 1.7 context keeps matching the slot it will actually receive.
            cached = null;
            attempted = false;
        }
        if (attempted)
        {
            return cached;
        }

        attempted = true;
        cachedWidth = width;
        try
        {
            var view = provider.CreateCapsuleView(new PaperCapsuleViewContext(
                surface,
                width,
                CapsuleBodyHeight,
                CurrentPaperBodyTheme()));
            if (view == null)
            {
                return null;
            }
            if (view is Window ||
                view.Parent != null ||
                !PluginVisualTreePolicy.IsSupportedPureWpfTree(view))
            {
                throw new InvalidOperationException(
                    "Custom capsule view must be a fresh, unparented pure-WPF FrameworkElement.");
            }

            view.Width = double.NaN;
            view.Height = double.NaN;
            view.Margin = new Thickness(0);
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;
            view.IsHitTestVisible = false;
            view.Focusable = false;
            view.ClipToBounds = true;
            cached = view;
            return cached;
        }
        catch
        {
            // Free rendering is an optional presentation layer. A bad custom view falls back to
            // the 1.6 template without replacing or terminating the main plugin body session.
            return null;
        }
    }

    private void EnsurePluginCapsuleCustomViewGeneration()
    {
        if (_pluginCapsuleCustomViewGeneration == _bodySessionGeneration)
        {
            return;
        }
        ResetPluginCapsuleCustomViews();
        _pluginCapsuleCustomViewGeneration = _bodySessionGeneration;
    }

    private void ResetPluginCapsuleCustomViews()
    {
        _pluginCapsuleCustomViewGeneration = -1;
        _pluginCapsuleRegularCustomView = null;
        _pluginCapsuleDockedCustomView = null;
        _pluginCapsuleRegularCustomViewAttempted = false;
        _pluginCapsuleDockedCustomViewAttempted = false;
        _pluginCapsuleRegularCustomViewWidth = double.NaN;
        _pluginCapsuleDockedCustomViewWidth = double.NaN;
    }

    private FrameworkElement BuildPluginCapsuleTemplateView(PaperCapsulePresentation presentation)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        for (var index = 0; index < presentation.Components.Length; index++)
        {
            var component = presentation.Components[index];
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = component.Fill
                    ? new GridLength(1, GridUnitType.Star)
                    : component.Width > 0
                        ? new GridLength(component.Width)
                        : GridLength.Auto
            });
            var element = BuildPluginCapsuleComponent(component);
            if (index > 0)
            {
                element.Margin = new Thickness(PluginCapsuleComponentGap, 0, 0, 0);
            }
            Grid.SetColumn(element, index);
            grid.Children.Add(element);
        }
        return grid;
    }

    private FrameworkElement BuildPluginCapsuleComponent(PaperCapsuleComponent component)
    {
        var brush = ResolvePluginCapsuleBrush(component);
        switch (component.Kind)
        {
            case PaperCapsuleComponentKind.Glyph:
                return new TextBlock
                {
                    Text = component.Text,
                    Foreground = brush,
                    FontFamily = AppTypography.SymbolFontFamily,
                    FontSize = CapsuleIconFontSizeForCurrentPaper(),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = component.Fill
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
            case PaperCapsuleComponentKind.StatusDot:
                return new Ellipse
                {
                    Width = PluginCapsuleStatusDotSize,
                    Height = PluginCapsuleStatusDotSize,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            case PaperCapsuleComponentKind.ProgressRing:
            {
                var diameter = component.Width > 0
                    ? Math.Min(component.Width, CapsuleBodyHeight)
                    : PluginCapsuleDefaultProgressRingSize;
                return new CapsuleProgressRing
                {
                    Width = diameter,
                    Height = diameter,
                    Value = component.Value,
                    ForegroundBrush = brush,
                    TrackBrush = Theme.Tint(38),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
            case PaperCapsuleComponentKind.ProgressBar:
                return new CapsuleProgressBar
                {
                    // Non-fill Width is an exact plugin declaration. Only an omitted Width gets the
                    // host default; a MinWidth must not silently override a smaller explicit value.
                    MinWidth = component.Fill
                        ? PluginCapsuleFillProgressBarMinWidth
                        : 0,
                    Width = component.Fill
                        ? double.NaN
                        : component.Width > 0
                            ? component.Width
                            : PluginCapsuleDefaultProgressBarWidth,
                    Height = 5,
                    Value = component.Value,
                    ForegroundBrush = brush,
                    TrackBrush = Theme.Tint(38),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = component.Fill
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left
                };
            default:
                return new TextBlock
                {
                    Text = component.Text,
                    Foreground = brush,
                    FontFamily = CapsuleLabelFontFamily,
                    FontSize = CapsuleLabelFontSize,
                    FontWeight = component.Tone == PaperCapsuleTone.Accent
                        ? FontWeights.SemiBold
                        : CapsuleLabelFontWeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = component.Fill
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
        }
    }

    private double MeasurePluginCapsuleTemplateWidth(
        PaperCapsulePresentation presentation,
        double? pixelsPerDip)
    {
        var width = CapsuleLeftPadding + CapsuleRightPadding;
        for (var index = 0; index < presentation.Components.Length; index++)
        {
            if (index > 0)
            {
                width += PluginCapsuleComponentGap;
            }
            width += MeasurePluginCapsuleComponentWidth(
                presentation.Components[index],
                pixelsPerDip);
        }
        return Math.Clamp(
            Math.Ceiling(width),
            PluginCapsuleMinContentWidth,
            PluginCapsuleMaxContentWidth);
    }

    private double MeasurePluginCapsuleComponentWidth(
        PaperCapsuleComponent component,
        double? pixelsPerDip)
    {
        // A non-fill component with an explicit width owns exactly that template column. Fill
        // components instead use their natural minimum when the complete capsule is auto-sized.
        if (!component.Fill && component.Width > 0)
        {
            return component.Width;
        }

        return component.Kind switch
        {
            PaperCapsuleComponentKind.Glyph => MeasureCapsuleTextWidth(
                component.Text,
                CapsuleIconFontSizeForCurrentPaper(),
                FontWeights.SemiBold,
                AppTypography.SymbolFontFamily,
                pixelsPerDip),
            PaperCapsuleComponentKind.StatusDot => PluginCapsuleStatusDotSize,
            PaperCapsuleComponentKind.ProgressRing => component.Width > 0
                ? Math.Min(component.Width, CapsuleBodyHeight)
                : PluginCapsuleDefaultProgressRingSize,
            PaperCapsuleComponentKind.ProgressBar => component.Fill
                ? PluginCapsuleFillProgressBarMinWidth
                : PluginCapsuleDefaultProgressBarWidth,
            _ => MeasureCapsuleTextWidth(
                component.Text,
                CapsuleLabelFontSize,
                component.Tone == PaperCapsuleTone.Accent
                    ? FontWeights.SemiBold
                    : CapsuleLabelFontWeight,
                CapsuleLabelFontFamily,
                pixelsPerDip)
        };
    }

    private static Brush ResolvePluginCapsuleBrush(PaperCapsuleComponent component)
    {
        if (!string.IsNullOrWhiteSpace(component.Color))
        {
            try
            {
                if (ColorConverter.ConvertFromString(component.Color) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
            }
        }

        return component.Tone switch
        {
            PaperCapsuleTone.Muted => Theme.WeakTextBrush,
            PaperCapsuleTone.Accent => Theme.ActiveBrush,
            PaperCapsuleTone.Warning => Theme.Tint(210),
            PaperCapsuleTone.Danger => Theme.DangerBrush,
            _ => Theme.BrightWeakTextBrush
        };
    }

    private sealed class CapsuleProgressRing : FrameworkElement
    {
        public double Value { get; init; }
        public required Brush ForegroundBrush { get; init; }
        public required Brush TrackBrush { get; init; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 2)
            {
                return;
            }

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var radius = Math.Max(1, size / 2 - 1.5);
            var trackPen = new Pen(TrackBrush, 2);
            var valuePen = new Pen(ForegroundBrush, 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawingContext.DrawEllipse(null, trackPen, center, radius, radius);
            var value = Math.Clamp(Value, 0, 1);
            if (value <= 0)
            {
                return;
            }
            if (value >= 0.999)
            {
                drawingContext.DrawEllipse(null, valuePen, center, radius, radius);
                return;
            }

            var startAngle = -90.0;
            var endAngle = startAngle + value * 360.0;
            Point PointAt(double angle)
            {
                var radians = angle * Math.PI / 180.0;
                return new Point(
                    center.X + Math.Cos(radians) * radius,
                    center.Y + Math.Sin(radians) * radius);
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(PointAt(startAngle), false, false);
                context.ArcTo(
                    PointAt(endAngle),
                    new Size(radius, radius),
                    0,
                    value > 0.5,
                    SweepDirection.Clockwise,
                    true,
                    false);
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, valuePen, geometry);
        }
    }

    private sealed class CapsuleProgressBar : FrameworkElement
    {
        public double Value { get; init; }
        public required Brush ForegroundBrush { get; init; }
        public required Brush TrackBrush { get; init; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }
            var radius = ActualHeight / 2;
            drawingContext.DrawRoundedRectangle(
                TrackBrush,
                null,
                new Rect(0, 0, ActualWidth, ActualHeight),
                radius,
                radius);
            var valueWidth = ActualWidth * Math.Clamp(Value, 0, 1);
            if (valueWidth <= 0)
            {
                return;
            }
            drawingContext.DrawRoundedRectangle(
                ForegroundBrush,
                null,
                new Rect(0, 0, valueWidth, ActualHeight),
                radius,
                radius);
        }
    }
}
