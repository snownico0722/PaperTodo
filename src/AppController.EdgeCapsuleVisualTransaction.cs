using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record EdgeCapsuleVisualTransactionEntry(
        PaperWindow Window,
        string QueueKey,
        EdgeCapsuleMotion Motion,
        bool RefreshLayout,
        IDisposable? ReconcileDeferral = null);

    private readonly Dictionary<
        PaperWindow,
        EdgeCapsuleVisualTransactionEntry>
        _edgeCapsuleVisualTransactionEntries = new();
    private DispatcherOperation?
        _edgeCapsuleVisualTransactionCommitOperation;
    private readonly HashSet<string>
        _edgeCapsuleVisualTransactionQueueKeys =
            new(StringComparer.Ordinal);
    private long _edgeCapsuleNativeTransactionGroupGeneration;

    internal void BeginEdgeCapsuleVisualTransaction(
        PaperWindow initiator)
    {
        if (IsExiting)
        {
            return;
        }

        var queueKey =
            QueueKey(initiator.EdgeCapsulePreviewPaper);
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Pending })
        {
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            return;
        }
        if (_edgeCapsuleVisualTransactionCommitOperation is
            { Status: DispatcherOperationStatus.Executing })
        {
            _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
            _edgeCapsuleVisualTransactionCommitOperation =
                initiator.Dispatcher.BeginInvoke(
                    (Action)CommitEdgeCapsuleVisualTransaction,
                    DispatcherPriority.Send);
            return;
        }

        _edgeCapsuleVisualTransactionQueueKeys.Clear();
        _edgeCapsuleVisualTransactionQueueKeys.Add(queueKey);
        _edgeCapsuleVisualTransactionCommitOperation =
            initiator.Dispatcher.BeginInvoke(
                (Action)CommitEdgeCapsuleVisualTransaction,
                DispatcherPriority.Send);
    }

    internal bool TryStageEdgeCapsuleVisualTransaction(
        PaperWindow window,
        EdgeCapsuleMotion motion,
        bool refreshLayout)
    {
        if (IsExiting ||
            _edgeCapsuleVisualTransactionCommitOperation is not
                { Status: DispatcherOperationStatus.Pending })
        {
            return false;
        }

        if (_edgeCapsuleVisualTransactionEntries.TryGetValue(
                window,
                out var existing))
        {
            _edgeCapsuleVisualTransactionEntries[window] =
                existing with
                {
                    Motion =
                        MergeEdgeCapsuleVisualTransactionMotion(
                            existing.Motion,
                            motion),
                    RefreshLayout =
                        existing.RefreshLayout || refreshLayout
                };
        }
        else
        {
            _edgeCapsuleVisualTransactionEntries[window] =
                new EdgeCapsuleVisualTransactionEntry(
                    window,
                    QueueKey(window.EdgeCapsulePreviewPaper),
                    motion,
                    refreshLayout,
                    window.DeferEdgeCapsuleVisualTransactionReconcile());
        }
        return true;
    }

    private static EdgeCapsuleMotion
        MergeEdgeCapsuleVisualTransactionMotion(
            EdgeCapsuleMotion existing,
            EdgeCapsuleMotion incoming)
    {
        if (incoming.Kind == EdgeCapsuleMotionKind.Snap)
        {
            return incoming;
        }
        if (existing.Kind == EdgeCapsuleMotionKind.Snap)
        {
            return existing;
        }
        return incoming.Kind == EdgeCapsuleMotionKind.Animate
            ? incoming
            : existing;
    }

    private void CommitEdgeCapsuleVisualTransaction()
    {
#if DEBUG
        var commitStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
        var operation =
            _edgeCapsuleVisualTransactionCommitOperation;
        var transactionQueueKeys =
            _edgeCapsuleVisualTransactionQueueKeys
                .ToHashSet(StringComparer.Ordinal);
        var entries =
            _edgeCapsuleVisualTransactionEntries.Values.ToArray();
        _edgeCapsuleVisualTransactionEntries.Clear();
        try
        {
            if (entries.Length == 0 || IsExiting)
            {
                return;
            }

            TraceEdgeCapsulePreview(
                $"visual transaction commit count={entries.Length}");

            var transactionTimestamp = Stopwatch.GetTimestamp();
            var transactionEntries = entries
                .Where(entry =>
                    transactionQueueKeys.Contains(entry.QueueKey))
                .ToArray();
            if (EdgeCapsuleNativeTransactionPolicy
                    .RequiresCrossQueueGroup(
                        transactionEntries
                            .Where(entry => !entry.Window.IsClosed)
                            .Select(entry => entry.QueueKey)))
            {
                var transactionGroupId =
                    NextEdgeCapsuleNativeTransactionGroupId();
                foreach (var entry in transactionEntries)
                {
                    if (!entry.Window.IsClosed)
                    {
                        entry.Window
                            .JoinEdgeCapsuleNativeTransactionGroup(
                                transactionGroupId);
                    }
                }
            }

            CommitEdgeCapsuleVisualTransactionGroup(
                transactionEntries,
                transactionQueueKeys,
                transactionTimestamp);

            foreach (var queueGroup in entries
                         .Where(entry =>
                             !transactionQueueKeys.Contains(
                                 entry.QueueKey))
                         .GroupBy(
                             entry => entry.QueueKey,
                             StringComparer.Ordinal))
            {
                CommitEdgeCapsuleVisualTransactionGroup(
                    queueGroup.ToArray(),
                    EmptyQueueKeySet,
                    transactionTimestamp);
            }
        }
        finally
        {
            foreach (var entry in entries)
            {
                entry.ReconcileDeferral?.Dispose();
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"transaction.commit totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(commitStartedAt):F3} " +
                $"entries={entries.Length} queues={transactionQueueKeys.Count}");
#endif
            if (ReferenceEquals(
                    operation,
                    _edgeCapsuleVisualTransactionCommitOperation))
            {
                _edgeCapsuleVisualTransactionCommitOperation = null;
                _edgeCapsuleVisualTransactionQueueKeys.Clear();
            }
        }
    }

    private static readonly IReadOnlySet<string>
        EmptyQueueKeySet =
            new HashSet<string>(StringComparer.Ordinal);

    private long NextEdgeCapsuleNativeTransactionGroupId()
    {
        unchecked
        {
            _edgeCapsuleNativeTransactionGroupGeneration++;
        }
        if (_edgeCapsuleNativeTransactionGroupGeneration <= 0)
        {
            _edgeCapsuleNativeTransactionGroupGeneration = 1;
        }
        return _edgeCapsuleNativeTransactionGroupGeneration;
    }

    private void CommitEdgeCapsuleVisualTransactionGroup(
        EdgeCapsuleVisualTransactionEntry[] entries,
        IReadOnlySet<string> transactionQueueKeys,
        long transactionTimestamp)
    {
        if (entries.Length == 0)
        {
            return;
        }

        var queueKeys = entries
            .Select(entry => entry.QueueKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        EdgeCapsuleQueueCompositionProxy? predecessor = null;
        var realHostMayHaveChanged = false;

        if (queueKeys.Length == 1)
        {
            _edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKeys[0],
                out predecessor);
            if (predecessor != null)
            {
                if (!predecessor.TryReserveForSuccessor())
                {
                    // A generation that is still starting or handing off already retains the real
                    // sources. Its completion resolves the latest reducer endpoint under cover.
                    return;
                }
                entries = CarryForwardEdgeCapsuleQueueProxyMembers(
                    entries,
                    predecessor);
            }
        }
        else
        {
            // Cross-queue visual ownership is deliberately not merged into one native target. End
            // each existing cover safely, then use the established batched HWND fallback.
            var completed = true;
            foreach (var queueKey in queueKeys)
            {
                if (_edgeCapsuleQueueCompositionProxies
                        .ContainsKey(queueKey))
                {
                    realHostMayHaveChanged = true;
                }
                completed &=
                    CompleteEdgeCapsuleQueueCompositionProxy(
                        queueKey,
                        success: true);
            }
            if (!completed)
            {
                return;
            }
        }

        var proxyPlan =
            TryCreateEdgeCapsuleQueueProxyPlan(
                entries,
                predecessor);
        if (proxyPlan != null)
        {
            var started =
                TryStartEdgeCapsuleQueueCompositionProxy(
                    proxyPlan,
                    entries,
                    predecessor,
                    out var proxyChangedRealHost);
            realHostMayHaveChanged |= proxyChangedRealHost;
            if (started)
            {
                // The proxy startup callback already committed every logical endpoint and began
                // the WPF morph from the same QPC timestamp as the DComp translation.
                return;
            }

            // A failed published generation may retain its cover for endpoint/uncloak retry. Never
            // drive a native fallback through that queue while any compositor owner remains.
            if (_edgeCapsuleQueueCompositionProxies.TryGetValue(
                    proxyPlan.QueueKey,
                    out var retained) &&
                !ReferenceEquals(retained, predecessor))
            {
                return;
            }
        }

        if (predecessor != null &&
            _edgeCapsuleQueueCompositionProxies.TryGetValue(
                predecessor.QueueKey,
                out var stillCurrent) &&
            ReferenceEquals(stillCurrent, predecessor))
        {
            predecessor.CompleteAfterFailedSuccessor(
                success: true);
            return;
        }

        if (predecessor != null)
        {
            // If predecessor finished while successor admission failed, its final handoff already
            // applied the latest reducer endpoint. Native fallback must snap from that authority.
            realHostMayHaveChanged = true;
        }

        if (realHostMayHaveChanged)
        {
            entries = entries
                .Select(entry => entry with
                {
                    Motion = EdgeCapsuleMotion.Snap(
                        entry.Motion.Reason)
                })
                .ToArray();
        }

        CommitEdgeCapsuleVisualTransactionNativeFallback(
            entries,
            transactionQueueKeys,
            transactionTimestamp);
    }

    private static EdgeCapsuleVisualTransactionEntry[]
        CarryForwardEdgeCapsuleQueueProxyMembers(
            EdgeCapsuleVisualTransactionEntry[] entries,
            EdgeCapsuleQueueCompositionProxy predecessor)
    {
        var durationMilliseconds = entries
            .Where(entry =>
                entry.Motion.Kind == EdgeCapsuleMotionKind.Animate)
            .Select(entry => entry.Motion.DurationMilliseconds)
            .DefaultIfEmpty(EdgeCapsuleLayout.SlotMoveMilliseconds)
            .Max();
        var byWindow = entries.ToDictionary(
            entry => entry.Window);

        foreach (var member in predecessor.Members)
        {
            if (member.Window.IsClosed)
            {
                continue;
            }
            if (byWindow.TryGetValue(
                    member.Window,
                    out var existing))
            {
                if (existing.Motion.Kind !=
                    EdgeCapsuleMotionKind.Animate)
                {
                    byWindow[member.Window] = existing with
                    {
                        Motion = EdgeCapsuleMotion.Animate(
                            EdgeCapsuleTransitionReason.Placement,
                            durationMilliseconds)
                    };
                }
                continue;
            }

            byWindow[member.Window] =
                new EdgeCapsuleVisualTransactionEntry(
                    member.Window,
                    predecessor.QueueKey,
                    EdgeCapsuleMotion.Animate(
                        EdgeCapsuleTransitionReason.Placement,
                        durationMilliseconds),
                    RefreshLayout: false);
        }
        return byWindow.Values.ToArray();
    }

    private void CommitEdgeCapsuleVisualTransactionNativeFallback(
        EdgeCapsuleVisualTransactionEntry[] entries,
        IReadOnlySet<string> transactionQueueKeys,
        long transactionTimestamp)
    {
        var snapQueueKeys = entries
            .Where(entry =>
                !entry.Window.IsClosed &&
                transactionQueueKeys.Contains(entry.QueueKey) &&
                entry.Motion.Kind == EdgeCapsuleMotionKind.Snap)
            .Select(entry => entry.QueueKey)
            .ToHashSet(StringComparer.Ordinal);
        var nativeBatchCommitted = true;
        var logicalBatchDeferred = false;
        var logicalBatchFailed = false;
        bool transactionCommitted;
        bool transactionDeferred;

        using (entries[0].Window.Dispatcher.DisableProcessing())
        {
            using var nativeBoundsBatch =
                WindowNative.BeginWindowDeviceBoundsBatch(
                    entries.Length);
            foreach (var entry in entries)
            {
                if (entry.Window.IsClosed)
                {
                    continue;
                }

                var belongsToTransactionQueue =
                    transactionQueueKeys.Contains(entry.QueueKey);
                var motion = entry.Motion;
                if (belongsToTransactionQueue &&
                    snapQueueKeys.Contains(entry.QueueKey) &&
                    motion.Kind != EdgeCapsuleMotionKind.Snap)
                {
                    motion = EdgeCapsuleMotion.Snap(
                        motion.Reason);
                }
                else if (!belongsToTransactionQueue &&
                    motion.Kind == EdgeCapsuleMotionKind.Snap)
                {
                    motion = EdgeCapsuleMotion.Preserve(
                        motion.Reason);
                }

                if (motion.Kind == EdgeCapsuleMotionKind.Animate)
                {
                    var fallbackCandidate = entry.Window
                        .CaptureEdgeCapsuleQueueProxyCandidate(
                            entry.QueueKey,
                            motion);
                    if (fallbackCandidate.HasValue &&
                        EdgeCapsuleQueueProxyPolicy.RequiresTranslation(
                            fallbackCandidate.Value.Start,
                            fallbackCandidate.Value.Target))
                    {
                        motion = EdgeCapsuleMotion.Snap(motion.Reason);
                    }
                }

                var status =
                    entry.Window.CommitEdgeCapsuleVisualTransaction(
                        motion,
                        entry.RefreshLayout,
                        transactionTimestamp,
                        rebaseActiveTransition:
                            belongsToTransactionQueue);
                if (status ==
                    EdgeCapsuleNativeBatchApplyStatus.Deferred)
                {
                    logicalBatchDeferred = true;
                }
                else if (status ==
                    EdgeCapsuleNativeBatchApplyStatus.Failed)
                {
                    logicalBatchFailed = true;
                }
            }

            nativeBatchCommitted = nativeBoundsBatch.Commit();
            transactionDeferred =
                nativeBatchCommitted &&
                logicalBatchDeferred &&
                !logicalBatchFailed;
            transactionCommitted =
                nativeBatchCommitted &&
                !logicalBatchDeferred &&
                !logicalBatchFailed;

            foreach (var entry in entries)
            {
                entry.Window
                    .CompleteEdgeCapsuleVisualTransactionApply(
                        transactionCommitted,
                        transactionDeferred,
                        transactionTimestamp);
            }
        }

        if (!transactionCommitted)
        {
            return;
        }
        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window
                    .PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
    }
}
