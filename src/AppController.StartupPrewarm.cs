using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private const int SmallPrewarmPaperLimit = 10;
    private Task _startupShellPrewarmTask = Task.CompletedTask;

    private void ScheduleStartupShellPrewarm(IEnumerable<PaperData> papers, bool startPreviewPreload = false)
    {
        var pending = new Queue<(PaperData Paper, PaperWindow Window)>();
        foreach (var paper in papers)
            if (_windows.TryGetValue(paper.Id, out var window) && !window.IsClosed && !window.IsShellBuilt)
                pending.Enqueue((paper, window));
        var generation = ++_startupShellPrewarmGeneration;
        _startupShellPrewarmTask = PrewarmShellsAsync(pending, generation, startPreviewPreload);
    }

    private async Task PrewarmShellsAsync(Queue<(PaperData Paper, PaperWindow Window)> pending, int generation, bool startPreviewPreload)
    {
        var dispatcher = Application.Current.Dispatcher;
        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;
        try
        {
            // Collapsed notes already have their edge host and model. Prepare their preview
            // before paying the optional editor/Shell first-use cost; demand can still build
            // one Shell immediately through EnsureShellBuilt.
            if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
                await MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();
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
                }, startPreviewPreload ? DispatcherPriority.SystemIdle : DispatcherPriority.ApplicationIdle);
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
