using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private EdgePrewarmCoordinator? _edgePrewarm;
    private bool _edgePrewarmStartupReady;
    private int _edgePrewarmMutationDepth;
    private bool _edgePrewarmInputHooked;

    private bool EdgePrewarmEnabled => !IsExiting && State.UseCapsuleMode &&
        State.UseDeepCapsuleMode && State.EnableAnimations && State.ExperimentalEdgeCapsuleHoverPreview;

    private void CompleteStartupEdgePrewarm(bool startPreviewPreload)
    {
        if (IsExiting) return;
        _edgePrewarmStartupReady = true;
        RequestEdgePrewarmForVisibleQueues();
        if (startPreviewPreload)
            MarkdownEdgePreviewPreload.For(Application.Current.Dispatcher).StartStartupWork();
    }

    internal void RequestEdgePrewarmForVisibleQueues()
    {
        if (!_edgePrewarmStartupReady || IsExiting || _edgePrewarmMutationDepth > 0) return;
        var dispatcher = Application.Current.Dispatcher;
        if (_edgePrewarm == null)
        {
            _edgePrewarm = new EdgePrewarmCoordinator(dispatcher,
                CanPrepareEdgePrewarm,
                () => EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(dispatcher),
                PrepareStaticEdgeQueue,
                suspended => MarkdownEdgePreviewPreload.For(dispatcher).SetSuspended(suspended));
            InputManager.Current.PreProcessInput += OnEdgePrewarmInput;
            _edgePrewarmInputHooked = true;
        }
        _edgePrewarm.SetEnabled(EdgePrewarmEnabled);
        if (!EdgePrewarmEnabled) return;
        foreach (var queue in _windows.Values.Where(window => !window.IsClosed &&
                     window.EdgeCapsulePreviewPaper.IsVisible && window.IsDeepCapsulePlaced)
                     .Select(window => QueueKey(window.EdgeCapsulePreviewPaper)).Distinct(StringComparer.Ordinal))
        {
            if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(queue, out var proxy) ||
                !proxy.IsRetainedForQueueBrowsing) _edgePrewarm.Request(queue);
        }
    }

    private bool CanPrepareEdgePrewarm() => EdgePrewarmEnabled && _edgePrewarmMutationDepth == 0 &&
        !_isRestoringStartupPapers && !_isPreparingStartupEdgeCapsules && _startupShellPrewarmTask.IsCompleted &&
        _edgeCapsulePreviewSession == null && !HasDeepCapsuleReorderDragInProgress() &&
        _deepCapsuleContextMenuOwners.Count == 0 &&
        _edgeCapsuleVisualTransactionCommitOperation is not
            { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing } &&
        _edgeCapsuleVisualTransactionEntries.Count == 0 &&
        Mouse.Captured == null && Mouse.LeftButton == MouseButtonState.Released &&
        Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released;

    private EdgePrewarmOutcome PrepareStaticEdgeQueue(string queueKey)
    {
        if (!CanPrepareEdgePrewarm()) return EdgePrewarmOutcome.Deferred;
        if (_edgeCapsuleQueueCompositionProxies.TryGetValue(queueKey, out var existing))
            return existing.IsRetainedForQueueBrowsing ? EdgePrewarmOutcome.Skipped : EdgePrewarmOutcome.Deferred;
        var windows = DeepCapsulePapersInOrder().Where(paper =>
                string.Equals(QueueKey(paper), queueKey, StringComparison.Ordinal))
            .Select(paper => _windows.GetValueOrDefault(paper.Id)).ToArray();
        if (windows.Length == 0) return EdgePrewarmOutcome.Skipped;
        if (windows.Any(window => window == null || !window.CanPreacquireEdgeCapsuleSource))
            return EdgePrewarmOutcome.Deferred;
        if (WindowNative.TryGetCursorScreenPosition(out var pointer) &&
            windows.Any(window => window!.IsEdgeCapsuleInteractiveAt(pointer)))
            return EdgePrewarmOutcome.Deferred;
        var capacityReady = true;
        var capacityChanged = false;
        foreach (var window in windows)
        {
            capacityReady &= window!.PrepareEdgeCapsulePreacquisitionCapacity(out var changed);
            capacityChanged |= changed;
        }
        if (!capacityReady)
        {
            // Enabling preview after startup may leave visible capsule-sized sources. Reserve the
            // product maximum now, then cross a new Rendering barrier before native acquisition.
            if (capacityChanged) _edgePrewarm!.Request(queueKey);
            return EdgePrewarmOutcome.Deferred;
        }
        var motion = EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State);
        var candidates = new List<EdgeCapsuleQueueProxyCandidate>(windows.Length);
        foreach (var window in windows)
        {
            var candidate = window!.CaptureEdgeCapsuleQueueProxyCandidate(queueKey, motion);
            if (!candidate.HasValue || !window.VerifyEdgeCapsuleQueueProxyEndpoint(candidate.Value.Target))
                return EdgePrewarmOutcome.Deferred;
            candidates.Add(candidate.Value);
        }
        var plan = EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention(queueKey, candidates);
        if (plan == null) return EdgePrewarmOutcome.Deferred;
        plan = ReserveEdgeCapsuleQueueProxyOutputCapacity(plan);
        var entries = windows.Select(window => new EdgeCapsuleVisualTransactionEntry(
            window!, queueKey, motion, RefreshLayout: true)).ToArray();
        var version = _edgePrewarm!.Version;
        var sourceHandles = windows.Select(window => window!.EdgeCapsuleQueueProxySourceHandle).ToArray();
        // The endpoint commit changes the presenter's dirty/authority state itself. Recheck the
        // lifecycle and source identity here; the endpoint verifier owns settled geometry checks.
        bool StillCurrent() => _edgePrewarm?.Version == version && CanPrepareEdgePrewarm() &&
            DeepCapsulePapersInOrder().Where(paper =>
                string.Equals(QueueKey(paper), queueKey, StringComparison.Ordinal))
                .Select(paper => _windows.GetValueOrDefault(paper.Id)).SequenceEqual(windows) &&
            windows.Select((window, index) => window!.CanEnterEdgeCapsulePreview &&
                !window.IsEdgeCapsulePreviewOpen &&
                window.EdgeCapsuleQueueProxySourceHandle == sourceHandles[index]).All(valid => valid);
        var startedAt = Stopwatch.GetTimestamp();
        var started = TryStartEdgeCapsuleQueueCompositionProxy(plan, entries, predecessor: null,
            out _, StillCurrent);
        var retained = started && _edgeCapsuleQueueCompositionProxies.TryGetValue(queueKey, out var proxy) &&
            proxy.IsRetainedForQueueBrowsing;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"prewarm.queue phase={(retained ? "ready" : "failed")} queue={queueKey} members={windows.Length} " +
            $"version={version} totalMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F3}");
        return retained ? EdgePrewarmOutcome.Prepared : EdgePrewarmOutcome.Failed;
    }

    internal void NotifyEdgePrewarmPresentation(PaperWindow window)
    {
        // A presentation only advances an existing candidate. Hide/close/input cancellation must
        // not be undone by a reentrant source flush before the lifecycle mutation is finished.
        if (_edgePrewarmMutationDepth == 0 && _edgePrewarm is { IsPreparing: false })
            _edgePrewarm.Wake(QueueKey(window.EdgeCapsulePreviewPaper));
    }

    private void NotifyEdgePrewarmProxyReleased(string queueKey)
    {
        // Only resume interest already requested by a lifecycle mutation. A generic release must
        // not recreate a candidate cancelled by hide, close, or routed button-down.
        if (_edgePrewarmMutationDepth == 0) _edgePrewarm?.Wake(queueKey);
    }

    private void OnEdgePrewarmInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseButtonEventArgs or MouseWheelEventArgs or KeyEventArgs or TouchEventArgs)
            _edgePrewarm?.NotifyInteraction();
        else if (e.StagingItem.Input is MouseEventArgs)
            // WPF mouse input is already application-local. Preserve the conservative pause for
            // real in-app movement without routing it through the desktop-pointer filter below.
            _edgePrewarm?.NotifyInteraction();
    }

    private void ObserveEdgePrewarmPhysicalPointer(
        PaperWindow inputWindow,
        DeviceScreenPoint? pointer)
    {
        if (_edgePrewarm == null ||
            pointer is not { } point ||
            !inputWindow.TryGetEdgeCapsuleInteractiveGeometry(out var geometry) ||
            !EdgeCapsuleGeometry.Contains(geometry.Bounds, point))
        {
            return;
        }

        // Proxy sampling observes the global desktop pointer. Only a pointer actually intersecting
        // this PaperTodo card is application interaction; motion over another application must not
        // repeatedly cancel speculative prewarm or Markdown work.
        _edgePrewarm.NotifyInteraction();
    }

    internal IDisposable SuspendEdgePrewarmForMutation()
    {
        _edgePrewarmMutationDepth++;
        _edgePrewarm?.CancelAll();
        return new EdgePrewarmMutationScope(this);
    }

    private sealed class EdgePrewarmMutationScope(AppController owner) : IDisposable
    {
        private AppController? _owner = owner;
        public void Dispose()
        {
            var controller = _owner;
            _owner = null;
            if (controller == null) return;
            if (--controller._edgePrewarmMutationDepth == 0) controller.RequestEdgePrewarmForVisibleQueues();
        }
    }

    private void DisposeEdgePrewarm()
    {
        _edgePrewarm?.Dispose();
        _edgePrewarm = null;
        _edgePrewarmStartupReady = false;
        if (_edgePrewarmInputHooked)
        {
            InputManager.Current.PreProcessInput -= OnEdgePrewarmInput;
            _edgePrewarmInputHooked = false;
        }
    }
}
