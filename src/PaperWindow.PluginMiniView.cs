using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PluginEdgeCapsulePreviewProvider(
    PaperWindow owner) : IEdgeCapsulePreviewProvider
{
    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context) =>
        owner.DescribePluginEdgeCapsulePreview(context);
}

internal sealed class PluginCapsuleFallbackLivePreviewView :
    EdgeCapsuleLivePreviewView
{
    private readonly Func<FrameworkElement> _build;

    public PluginCapsuleFallbackLivePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size,
        Func<FrameworkElement> build)
        : base(context, size)
    {
        _build = build;
        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        Children.Clear();
        Children.Add(_build());
    }
}

public sealed partial class PaperWindow
{
    private const double PluginFallbackMiniHeight = 140;
    private const double PluginFallbackMiniMinimumWidth = 220;
    private const double PluginFallbackMiniMaximumWidth = 360;

    private int _pluginMiniViewGeneration = -1;
    private IPaperMiniViewProvider? _pluginMiniViewProvider;
    private FrameworkElement? _pluginMiniView;
    private EdgeCapsulePreviewSize _pluginMiniViewSize;
    private bool _pluginMiniViewAttempted;
    private bool _pluginMiniViewActive;
    private bool _pluginMiniViewVisible;

    internal EdgeCapsulePreviewDescriptor DescribePluginEdgeCapsulePreview(
        EdgeCapsulePreviewContext context)
    {
        if (_bodyDescriptor?.Kind == PaperBodyPluginKind.Native &&
            _paperBodyHost.Current is IPaperMiniViewProvider nativeProvider)
        {
            var preferred = ReadPreferredMiniSize(
                () => nativeProvider.PreferredMiniViewSize,
                PaperMiniViewSize.Default);
            return new EdgeCapsulePreviewDescriptor(
                preferred,
                size => CreateNativePluginMiniView(
                    nativeProvider,
                    context,
                    size),
                visible => NotifyNativePluginMiniViewVisibility(
                    nativeProvider,
                    visible));
        }

        if (_paperBodyHost.Current is WebPaperBodySession webSession &&
            webSession.HasMiniEntry)
        {
            // A declared 1.8 mini surface is itself the preview content. Do not paint an enlarged
            // 1.6/1.7 capsule while the WebView is becoming ready; that compatibility fallback is
            // only the final preview for plugins that did not declare a 1.8 mini surface.
            var descriptor = webSession.DescribeMiniView(
                context,
                static (_, _) => new Grid { Background = Brushes.Transparent });
            return descriptor with
            {
                Size = ValidatePluginMiniPreferredSize(descriptor.Size),
                DeferContentCreation = false
            };
        }


        // No dedicated preview capability: the enlarged capsule is the final preview, not
        // an intermediate loading view that will later be replaced.
        return DescribePluginCapsuleFallback(context);
    }

    private EdgeCapsulePreviewSize ReadPreferredMiniSize(
        Func<PaperMiniViewSize> read,
        PaperMiniViewSize fallback)
    {
        PaperMiniViewSize value;
        try
        {
            value = read();
        }
        catch
        {
            value = fallback;
        }

        var width = double.IsFinite(value.Width) && value.Width > 0
            ? value.Width
            : fallback.Width;
        var height = double.IsFinite(value.Height) && value.Height > 0
            ? value.Height
            : fallback.Height;
        return ValidatePluginMiniPreferredSize(
            new EdgeCapsulePreviewSize(width, height));
    }

    private EdgeCapsulePreviewSize NormalizePluginMiniSizeForCurrentMonitor(
        EdgeCapsulePreviewSize size)
    {
        var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        return size.Normalize(
            Math.Max(EdgeCapsulePreviewSize.MinimumWidthDip, workArea.Width - 16),
            Math.Max(EdgeCapsulePreviewSize.MinimumHeightDip, workArea.Height - 16));
    }

    private FrameworkElement CreateNativePluginMiniView(
        IPaperMiniViewProvider provider,
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        EnsurePluginMiniViewGeneration(provider, size);
        if (_pluginMiniViewAttempted)
        {
            _pluginMiniViewActive = _pluginMiniView != null;
            return _pluginMiniView ??
                BuildPluginCapsuleEdgePreviewContent(context, size);
        }

        _pluginMiniViewAttempted = true;
        try
        {
            var contentWidth = Math.Max(
                1,
                size.WidthDip - CapsuleCloseWidth - WindowChromeMargin);
            var contentHeight = Math.Max(
                1,
                size.HeightDip - WindowChromeMargin * 2);
            var view = provider.CreateMiniView(new PaperMiniViewContext(
                size.WidthDip,
                size.HeightDip,
                contentWidth,
                contentHeight,
                CurrentPaperBodyTheme()));
            if (view == null ||
                view is Window ||
                view.Parent != null ||
                !PluginVisualTreePolicy.IsSupportedPureWpfTree(view))
            {
                throw new InvalidOperationException(
                    "Native mini view must be a fresh, unparented pure-WPF FrameworkElement.");
            }

            view.Width = double.NaN;
            view.Height = double.NaN;
            view.Margin = new Thickness(0);
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;
            view.ClipToBounds = true;
            _pluginMiniView = view;
            _pluginMiniViewActive = true;
            return view;
        }
        catch
        {
            _pluginMiniView = null;
            _pluginMiniViewActive = false;
            return BuildPluginCapsuleEdgePreviewContent(context, size);
        }
    }

    private void EnsurePluginMiniViewGeneration(
        IPaperMiniViewProvider provider,
        EdgeCapsulePreviewSize size)
    {
        if (_pluginMiniViewGeneration == _bodySessionGeneration &&
            ReferenceEquals(_pluginMiniViewProvider, provider) &&
            Math.Abs(_pluginMiniViewSize.WidthDip - size.WidthDip) <= 0.001 &&
            Math.Abs(_pluginMiniViewSize.HeightDip - size.HeightDip) <= 0.001)
        {
            return;
        }

        ResetPluginMiniViewCache();
        _pluginMiniViewGeneration = _bodySessionGeneration;
        _pluginMiniViewProvider = provider;
        _pluginMiniViewSize = size;
    }

    private void NotifyNativePluginMiniViewVisibility(
        IPaperMiniViewProvider provider,
        bool visible)
    {
        if (!_pluginMiniViewActive ||
            !ReferenceEquals(provider, _pluginMiniViewProvider) ||
            _pluginMiniViewVisible == visible)
        {
            return;
        }

        _pluginMiniViewVisible = visible;
        try
        {
            provider.OnMiniViewVisibilityChanged(visible);
        }
        catch
        {
            // A lifecycle callback cannot disable the body or its capsule fallback.
        }
    }

    private void ResetPluginMiniViewCache()
    {
        var visibleProvider = _pluginMiniViewVisible ? _pluginMiniViewProvider : null;
        // Revoke this cache before notifying foreign code. A callback may close/reload the body
        // or establish a replacement mini; it must not recursively retire the same generation.
        _pluginMiniViewGeneration = -1;
        _pluginMiniViewProvider = null;
        _pluginMiniView = null;
        _pluginMiniViewSize = default;
        _pluginMiniViewAttempted = false;
        _pluginMiniViewActive = false;
        _pluginMiniViewVisible = false;
        try
        {
            visibleProvider?.OnMiniViewVisibilityChanged(false);
        }
        catch
        {
            // Optional mini cleanup cannot block body retirement.
        }
    }

    private EdgeCapsulePreviewDescriptor DescribePluginCapsuleFallback(
        EdgeCapsulePreviewContext context)
    {
        var presentation = _pluginCapsulePresentation;
        if (presentation == null)
        {
            return DefaultEdgeCapsulePreviewProvider.Instance.Describe(context);
        }

        var compactWidth = (PluginCapsuleRequestedContentWidth() ?? 110) +
            CapsuleCloseWidth + WindowChromeMargin;
        var width = Math.Clamp(
            Math.Ceiling(compactWidth * 1.65),
            PluginFallbackMiniMinimumWidth,
            PluginFallbackMiniMaximumWidth);
        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, PluginFallbackMiniHeight),
            size => BuildPluginCapsuleEdgePreviewContent(context, size));
    }

    private FrameworkElement BuildPluginCapsuleEdgePreviewContent(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        if (_pluginCapsulePresentation == null)
        {
            return DefaultEdgeCapsulePreviewProvider.Instance
                .Describe(context)
                .CreateContent(size);
        }
        return new PluginCapsuleFallbackLivePreviewView(
            context,
            size,
            () => BuildPluginCapsuleEdgePreviewCore(context, size));
    }

    private FrameworkElement BuildPluginCapsuleEdgePreviewCore(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size)
    {
        var presentation = _pluginCapsulePresentation;
        if (presentation == null)
        {
            return new Grid { Background = Brushes.Transparent };
        }

        var mirror = PluginCapsuleMirrorVisual();
        if (mirror != null)
        {
            var brush = new VisualBrush(mirror)
            {
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            return new Border
            {
                Margin = new Thickness(14, 16, 14, 16),
                Background = Brushes.Transparent,
                Child = new Rectangle
                {
                    Fill = brush,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    IsHitTestVisible = false
                }
            };
        }

        return new Border
        {
            Margin = new Thickness(14, 14, 14, 14),
            Padding = new Thickness(10, 8, 10, 8),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Child = BuildPluginMiniCapsuleTemplateView(presentation)
        };
    }

    private FrameworkElement? PluginCapsuleMirrorVisual()
    {
        var candidate = _pluginCapsuleDockedCustomView ??
            _pluginCapsuleRegularCustomView;
        return candidate != null &&
               candidate.Parent != null &&
               PluginVisualTreePolicy.IsSupportedPureWpfTree(candidate)
            ? candidate
            : null;
    }

    private FrameworkElement BuildPluginMiniCapsuleTemplateView(
        PaperCapsulePresentation presentation)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
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
                        ? new GridLength(Math.Max(component.Width * 1.35, 12))
                        : GridLength.Auto
            });
            var element = BuildPluginMiniCapsuleComponent(component);
            if (index > 0)
            {
                element.Margin = new Thickness(9, 0, 0, 0);
            }
            Grid.SetColumn(element, index);
            grid.Children.Add(element);
        }
        return grid;
    }

    private FrameworkElement BuildPluginMiniCapsuleComponent(
        PaperCapsuleComponent component)
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
                    FontSize = AppTypography.Scale(22),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
            case PaperCapsuleComponentKind.StatusDot:
                return new Ellipse
                {
                    Width = 11,
                    Height = 11,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            case PaperCapsuleComponentKind.ProgressRing:
                return new CapsuleProgressRing
                {
                    Width = 30,
                    Height = 30,
                    Value = component.Value,
                    ForegroundBrush = brush,
                    TrackBrush = Theme.Tint(38),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            case PaperCapsuleComponentKind.ProgressBar:
                return new CapsuleProgressBar
                {
                    MinWidth = component.Fill ? 38 : 46,
                    Width = component.Fill ? double.NaN : Math.Max(46, component.Width * 1.35),
                    Height = 7,
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
                    FontSize = AppTypography.Scale(18),
                    FontWeight = component.Tone == PaperCapsuleTone.Accent
                        ? FontWeights.SemiBold
                        : CapsuleLabelFontWeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = component.Fill
                        ? HorizontalAlignment.Stretch
                        : HorizontalAlignment.Left,
                    TextAlignment = component.Fill
                        ? TextAlignment.Center
                        : TextAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
        }
    }

}
