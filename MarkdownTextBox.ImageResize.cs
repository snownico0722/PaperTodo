using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private DispatcherTimer? _imageResizeSettleTimer;
    private bool _isImageResizePreview;
    private bool _imageRenderingSuspended;
    private bool _isImageViewportPreviewQueued;
    private bool _isViewportProtectedRefreshQueued;
    private double _lastImageViewportWidth = -1;

    public void SetImageRenderingSuspended(bool suspended)
    {
        if (_imageRenderingSuspended == suspended)
        {
            return;
        }

        _imageRenderingSuspended = suspended;
        if (suspended)
        {
            _imageResizeSettleTimer?.Stop();
            _isImageResizePreview = false;
            _lastImageViewportWidth = -1;
            SetBitmapScalingMode(BitmapScalingMode.HighQuality);
            ClearViewportProtectedBitmaps();
        }

        RefreshTextView();
        if (!suspended)
        {
            QueueRefreshViewportProtectedBitmaps();
        }
    }

    private void HandleImageViewportSizeChanged()
    {
        if (!_hadInternalImageReferences || _imageRenderingSuspended)
        {
            return;
        }

        var currentWidth = ActualWidth;
        if (currentWidth <= 0)
        {
            return;
        }

        if (Math.Abs(_lastImageViewportWidth - currentWidth) < 1.0)
        {
            return;
        }

        _lastImageViewportWidth = currentWidth;
        _isImageResizePreview = true;
        SetBitmapScalingMode(BitmapScalingMode.LowQuality);

        // During drag: keep the current decode and only retarget visible image block widths.
        QueueImageViewportPreviewLayout();

        _imageResizeSettleTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            (_, _) => CompleteImageResizePreview(),
            Dispatcher);

        _imageResizeSettleTimer.Stop();
        _imageResizeSettleTimer.Start();
    }

    private void CompleteImageResizePreview(bool force = false)
    {
        _imageResizeSettleTimer?.Stop();
        if (_imageRenderingSuspended || (!force && !_isImageResizePreview))
        {
            return;
        }

        _isImageResizePreview = false;
        SetBitmapScalingMode(BitmapScalingMode.HighQuality);

        // One final redraw re-resolves display width and may up/down-grade the single cached decode.
        QueuePostPasteRefresh();
    }

    internal void RefreshImageDecodeForCurrentDpi()
    {
        if (!_hadInternalImageReferences || _imageRenderingSuspended)
        {
            return;
        }

        CompleteImageResizePreview(force: true);
    }

    private void QueueImageViewportPreviewLayout()
    {
        if (_isImageViewportPreviewQueued)
        {
            return;
        }

        _isImageViewportPreviewQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isImageViewportPreviewQueued = false;
                if (_imageRenderingSuspended || !_isImageResizePreview || !_hadInternalImageReferences)
                {
                    return;
                }

                if (!TryApplyImageViewportPreviewLayout())
                {
                    // Visual lines not ready: light redraw only, without a decode resize in preview.
                    var textView = TextArea.TextView;
                    if (Document != null && Document.TextLength > 0)
                    {
                        textView.Redraw(0, Document.TextLength, DispatcherPriority.Render);
                    }
                    else
                    {
                        textView.Redraw(DispatcherPriority.Render);
                    }
                }
            }),
            DispatcherPriority.Background);
    }

    private bool TryApplyImageViewportPreviewLayout()
    {
        var textView = TextArea.TextView;
        if (!textView.VisualLinesValid)
        {
            return false;
        }

        var targetWidth = ImageTargetWidth();
        var updated = 0;
        ApplyImageViewportPreviewLayout(textView, targetWidth, ref updated);
        return updated > 0;
    }

    private static void ApplyImageViewportPreviewLayout(
        DependencyObject node,
        double targetWidth,
        ref int updated)
    {
        if (node is Border { Tag: ImageBlockTag tag } host)
        {
            var displayWidth = ResolveImageDisplayWidth(
                tag.DisplayOptions,
                tag.NaturalWidth,
                targetWidth);
            host.Width = targetWidth;
            switch (host.Child)
            {
                case System.Windows.Controls.Image image:
                    image.Width = displayWidth;
                    break;
                case Border placeholder:
                    placeholder.Width = Math.Max(120, Math.Min(targetWidth, displayWidth));
                    break;
            }

            updated++;
            return;
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        for (var i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, i);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            ApplyImageViewportPreviewLayout(child, targetWidth, ref updated);
        }
    }

    private void QueueRefreshViewportProtectedBitmaps()
    {
        if (_isViewportProtectedRefreshQueued)
        {
            return;
        }

        _isViewportProtectedRefreshQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isViewportProtectedRefreshQueued = false;
                RefreshViewportProtectedBitmaps();
            }),
            DispatcherPriority.Background);
    }

    private void RefreshViewportProtectedBitmaps()
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId))
        {
            return;
        }

        if (_imageRenderingSuspended || !ShouldRenderImages || !_hadInternalImageReferences)
        {
            _imageStore.SetViewportProtectedBitmapIds(_noteId, Array.Empty<string>());
            return;
        }

        var textView = TextArea.TextView;
        if (!textView.VisualLinesValid)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        CollectVisibleImageIds(textView, ids);
        _imageStore.SetViewportProtectedBitmapIds(_noteId, ids);
    }

    private void ClearViewportProtectedBitmaps()
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId))
        {
            return;
        }

        _imageStore.SetViewportProtectedBitmapIds(_noteId, Array.Empty<string>());
    }

    private static void CollectVisibleImageIds(DependencyObject node, ISet<string> imageIds)
    {
        if (node is FrameworkElement { Tag: ImageBlockTag tag } &&
            !string.IsNullOrWhiteSpace(tag.ImageId))
        {
            imageIds.Add(tag.ImageId);
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        for (var i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, i);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            CollectVisibleImageIds(child, imageIds);
        }
    }

    private void SetBitmapScalingMode(BitmapScalingMode mode)
    {
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, mode);
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(TextArea.TextView, mode);
    }
}
