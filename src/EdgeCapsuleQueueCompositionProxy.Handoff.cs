using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    internal HashSet<IntPtr> SnapshotCloakedSourceHandles() =>
        _cloakedRealSourceHandles
            .Where(WindowNative.IsWindowHandleAlive)
            .ToHashSet();

    internal void CompleteSourceTransferAfterSuccessfulBoundary()
    {
        if (_disposed || _sourcesReleased)
        {
            return;
        }

        // The DWM batch verified that retained handles stay cloaked and
        // excluded handles are visible. The predecessor now relinquishes
        // its complete source set.
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        _sampleTimer.Stop();
        _completionTimer.Stop();
        DisposeCore(clearTargetRoot: false);
    }

    private bool TryRollbackInstalledRoot()
    {
        if (_controllerPublished)
        {
            try
            {
                _coverRollback(this);
            }
            catch
            {
                _coverLost = true;
            }
            _controllerPublished = false;
        }

        if (!_targetRootInstalled)
        {
            return _host.RollbackPromotion(this, _predecessor);
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
            if (!_host.RollbackPromotion(this, _predecessor))
            {
                throw new InvalidOperationException(
                    "The queue compositor host could not restore " +
                    "its predecessor owner.");
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
                "Edge capsule queue root rollback failed. " +
                "Queue={0}; Session={1}; Exception={2}",
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

    public bool TryReleaseForHandoff()
    {
        if (_disposed || _sourcesReleased)
        {
            return _sourcesReleased;
        }
        if (_coverLost)
        {
            return ReleaseAfterCoverLoss();
        }
        if (!ReferenceEquals(_host.Current, this))
        {
            return false;
        }

        var liveHandles = SnapshotCloakedSourceHandles().ToArray();
        var revealChanges = liveHandles.Select(handle =>
            new WindowNative.WindowCloakChange(
                handle,
                Cloaked: false,
                RollbackCloaked: true)).ToArray();
        var swapAttempted = false;

        bool PublishAuthoritySwap()
        {
            if (!ReferenceEquals(_host.Current, this))
            {
                return false;
            }

            swapAttempted = true;
            _target.SetRoot(null!).CheckError();
            _device.Commit().CheckError();
            _targetRootInstalled = false;
            return true;
        }

        void RollbackAuthoritySwap()
        {
            if (!swapAttempted)
            {
                return;
            }

            try
            {
                _target.SetRoot(_root).CheckError();
                _device.Commit().CheckError();
                _targetRootInstalled = true;
            }
            catch
            {
                _coverLost = true;
                throw;
            }
        }

        var result = WindowNative.TrySetWindowCloakedBatchDetailed(
            revealChanges,
            PublishAuthoritySwap,
            RollbackAuthoritySwap);
        if (result != WindowNative.WindowCloakBatchResult.Success)
        {
            if (result ==
                WindowNative.WindowCloakBatchResult.RollbackFailed)
            {
                _coverLost = true;
                return ReleaseAfterCoverLoss();
            }
            return false;
        }

        try
        {
            if (ReferenceEquals(_host.Current, this))
            {
                _window.Hide();
                _host.Detach(this);
            }
        }
        catch (Exception ex)
        {
            _coverLost = true;
            Trace.TraceError(
                "Edge capsule queue proxy release cleanup failed. " +
                "Queue={0}; Session={1}; Exception={2}",
                _plan.QueueKey,
                _sessionOrdinal,
                ex);
        }

        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        return true;
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
            _host.Detach(this);
        }
        WindowNative.FlushDesktopComposition();
        return true;
    }

    private bool TryRestoreSourcesAfterCoverLoss()
    {
        var liveHandles = SnapshotCloakedSourceHandles().ToArray();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var restored = true;
            foreach (var handle in liveHandles)
            {
                restored &= WindowNative.TrySetWindowCloaked(
                    handle,
                    cloaked: false);
            }
            if (restored && WindowNative.TryFlushDesktopComposition())
            {
                return true;
            }
        }
        return liveHandles.Length == 0;
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
        if (!_sourcesReleased && !TryReleaseForHandoff())
        {
            return;
        }
        if (_sourcesReleased && !_targetRootInstalled && !_coverLost)
        {
            ScheduleSuccessfulRetire();
            return;
        }
        if (_sourcesReleased &&
            !ReferenceEquals(_host.Current, this))
        {
            DisposeCore(clearTargetRoot: false);
            return;
        }
        if (_sourcesReleased && _coverLost)
        {
            DisposeCore(clearTargetRoot: false);
            return;
        }
        DisposeCore(clearTargetRoot: true);
    }

    public void ForceDisposeForShutdown()
    {
        _inputHandoff?.Cancel();
        if (_disposed)
        {
            RetireVisualResources();
            ReleaseRuntimeOnce(broken: _coverLost);
            return;
        }

        _ = TryRestoreSourcesAfterCoverLoss();
        _sourcesReleased = true;
        _cloakedRealSourceHandles.Clear();
        DisposeCore(
            clearTargetRoot:
                ReferenceEquals(_host.Current, this));
    }

    private void ScheduleSuccessfulRetire()
    {
        if (_disposed || _successfulRetireScheduled)
        {
            return;
        }

        _successfulRetireScheduled = true;
        _disposed = true;
        _sampleTimer.Stop();
        _completionTimer.Stop();

        var dispatcher = _members[0].Window.Dispatcher;
        if (dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            RetireVisualResources();
            return;
        }

        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            (Action)RetireVisualResources);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retire-scheduled " +
            $"session={_sessionOrdinal} cold={IsColdSession} " +
            $"queue={_plan.QueueKey} visuals={_visuals.Count}");
#endif
    }

    private void RetireVisualResources()
    {
        if (_visualResourcesRetired)
        {
            return;
        }

        _visualResourcesRetired = true;
        ReleaseSuccessorAdmissionCover();
        foreach (var visual in _visuals)
        {
            try { visual.Dispose(); } catch { }
        }
        _visuals.Clear();
        try { _root.Dispose(); } catch { }
        ReleaseRuntimeOnce(broken: _coverLost);
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=retired session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey}");
#endif
    }

    private void ReleaseRuntimeOnce(bool broken)
    {
        if (_runtimeReleased)
        {
            return;
        }
        _runtimeReleased = true;
        _runtime.Release(_host, this, broken);
    }

    private void DisposeCore(bool clearTargetRoot)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

            RetireVisualResources();
        }
        finally
        {
            ReleaseRuntimeOnce(broken: _coverLost);
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.handoff phase=dispose session={_sessionOrdinal} " +
            $"cold={IsColdSession} queue={_plan.QueueKey} " +
            $"released={_sourcesReleased} " +
            $"successor={_predecessor != null} reusedHost=true");
#endif
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice2(
        IntPtr renderingDevice,
        ref Guid iid,
        out IntPtr dcompositionDevice);
}
