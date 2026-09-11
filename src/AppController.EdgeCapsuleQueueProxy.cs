using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly Dictionary<
        string,
        EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxies =
            new(StringComparer.Ordinal);
    private readonly Dictionary<
        PaperWindow,
        EdgeCapsuleQueueCompositionProxy>
        _edgeCapsuleQueueCompositionProxyByWindow = new();

    private EdgeCapsuleQueueProxyPlan?
        TryCreateEdgeCapsuleQueueProxyPlan(
            EdgeCapsuleVisualTransactionEntry[] entries,
            EdgeCapsuleQueueCompositionProxy? predecessor = null)
    {
        if (entries.Length == 0 ||
            entries.Select(entry => entry.QueueKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            return null;
        }

        var queueKey = entries[0].QueueKey;

        var candidates =
            new List<EdgeCapsuleQueueProxyCandidate>(
                entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Window.IsClosed)
            {
                return null;
            }

            EdgeCapsulePresentationFrame? start = null;
            EdgeCapsulePresentationFrame? source = null;
            var retained = false;
            if (predecessor != null &&
                predecessor.TryGetPresentation(
                    entry.Window,
                    out var sampled) &&
                predecessor.TryGetSourcePresentation(
                    entry.Window,
                    out var nativeSource))
            {
                start = sampled;
                source = nativeSource;
                retained = predecessor.RetainsSource(
                    entry.Window);
            }

            var candidate = entry.Window
                .CaptureEdgeCapsuleQueueProxyCandidate(
                    entry.QueueKey,
                    entry.Motion,
                    start,
                    source,
                    retained);
            if (!candidate.HasValue)
            {
                return null;
            }
            candidates.Add(candidate.Value);
        }

        var plan = EdgeCapsuleQueueProxyPolicy.TryCreate(
            queueKey,
            candidates);
        if (plan == null)
        {
            return null;
        }

        // Size the persistent queue output for every member's possible native preview, not only the
        // current owner. This keeps rapid A -> B successor roots on the same HWND even when B is a
        // wider/taller plugin, while still limiting the transparent output to queue-owned geometry.
        var capacityEnvelope = plan.Envelope;
        var maximumDownwardShift = 0;
        var workAreaBottom = plan.Envelope.Bottom;
        foreach (var capacityWindow in _windows.Values.Where(window =>
                     !window.IsClosed &&
                     string.Equals(
                         QueueKey(window.EdgeCapsulePreviewPaper),
                         queueKey,
                         StringComparison.Ordinal)))
        {
            var capacity = capacityWindow
                .CaptureEdgeCapsuleQueueProxyCapacity();
            if (capacity.PreviewBounds.IsEmpty)
            {
                continue;
            }
            capacityEnvelope = EdgeCapsuleQueueProxyGeometry.Union(
                capacityEnvelope,
                capacity.PreviewBounds);
            maximumDownwardShift = Math.Max(
                maximumDownwardShift,
                capacity.MaximumDownwardShiftDevice);
            workAreaBottom = Math.Max(
                workAreaBottom,
                capacity.WorkAreaBottomDevice);
        }
        capacityEnvelope =
            EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(
                capacityEnvelope,
                maximumDownwardShift,
                workAreaBottom);
        return plan with
        {
            Envelope = capacityEnvelope
        };
    }

    private static bool
        CommitEdgeCapsuleQueueProxyLogicalEndpoints(
            EdgeCapsuleVisualTransactionEntry[] entries,
            EdgeCapsuleQueueProxyPlan plan,
            long transactionTimestamp)
    {
        var proxiedPlans = plan.Members.ToDictionary(
            member => member.PaperId,
            StringComparer.Ordinal);
        var committed = true;
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

                var candidate = entry.Window
                    .CaptureEdgeCapsuleQueueProxyCandidate(
                        entry.QueueKey,
                        entry.Motion);
                var paperId =
                    entry.Window.EdgeCapsulePreviewPaperId;
                var proxied = proxiedPlans.TryGetValue(
                    paperId,
                    out var memberPlan);
                if (proxied &&
                    (!candidate.HasValue ||
                     candidate.Value.Target != memberPlan.Target))
                {
                    committed = false;
                    continue;
                }

                var motion = entry.Motion;
                if (motion.Kind == EdgeCapsuleMotionKind.Animate)
                {
                    motion = EdgeCapsuleMotion.Animate(
                        motion.Reason,
                        plan.DurationMilliseconds);
                }

                var requiresTranslation =
                    candidate.HasValue &&
                    EdgeCapsuleQueueProxyPolicy.RequiresTranslation(
                        candidate.Value.Start,
                        candidate.Value.Target);
                if (requiresTranslation && !proxied)
                {
                    // A floating/direct member settles once. It never
                    // re-enters per-frame SetWindowPos while peers move.
                    motion = EdgeCapsuleMotion.Snap(motion.Reason);
                }

                var status = entry.Window
                    .CommitEdgeCapsuleVisualTransaction(
                        motion,
                        entry.RefreshLayout,
                        transactionTimestamp,
                        rebaseActiveTransition: true);
                committed &=
                    status ==
                    EdgeCapsuleNativeBatchApplyStatus.Ready;
            }

            committed &= nativeBoundsBatch.Commit();
            foreach (var entry in entries)
            {
                entry.Window
                    .CompleteEdgeCapsuleVisualTransactionApply(
                        success: committed,
                        deferred: false,
                        transactionTimestamp);
            }
        }

        if (!committed)
        {
            return false;
        }
        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window
                    .PublishEdgeCapsuleVisualTransactionNotifications();
            }
        }
        return true;
    }
    private static bool RebaseEdgeCapsuleQueueProxyAnimationClock(
        EdgeCapsuleVisualTransactionEntry[] entries,
        long transactionTimestamp)
    {
        foreach (var entry in entries)
        {
            if (!entry.Window.IsClosed)
            {
                entry.Window.RebaseEdgeCapsuleQueueProxyAnimationClock(
                    transactionTimestamp);
            }
        }
        return true;
    }

    private bool TryStartEdgeCapsuleQueueCompositionProxy(
        EdgeCapsuleQueueProxyPlan plan,
        EdgeCapsuleVisualTransactionEntry[] entries,
        EdgeCapsuleQueueCompositionProxy? predecessor,
        out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var sessionOrdinal =
            EdgeCapsuleQueueCompositionProxy.ReserveSessionOrdinal();
        var byPaperId = entries
            .GroupBy(
                entry => entry.Window.EdgeCapsulePreviewPaperId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.Ordinal);
        var members =
            new List<EdgeCapsuleQueueCompositionProxyMember>(
                plan.Members.Count);
        foreach (var memberPlan in plan.Members)
        {
            if (!byPaperId.TryGetValue(
                    memberPlan.PaperId,
                    out var entry))
            {
                return false;
            }
            members.Add(
                new EdgeCapsuleQueueCompositionProxyMember(
                    entry.Window,
                    memberPlan,
                    entry.Window.EdgeCapsuleQueueProxySourceHandle));
        }

        var proxy = EdgeCapsuleQueueCompositionProxy.TryCreate(
            sessionOrdinal,
            plan,
            members,
            predecessor,
            endpointCommitRequested: timestamp =>
                CommitEdgeCapsuleQueueProxyLogicalEndpoints(
                    entries,
                    plan,
                    timestamp),
            animationStartRequested: timestamp =>
                RebaseEdgeCapsuleQueueProxyAnimationClock(
                    entries,
                    timestamp),
            interactionRequested: (point, message) =>
                CompleteAndRouteEdgeCapsuleQueueProxyInput(
                    plan.QueueKey,
                    point,
                    message),
            environmentChanged: () =>
                CompleteEdgeCapsuleQueueCompositionProxy(
                    plan.QueueKey,
                    success: false),
            coverReady: successor =>
                PublishEdgeCapsuleQueueCompositionProxy(
                    plan.QueueKey,
                    successor,
                    predecessor),
            coverRollback: successor =>
                RollbackEdgeCapsuleQueueCompositionProxyPublication(
                    plan.QueueKey,
                    successor,
                    predecessor),
            completed: (completedProxy, success) =>
                FinishEdgeCapsuleQueueCompositionProxy(
                    plan.QueueKey,
                    completedProxy,
                    success));
        if (proxy == null)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=fallback mode=live-translation " +
                $"session={sessionOrdinal} queue={plan.QueueKey} " +
                $"reason=create-failed");
#endif
            return false;
        }

        TraceEdgeCapsuleQueueVisibility(proxy, "before-start");
        if (!proxy.TryStart(out realHostMayHaveChanged))
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=fallback mode=live-translation " +
                $"session={sessionOrdinal} queue={plan.QueueKey} " +
                $"reason=startup-failed published={proxy.CoverPublished}");
#endif
            if (proxy.CoverPublished)
            {
                _ = FinishEdgeCapsuleQueueCompositionProxy(
                    plan.QueueKey,
                    proxy,
                    success: false);
                realHostMayHaveChanged = true;
            }
            else
            {
                proxy.AbortStaged();
            }
            return false;
        }
        TraceEdgeCapsuleQueueVisibility(proxy, "started");
        return true;
    }

    [Conditional("DEBUG")]
    private void TraceEdgeCapsuleQueueVisibility(
        EdgeCapsuleQueueCompositionProxy proxy,
        string phase)
    {
#if DEBUG
        // Include stationary peers: the transparent output can overlap windows it does not wrap.
        // These are lifecycle observations, not proof that a desktop frame has been presented.
        try
        {
            var startedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
            var members = proxy.Members.Select(member => member.Window).ToHashSet();
            var context = $"phase={phase} session={proxy.SessionOrdinal}";
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.visibility {context} role=output queue={proxy.QueueKey} " +
                WindowNative.DescribeCompositionVisibility(proxy.OutputHandle));
            foreach (var window in _windows.Values)
            {
                if (!window.IsClosed && string.Equals(
                    QueueKey(window.EdgeCapsulePreviewPaper), proxy.QueueKey,
                    StringComparison.Ordinal))
                {
                    window.TraceEdgeCapsuleCompositionVisibility(
                        $"{context} wrapped={members.Contains(window)}");
                }
            }
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.visibility {context} role=diagnostic " +
                $"elapsedMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
        }
        catch
        {
            // Diagnostics must not interrupt an authority handoff.
        }
#endif
    }

    private bool PublishEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        EdgeCapsuleQueueCompositionProxy successor,
        EdgeCapsuleQueueCompositionProxy? predecessor)
    {
        if (predecessor == null)
        {
            if (_edgeCapsuleQueueCompositionProxies
                    .ContainsKey(queueKey))
            {
                return false;
            }
        }
        else if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(
                     queueKey,
                     out var current) ||
                 !ReferenceEquals(current, predecessor))
        {
            return false;
        }

        if (predecessor != null)
        {
            foreach (var pair in
                     _edgeCapsuleQueueCompositionProxyByWindow
                         .Where(pair =>
                             ReferenceEquals(
                                 pair.Value,
                                 predecessor))
                         .ToArray())
            {
                _edgeCapsuleQueueCompositionProxyByWindow
                    .Remove(pair.Key);
            }
        }

        _edgeCapsuleQueueCompositionProxies[queueKey] = successor;
        foreach (var member in successor.Members)
        {
            _edgeCapsuleQueueCompositionProxyByWindow[
                member.Window] = successor;
        }

#if DEBUG
        if (predecessor != null)
        {
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.successor phase=publish " +
                $"from={predecessor.SessionOrdinal} " +
                $"to={successor.SessionOrdinal} queue={queueKey} " +
                $"members={successor.Members.Count}");
        }
#endif
        return true;
    }

    private void RollbackEdgeCapsuleQueueCompositionProxyPublication(
        string queueKey,
        EdgeCapsuleQueueCompositionProxy successor,
        EdgeCapsuleQueueCompositionProxy? predecessor)
    {
        if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var current) ||
            !ReferenceEquals(current, successor))
        {
            return;
        }

        _edgeCapsuleQueueCompositionProxies.Remove(queueKey);
        foreach (var pair in
                 _edgeCapsuleQueueCompositionProxyByWindow
                     .Where(pair =>
                         ReferenceEquals(pair.Value, successor))
                     .ToArray())
        {
            _edgeCapsuleQueueCompositionProxyByWindow
                .Remove(pair.Key);
        }

        if (predecessor == null)
        {
            return;
        }

        _edgeCapsuleQueueCompositionProxies[queueKey] = predecessor;
        foreach (var member in predecessor.Members)
        {
            _edgeCapsuleQueueCompositionProxyByWindow[
                member.Window] = predecessor;
        }
    }

    private bool FinishEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        EdgeCapsuleQueueCompositionProxy? expected,
        bool success)
    {
        if (expected == null ||
            !_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var current) ||
            !ReferenceEquals(expected, current))
        {
            return true;
        }

        var windows = current.Members
            .Select(member => member.Window)
            .Distinct()
            .ToArray();

        if (current.CoverLost)
        {
            var sourcesReleased =
                current.ReleaseAfterCoverLoss();
            if (!sourcesReleased)
            {
                current.ScheduleCompletionRetry(
                    success: false);
                return false;
            }

            _edgeCapsuleQueueCompositionProxies
                .Remove(queueKey);
            foreach (var window in windows)
            {
                if (_edgeCapsuleQueueCompositionProxyByWindow
                        .TryGetValue(window, out var routed) &&
                    ReferenceEquals(routed, current))
                {
                    _edgeCapsuleQueueCompositionProxyByWindow
                        .Remove(window);
                }
            }
            try
            {
                current.Dispose();
            }
            catch
            {
                current.ForceDisposeForShutdown();
            }

            foreach (var window in windows)
            {
                if (!window.IsClosed)
                {
                    try
                    {
                        window.FlushEdgeCapsuleQueueProxyEndpoint();
                    }
                    catch { }
                }
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=emergency-release session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} members={windows.Length}");
#endif
            return true;
        }

        var endpointsReady = true;
        var endpoints = new List<(
            PaperWindow Window,
            EdgeCapsulePresentationFrame Endpoint)>(windows.Length);
#if DEBUG
        var handoffStartedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        var endpointStartedAt = handoffStartedAt;
#endif
        try
        {
            // A rejected rapid successor must not fall back to one synchronous SetWindowPos per
            // retained source. Keep the old cover authoritative while every Presenter consumes its
            // latest Measure/Pointer/Presentation work first; only then capture and submit all real
            // endpoints through one HDWP transaction. Any final text/pixel correction therefore
            // happens under cover, never after the real HWNDs are revealed.
            using (windows[0].Dispatcher.DisableProcessing())
            {
                using var nativeBoundsBatch =
                    WindowNative.BeginWindowDeviceBoundsBatch(
                        windows.Length);
                foreach (var window in windows)
                {
                    if (!window.IsClosed)
                    {
                        window.FlushEdgeCapsuleQueueProxyEndpoint();
                    }
                }
                foreach (var window in windows)
                {
                    var applied = window
                        .TryApplyLatestEdgeCapsuleQueueProxyEndpoint(
                            out var endpoint);
                    endpointsReady &= applied;
                    endpoints.Add((window, endpoint));
                }
                endpointsReady &= nativeBoundsBatch.Commit();
            }

            if (endpointsReady)
            {
                foreach (var item in endpoints.Where(item =>
                             item.Endpoint.Visible))
                {
                    endpointsReady &= item.Window
                        .PrepareEdgeCapsuleQueueProxyEndpointLayoutForHandoff();
                }
            }

            if (endpointsReady &&
                endpoints.Any(item => item.Endpoint.Visible))
            {
                // Submit every real endpoint in one WPF render turn, but do not cross DWM here.
                // TryReleaseForHandoff publishes these queued surface updates together with the
                // real/proxy authority swap. A separate flush held the final proxy frame for one
                // more presentation and made close look frozen before its last-frame flash.
                windows[0].Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.Render);
            }

            if (endpointsReady)
            {
                foreach (var item in endpoints)
                {
                    endpointsReady &= item.Window
                        .VerifyEdgeCapsuleQueueProxyEndpoint(
                            item.Endpoint);
                }
            }
        }
        catch (Exception ex)
        {
            endpointsReady = false;
            Trace.TraceError(
                "Edge capsule queue proxy handoff failed. Queue={0}; Session={1}; Exception={2}",
                queueKey,
                current.SessionOrdinal,
                ex);
        }

        if (!endpointsReady)
        {
            current.ScheduleCompletionRetry(
                success: false);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.endpoint phase=handoff session={current.SessionOrdinal} " +
                $"cold={current.IsColdSession} queue={queueKey} " +
                $"members={windows.Length} requestedSuccess={success} " +
                $"ready=false retry=true totalMs=" +
                $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(endpointStartedAt):F3}");
#endif
            return false;
        }

        TraceEdgeCapsuleQueueVisibility(current, "before-release");
        if (!current.TryReleaseForHandoff())
        {
            current.ScheduleCompletionRetry(
                success: false);
            return false;
        }

        _edgeCapsuleQueueCompositionProxies.Remove(queueKey);
        foreach (var window in windows)
        {
            if (_edgeCapsuleQueueCompositionProxyByWindow
                    .TryGetValue(window, out var routed) &&
                ReferenceEquals(routed, current))
            {
                _edgeCapsuleQueueCompositionProxyByWindow
                    .Remove(window);
            }
        }
        TraceEdgeCapsuleQueueVisibility(current, "released");
        try
        {
            current.Dispose();
        }
        catch
        {
            current.ForceDisposeForShutdown();
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.session phase=complete session={current.SessionOrdinal} " +
            $"cold={current.IsColdSession} queue={queueKey} " +
            $"members={windows.Length} requestedSuccess={success} " +
            $"endpointsReady={endpointsReady} handoffMs=" +
            $"{EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(handoffStartedAt):F3}");
#endif
        return true;
    }

    private bool CompleteEdgeCapsuleQueueCompositionProxy(
        string queueKey,
        bool success)
    {
        if (_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var proxy))
        {
            proxy.CompleteNow(success);
        }
        return !_edgeCapsuleQueueCompositionProxies
            .ContainsKey(queueKey);
    }

    private void CompleteAndRouteEdgeCapsuleQueueProxyInput(
        string queueKey,
        DeviceScreenPoint point,
        int message)
    {
        if (!_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var proxy))
        {
            return;
        }

        var hasTarget = proxy.TryResolveInputTarget(
            point,
            out var targetHandle,
            out var endpointPoint);
        proxy.CompleteNow(success: true);
        var handoffCompleted =
            !_edgeCapsuleQueueCompositionProxies.TryGetValue(
                queueKey,
                out var remaining) ||
            !ReferenceEquals(remaining, proxy);
        if (hasTarget && handoffCompleted)
        {
            _ = WindowNative.TryPostMouseButtonDown(
                targetHandle,
                message,
                endpointPoint);
        }
    }


    internal bool TryGetEdgeCapsuleQueueProxyPresentation(
        PaperWindow window,
        out EdgeCapsulePresentationFrame frame)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            return proxy.TryGetPresentation(
                window,
                out frame);
        }
        frame = EdgeCapsulePresentationFrame.Hidden;
        return false;
    }

    internal bool TryGetEdgeCapsuleQueueProxyDiagnosticHandle(
        PaperWindow window,
        out IntPtr handle)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            handle = proxy.OutputHandle;
            return handle != IntPtr.Zero;
        }
        handle = IntPtr.Zero;
        return false;
    }

    internal bool IsEdgeCapsuleQueueProxyRetainingSource(
        PaperWindow window) =>
        _edgeCapsuleQueueCompositionProxyByWindow
            .TryGetValue(window, out var proxy) &&
        proxy.RetainsSource(window);

    internal void CompleteEdgeCapsuleQueueCompositionProxyFor(
        PaperWindow window,
        bool success = false)
    {
        if (_edgeCapsuleQueueCompositionProxyByWindow
                .TryGetValue(window, out var proxy))
        {
            proxy.CompleteNow(success);
        }
    }

    private void DisposeEdgeCapsuleQueueCompositionProxies()
    {
        var proxies =
            _edgeCapsuleQueueCompositionProxies.Values
                .Distinct()
                .ToArray();
        _edgeCapsuleQueueCompositionProxies.Clear();
        _edgeCapsuleQueueCompositionProxyByWindow.Clear();
        foreach (var proxy in proxies)
        {
            proxy.ForceDisposeForShutdown();
        }
    }
}
