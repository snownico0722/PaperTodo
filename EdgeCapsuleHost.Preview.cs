using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    private Border? _previewViewportLayer;
    private Border? _previewContentLayer;
    private FrameworkElement? _previewContent;
    private bool _previewVisible;

    public bool IsPreviewPointerCaptureActive
    {
        get
        {
            if (_disposed || !_previewVisible || _previewContent == null)
            {
                return false;
            }

            return Mouse.Captured is DependencyObject captured &&
                IsDescendantOfPreview(captured);
        }
    }

    // Stage and pre-render the final-size preview tree while it is still detached. The viewport
    // changes size during the shell animation, but the content tree itself keeps its final layout
    // size so shrinking never causes a ScrollViewer or wrapped text to re-layout frame-by-frame.
    public void StagePreviewContent(
        FrameworkElement content,
        double contentWidthDip,
        double contentHeightDip)
    {
        if (_disposed)
        {
            return;
        }
        if (content is Window ||
            (content.Parent != null &&
             !ReferenceEquals(content.Parent, _previewContentLayer)))
        {
            throw new InvalidOperationException(
                "Preview content must be a fresh, unparented FrameworkElement.");
        }

        EnsurePreviewLayers();
        if (_previewContentLayer == null)
        {
            return;
        }

        if (_previewContentLayer.Child != null &&
            !ReferenceEquals(_previewContentLayer.Child, content))
        {
            _previewContentLayer.Child = null;
        }

        contentWidthDip = Math.Max(1, contentWidthDip);
        contentHeightDip = Math.Max(1, contentHeightDip);
        _previewContentLayer.Width = contentWidthDip;
        _previewContentLayer.Height = contentHeightDip;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;

        if (content is EdgeCapsuleLivePreviewView livePreview)
        {
            livePreview.PrepareForFirstDisplay();
        }

        _previewContent = content;
        _previewContentLayer.Child = content;
    }

    public void ClearPreviewContent()
    {
        if (_disposed)
        {
            return;
        }

        DetachPreviewContent();
    }

    private void EnsurePreviewLayers()
    {
        if (_previewViewportLayer != null && _previewContentLayer != null)
        {
            return;
        }

        var contentLayer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = false,
            IsHitTestVisible = true
        };
        var viewportLayer = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = contentLayer
        };
        Panel.SetZIndex(viewportLayer, 30);
        ContentHost.Children.Add(viewportLayer);
        _previewContentLayer = contentLayer;
        _previewViewportLayer = viewportLayer;
    }

    private void ApplyPreviewPresentation(
        EdgeCapsulePresentationFrame frame)
    {
        var dpiScaleY = Math.Max(1, frame.DpiScaleY);
        var chromeMarginDevice = Math.Max(
            0,
            (int)Math.Round(
                _options.WindowChromeMargin * dpiScaleY,
                MidpointRounding.AwayFromZero));
        var bodyHeightDevice = Math.Max(
            1,
            frame.Bounds.Height - chromeMarginDevice * 2);
        var bodyHeight = bodyHeightDevice / dpiScaleY;

        // VisualSurface owns the exact device-pixel frame in both axes. Keep all three shells
        // stretched inside that one surface so width and height are committed by the same WPF
        // layout pass; assigning three independent heights here makes the native height resize
        // visibly lead the surface-width update while a preview is shrinking.
        Chrome.VerticalAlignment = VerticalAlignment.Stretch;
        Chrome.Height = double.NaN;
        Shell.VerticalAlignment = VerticalAlignment.Stretch;
        Shell.Height = double.NaN;
        Outline.VerticalAlignment = VerticalAlignment.Stretch;
        Outline.Height = double.NaN;

        var heightExpanded =
            bodyHeight > _options.BodyHeight + 0.5;
        var previewSurface =
            frame.Surface == EdgeCapsuleSurfaceKind.DockedPreview;
        // Opening starts with the old compact bounds, while an outgoing preview deliberately keeps
        // DockedPreview until the width/height transition reaches its common final frame. Surface,
        // not the intermediate height threshold, therefore owns the preview tree's lifetime.
        var retainPreview = previewSurface || heightExpanded;

        var hasContent =
            _previewViewportLayer != null &&
            _previewContentLayer != null &&
            _previewContent != null;
        _previewVisible = retainPreview && hasContent;

        if (_previewContentLayer != null)
        {
            _previewContentLayer.HorizontalAlignment = frame.Edge == EdgeCapsuleEdge.Left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }
        ApplyPreviewLayerState(
            _previewVisible,
            _previewVisible && frame.IsHitTestVisible);

        // Compact and preview text are mutually exclusive. During a rapid third-card transfer the
        // controller may deliberately release the oldest tree before its non-interactive shell has
        // finished shrinking; keep that shell blank, but retain the compact title if an interactive
        // preview ever reaches Apply without staged content.
        ApplyCompactContentVisibility(
            suppressed: _previewVisible ||
                (retainPreview && !frame.IsHitTestVisible));

        if (!retainPreview && hasContent)
        {
            // Keep the outgoing final-size tree while the viewport shrinks, then release it on the
            // common final compact frame. The controller may clear an older outgoing tree earlier
            // during a rapid third-card transfer.
            DetachPreviewContent();
        }
    }

    private void ApplyPreviewLayerState(
        bool visible,
        bool hitTestVisible)
    {
        if (_previewViewportLayer == null)
        {
            return;
        }

        _previewViewportLayer.Visibility =
            visible ? Visibility.Visible : Visibility.Collapsed;
        _previewViewportLayer.Opacity = visible ? 1 : 0;
        _previewViewportLayer.IsHitTestVisible = visible && hitTestVisible;
    }

    private void ApplyCompactContentVisibility(bool suppressed)
    {
        ContentGrid.Visibility =
            suppressed ? Visibility.Collapsed : Visibility.Visible;
        ContentGrid.Opacity = 1;
        if (_pluginContentLayer != null)
        {
            _pluginContentLayer.Visibility = suppressed
                ? Visibility.Collapsed
                : _pluginContentLayer.Child != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            _pluginContentLayer.Opacity = 1;
        }

        ContentArea.Background = suppressed
            ? Brushes.Transparent
            : ContentArea.IsMouseOver
                ? _hoverBrush
                : Brushes.Transparent;
    }

    private void DetachPreviewContent()
    {
        if (_previewContentLayer != null)
        {
            _previewContentLayer.Child = null;
            _previewContentLayer.Width = double.NaN;
            _previewContentLayer.Height = double.NaN;
        }
        _previewContent = null;
        _previewVisible = false;
        if (_previewViewportLayer != null)
        {
            _previewViewportLayer.Visibility = Visibility.Collapsed;
            _previewViewportLayer.Opacity = 0;
            _previewViewportLayer.IsHitTestVisible = false;
        }
    }

    private bool IsPreviewInteractiveSource(
        DependencyObject? source)
    {
        if (!_previewVisible ||
            _previewViewportLayer == null ||
            _previewContentLayer == null ||
            _previewContent == null)
        {
            return false;
        }

        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, _previewViewportLayer) ||
                ReferenceEquals(current, _previewContentLayer))
            {
                return false;
            }
            if (EdgeCapsulePreviewInteraction.GetConsumesPointer(current) ||
                current is ButtonBase or
                    TextBoxBase or
                    Selector or
                    ScrollBar or
                    Thumb or
                    PasswordBox or
                    MenuItem or
                    Hyperlink)
            {
                return true;
            }

            current = PreviewVisualParent(current);
        }

        return false;
    }

    private bool IsDescendantOfPreview(DependencyObject current)
    {
        DependencyObject? candidate = current;
        while (candidate != null)
        {
            if (ReferenceEquals(candidate, _previewViewportLayer) ||
                ReferenceEquals(candidate, _previewContentLayer) ||
                ReferenceEquals(candidate, _previewContent))
            {
                return true;
            }
            candidate = PreviewVisualParent(candidate);
        }
        return false;
    }

    private static DependencyObject? PreviewVisualParent(
        DependencyObject current)
    {
        if (current is Visual ||
            current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }
        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }
        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content);
        }
        return null;
    }
}
