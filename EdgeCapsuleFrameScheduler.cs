using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One animation-frame scheduler per UI dispatcher. Presenters still own their transitions and
/// reconcile pipelines; the shared scheduler samples one pointer/time per frame, then commits each
/// monitor/edge queue independently so one bad HWND cannot hide unrelated queues.
/// </summary>
internal sealed class EdgeCapsuleFrameScheduler
{
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly Dispatcher _dispatcher;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private readonly List<Action> _postCommitCallbacks = new();
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _acceptingPostCommitCallbacks;
    private int _pendingLoadedReconciles;
    private bool _hasRenderingTime;
    private TimeSpan _lastRenderingTime;
#if DEBUG
    private long _lastRenderingTimestamp;
    private long _debugFrameSequence;
#endif

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(
            dispatcher,
            static key => new EdgeCapsuleFrameScheduler(key));

    public void RegisterLoadedReconcile()
    {
        _dispatcher.VerifyAccess();
        _pendingLoadedReconciles++;
    }

    public void CompleteLoadedReconcile()
    {
        _dispatcher.VerifyAccess();
        if (_pendingLoadedReconciles > 0)
        {
            _pendingLoadedReconciles--;
        }
    }

    public void Activate(EdgeCapsulePresenter presenter)
    {
        _dispatcher.VerifyAccess();
        if (!_presenters.Contains(presenter))
        {
            _presenters.Add(presenter);
        }
        if (!_renderingSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
        }
    }

    public void Deactivate(EdgeCapsulePresenter presenter)
    {
        _dispatcher.VerifyAccess();
        if (_isTicking)
        {
            return;
        }

        _presenters.Remove(presenter);
        StopWhenEmpty();
    }

    internal bool TryEnqueuePostCommit(Action callback)
    {
        _dispatcher.VerifyAccess();
        if (!_isTicking || !_acceptingPostCommitCallbacks)
        {
            return false;
        }

        _postCommitCallbacks.Add(callback);
        return true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess() ||
            _isTicking ||
            _pendingLoadedReconciles > 0)
        {
            return;
        }

        // WPF can raise CompositionTarget.Rendering more than once for the same compositor frame
        // when multiple visual trees invalidate around one render pass. A queue-wide native batch
        // is expensive enough that replaying the exact same RenderingTime is never useful: all
        // intermediate target changes stay in Presenter state and the next real display frame
        // samples the latest target once.
        if (e is RenderingEventArgs renderingArgs)
        {
            if (_hasRenderingTime &&
                renderingArgs.RenderingTime == _lastRenderingTime)
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"scheduler.frame skipped=duplicate-render " +
                    $"renderMs={renderingArgs.RenderingTime.TotalMilliseconds:F3} " +
                    $"presenters={_presenters.Count} loadedPending={_pendingLoadedReconciles}");
#endif
                return;
            }

            _lastRenderingTime = renderingArgs.RenderingTime;
            _hasRenderingTime = true;
        }

#if DEBUG
        var callbackStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var frameSequence = ++_debugFrameSequence;
        var frameGapMilliseconds = _lastRenderingTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _lastRenderingTimestamp,
                callbackStartedAt);
        _lastRenderingTimestamp = callbackStartedAt;
        var debugInitialCount = 0;
        var debugGroupCount = 0;
#endif
        _isTicking = true;
        try
        {
            var initialCount = _presenters.Count;
#if DEBUG
            debugInitialCount = initialCount;
#endif
            if (initialCount == 0)
            {
                return;
            }

            var frameTimestamp = Stopwatch.GetTimestamp();
            var pointer = WindowNative.TryGetCursorScreenPosition(
                out var currentPointer)
                    ? currentPointer
                    : (DeviceScreenPoint?)null;
            var groups = BuildFrameGroups(initialCount);
#if DEBUG
            debugGroupCount = groups.Count;
#endif
            foreach (var group in groups)
            {
                AdvanceNativeBatchGroup(
                    group,
                    pointer,
                    frameTimestamp);
            }

            for (var index = _presenters.Count - 1; index >= 0; index--)
            {
                if (!_presenters[index].UsesSharedFrameScheduler(this))
                {
                    _presenters.RemoveAt(index);
                }
            }
        }
        finally
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.frame sequence={frameSequence} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callbackStartedAt):F3} " +
                $"gapMs={frameGapMilliseconds:F3} presenters={debugInitialCount} " +
                $"groups={debugGroupCount} loadedPending={_pendingLoadedReconciles}");
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
            _isTicking = false;
            StopWhenEmpty();
        }
    }

    private IReadOnlyList<List<EdgeCapsulePresenter>> BuildFrameGroups(
        int initialCount)
    {
        var groups = new List<List<EdgeCapsulePresenter>>();
        var groupIndices =
            new Dictionary<EdgeCapsuleNativeBatchGroup, int>();
        for (var index = 0; index < initialCount; index++)
        {
            var presenter = _presenters[index];
            var key = presenter.NativeBatchGroup;
            if (!groupIndices.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groups.Count;
                groupIndices[key] = groupIndex;
                groups.Add(new List<EdgeCapsulePresenter>());
            }
            groups[groupIndex].Add(presenter);
        }
        return groups;
    }

    private void AdvanceNativeBatchGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        DeviceScreenPoint? pointer,
        long frameTimestamp)
    {
        if (presenters.Count == 0)
        {
            return;
        }

        _postCommitCallbacks.Clear();
        _acceptingPostCommitCallbacks = true;
        var transactionGroupId =
            presenters[0].NativeBatchTransactionGroupId;
#if DEBUG
        var groupStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        double reconcileMilliseconds = 0;
        double statusMilliseconds = 0;
        double nativeCommitMilliseconds = 0;
        double completionMilliseconds = 0;
        double postCommitMilliseconds = 0;
        double slowestPresenterMilliseconds = 0;
        var slowestPresenter = "<none>";
        var debugOutcome = "exception";
        var debugNativeWindowCount = 0;
        var debugNativeCommitAttempted = false;
#endif
        try
        {
            bool nativeBatchCommitted;
            bool logicalBatchDeferred;
            bool logicalBatchFailed;
            bool frameCommitted;
            bool frameDeferred;
            using (_dispatcher.DisableProcessing())
            {
                using (var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(
                        presenters.Count))
                {
                    for (var index = presenters.Count - 1;
                         index >= 0;
                         index--)
                    {
#if DEBUG
                        var presenterStartedAt =
                            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                        _ = presenters[index].AdvanceSharedFrame(
                            this,
                            pointer,
                            frameTimestamp);
#if DEBUG
                        var presenterMilliseconds =
                            EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                                presenterStartedAt);
                        reconcileMilliseconds += presenterMilliseconds;
                        if (presenterMilliseconds > slowestPresenterMilliseconds)
                        {
                            slowestPresenterMilliseconds = presenterMilliseconds;
                            slowestPresenter = presenters[index].DiagnosticId;
                        }
#endif
                    }

                    _acceptingPostCommitCallbacks = false;
                    logicalBatchDeferred = false;
                    logicalBatchFailed = false;
#if DEBUG
                    var statusStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    for (var index = presenters.Count - 1;
                         index >= 0;
                         index--)
                    {
                        var presenter = presenters[index];
                        if (!presenter.NativeBatchApplyActive)
                        {
                            continue;
                        }

                        switch (presenter.NativeBatchApplyStatus)
                        {
                            case EdgeCapsuleNativeBatchApplyStatus.Deferred:
                                logicalBatchDeferred = true;
                                break;
                            case EdgeCapsuleNativeBatchApplyStatus.Failed:
                                logicalBatchFailed = true;
                                break;
                        }
                    }
#if DEBUG
                    statusMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            statusStartedAt);
                    debugNativeWindowCount = nativeBoundsBatch.PendingWindowCount;
                    var nativeCommitStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    nativeBatchCommitted = nativeBoundsBatch.Commit();
#if DEBUG
                    nativeCommitMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            nativeCommitStartedAt);
                    debugNativeCommitAttempted = nativeBoundsBatch.PerformedNativeCommit;
#endif
                }

                frameDeferred = nativeBatchCommitted &&
                    logicalBatchDeferred &&
                    !logicalBatchFailed;
                frameCommitted = nativeBatchCommitted &&
                    !logicalBatchDeferred &&
                    !logicalBatchFailed;
#if DEBUG
                var completionStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                for (var index = presenters.Count - 1;
                     index >= 0;
                     index--)
                {
                    var presenter = presenters[index];
                    if (frameCommitted)
                    {
                        presenter.CompleteNativeBatchApplySuccess();
                    }
                    else if (frameDeferred)
                    {
                        presenter.CompleteNativeBatchApplyDeferred();
                    }
                    else
                    {
                        presenter.CompleteNativeBatchApplyFailure(
                            frameTimestamp);
                    }
                }
#if DEBUG
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        completionStartedAt);
#endif
            }

#if DEBUG
            var groupCompletionStartedAt =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            CompleteNativeBatchTransactionGroup(
                presenters,
                transactionGroupId,
                frameCommitted,
                frameDeferred);

            if (frameCommitted)
            {
#if DEBUG
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        groupCompletionStartedAt);
                var postCommitStartedAt =
                    EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                for (var index = 0;
                     index < _postCommitCallbacks.Count;
                     index++)
                {
                    _postCommitCallbacks[index]();
                }
#if DEBUG
                postCommitMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        postCommitStartedAt);
#endif
            }
#if DEBUG
            if (!frameCommitted)
            {
                completionMilliseconds +=
                    EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                        groupCompletionStartedAt);
            }
            debugOutcome = frameCommitted
                ? "committed"
                : frameDeferred
                    ? "deferred"
                    : "failed";
#endif
        }
        finally
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.group sequence={_debugFrameSequence} outcome={debugOutcome} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(groupStartedAt):F3} " +
                $"reconcileMs={reconcileMilliseconds:F3} statusMs={statusMilliseconds:F3} " +
                $"nativeCommitMs={nativeCommitMilliseconds:F3} completeMs={completionMilliseconds:F3} " +
                $"postCommitMs={postCommitMilliseconds:F3} presenters={presenters.Count} " +
                $"nativeWindows={debugNativeWindowCount} nativeCommit={debugNativeCommitAttempted} " +
                $"slowest={slowestPresenter}:{slowestPresenterMilliseconds:F3} " +
                $"transaction={transactionGroupId}");
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
        }
    }

    private static void CompleteNativeBatchTransactionGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        long transactionGroupId,
        bool frameCommitted,
        bool frameDeferred)
    {
        if (transactionGroupId <= 0)
        {
            return;
        }

        if (!frameCommitted && !frameDeferred &&
            presenters.Any(presenter =>
                presenter.NativeBatchTransactionRetryExhausted))
        {
            foreach (var presenter in presenters)
            {
                presenter.AbortNativeBatchTransactionGroup(
                    transactionGroupId);
            }
            return;
        }

        if (!frameCommitted ||
            presenters.Any(presenter =>
                !presenter.CanReleaseNativeBatchTransactionGroup(
                    transactionGroupId)))
        {
            return;
        }

        foreach (var presenter in presenters)
        {
            presenter.ReleaseNativeBatchTransactionGroup(
                transactionGroupId);
        }
    }

    private void StopWhenEmpty()
    {
        if (_presenters.Count == 0 && _renderingSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
            _hasRenderingTime = false;
            _lastRenderingTime = default;
#if DEBUG
            _lastRenderingTimestamp = 0;
#endif
        }
    }
}
