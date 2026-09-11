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
        var weak = new WeakReference<PaperWindow>(this);
        cache.RequestText(_edgeCapsulePreviewInvalidationSource, () =>
            weak.TryGetTarget(out var window) && window.CanPreloadMarkdownText
                ? window.CreateEdgeCapsulePreviewContext() : null);
        RequestMarkdownPreviewLayoutPreload();
    }

    internal void RequestMarkdownPreviewLayoutPreload()
    {
        if (!CanPreloadMarkdownText) return;
        var weak = new WeakReference<PaperWindow>(this);
        MarkdownEdgePreviewPreload.For(Dispatcher).RequestLayout(_edgeCapsulePreviewInvalidationSource,
            () => weak.TryGetTarget(out var window) ? window.ReadMarkdownPreloadTarget() : null);
    }

    private MarkdownEdgePreviewPreload.Target? ReadMarkdownPreloadTarget()
    {
        if (!CanPreloadMarkdownText || !CanEnterEdgeCapsulePreview || IsEdgeCapsulePreviewOpen ||
            _edgeCapsuleHost?.MarkdownPreloadAnchor is not { } anchor) return null;
        var host = _edgeCapsuleHost;
        var generation = _bodySessionGeneration;
        var context = CreateEdgeCapsulePreviewContext();
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
    internal void ScheduleMarkdownPreviewNeighbors(PaperWindow owner)
    {
        if (IsExiting || !State.ExperimentalEdgeCapsuleHoverPreview || !owner.CanEnterEdgeCapsulePreview) return;
        // Reuse the authoritative queue ordering. This never changes owner/intent/hit geometry.
        var queueKey = QueueKey(owner.EdgeCapsulePreviewPaper);
        var queue = BuildCurrentEdgeCapsuleQueuePlan().Queues.FirstOrDefault(item => item.Key == queueKey);
        if (queue == null) return;
        var index = -1;
        for (var i = 0; i < queue.Papers.Count; i++)
            if (queue.Papers[i].Id == owner.EdgeCapsulePreviewPaperId) { index = i; break; }
        if (index < 0) return;
        foreach (var i in new[] { index, index + 1, index - 1 })
            if (i >= 0 && i < queue.Papers.Count && _windows.TryGetValue(queue.Papers[i].Id, out var window))
                window.RequestMarkdownPreviewLayoutPreload();
    }
}
