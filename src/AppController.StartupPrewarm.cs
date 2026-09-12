using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const int SmallPrewarmPaperLimit = 10;
    private Task _startupShellPrewarmTask = Task.CompletedTask;

    private void ScheduleStartupShellPrewarm(IEnumerable<PaperData> papers)
    {
        var pending = new Queue<(PaperData Paper, PaperWindow Window)>();
        foreach (var paper in papers)
            if (_windows.TryGetValue(paper.Id, out var window) && !window.IsClosed && !window.IsShellBuilt)
                pending.Enqueue((paper, window));
        var generation = ++_startupShellPrewarmGeneration;
        _startupShellPrewarmTask = PrewarmShellsAsync(pending, generation);
    }

    private async Task PrewarmShellsAsync(Queue<(PaperData Paper, PaperWindow Window)> pending, int generation)
    {
        var dispatcher = Application.Current.Dispatcher;
        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;
        try
        {
            while (pending.Count > 0 && !IsExiting && generation == _startupShellPrewarmGeneration)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    var started = Stopwatch.GetTimestamp();
                    var built = 0;
                    while (pending.Count > 0 && !IsExiting && generation == _startupShellPrewarmGeneration)
                    {
                        var (paper, window) = pending.Dequeue();
                        if (!paper.IsVisible || window.IsClosed || window.IsShellBuilt || !State.Papers.Contains(paper))
                            continue;
                        window.EnsureShellBuilt();
                        // A single WPF build cannot be preempted. Check BETWEEN items; 6ms is a
                        // soft batch budget, not a promise that an expensive shell fits in 6ms.
                        if (++built >= batchLimit || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 6)
                            break;
                    }
                }, DispatcherPriority.ApplicationIdle);
            }
        }
        catch (OperationCanceledException) when (dispatcher.HasShutdownStarted) { }
        catch (Exception ex)
        {
            // Optional preconstruction must not strand the startup continuation. Demand still
            // owns its normal error path; this queue does not retry a failed shell indefinitely.
            Trace.TraceWarning("Startup shell prewarm failed: {0}", ex);
        }
    }
}
