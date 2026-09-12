using System.Diagnostics;
using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private static readonly TimeSpan StartupDisplayTopologyPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StartupDisplayTopologySettleTimeout = TimeSpan.FromSeconds(5);
    private const int StartupDisplayTopologyReadySamples = 2;
    private readonly HashSet<PaperData> _startupDisplayDeferredPapers = new();
    private CancellationTokenSource? _startupDisplayRestore;

    private void DeferStartupPapersWithoutMonitor()
    {
        CancelStartupDisplayRestore();
        var workAreas = ConnectedStartupWorkAreas();
        foreach (var paper in State.Papers)
            if (PaperAwaitsStartupMonitor(paper, workAreas))
                _startupDisplayDeferredPapers.Add(paper);
    }

    private bool PaperAwaitsStartupMonitor(PaperData paper, List<Rect> workAreas)
    {
        // Deep capsules use persisted queue identity, not the parked PaperWindow coordinates.
        if (paper.IsCollapsed && State.UseCapsuleMode && State.UseDeepCapsuleMode && CanPaperDisplayAsCapsule(paper))
            return false;
        if (!IsFinite(paper.X) || !IsFinite(paper.Y) || !IsFinite(paper.Width) || !IsFinite(paper.Height) ||
            paper.Width <= 0 || paper.Height <= 0)
            return false; // corrupt geometry can be rescued immediately
        var center = new Point(paper.X + paper.Width / 2, paper.Y + paper.Height / 2);
        return !workAreas.Exists(area => area.Contains(center));
    }

    private async void CompleteDeferredStartupDisplayRestore()
    {
        if (_startupDisplayDeferredPapers.Count == 0 || IsExiting) return;
        using var cancellation = new CancellationTokenSource();
        _startupDisplayRestore = cancellation;
        var generation = _paperSurfaceRestoreGeneration;
        var started = Stopwatch.GetTimestamp();
        var readySamples = 0;
        try
        {
            // Only ambiguous papers wait. The tray, safe paper surfaces and single-instance
            // command handling are already usable; no coordinates are guessed during this wait.
            while (_startupDisplayDeferredPapers.Count > 0 &&
                Stopwatch.GetElapsedTime(started) < StartupDisplayTopologySettleTimeout)
            {
                await Task.Delay(StartupDisplayTopologyPollInterval, cancellation.Token);
                if (IsExiting || generation != _paperSurfaceRestoreGeneration) return;
                _startupDisplayDeferredPapers.RemoveWhere(paper => !State.Papers.Contains(paper));
                WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
                var workAreas = ConnectedStartupWorkAreas();
                if (_startupDisplayDeferredPapers.Any(paper => PaperAwaitsStartupMonitor(paper, workAreas)))
                    readySamples = 0;
                else if (++readySamples >= StartupDisplayTopologyReadySamples)
                    break;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsExiting || generation != _paperSurfaceRestoreGeneration) return;

            var pending = _startupDisplayDeferredPapers.ToArray();
            _startupDisplayDeferredPapers.Clear();
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            var wasSuppressingDirty = _suppressDirty;
            _suppressDirty = true;
            _trayRefreshSuppressionDepth++;
            var rescued = false;
            var shown = false;
            try
            {
                foreach (var paper in pending)
                {
                    var index = State.Papers.IndexOf(paper);
                    if (index < 0) continue;
                    rescued |= RescuePaperIfOffScreen(paper, index);
                    // Hide/delete/explicit show during the wait wins over this continuation.
                    if (!paper.IsVisible || (_windows.TryGetValue(paper.Id, out var window) && !window.IsClosed))
                        continue;
                    ShowPaper(paper, activate: false);
                    shown = true;
                }
            }
            finally
            {
                _trayRefreshSuppressionDepth--;
                _suppressDirty = wasSuppressingDirty;
            }
            if (shown)
            {
                ArrangeDeepCapsules(animate: State.EnableAnimations, flushInitialPresentations: true);
                ScheduleStartupShellPrewarm(State.Papers.Where(paper => paper.IsVisible));
                RefreshTrayMenu();
            }
            if (rescued) SaveNow();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Trace.TraceWarning("Deferred startup display restore failed: {0}", ex);
        }
        finally
        {
            if (ReferenceEquals(_startupDisplayRestore, cancellation))
            {
                _startupDisplayRestore = null;
                _startupDisplayDeferredPapers.Clear();
            }
        }
    }

    private void CancelStartupDisplayRestore()
    {
        _startupDisplayRestore?.Cancel();
        _startupDisplayRestore = null;
        _startupDisplayDeferredPapers.Clear();
    }

    private static List<Rect> ConnectedStartupWorkAreas()
    {
        var result = new List<Rect>();
        foreach (var monitor in WindowWorkAreaHelper.ConnectedMonitorGeometries())
        {
            var area = WindowWorkAreaHelper.WorkAreaForDevice(monitor.DeviceName);
            if (area.HasValue && !area.Value.IsEmpty && area.Value.Width > 0 && area.Value.Height > 0)
                result.Add(area.Value);
        }
        return result;
    }
}
