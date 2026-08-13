using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private bool ContainsVisual(DeviceScreenPoint point)
    {
        if (_disposed || _coverLost || _sourcesReleased)
        {
            return false;
        }

        var now = PresentationTimestamp;
        return _members.Any(member =>
        {
            if (!member.Window.CanRouteEdgeCapsuleQueueProxyInput)
            {
                return false;
            }
            var frame = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(
                member.Plan,
                AnimationStartedAtTimestamp,
                _plan.DurationMilliseconds,
                now);
            return frame.Visible &&
                frame.IsHitTestVisible &&
                !frame.InteractiveBounds.IsEmpty &&
                EdgeCapsuleGeometry.Contains(
                    frame.InteractiveBounds,
                    point);
        });
    }

    private long AnimationStartedAtTimestamp =>
        Volatile.Read(ref _animationStartedAtTimestamp) is var started &&
        started > 0
            ? started
            : Stopwatch.GetTimestamp();

    private long PresentationTimestamp =>
        _successorHeld && _heldAtTimestamp > 0
            ? _heldAtTimestamp
            : Stopwatch.GetTimestamp();

    private void OnSampleTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _finishing || _successorHeld)
        {
            return;
        }
        foreach (var member in _members)
        {
            member.Window.InvalidateEdgeCapsuleQueueProxyPointer();
        }
    }

    private void OnCompletionTimerTick(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        CompleteNow(_completionRetrySuccess);
    }

    internal void AdoptCloakedSource(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _cloakedRealSourceHandles.Add(handle);
        }
    }

    public bool TryTransferCloakedSourcesTo(
        EdgeCapsuleQueueCompositionProxy successor)
    {
        if (_disposed ||
            _sourcesReleased ||
            successor._disposed ||
            !ReferenceEquals(_host, successor._host) ||
            !ReferenceEquals(_host.Current, successor))
        {
            return false;
        }

        foreach (var handle in _cloakedRealSourceHandles)
        {
            successor.AdoptCloakedSource(handle);
        }
        _cloakedRealSourceHandles.Clear();
        _sourcesReleased = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.successor phase=source-transfer from={_sessionOrdinal} " +
            $"to={successor._sessionOrdinal} queue={_plan.QueueKey}");
#endif
        return true;
    }

    public void DisposeAfterSuccessorTransfer()
    {
        if (_disposed)
        {
            return;
        }
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        DisposeCore(clearTargetRoot: false);
    }

    private bool TryRollbackInstalledRoot()
    {
        if (!_targetRootInstalled)
        {
            return _host.RollbackPromotion(
                this,
                _predecessor);
        }

        try
        {
            if (_predecessor == null)
            {
                _target.SetRoot(null!).CheckError();
            }
            else
            {
                _target.SetRoot(_predecessor._root).CheckError();
            }
            _device.Commit().CheckError();
            _device.WaitForCommitCompletion().CheckError();
            if (!_host.RollbackPromotion(
                    this,
                    _predecessor))
            {
                throw new InvalidOperationException(
                    "The queue compositor host could not restore its predecessor owner.");
            }

            _targetRootInstalled = false;
            _coverPublished = false;
            if (_predecessor == null)
            {
                _window.Hide();
            }
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.successor phase=rollback session={_sessionOrdinal} " +
                $"predecessor={_predecessor?.SessionOrdinal.ToString() ?? "<none>"} " +
                $"queue={_plan.QueueKey} outcome=restored");
#endif
            return true;
        }
        catch (Exception ex)
        {
            _coverLost = true;
            Trace.TraceError(
                "Edge capsule queue root rollback failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
            return false;
        }
    }

    public void AbortStaged()
    {
        if (_disposed)
        {
            return;
        }

        _ = TryRollbackInstalledRoot();
        if (_cloakedRealSourceHandles.Count > 0)
        {
            _ = TryRestoreSourcesAfterCoverLoss();
        }
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        DisposeCore(clearTargetRoot: false);
    }

    private bool TryUncloakSourcesForHandoff()
    {
        var restored =
            new List<IntPtr>(_cloakedRealSourceHandles.Count);
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }

            if (WindowNative.TrySetWindowCloaked(
                    handle,
                    cloaked: false))
            {
                restored.Add(handle);
            }
            else
            {
                allRestored = false;
            }
        }

        if (allRestored)
        {
            return true;
        }

        foreach (var handle in restored)
        {
            if (WindowNative.IsWindowHandleAlive(handle))
            {
                _ = WindowNative.TrySetWindowCloaked(
                    handle,
                    cloaked: true);
            }
        }
        WindowNative.FlushDesktopComposition();
        return false;
    }

    public bool TryReleaseForHandoff()
    {
        if (_disposed)
        {
            return false;
        }
        if (_handoffRetirementPending || _sourcesReleased)
        {
            return true;
        }
        if (_coverLost)
        {
            return ReleaseAfterCoverLoss();
        }
        if (!ReferenceEquals(_host.Current, this) ||
            !TryUncloakSourcesForHandoff())
        {
            return false;
        }

        // The exact final proxy frame remains above the newly uncloaked native endpoints, but the
        // desktop barrier runs off the UI dispatcher. Existing controller cleanup can remove the
        // logical route immediately; Dispose defers physical DComp teardown until this overlap ends.
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        _handoffRetirementPending = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();
#if DEBUG
        _handoffRetirementStartedAtTimestamp =
            EdgeCapsulePerformanceDiagnostics.Timestamp();
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retire-begin session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey}");
#endif

        var dispatcher = _members[0].Window.Dispatcher;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                WindowNative.FlushDesktopComposition();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Edge capsule queue asynchronous DWM barrier failed. Queue={0}; Session={1}; Exception={2}",
                    _plan.QueueKey,
                    _sessionOrdinal,
                    ex);
            }

            if (dispatcher.HasShutdownStarted)
            {
                return;
            }
            try
            {
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    (Action)CompleteHandoffRetirement);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Edge capsule queue retirement dispatch failed. Queue={0}; Session={1}; Exception={2}",
                    _plan.QueueKey,
                    _sessionOrdinal,
                    ex);
            }
        });
        return true;
    }

    private void CompleteHandoffRetirement()
    {
        if (_disposed || !_handoffRetirementPending)
        {
            return;
        }

        try
        {
            if (ReferenceEquals(_host.Current, this))
            {
                _target.SetRoot(null!).CheckError();
                _device.Commit().CheckError();
                _targetRootInstalled = false;
                _window.Hide();
            }
        }
        catch (Exception ex)
        {
            _coverLost = true;
            try { _window.Hide(); } catch { }
            Trace.TraceError(
                "Edge capsule queue asynchronous retirement failed. Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }
        finally
        {
            _handoffRetirementPending = false;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"proxy.handoff phase=retire-complete session={_sessionOrdinal} " +
                $"cold={IsColdSession} queue={_plan.QueueKey} " +
                $"barrierMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(_handoffRetirementStartedAtTimestamp):F3}");
#endif
            if (_disposeAfterHandoffRetirement)
            {
                DisposeCore(clearTargetRoot: false);
            }
        }
    }

    public bool ReleaseAfterCoverLoss()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }
        if (!TryRestoreSourcesAfterCoverLoss())
        {
            return false;
        }

        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        if (ReferenceEquals(_host.Current, this))
        {
            try { _target.SetRoot(null!).CheckError(); } catch { }
            try { _device.Commit().CheckError(); } catch { }
            _targetRootInstalled = false;
            _window.Hide();
        }
        WindowNative.FlushDesktopComposition();
        return true;
    }

    private bool TryRestoreSourcesAfterCoverLoss()
    {
        var allRestored = true;
        foreach (var handle in _cloakedRealSourceHandles)
        {
            if (!WindowNative.IsWindowHandleAlive(handle))
            {
                continue;
            }

            var restored = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (WindowNative.TrySetWindowCloaked(
                        handle,
                        cloaked: false))
                {
                    restored = true;
                    break;
                }
            }
            allRestored &= restored;
        }
        WindowNative.FlushDesktopComposition();
        return allRestored;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_coverPublished)
        {
            AbortStaged();
            return;
        }
        if (_handoffRetirementPending)
        {
            _disposeAfterHandoffRetirement = true;
            return;
        }
        if (_sourcesReleased &&
            !ReferenceEquals(_host.Current, this))
        {
            DisposeCore(clearTargetRoot: false);
            return;
        }
        if (!_sourcesReleased && !TryReleaseForHandoff())
        {
            return;
        }
        DisposeCore(clearTargetRoot: true);
    }

    public void ForceDisposeForShutdown()
    {
        if (_disposed)
        {
            return;
        }

        _handoffRetirementPending = false;
        _disposeAfterHandoffRetirement = false;
        _ = TryRestoreSourcesAfterCoverLoss();
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        DisposeCore(
            clearTargetRoot:
                ReferenceEquals(_host.Current, this));
    }

    private void DisposeCore(bool clearTargetRoot)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handoffRetirementPending = false;
        _disposeAfterHandoffRetirement = false;
        _sampleTimer.Stop();
        _completionTimer.Stop();
        try
        {
            if (clearTargetRoot &&
                ReferenceEquals(_host.Current, this))
            {
                try { _target.SetRoot(null!).CheckError(); } catch { }
                try { _device.Commit().CheckError(); } catch { }
                _targetRootInstalled = false;
            }

            foreach (var visual in _visuals)
            {
                try { visual.Dispose(); } catch { }
            }
            _visuals.Clear();
            try { _root.Dispose(); } catch { }
        }
        finally
        {
            foreach (var member in _members)
            {
                try { member.SnapshotHost?.Dispose(); } catch { }
            }
            _runtime.Release(
                _host,
                this,
                broken: _coverLost);
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=dispose session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"released={_sourcesReleased} successor={_predecessor != null} " +
            $"reusedHost=true");
#endif
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(
        IntPtr renderingDevice,
        ref Guid iid,
        out IntPtr dcompositionDevice);
}
