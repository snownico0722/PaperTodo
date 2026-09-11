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
    private readonly List<List<EdgeCapsulePresenter>> _frameGroups = new();
    private readonly Dictionary<EdgeCapsuleNativeBatchGroup, int> _frameGroupIndices = new();
    private bool _renderingSubscribed;
    private bool _isTicking;
    private bool _acceptingPostCommitCallbacks;
    private readonly Dictionary<EdgeCapsulePresenter, int> _pendingReconcileOwners = new();
    private int _pendingRenderReconciles;
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
    private int _suppressedExternalNativeBatchRenderingCallbacks;
    private int _suppressedReentrantRenderingCallbacks;
    private long _suppressedRenderingStartedAtTimestamp;
#endif

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(
            dispatcher,
            static key => new EdgeCapsuleFrameScheduler(key));

    public void RegisterRenderReconcile(EdgeCapsulePresenter owner)
    {
        _dispatcher.VerifyAccess();
#if DEBUG
        if (_pendingRenderReconciles == 0)
        {
            _pendingRenderReconcileStartedAtTimestamp =
                EdgeCapsulePerformanceDiagnostics.Timestamp();
        }
#endif
        _pendingReconcileOwners.TryGetValue(owner, out var count);
        _pendingReconcileOwners[owner] = count + 1;
        _pendingRenderReconciles++;
        ReconcileReadinessChanged();
    }

    public void CompleteRenderReconcile(EdgeCapsulePresenter owner)
    {
        _dispatcher.VerifyAccess();
        if (!_pendingReconcileOwners.TryGetValue(owner, out var count))
        {
            return;
        }
        if (count == 1) _pendingReconcileOwners.Remove(owner);
        else _pendingReconcileOwners[owner] = count - 1;
        _pendingRenderReconciles--;
#if DEBUG
        if (_pendingRenderReconciles == 0)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"scheduler.pending phase=drained spanMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_pendingRenderReconcileStartedAtTimestamp):F3}");
            _pendingRenderReconcileStartedAtTimestamp = 0;
        }
#endif
        // The owner has finished/aborted its callback or released a visual transaction deferral.
        // Reattach to WPF's frame source
        // when a queue becomes ready; never synthesize a frame or poll for a missing callback.
        ReconcileReadinessChanged();
    }

    internal void ReconcileReadinessChanged()
    {
        _dispatcher.VerifyAccess();
        if (!_isTicking) UpdateRenderingSubscription();
    }

    private bool CanAdvanceQueue(EdgeCapsuleNativeBatchGroup group)
    {
        foreach (var owner in _pendingReconcileOwners.Keys)
        {
            if (owner.NativeBatchGroup == group) return false;
        }
        return true;
    }

    private void UpdateRenderingSubscription()
    {
        var shouldSubscribe = !_dispatcher.HasShutdownStarted &&
            !_dispatcher.HasShutdownFinished &&
            _presenters.Any(p => CanAdvanceQueue(p.NativeBatchGroup));
        if (shouldSubscribe == _renderingSubscribed) return;
        if (shouldSubscribe)
        {
            // WPF's Rendering add accessor requests a render. First activation and the release
            // of the last owner barrier therefore have explicit event-driven restart boundaries.
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
        _renderingSubscribed = shouldSubscribe;
    }

    public void Activate(EdgeCapsulePresenter presenter)
    {
        _dispatcher.VerifyAccess();
        if (!_presenters.Contains(presenter))
        {
            _presenters.Add(presenter);
        }
        UpdateRenderingSubscription();
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
        UpdateRenderingSubscription();
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

        AdvanceSharedFrame(renderingTime, source: "render");
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
        var blockedQueueCount = 0;
        var suppressedExternalCallbacks = _suppressedExternalNativeBatchRenderingCallbacks;
        var suppressedReentrantCallbacks = _suppressedReentrantRenderingCallbacks;
        var suppressedSpanMilliseconds = _suppressedRenderingStartedAtTimestamp == 0
            ? 0
            : EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(
                _suppressedRenderingStartedAtTimestamp,
                callbackStartedAt);
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
                var group = _frameGroups[groupIndex];
                if (!CanAdvanceQueue(group[0].NativeBatchGroup))
                {
#if DEBUG
                    blockedQueueCount++;
#endif
                    continue;
                }
                anyCommittedApply |= AdvanceNativeBatchGroup(
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
                $"blockedQueues={blockedQueueCount} " +
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
            UpdateRenderingSubscription();
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
        if (_presenters.Count == 0)
        {
            UpdateRenderingSubscription();
            _lastRenderingTime = null;
#if DEBUG
            _lastRawRenderingCallbackTimestamp = 0;
            _lastRenderingTimestamp = 0;
            _lastWpfPresentationChangeTimestamp = 0;
            _lastWpfTransitionFingerprint = 0;
            _debugWpfPresentationSamples.Clear();
            _suppressedDuplicateRenderingCallbacks = 0;
            _suppressedExternalNativeBatchRenderingCallbacks = 0;
            _suppressedReentrantRenderingCallbacks = 0;
            _suppressedRenderingStartedAtTimestamp = 0;
#endif
            ClearFrameGroups();
        }
    }
}
