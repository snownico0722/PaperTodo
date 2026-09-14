using System.Diagnostics;
using SharpGen.Runtime;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    internal static bool ShapeEnabled
    {
        get
        {
#if DEBUG
            // Keep a same-binary diagnostic control; normal local and Release behavior uses shape.
            return Environment.GetEnvironmentVariable("PAPERTODO_EDGE_PROXY_SHAPE_EXPERIMENT") != "0";
#else
            return true;
#endif
        }
    }

    private DispatcherOperation? _shapeRefresh;
    private DispatcherOperation? _shapeRecovery;

    internal void UpdateProxyShape(PaperWindow window, EdgeCapsulePresentationFrame frame,
        EdgeCapsuleTransition? transition)
    {
        if (_disposed || _starting || _finishing || _successorHeld ||
            !_coverPublished || _coverLost || _sourcesReleased) return;
        var state = _visuals.FirstOrDefault(state => ReferenceEquals(state.Member.Window, window));
        if (state?.Shape == null || state.ShapeSource == null) return;
        try
        {
            if (!state.ShapeSource.Synchronize(frame))
            {
                // Host.Apply may have invalidated WPF arrangement. Its natural layout-completed
                // notification will retry this same submitted contract; that is not source loss.
                if (state.ShapeSource.IsLayoutPending) return;
                throw new InvalidOperationException("Independent live source no longer matches its Host generation.");
            }
            if (!state.Shape.Update(frame, transition, state.ShapeSource.Description)) return;
            _device.Commit().CheckError();
            TraceProxyShape(state, "submit");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Edge proxy shape update failed: {0}", ex);
            // Host.Apply has not yet returned its frame to Presenter. Re-entering completion
            // here could install an endpoint which the caller would overwrite on return.
            OnProxySourceInvalidated();
        }
    }

    private void OnProxySourceInvalidated()
    {
        if (_disposed || _sourcesReleased || _shapeRecovery is { Status: DispatcherOperationStatus.Pending }) return;
        var dispatcher = _members[0].Window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        // Invalidation can originate inside Host.Apply or preview detach. Exit that stack before
        // preparing the real endpoint; no timer or synchronous nested Presenter reconciliation.
        _shapeRecovery = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            _shapeRecovery = null;
            if (!_disposed && !_sourcesReleased) CompleteNow(success: false);
        }));
    }

    private void OnProxySourceUpdated()
    {
        if (_disposed || _sourcesReleased || _shapeRefresh is { Status: DispatcherOperationStatus.Pending }) return;
        var dispatcher = _members[0].Window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        _shapeRefresh = dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            _shapeRefresh = null;
            if (_disposed || _sourcesReleased) return;
            foreach (var state in _visuals)
                if (state.Shape != null && state.Member.Window.TryGetEdgeCapsuleQueueProxyAppliedPresentation(out var frame))
                    UpdateProxyShape(state.Member.Window, frame, state.Member.Window.EdgeCapsuleTransitionSnapshot);
        }));
    }

    private void TraceProxyShape(VisualState state, string phase)
    {
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"proxy.shape phase={phase} session={_sessionOrdinal} " +
            $"paper={state.Member.Window.EdgeCapsulePreviewPaperId} " +
            $"owned={state.ShapeSource != null} " +
            $"submissions={state.Shape?.SubmissionCount ?? 0} " +
            $"alpha={state.Shape?.Sample(Stopwatch.GetTimestamp()).ContentOpacity ?? -1:F6}");
#endif
    }

}
