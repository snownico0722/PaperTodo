using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _markdownPreloadCloseHook;

    private bool CanPreloadMarkdownText =>
        _windowLifecycle == PaperWindowLifecycleState.Alive && _controller.MarkdownPreviewPreloadingAllowed &&
        _controller.State.ExperimentalEdgeCapsuleHoverPreview && _paper.IsVisible &&
        _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown;

    private void ScheduleMarkdownPreviewPreload()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        cache.Invalidate(_edgeCapsulePreviewInvalidationSource);
        if (!CanPreloadMarkdownText)
        {
            cache.Forget(_edgeCapsulePreviewInvalidationSource);
            return;
        }
        if (!_markdownPreloadCloseHook)
        {
            _markdownPreloadCloseHook = true;
            Closed += (_, _) =>
            {
                _edgeCapsulePreviewInvalidationSource.Invalidate();
                cache.Forget(_edgeCapsulePreviewInvalidationSource);
            };
        }
        RequestMarkdownPreviewLayoutPreload();
    }

    internal void RequestMarkdownPreviewLayoutPreload()
    {
        if (!CanPreloadMarkdownText) return;
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        var context = CreateEdgeCapsulePreviewContext();
        var content = cache.Capture(context);
        // Light notes intentionally do nothing here. The expensive-path classifier is the same
        // content signal that chooses prepared TextFormatter paragraphs at demand time.
        if (!MarkdownEdgePreviewPreload.IsClearlyHighLoad(content)) return;
        var weak = new WeakReference<PaperWindow>(this);
        cache.RequestLayout(_edgeCapsulePreviewInvalidationSource,
            () => weak.TryGetTarget(out var window) ? window.ReadMarkdownPreloadTarget() : null);
    }

    private MarkdownEdgePreviewPreload.Target? ReadMarkdownPreloadTarget()
    {
        if (!CanPreloadMarkdownText || !CanEnterEdgeCapsulePreview || IsEdgeCapsulePreviewOpen ||
            _edgeCapsuleHost?.MarkdownPreloadAnchor is not { } anchor) return null;
        var host = _edgeCapsuleHost;
        var generation = _bodySessionGeneration;
        var context = CreateEdgeCapsulePreviewContext();
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        if (!MarkdownEdgePreviewPreload.IsClearlyHighLoad(cache.Capture(context))) return null;
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        var size = descriptor.Size.Normalize(Math.Max(1, workArea.Width - 16), Math.Max(1, workArea.Height - 16));
        // Speculation may use already reserved capacity but may never resize a live/proxied HWND.
        if (!TryConstrainEdgeCapsulePreviewToCurrentHostCapacity(size, out size)) return null;
        return new(context, anchor, size, () =>
            CanPreloadMarkdownText && CanEnterEdgeCapsulePreview && !IsEdgeCapsulePreviewOpen &&
            generation == _bodySessionGeneration && ReferenceEquals(host, _edgeCapsuleHost));
    }
}

public sealed partial class AppController
{
    internal bool MarkdownPreviewPreloadingAllowed => !IsExiting;

    // Kept at the two existing preview-interest call sites, but no longer predicts nearby targets.
    // Each live note decides from its own bounded content whether it is expensive; every expensive
    // note is queued, regardless of queue distance or how many expensive notes currently exist.
    internal void ScheduleMarkdownPreviewNeighbors(PaperWindow owner)
    {
        if (IsExiting || !State.ExperimentalEdgeCapsuleHoverPreview || !owner.CanEnterEdgeCapsulePreview) return;
        foreach (var window in _windows.Values)
            window.RequestMarkdownPreviewLayoutPreload();
    }
}
