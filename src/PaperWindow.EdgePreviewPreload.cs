using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _markdownPreloadCloseHook;
    private FrameworkElement? _markdownPreloadLifecycleAnchor;

    internal bool CanPreloadMarkdownText =>
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
            Closed += (_, _) =>
            {
                cache.Forget(_edgeCapsulePreviewInvalidationSource);
                ObserveMarkdownPreloadHost(null);
            };
        }
        // Only queue a weak reader here. Even the eligibility parse waits for the shared 500ms.
        var weak = new WeakReference<PaperWindow>(this);
        cache.RequestLayout(_edgeCapsulePreviewInvalidationSource,
            () => weak.TryGetTarget(out var window) ? window.ReadMarkdownPreloadTarget() : MarkdownEdgePreviewPreload.ReadResult.Discard);
    }

    private void ResumeMarkdownPreviewPreload()
    {
        if (_markdownPreloadCloseHook && CanPreloadMarkdownText &&
            _edgeCapsuleHost?.MarkdownPreloadAnchor is { IsLoaded: true, IsVisible: true })
            MarkdownEdgePreviewPreload.For(Dispatcher).Resume(_edgeCapsulePreviewInvalidationSource);
    }

    private void ObserveMarkdownPreloadHost(FrameworkElement? anchor)
    {
        if (ReferenceEquals(anchor, _markdownPreloadLifecycleAnchor)) return;
        if (_markdownPreloadLifecycleAnchor is { } old)
        {
            old.Loaded -= OnMarkdownPreloadHostLoaded;
            old.IsVisibleChanged -= OnMarkdownPreloadHostVisibilityChanged;
        }
        _markdownPreloadLifecycleAnchor = anchor;
        if (anchor != null)
        {
            anchor.Loaded += OnMarkdownPreloadHostLoaded;
            anchor.IsVisibleChanged += OnMarkdownPreloadHostVisibilityChanged;
            ResumeMarkdownPreviewPreload();
        }
    }

    private void OnMarkdownPreloadHostLoaded(object sender, RoutedEventArgs e) => ResumeMarkdownPreviewPreload();
    private void OnMarkdownPreloadHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) ResumeMarkdownPreviewPreload();
    }

    private MarkdownEdgePreviewPreload.ReadResult ReadMarkdownPreloadTarget()
    {
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher);
        if (!CanPreloadMarkdownText)
        {
            cache.Forget(_edgeCapsulePreviewInvalidationSource);
            return MarkdownEdgePreviewPreload.ReadResult.Discard;
        }
        // Artifact preparation reads resources/DPI/geometry; it neither opens a preview nor
        // borrows visual authority. Menus, input locks and gestures are not readiness barriers.
        if (_edgeCapsuleHost?.MarkdownPreloadAnchor is not { IsLoaded: true, IsVisible: true } anchor)
            return MarkdownEdgePreviewPreload.ReadResult.Deferred;
        var host = _edgeCapsuleHost;
        var generation = _bodySessionGeneration;
        var context = CreateEdgeCapsulePreviewContext();
        if (!MarkdownEdgePreviewPreload.ShouldPreload(context, cache.Capture(context)))
            return MarkdownEdgePreviewPreload.ReadResult.Discard;
        var descriptor = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(context);
        var workArea = DeepCapsuleMonitorGeometry().LocalWorkAreaDip;
        var size = descriptor.Size.Normalize(Math.Max(1, workArea.Width - 16), Math.Max(1, workArea.Height - 16));
        // Preloading never grows HWND capacity or takes presentation authority.
        if (!TryConstrainEdgeCapsulePreviewToCurrentHostCapacity(size, out size))
            return MarkdownEdgePreviewPreload.ReadResult.Deferred;
        return MarkdownEdgePreviewPreload.ReadResult.Ready(new(context, anchor, size, () =>
            CanPreloadMarkdownText && generation == _bodySessionGeneration &&
            ReferenceEquals(host, _edgeCapsuleHost)));
    }
}

public sealed partial class AppController
{
    internal bool MarkdownPreviewPreloadingAllowed => !IsExiting;

    // Count actual live, eligible edge notes, not all persisted papers or cached artifacts.
    // This is also read after edits; light notes do not lose eligibility after their first warm.
    internal bool PreloadAllEdgeMarkdownNotes => !IsExiting &&
        _windows.Values.Count(window => window.CanPreloadMarkdownText) is > 0 and <= SmallPrewarmPaperLimit;
}
