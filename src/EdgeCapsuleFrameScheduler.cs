using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
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
    // A missing-render safety timeout, not a target frame interval. The former 12 ms deadline
    // raced ordinary 60 Hz composition callbacks and effectively supplied a second frame clock.
    private const int TransitionLivenessWatchdogMilliseconds = 100;
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly Dispatcher _dispatcher;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private readonly List<Action> _postCommitCallbacks = new();
    private readonly List<List<EdgeCapsulePresenter>> _frameGroups = new();
    private readonly Dictionary<EdgeCapsuleNativeBatchGroup, int> _frameGroupIndices = new();
    private readonly Timer _transitionLivenessWatchdog;
    private bool _transitionLivenessWatchdogScheduled;
    private DispatcherOperation? _transitionLivenessRescueOperation;
    private int _transitionLivenessThreadPoolWakeQueued;
    private long _transitionLivenessThreadPoolWakeTimestamp;
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _acceptingPostCommitCallbacks;
    private int _pendingRenderReconciles;
    private long _transitionLivenessWatchdogGeneration;
    private long _transitionLivenessWatchdogDeadlineTimestamp;
    private TimeSpan? _lastRenderingTime;
#if DEBUG
    private long _pendingRenderReconcileStartedAtTimestamp;
    private long _lastRawRenderingCallbackTimestamp;
    private long _lastRenderingTimestamp;
    private long _lastWpfPresentationChangeTimestamp;
    private ulong _lastWpfTransitionFingerprint;
    private readonly List<(
        EdgeCapsulePresenter Presenter,
        int Version,
        bool ActiveBefore)> _debugWpfPresentationSamples = new();
    private long _debugRenderingCallbackSequence;
    private long _debugFrameSequence;
    private int _suppressedDuplicateRenderingCallbacks;
    private int _suppressedPendingReconcileRenderingCallbacks;
    private int _suppressedExternalNativeBatchRenderingCallbacks;
    private int _suppressedReentrantRenderingCallbacks;
    private long _suppressedRenderingStartedAtTimestamp;
#endif

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _transitionLivenessWatchdog = new Timer(
            static state =>
                ((EdgeCapsuleFrameScheduler)state!)
                    .OnTransitionLivenessWatchdogThreadPool(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(
            dispatcher,
            static key => new EdgeCapsuleFrameScheduler(key));

    public void RegisterRenderReconcile()
    {
        _dispatcher.VerifyAccess();
#if DEBUG
        if (_pendingRenderReconciles == 0)
        {
            _pendingRenderReconcileStartedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.pending phase=begin generation={_transitionLivenessWatchdogGeneration} " +
                $"watchdogArmed={_transitionLivenessWatchdogDeadlineTimestamp > 0}");
        }
#endif
        _pendingRenderReconciles++;
    }

    public void CompleteRenderReconcile()
    {
        _dispatcher.VerifyAccess();
        if (_pendingRenderReconciles <= 0)
        {
            return;
        }

        _pendingRenderReconciles--;
        if (_pendingRenderReconciles != 0)
        {
            return;
        }

#if DEBUG
        var completedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var pendingSpanMilliseconds = _pendingRenderReconcileStartedAtTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _pendingRenderReconcileStartedAtTimestamp,
                completedAt);
        var watchdogOverdueMilliseconds =
            _transitionLivenessWatchdogDeadlineTimestamp > 0 &&
            completedAt >= _transitionLivenessWatchdogDeadlineTimestamp
                ? EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    _transitionLivenessWatchdogDeadlineTimestamp,
                    completedAt)
                : 0;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"scheduler.pending phase=drained spanMs={pendingSpanMilliseconds:F3} " +
            $"watchdogOverdueMs={watchdogOverdueMilliseconds:F3} " +
            $"generation={_transitionLivenessWatchdogGeneration}");
        _pendingRenderReconcileStartedAtTimestamp = 0;
#endif
        QueueExpiredTransitionLivenessRescue("pending-release");
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
        // Cover a missing first Rendering callback too. Repeated activation must not postpone
        // an already-armed deadline; only an accepted shared frame records actual progress.
        if (_transitionLivenessWatchdogDeadlineTimestamp == 0 && presenter.HasActiveTransition)
        {
            ArmTransitionLivenessWatchdog();
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
        if (!HasActiveTransitionPresenter())
        {
            DisarmTransitionLivenessWatchdog();
        }
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

    private bool HasExternallyOwnedNativeBatchApply()
    {
        // The scheduler always completes its own BeginNativeBatchApply calls before _isTicking is
        // cleared. Therefore an active apply observed here belongs to a controller-owned visual
        // transaction that was synchronously re-entered by native HWND message dispatch.
        for (var index = 0; index < _presenters.Count; index++)
        {
            if (EdgeCapsuleNativeTransactionPolicy.ShouldDeferSharedFrameForNativeApply(
                    _presenters[index].NativeBatchApplyActive))
            {
                return true;
            }
        }
        return false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            return;
        }

        var renderingTime = e is RenderingEventArgs renderingArgs
            ? renderingArgs.RenderingTime
            : (TimeSpan?)null;
#if DEBUG
        var rawCallbackTimestamp = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var rawRenderingSequence = ++_debugRenderingCallbackSequence;
        var rawGapMilliseconds = _lastRawRenderingCallbackTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _lastRawRenderingCallbackTimestamp,
                rawCallbackTimestamp);
        _lastRawRenderingCallbackTimestamp = rawCallbackTimestamp;
#endif
        if (_isTicking)
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedReentrantRenderingCallbacks);
            TraceRenderingCallback(
                rawRenderingSequence,
                rawGapMilliseconds,
                renderingTime,
                "suppressed",
                "reentrant");
#endif
            return;
        }
        if (_pendingRenderReconciles > 0)
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedPendingReconcileRenderingCallbacks);
            TraceRenderingCallback(
                rawRenderingSequence,
                rawGapMilliseconds,
                renderingTime,
                "suppressed",
                "pending-reconcile");
#endif
            return;
        }
        if (HasExternallyOwnedNativeBatchApply())
        {
#if DEBUG
            RecordSuppressedRenderingCallback(ref _suppressedExternalNativeBatchRenderingCallbacks);
            TraceRenderingCallback(
                rawRenderingSequence,
                rawGapMilliseconds,
                renderingTime,
                "suppressed",
                "external-native-batch");
#endif
            return;
        }

        if (renderingTime.HasValue &&
            _lastRenderingTime.HasValue &&
            renderingTime.Value == _lastRenderingTime.Value)
        {
#if DEBUG
            _suppressedDuplicateRenderingCallbacks++;
            TraceRenderingCallback(
                rawRenderingSequence,
                rawGapMilliseconds,
                renderingTime,
                "suppressed",
                "duplicate");
#endif
            return;
        }
        _lastRenderingTime = renderingTime;
#if DEBUG
        TraceRenderingCallback(
            rawRenderingSequence,
            rawGapMilliseconds,
            renderingTime,
            "accepted",
            "accepted");
#endif

        // Accepted composition frames slide the rescue deadline after committing. The existing
        // one-shot wake rechecks that deadline; do not cancel/change a ThreadPool timer per frame.
        AdvanceSharedFrame(renderingTime, source: "render");
    }

    private void OnTransitionLivenessWatchdogThreadPool()
    {
        var firedAtTimestamp = Stopwatch.GetTimestamp();
        if (Interlocked.Exchange(
                ref _transitionLivenessThreadPoolWakeQueued,
                1) != 0)
        {
            return;
        }

        Interlocked.Exchange(
            ref _transitionLivenessThreadPoolWakeTimestamp,
            firedAtTimestamp);
        var operation = _dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            (Action)OnTransitionLivenessWatchdogDispatcherWake);
        if (operation.Status == DispatcherOperationStatus.Aborted)
        {
            Interlocked.Exchange(
                ref _transitionLivenessThreadPoolWakeQueued,
                0);
            Interlocked.Exchange(
                ref _transitionLivenessThreadPoolWakeTimestamp,
                0);
        }
    }

    private void OnTransitionLivenessWatchdogDispatcherWake()
    {
        // This flag is UI-thread-owned, including when a stale wake outlives cancellation.
        // Such a wake can only inspect/reschedule the latest deadline, never replay old work.
        _transitionLivenessWatchdogScheduled = false;
        Interlocked.Exchange(
            ref _transitionLivenessThreadPoolWakeQueued,
            0);
        var firedAtTimestamp = Interlocked.Exchange(
            ref _transitionLivenessThreadPoolWakeTimestamp,
            0);
        if (!_dispatcher.CheckAccess() ||
            _dispatcher.HasShutdownStarted ||
            _presenters.Count == 0 ||
            !HasActiveTransitionPresenter())
        {
            DisarmTransitionLivenessWatchdog();
            return;
        }

        var generation = _transitionLivenessWatchdogGeneration;
        var nowTimestamp = Stopwatch.GetTimestamp();
#if DEBUG
        var dispatchMilliseconds = firedAtTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                firedAtTimestamp,
                nowTimestamp);
        var deadlineOverdueMilliseconds =
            _transitionLivenessWatchdogDeadlineTimestamp > 0 &&
            nowTimestamp >= _transitionLivenessWatchdogDeadlineTimestamp
                ? EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                    _transitionLivenessWatchdogDeadlineTimestamp,
                    nowTimestamp)
                : 0;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"scheduler.watchdog phase=wake source=threadpool generation={generation} " +
            $"dispatchMs={dispatchMilliseconds:F3} " +
            $"deadlineOverdueMs={deadlineOverdueMilliseconds:F3}");
#endif
        if (_transitionLivenessWatchdogDeadlineTimestamp <= 0)
        {
            return;
        }
        if (nowTimestamp < _transitionLivenessWatchdogDeadlineTimestamp)
        {
            var remainingTimestampTicks =
                _transitionLivenessWatchdogDeadlineTimestamp - nowTimestamp;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.watchdog phase=early generation={generation} " +
                $"remainingMs={TimestampTicksToMilliseconds(remainingTimestampTicks):F3}");
#endif
            ScheduleTransitionLivenessWatchdog(
                generation,
                remainingTimestampTicks);
            return;
        }

        TryRunTransitionLivenessRescue(
            trigger: "timer",
            expectedGeneration: generation);
    }

    private void AdvanceSharedFrame(TimeSpan? renderingTime, string source)
    {
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
        var duplicateRenderingCallbacks = _suppressedDuplicateRenderingCallbacks;
        _suppressedDuplicateRenderingCallbacks = 0;
        var suppressedPendingCallbacks = _suppressedPendingReconcileRenderingCallbacks;
        var suppressedExternalCallbacks = _suppressedExternalNativeBatchRenderingCallbacks;
        var suppressedReentrantCallbacks = _suppressedReentrantRenderingCallbacks;
        var suppressedSpanMilliseconds = _suppressedRenderingStartedAtTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _suppressedRenderingStartedAtTimestamp,
                callbackStartedAt);
        _suppressedPendingReconcileRenderingCallbacks = 0;
        _suppressedExternalNativeBatchRenderingCallbacks = 0;
        _suppressedReentrantRenderingCallbacks = 0;
        _suppressedRenderingStartedAtTimestamp = 0;
        var renderingTimeMilliseconds = renderingTime?.TotalMilliseconds ?? -1;
        var debugHadActiveTransitionBefore = HasActiveTransitionPresenter();
        var debugTransitionFingerprintBefore =
            GetDebugActiveTransitionFingerprint();
#endif
        var anyCommittedApply = false;
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

#if DEBUG
            _debugWpfPresentationSamples.Clear();
            for (var index = 0; index < initialCount; index++)
            {
                var presenter = _presenters[index];
                _debugWpfPresentationSamples.Add((
                    presenter,
                    presenter.AppliedPresentationVersion,
                    presenter.HasActiveTransition));
            }
#endif
            var frameTimestamp = Stopwatch.GetTimestamp();
            var pointer = WindowNative.TryGetCursorScreenPosition(
                out var currentPointer)
                    ? currentPointer
                    : (DeviceScreenPoint?)null;
            var groupCount = BuildFrameGroups(initialCount);
#if DEBUG
            debugGroupCount = groupCount;
#endif
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                anyCommittedApply |= AdvanceNativeBatchGroup(
                    _frameGroups[groupIndex],
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
            var debugWpfPresentationVersionDelta = 0;
            var debugWpfSampleChanged = 0;
            var debugWpfSampleEqual = 0;
            var debugWpfCompleteEqual = 0;
            var debugWpfSettledEqual = 0;
            var debugWpfApplyFailed = 0;
            for (var index = 0; index < _debugWpfPresentationSamples.Count; index++)
            {
                var sample = _debugWpfPresentationSamples[index];
                var debugDelta =
                    sample.Presenter.AppliedPresentationVersion - sample.Version;
                var activeAfter = sample.Presenter.HasActiveTransition;
                if (debugDelta > 0)
                {
                    debugWpfPresentationVersionDelta += debugDelta;
                    debugWpfSampleChanged++;
                }
                else if (sample.Presenter.NativeBatchRetryPending)
                {
                    debugWpfApplyFailed++;
                }
                else if (sample.ActiveBefore && !activeAfter)
                {
                    debugWpfCompleteEqual++;
                }
                else if (sample.ActiveBefore || activeAfter)
                {
                    debugWpfSampleEqual++;
                }
                else
                {
                    debugWpfSettledEqual++;
                }
            }
            var debugWpfPresentationChanged =
                debugWpfPresentationVersionDelta > 0;
            var debugActiveTransitionAfter = HasActiveTransitionPresenter();
            var debugTransitionFingerprintAfter =
                GetDebugActiveTransitionFingerprint();
            var debugWpfTransitionFingerprint =
                debugTransitionFingerprintAfter != 0
                    ? debugTransitionFingerprintAfter
                    : debugTransitionFingerprintBefore;
            var debugWpfPresentationGapMilliseconds = -1.0;
            if (debugWpfPresentationChanged)
            {
                debugWpfPresentationGapMilliseconds =
                    _lastWpfPresentationChangeTimestamp == 0 ||
                    _lastWpfTransitionFingerprint == 0 ||
                    debugWpfTransitionFingerprint == 0 ||
                    _lastWpfTransitionFingerprint != debugWpfTransitionFingerprint
                        ? 0
                        : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            _lastWpfPresentationChangeTimestamp,
                            callbackStartedAt);
                _lastWpfPresentationChangeTimestamp = callbackStartedAt;
                _lastWpfTransitionFingerprint = debugWpfTransitionFingerprint;
            }
            if (!debugActiveTransitionAfter)
            {
                // Idle time between independent interactions is not a dropped WPF frame.
                _lastWpfPresentationChangeTimestamp = 0;
                _lastWpfTransitionFingerprint = 0;
            }

            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.frame sequence={frameSequence} source={source} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(callbackStartedAt):F3} " +
                $"gapMs={frameGapMilliseconds:F3} renderMs={renderingTimeMilliseconds:F3} " +
                $"committedApply={anyCommittedApply} activeTransition={debugActiveTransitionAfter} " +
                $"wpfActiveBefore={debugHadActiveTransitionBefore} " +
                $"wpfChanged={debugWpfPresentationChanged} " +
                $"wpfDelta={debugWpfPresentationVersionDelta} " +
                $"wpfGapMs={debugWpfPresentationGapMilliseconds:F3} " +
                $"wpfTransitionId={debugWpfTransitionFingerprint:X16} " +
                $"wpfSampleChanged={debugWpfSampleChanged} " +
                $"wpfSampleEqual={debugWpfSampleEqual} " +
                $"wpfCompleteEqual={debugWpfCompleteEqual} " +
                $"wpfSettledEqual={debugWpfSettledEqual} " +
                $"wpfApplyFailed={debugWpfApplyFailed} " +
                $"duplicateCallbacks={duplicateRenderingCallbacks} presenters={debugInitialCount} " +
                $"groups={debugGroupCount} renderPending={_pendingRenderReconciles} " +
                $"skippedPending={suppressedPendingCallbacks} " +
                $"skippedExternal={suppressedExternalCallbacks} " +
                $"skippedReentrant={suppressedReentrantCallbacks} " +
                $"skipSpanMs={suppressedSpanMilliseconds:F3}");
            _debugWpfPresentationSamples.Clear();
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
            ClearFrameGroups();
            _isTicking = false;
            StopWhenEmpty();
            if (_presenters.Count > 0 &&
                HasActiveTransitionPresenter())
            {
                ArmTransitionLivenessWatchdog();
            }
            else
            {
                DisarmTransitionLivenessWatchdog();
            }
        }
    }

    private bool HasActiveTransitionPresenter()
    {
        for (var index = 0; index < _presenters.Count; index++)
        {
            if (_presenters[index].HasActiveTransition)
            {
                return true;
            }
        }
        return false;
    }

    private void ArmTransitionLivenessWatchdog()
    {
        if (_presenters.Count == 0 ||
            _dispatcher.HasShutdownStarted ||
            !HasActiveTransitionPresenter())
        {
            DisarmTransitionLivenessWatchdog();
            return;
        }

        var generation = ++_transitionLivenessWatchdogGeneration;
        var nowTimestamp = Stopwatch.GetTimestamp();
        var deadlineTimestamp = nowTimestamp +
            MillisecondsToTimestampTicks(TransitionLivenessWatchdogMilliseconds);
        _transitionLivenessWatchdogDeadlineTimestamp = deadlineTimestamp;
        // Normal frames only update the deadline. A single outstanding one-shot wake covers
        // many frames and reschedules itself only when it discovers more recent progress.
        if (!_transitionLivenessWatchdogScheduled)
        {
            ScheduleTransitionLivenessWatchdog(
                generation,
                deadlineTimestamp - nowTimestamp);
        }
    }

    private void ScheduleTransitionLivenessWatchdog(
        long expectedGeneration,
        long remainingTimestampTicks)
    {
        if (expectedGeneration != _transitionLivenessWatchdogGeneration ||
            _transitionLivenessWatchdogDeadlineTimestamp <= 0 ||
            _presenters.Count == 0 ||
            _dispatcher.HasShutdownStarted)
        {
            return;
        }

        var delayMilliseconds = Math.Max(
            1.0,
            TimestampTicksToMilliseconds(
                Math.Max(1, remainingTimestampTicks)));
        _transitionLivenessWatchdog.Change(
            TimeSpan.FromMilliseconds(delayMilliseconds),
            Timeout.InfiniteTimeSpan);
        _transitionLivenessWatchdogScheduled = true;
    }

    private void QueueExpiredTransitionLivenessRescue(string trigger)
    {
        if (_transitionLivenessWatchdogDeadlineTimestamp <= 0 ||
            _presenters.Count == 0 ||
            _dispatcher.HasShutdownStarted ||
            !HasActiveTransitionPresenter())
        {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        if (nowTimestamp < _transitionLivenessWatchdogDeadlineTimestamp)
        {
            return;
        }
        if (_transitionLivenessRescueOperation != null &&
            _transitionLivenessRescueOperation.Status ==
                DispatcherOperationStatus.Pending)
        {
            return;
        }

        var generation = _transitionLivenessWatchdogGeneration;
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"scheduler.watchdog phase=queued trigger={trigger} generation={generation} " +
            $"overdueMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_transitionLivenessWatchdogDeadlineTimestamp, nowTimestamp):F3}");
#endif
        _transitionLivenessRescueOperation = _dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            (Action)(() =>
            {
                _transitionLivenessRescueOperation = null;
                TryRunTransitionLivenessRescue(trigger, generation);
            }));
    }

    private void TryRunTransitionLivenessRescue(
        string trigger,
        long expectedGeneration)
    {
        if (expectedGeneration != _transitionLivenessWatchdogGeneration ||
            _transitionLivenessWatchdogDeadlineTimestamp <= 0 ||
            _presenters.Count == 0 ||
            _dispatcher.HasShutdownStarted ||
            !HasActiveTransitionPresenter())
        {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        if (nowTimestamp < _transitionLivenessWatchdogDeadlineTimestamp)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.watchdog phase=early trigger={trigger} generation={expectedGeneration} " +
                $"remainingMs={TimestampTicksToMilliseconds(_transitionLivenessWatchdogDeadlineTimestamp - nowTimestamp):F3}");
#endif
            ScheduleTransitionLivenessWatchdog(
                expectedGeneration,
                _transitionLivenessWatchdogDeadlineTimestamp - nowTimestamp);
            return;
        }

        var blockedByPendingReconcile = _pendingRenderReconciles > 0;
        var blockedByExternalNativeBatch =
            !blockedByPendingReconcile && HasExternallyOwnedNativeBatchApply();
        if (_isTicking ||
            blockedByPendingReconcile ||
            blockedByExternalNativeBatch)
        {
#if DEBUG
            var reason = _isTicking
                ? "reentrant"
                : blockedByPendingReconcile
                    ? "pending-reconcile"
                    : "external-native-batch";
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.watchdog phase=deferred trigger={trigger} reason={reason} " +
                $"generation={expectedGeneration} presenters={_presenters.Count} " +
                $"renderPending={_pendingRenderReconciles} " +
                $"overdueMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_transitionLivenessWatchdogDeadlineTimestamp, nowTimestamp):F3}");
#endif
            if (!blockedByPendingReconcile)
            {
                // Never poll a native/dispatcher transaction at 1 ms. A genuine Rendering
                // callback may resume immediately; absent one, retain only the slow safety wake.
                ScheduleTransitionLivenessWatchdog(
                    expectedGeneration,
                    MillisecondsToTimestampTicks(TransitionLivenessWatchdogMilliseconds));
            }
            return;
        }

#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"scheduler.watchdog phase=run trigger={trigger} generation={expectedGeneration} " +
            $"overdueMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_transitionLivenessWatchdogDeadlineTimestamp, nowTimestamp):F3}");
#endif
        CancelTransitionLivenessWatchdogSchedule();
        AdvanceSharedFrame(renderingTime: null, source: "watchdog");
    }

    private void CancelTransitionLivenessWatchdogSchedule()
    {
        if (_transitionLivenessWatchdogScheduled)
        {
            _transitionLivenessWatchdog.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _transitionLivenessWatchdogScheduled = false;
        }
        var rescueOperation = _transitionLivenessRescueOperation;
        _transitionLivenessRescueOperation = null;
        if (rescueOperation != null &&
            rescueOperation.Status == DispatcherOperationStatus.Pending)
        {
            rescueOperation.Abort();
        }
    }

    private void DisarmTransitionLivenessWatchdog()
    {
        if (_transitionLivenessWatchdogDeadlineTimestamp == 0 &&
            !_transitionLivenessWatchdogScheduled &&
            _transitionLivenessRescueOperation == null)
        {
            return;
        }
        CancelTransitionLivenessWatchdogSchedule();
        _transitionLivenessWatchdogDeadlineTimestamp = 0;
        _transitionLivenessWatchdogGeneration++;
    }

    private static long MillisecondsToTimestampTicks(double milliseconds) =>
        Math.Max(
            1,
            (long)Math.Ceiling(
                milliseconds * Stopwatch.Frequency / 1000.0));

    private static double TimestampTicksToMilliseconds(long timestampTicks) =>
        timestampTicks * 1000.0 / Stopwatch.Frequency;

    private int BuildFrameGroups(int initialCount)
    {
        _frameGroupIndices.Clear();
        var groupCount = 0;
        for (var index = 0; index < initialCount; index++)
        {
            var presenter = _presenters[index];
            var key = presenter.NativeBatchGroup;
            if (!_frameGroupIndices.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groupCount++;
                _frameGroupIndices[key] = groupIndex;
                if (groupIndex == _frameGroups.Count)
                {
                    _frameGroups.Add(new List<EdgeCapsulePresenter>());
                }
            }
            _frameGroups[groupIndex].Add(presenter);
        }
        return groupCount;
    }

    private void ClearFrameGroups()
    {
        _frameGroupIndices.Clear();
        for (var index = 0; index < _frameGroups.Count; index++)
        {
            _frameGroups[index].Clear();
        }
    }

#if DEBUG
    private void TraceRenderingCallback(
        long sequence,
        double rawGapMilliseconds,
        TimeSpan? renderingTime,
        string outcome,
        string reason)
    {
        var renderingTimeMilliseconds = renderingTime?.TotalMilliseconds ?? -1;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"scheduler.rendering sequence={sequence} " +
            $"rawGapMs={rawGapMilliseconds:F3} " +
            $"renderMs={renderingTimeMilliseconds:F3} " +
            $"outcome={outcome} reason={reason} " +
            $"activeTransition={HasActiveTransitionPresenter()} " +
            $"renderPending={_pendingRenderReconciles}");
    }

    private ulong GetDebugActiveTransitionFingerprint()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        var activeCount = 0;
        unchecked
        {
            for (var index = 0; index < _presenters.Count; index++)
            {
                var presenter = _presenters[index];
                if (!presenter.HasActiveTransition)
                {
                    continue;
                }

                activeCount++;
                var frame = presenter.AppliedPresentation;
                hash ^= (uint)RuntimeHelpers.GetHashCode(presenter);
                hash *= prime;
                hash ^= (uint)frame.Surface;
                hash *= prime;
                hash ^= (uint)frame.Edge;
                hash *= prime;
                hash ^= (uint)frame.HostBounds.GetHashCode();
                hash *= prime;
                hash ^= (uint)frame.WallDeviceX;
                hash *= prime;
            }

            if (activeCount == 0)
            {
                return 0;
            }

            hash ^= (uint)activeCount;
            hash *= prime;
        }
        return hash == 0 ? 1UL : hash;
    }

    private void RecordSuppressedRenderingCallback(ref int counter)
    {
        if (_suppressedRenderingStartedAtTimestamp == 0)
        {
            _suppressedRenderingStartedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
        }
        if (counter < int.MaxValue)
        {
            counter++;
        }
    }
#endif

    private bool AdvanceNativeBatchGroup(
        IReadOnlyList<EdgeCapsulePresenter> presenters,
        DeviceScreenPoint? pointer,
        long frameTimestamp)
    {
        if (presenters.Count == 0)
        {
            return false;
        }

        long nativeCommitVersionBefore = 0;
        for (var index = 0; index < presenters.Count; index++)
        {
            nativeCommitVersionBefore += presenters[index].NativeBatchCommitVersion;
        }

        _postCommitCallbacks.Clear();
        _acceptingPostCommitCallbacks = true;
        var transactionGroupId =
            presenters[0].NativeBatchTransactionGroupId;
        // A positive transaction id means several physical queues are still completing one
        // controller-owned visual transaction and need the existing atomic HDWP commit. Ordinary
        // proxy-backed preview animation has no HWND frame work here: real hosts already sit at the
        // endpoint and DirectComposition advances its visual tree. Fallback paths may still issue
        // direct X/Y changes;
        // sending those through EndDeferWindowPos repeatedly blocks the UI thread for 10-20+ ms on
        // affected systems. Dispatcher processing stays disabled for the whole group, so no input
        // or app callback can observe an interleaved logical frame.
        var useNativeBoundsBatch = transactionGroupId > 0;
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
        var boundsRequested = 0;
        var boundsPending = 0;
        var boundsUnchanged = 0;
        var boundsMoveChanges = 0;
        var boundsSizeChanges = 0;
        var nativeMode = useNativeBoundsBatch ? "batch" : "direct";
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
                WindowNative.WindowDeviceBoundsBatch? nativeBoundsBatch = null;
                try
                {
                    if (useNativeBoundsBatch)
                    {
                        nativeBoundsBatch = WindowNative.BeginWindowDeviceBoundsBatch(
                            presenters.Count);
                    }

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
                    var nativeCommitStartedAt =
                        EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
                    nativeBatchCommitted = nativeBoundsBatch?.Commit() ?? true;
#if DEBUG
                    nativeCommitMilliseconds +=
                        EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                            nativeCommitStartedAt);
                    if (nativeBoundsBatch != null)
                    {
                        boundsRequested = nativeBoundsBatch.RequestedWindowCount;
                        boundsPending = nativeBoundsBatch.PendingWindowCount;
                        boundsUnchanged = nativeBoundsBatch.UnchangedWindowCount;
                        boundsMoveChanges = nativeBoundsBatch.MoveChangeCount;
                        boundsSizeChanges = nativeBoundsBatch.SizeChangeCount;
                    }
#endif
                }
                finally
                {
                    nativeBoundsBatch?.Dispose();
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
                $"boundsRequested={boundsRequested} boundsPending={boundsPending} " +
                $"boundsUnchanged={boundsUnchanged} moveChanges={boundsMoveChanges} " +
                $"sizeChanges={boundsSizeChanges} nativeMode={nativeMode} " +
                $"slowest={slowestPresenter}:{slowestPresenterMilliseconds:F3} " +
                $"transaction={transactionGroupId}");
#endif
            _acceptingPostCommitCallbacks = false;
            _postCommitCallbacks.Clear();
        }

        long nativeCommitVersionAfter = 0;
        for (var index = 0; index < presenters.Count; index++)
        {
            nativeCommitVersionAfter += presenters[index].NativeBatchCommitVersion;
        }
        return nativeCommitVersionAfter != nativeCommitVersionBefore;
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
            DisarmTransitionLivenessWatchdog();
            CompositionTarget.Rendering -= OnRendering;
            _renderingSubscribed = false;
            _lastRenderingTime = null;
#if DEBUG
            _lastRawRenderingCallbackTimestamp = 0;
            _lastRenderingTimestamp = 0;
            _lastWpfPresentationChangeTimestamp = 0;
            _lastWpfTransitionFingerprint = 0;
            _debugWpfPresentationSamples.Clear();
            _suppressedDuplicateRenderingCallbacks = 0;
            _suppressedPendingReconcileRenderingCallbacks = 0;
            _suppressedExternalNativeBatchRenderingCallbacks = 0;
            _suppressedReentrantRenderingCallbacks = 0;
            _suppressedRenderingStartedAtTimestamp = 0;
#endif
            ClearFrameGroups();
        }
    }
}
