using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    public bool TryStart(out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        var started = false;
        try
        {
            started = PrepareAndStart();
            realHostMayHaveChanged = _realEndpointMutationStarted;
            return started;
        }
        finally
        {
            FinishStartup(started);
        }
    }

    private void FinishStartup(bool started)
    {
        // Publication has transferred the live sources. Rollback can no longer return to the
        // retired generation; retaining it would keep the entire browsing history alive.
        if (_coverPublished) _predecessor = null;
        _starting = false;
        if (started && _coverPublished && !RefreshNativeInputRegion())
        {
            // A native input allocation failure must restore normal source-window interaction.
            CompleteNow(success: false);
            return;
        }
        if (_completionPendingDuringStart)
        {
            CompleteNow(_pendingStartCompletionSuccess);
        }
        else if (started && (_plan.IsStaticPreacquisition || _plan.IsSettledInputHandoff))
        {
            // The ordinary endpoint verification grants retention. A static acquisition has
            // no animation to wait for, and a reentrant explicit completion always wins above.
            CompleteNow(success: true, allowBrowseRetention: true);
        }
    }

    private bool PrepareAndStart()
    {
#if DEBUG
        using var edgeJournalStage = EdgeDiagnosticObservation.Begin("proxy.prepare", this);
#endif

#if DEBUG
        var startedAt =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        using var coldStartScope =
            EdgeCapsuleColdStartDiagnostics.Enter(
                IsColdSession,
                _plan.QueueKey,
                _sessionOrdinal);
        EdgeCapsuleColdStartDiagnostics.Boundary("prepare-enter");
#endif
        try
        {
            foreach (var member in _members)
            {
                if (!EdgeCapsuleQueueProxyPolicy.CanWrapMovingMemberLive(
                        member.Plan.Source,
                        member.Plan.Target))
                {
                    return false;
                }

                var sourceHost = member.Plan.Source.HostBounds;
                var startHost =
                    EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(
                        member.Plan.Start);
                var targetHost = member.Plan.Target.HostBounds;
                if (startHost.IsEmpty ||
                    sourceHost.Width != startHost.Width ||
                    sourceHost.Height != startHost.Height ||
                    sourceHost.Width != targetHost.Width ||
                    sourceHost.Height != targetHost.Height)
                {
                    return false;
                }

                var reference = _visuals.Count == 0
                    ? null
                    : _visuals[^1].Visual;
                _ = AddVisual(
                    member,
                    member.SourceHandle,
                    sourceHost,
                    startHost,
                    targetHost,
                    reference);
            }

#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("resources-ready");
#endif
            var successorHandles = new HashSet<IntPtr>(_members.Count);
            foreach (var member in _members)
            {
                if (member.SourceHandle != IntPtr.Zero)
                {
                    successorHandles.Add(member.SourceHandle);
                }
            }
            var predecessorHandles =
                _predecessor?.SnapshotCloakedSourceHandles() ??
                new HashSet<IntPtr>();
            var inheritedCount = 0;
            var newHandles = new HashSet<IntPtr>();
            var outgoingCount = 0;
            var cloakChanges =
                new List<WindowNative.WindowCloakChange>(
                    successorHandles.Count + predecessorHandles.Count);
            foreach (var handle in successorHandles)
            {
                if (predecessorHandles.Contains(handle))
                {
                    inheritedCount++;
                    continue;
                }
                newHandles.Add(handle);
                cloakChanges.Add(new WindowNative.WindowCloakChange(
                    handle, Cloaked: true, RollbackCloaked: false));
            }
            foreach (var handle in predecessorHandles)
            {
                if (successorHandles.Contains(handle)) continue;
                outgoingCount++;
                cloakChanges.Add(new WindowNative.WindowCloakChange(
                    handle, Cloaked: false, RollbackCloaked: true));
            }
#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("source-sets-ready");
#endif

            var hostPromoted = false;

            // Cold startup has no compositor authority yet, so publish its exact-start root
            // before any real HWND is cloaked. A successor normally keeps the predecessor root
            // installed until the coordinated cloak/root flush. If it introduces a new source,
            // however, the predecessor cannot cover that source during the potentially multi-ms
            // endpoint callback after DWMWA_CLOAK has already been requested. Publish a temporary
            // union of predecessor live surfaces + new live sources first; duplicate pixels are
            // preferable to an authority hole and no snapshot/clip/scale/effect is involved.
            var coverTimestamp = Stopwatch.GetTimestamp();
            if (_predecessor == null)
            {
                // AddVisual already installed the immutable cold start offsets. Only a
                // successor needs to sample a predecessor that can move during preparation.
                _target.SetRoot(_root).CheckError();
#if DEBUG
                using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                    _device.Commit().CheckError();
                _targetRootInstalled = true;
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("root-committed-static");
#endif

                if (!_window.Show(_outputBounds, _plan.Topmost))
                {
                    return false;
                }
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("output-shown");
#endif
                if (!WindowNative.TryFlushDesktopComposition())
                {
                    return false;
                }
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("output-ready");
                EdgeCapsuleColdStartDiagnostics.Boundary("cover-static-visible");
#endif
            }
            else if (newHandles.Count > 0)
            {
                _successorAdmissionCover =
                    CreateSuccessorAdmissionCover(
                        coverTimestamp,
                        newHandles);
                _target.SetRoot(
                    _successorAdmissionCover.Root).CheckError();
#if DEBUG
                using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                    _device.Commit().CheckError();
                _targetRootInstalled = true;
                if (!WindowNative.TryFlushDesktopComposition())
                {
                    return false;
                }
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary(
                    "successor-union-cover-visible");
#endif
            }
#if DEBUG
            else
            {
                EdgeCapsuleColdStartDiagnostics.Boundary(
                    "predecessor-cover-retained");
            }
#endif

            bool PublishBeforeFlush()
            {
                if (!_host.Promote(this, _predecessor))
                {
                    return false;
                }
                hostPromoted = true;
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("host-promoted");
#endif

                // DWM has queued the cloak changes but has not crossed the batch flush yet. The
                // cold static root, retained predecessor root, or successor union cover remains
                // visible while every real HWND settles once at its logical endpoint.
                var endpointTimestamp = Stopwatch.GetTimestamp();
                _realEndpointMutationStarted = true;
                if (!_endpointCommitRequested(endpointTimestamp))
                {
                    return false;
                }
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("endpoint-ready");
#endif

                // Publish the live cover at its start position. The coordinated DwmFlush below
                // blocks this UI thread; starting autonomous DComp motion here would let peers
                // move while WPF is still unable to produce the first shape frame.
                if (_predecessor != null)
                {
                    RebaseVisualStarts(Stopwatch.GetTimestamp());
                    // Outgoing real HWNDs are being un-cloaked in this same DWM batch. Keep the
                    // predecessor root installed until this callback, then replace the root so
                    // outgoing reveal, incoming cloak and successor publication cross one flush.
                    _target.SetRoot(_root).CheckError();
                    _targetRootInstalled = true;
#if DEBUG
                    using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                        _device.Commit().CheckError();
#if DEBUG
                    EdgeCapsuleColdStartDiagnostics.Boundary("successor-root-staged");
#endif
                }

                // A cold root is already visible at these same offsets. Settling the source
                // HWNDs updates its live content without another DComp root/offset command.
                // The coordinated cloak flush and verification below are still required.
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("start-cover-published");
#endif

                if (!_coverReady(this, _predecessor))
                {
                    return false;
                }
                _controllerPublished = true;
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("controller-published");
#endif
                return true;
            }

            void RollbackBeforeFlush()
            {
                Exception? rollbackFailure = null;
                if (_controllerPublished)
                {
                    try
                    {
                        _coverRollback(this, _predecessor);
                    }
                    catch (Exception ex)
                    {
                        rollbackFailure = ex;
                    }
                    _controllerPublished = false;
                }

                try
                {
                    if (_predecessor == null)
                    {
                        _target.SetRoot(null!).CheckError();
                    }
                    else
                    {
                        _target.SetRoot(
                            _predecessor._root).CheckError();
                    }
#if DEBUG
                    using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                        _device.Commit().CheckError();
                    _targetRootInstalled = false;
                }
                catch (Exception ex)
                {
                    rollbackFailure ??= ex;
                }

                if (hostPromoted &&
                    !_host.RollbackPromotion(this, _predecessor))
                {
                    rollbackFailure ??=
                        new InvalidOperationException(
                            "The queue host owner could not be restored.");
                }

                if (rollbackFailure != null)
                {
                    _coverLost = true;
                    throw rollbackFailure;
                }
            }

#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("before-cloak-batch");
#endif
            // When a successor keeps precisely the same live sources, no HWND authority changes.
            // Root replacement is already an atomic DComp commit on the existing visible output;
            // it must not wait for another scanout before WPF can produce the next shape frame.
            // New/revealed sources still require the full cover/cloak/flush/verify transaction.
            var retainedAuthority = _predecessor != null && cloakChanges.Count == 0;
            WindowNative.WindowCloakBatchResult publication;
            if (_plan.IsSettledInputHandoff)
            {
                // This handoff changes only input ownership at already-settled endpoints.
                // Keep the old live image until the outgoing real HWND has actually been
                // revealed; a DComp root change and a DWM uncloak are separate submissions.
                if (_predecessor == null || newHandles.Count != 0 || outgoingCount != 1 ||
                    cloakChanges.Count != 1 || cloakChanges[0].Cloaked ||
                    !cloakChanges[0].RollbackCloaked ||
                    !_predecessor.CanCreateSettledInputSuccessor(_plan, _members) ||
                    !_endpointCommitRequested(Stopwatch.GetTimestamp())) return false;
                publication = PublishSettledInputCover(cloakChanges,
                    PublishBeforeFlush, RollbackBeforeFlush);
            }
            else
            {
                publication = retainedAuthority
                    ? PublishRetainedCover(PublishBeforeFlush, RollbackBeforeFlush)
                    : WindowNative.TrySetWindowCloakedBatchDetailed(
                        cloakChanges,
                        PublishBeforeFlush,
                        RollbackBeforeFlush);
            }
            if (publication !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                if (publication ==
                    WindowNative.WindowCloakBatchResult.RollbackFailed)
                {
                    _coverLost = true;
                    foreach (var handle in
                             successorHandles.Concat(
                                 predecessorHandles))
                    {
                        _cloakedRealSourceHandles.Add(handle);
                    }
                    _ = ReleaseAfterCoverLoss();
                }
                else
                {
                    // Rollback restored the predecessor root and completed its publication fence.
                    ReleaseSuccessorAdmissionCover();
                }
                return false;
            }
#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("publication-verified");
#endif

            // Either the cloak batch verified the authority swap, or the atomic successor commit
            // preserved the existing cover and its unchanged, already-cloaked source set.
            ReleaseSuccessorAdmissionCover();
            foreach (var handle in successorHandles)
            {
                _cloakedRealSourceHandles.Add(handle);
            }
            if (_predecessor != null)
            {
                _predecessor
                    .CompleteSourceTransferAfterSuccessfulBoundary();
            }
            _coverPublished = true;

            if (!_plan.IsStaticPreacquisition && !_plan.IsSettledInputHandoff)
            {
                // All blocking publication work is finished. Start shape and translation together
                // from this fresh QPC, keeping the same curve and full duration for both. Do not
                // flush again after starting: WPF must be free to produce its first frame. The cover
                // already owns its sources, so a failure here uses the normal published-cover handoff.
                var animationTimestamp = Stopwatch.GetTimestamp();
                if (!_animationStartRequested(animationTimestamp))
                {
                    return false;
                }
                var inputTicket = ConfigureAnimations(animationTimestamp);
#if DEBUG
                using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                    _device.Commit().CheckError();
                // The input owner receives the same absolute QPC only after DComp accepted the
                // visual transaction. If Commit stalls, HRGN remains at the old finite region;
                // activation immediately samples the elapsed ticket instead of starting a new clock.
                if (inputTicket != null && !_window.TryStartInputAnimation(inputTicket))
                {
                    throw new InvalidOperationException(
                        "The dedicated native input owner could not activate the committed queue animation ticket.");
                }
                _animationStartedAtTimestamp = animationTimestamp;
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("animation-clock-published");
#endif

                if (RoutesPointerInput) _sampleTimer.Start();
                var elapsed = Stopwatch.GetElapsedTime(
                    _animationStartedAtTimestamp,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
                _completionTimer.Interval =
                    TimeSpan.FromMilliseconds(Math.Max(
                        1,
                        _plan.DurationMilliseconds +
                        CompletionGuardMilliseconds -
                        elapsed));
                _completionTimer.Start();
            }

#if DEBUG
            var outputPixels =
                (long)_outputBounds.Width * _outputBounds.Height;
            var wrappedPixels = _visuals.Sum(state =>
                (long)state.SourceBounds.Width *
                state.SourceBounds.Height);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=start mode=live-translation " +
                $"session={_sessionOrdinal} cold={IsColdSession} " +
                $"freshAuthority={_predecessor == null} " +
                $"successor={_predecessor != null} " +
                $"staticPreacquisition={_plan.IsStaticPreacquisition} inputHandoff={_plan.IsSettledInputHandoff} " +
                $"queue={_plan.QueueKey} members={_members.Count} " +
                $"inherited={inheritedCount} " +
                $"revealed={outgoingCount} " +
                $"durationMs={_plan.DurationMilliseconds} " +
                $"output={_outputBounds.Left},{_outputBounds.Top}," +
                $"{_outputBounds.Width}x{_outputBounds.Height} " +
                $"prepareMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(startedAt):F3}");
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"resource.proxy mode=live-translation " +
                $"session={_sessionOrdinal} queue={_plan.QueueKey} " +
                $"outputPixels={outputPixels} " +
                $"wrappedPixels={wrappedPixels} " +
                $"snapshotHosts=0 clips=0 effects=0");
#endif
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule V3 Lite translation startup failed. " +
                "Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            return false;
        }
    }

    private WindowNative.WindowCloakBatchResult PublishRetainedCover(
        Func<bool> publish,
        Action rollback)
    {
        try
        {
            if (publish()) return WindowNative.WindowCloakBatchResult.Success;
        }
        catch
        {
            // The predecessor still covers every source. Restore it before reporting failure.
        }
        try
        {
            rollback();
#if DEBUG
            using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-wait"))
#endif
                _device.WaitForCommitCompletion().CheckError();
            return WindowNative.WindowCloakBatchResult.RolledBack;
        }
        catch
        {
            return WindowNative.WindowCloakBatchResult.RollbackFailed;
        }
    }
}
