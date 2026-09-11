using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    public bool TryStart(out bool realHostMayHaveChanged)
    {
        realHostMayHaveChanged = false;
        try
        {
            var started = PrepareAndStart();
            realHostMayHaveChanged = _realEndpointMutationStarted;
            return started;
        }
        finally
        {
            _starting = false;
            if (_completionPendingDuringStart)
            {
                CompleteNow(_pendingStartCompletionSuccess);
            }
        }
    }

    private bool PrepareAndStart()
    {
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
            var successorHandles = _members
                .Select(member => member.SourceHandle)
                .Where(handle => handle != IntPtr.Zero)
                .ToHashSet();
            var predecessorHandles =
                _predecessor?.SnapshotCloakedSourceHandles() ??
                new HashSet<IntPtr>();
            var inheritedHandles = predecessorHandles
                .Where(successorHandles.Contains)
                .ToHashSet();
            var newHandles = successorHandles
                .Where(handle =>
                    !predecessorHandles.Contains(handle))
                .ToArray();
            var outgoingHandles = predecessorHandles
                .Where(handle =>
                    !successorHandles.Contains(handle))
                .ToArray();

            var cloakChanges =
                new List<WindowNative.WindowCloakChange>(
                    newHandles.Length + outgoingHandles.Length);
            cloakChanges.AddRange(newHandles.Select(handle =>
                new WindowNative.WindowCloakChange(
                    handle,
                    Cloaked: true,
                    RollbackCloaked: false)));
            cloakChanges.AddRange(outgoingHandles.Select(handle =>
                new WindowNative.WindowCloakChange(
                    handle,
                    Cloaked: false,
                    RollbackCloaked: true)));

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
                RebaseVisualStarts(coverTimestamp);
                _target.SetRoot(_root).CheckError();
                _device.Commit().CheckError();
                _targetRootInstalled = true;
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("root-committed-static");
#endif

                if (!_window.Show(_outputBounds, _plan.Topmost))
                {
                    return false;
                }
                if (!WindowNative.TryFlushDesktopComposition())
                {
                    return false;
                }
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("output-ready");
                EdgeCapsuleColdStartDiagnostics.Boundary("cover-static-visible");
#endif
            }
            else if (newHandles.Length > 0)
            {
                _successorAdmissionCover =
                    CreateSuccessorAdmissionCover(
                        coverTimestamp,
                        newHandles.ToHashSet());
                _target.SetRoot(
                    _successorAdmissionCover.Root).CheckError();
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
                RebaseVisualStarts(Stopwatch.GetTimestamp());

                if (_predecessor != null)
                {
                    // Outgoing real HWNDs are being un-cloaked in this same DWM batch. Keep the
                    // predecessor root installed until this callback, then replace the root so
                    // outgoing reveal, incoming cloak and successor publication cross one flush.
                    _target.SetRoot(_root).CheckError();
                    _targetRootInstalled = true;
#if DEBUG
                    EdgeCapsuleColdStartDiagnostics.Boundary("successor-root-staged");
#endif
                }

                _device.Commit().CheckError();
#if DEBUG
                EdgeCapsuleColdStartDiagnostics.Boundary("start-cover-published");
#endif

                if (!_coverReady(this))
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
                        _coverRollback(this);
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
            var publication =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    cloakChanges,
                    PublishBeforeFlush,
                    RollbackBeforeFlush);
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
                    // Rollback crossed its own DwmFlush and restored the predecessor root.
                    ReleaseSuccessorAdmissionCover();
                }
                return false;
            }
#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("publication-verified");
#endif

            // The batch DwmFlush has now made the final successor root authoritative.
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

            // All blocking publication work is finished. Start shape and translation together
            // from this fresh QPC, keeping the same curve and full duration for both. Do not
            // flush again after starting: WPF must be free to produce its first frame. The cover
            // already owns its sources, so a failure here uses the normal published-cover handoff.
            var animationTimestamp = Stopwatch.GetTimestamp();
            if (!_animationStartRequested(animationTimestamp))
            {
                return false;
            }
            ConfigureAnimations(animationTimestamp);
            _device.Commit().CheckError();
            _animationStartedAtTimestamp = animationTimestamp;
#if DEBUG
            EdgeCapsuleColdStartDiagnostics.Boundary("animation-clock-published");
#endif

            _sampleTimer.Start();
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

#if DEBUG
            var outputPixels =
                (long)_outputBounds.Width * _outputBounds.Height;
            var wrappedPixels = _visuals.Sum(state =>
                (long)state.SourceBounds.Width *
                state.SourceBounds.Height);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.session phase=start mode=live-translation " +
                $"session={_sessionOrdinal} cold={IsColdSession} " +
                $"successor={_predecessor != null} " +
                $"queue={_plan.QueueKey} members={_members.Count} " +
                $"inherited={inheritedHandles.Count} " +
                $"revealed={outgoingHandles.Length} " +
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
}
