using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _markdownPreloadCloseHook;

    private bool CanPreloadMarkdownText =>
        _windowLifecycle == PaperWindowLifecycleState.Alive && _controller.MarkdownPreviewPreloadingAllowed &&
        _controller.State.ExperimentalEdgeCapsuleHoverPreview && _paper.IsVisible &&
        _controller.State.UseCapsuleMode && _controller.State.UseDeepCapsuleMode && HasDeepCapsuleSlotPlacement &&
        _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown;

    private void ScheduleMarkdownPreviewPreload()
    {
        MarkdownEdgePreviewPreload.For(Dispatcher).Invalidate(_edgeCapsulePreviewInvalidationSource);
        RequestMarkdownPreviewLayoutPreload();
    }

    internal void RequestMarkdownPreviewLayoutPreload()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        if (!CanPreloadMarkdownText)
        {
            cache.Forget(_edgeCapsulePreviewInvalidationSource);
            return;
        }
        if (!_markdownPreloadCloseHook)
        {
            _markdownPreloadCloseHook = true;
            Closed += (_, _) => cache.Forget(_edgeCapsulePreviewInvalidationSource);
        }
        // Only queue a weak reader here. Even the eligibility parse waits for the shared 500ms.
        var weak = new WeakReference<PaperWindow>(this);
        cache.RequestLayout(_edgeCapsulePreviewInvalidationSource,
            () => weak.TryGetTarget(out var window) ? window.ReadMarkdownPreloadTarget() : null);
    }

    private MarkdownEdgePreviewPreload.Target? ReadMarkdownPreloadTarget()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        if (!CanPreloadMarkdownText)
        {
            cache.Forget(_edgeCapsulePreviewInvalidationSource);
            return null;
        }
        if (!CanEnterEdgeCapsulePreview || IsEdgeCapsulePreviewOpen ||
            _edgeCapsuleHost?.MarkdownPreloadAnchor is not { } anchor) return null;
        var host = _edgeCapsuleHost;
        var generation = _bodySessionGeneration;
        var context = CreateEdgeCapsulePreviewContext();
        if (!MarkdownEdgePreviewPreload.IsClearlyHighLoad(cache.Capture(context))) return null;
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        var size = descriptor.Size.Normalize(Math.Max(1, workArea.Width - 16), Math.Max(1, workArea.Height - 16));
        // Preloading never grows HWND capacity or takes presentation authority.
        if (!TryConstrainEdgeCapsulePreviewToCurrentHostCapacity(size, out size)) return null;
        return new(context, anchor, size, () =>
            CanPreloadMarkdownText && CanEnterEdgeCapsulePreview && !IsEdgeCapsulePreviewOpen &&
            generation == _bodySessionGeneration && ReferenceEquals(host, _edgeCapsuleHost));
    }
}

public sealed partial class AppController
{
    internal bool MarkdownPreviewPreloadingAllowed => !IsExiting;
}
