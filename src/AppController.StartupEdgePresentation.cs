using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private void FlushStartupDeepCapsulePresentations(EdgeCapsuleQueuePlan plan)
    {
        var staged = new List<PaperWindow>();

        foreach (var queue in plan.Queues)
        {
            foreach (var paper in queue.Papers)
            {
                if (!_windows.TryGetValue(paper.Id, out var window) ||
                    !ShouldPaperOccupyDeepCapsuleSlot(paper, window))
                {
                    continue;
                }

                if (window.StageStartupDeepCapsulePresentation())
                {
                    staged.Add(window);
                }
                else
                {
                    // Keep the existing per-paper path as a bounded recovery for a failed first
                    // stage. A normal startup should not enter this branch.
                    window.FlushStartupDeepCapsulePresentation();
                }
            }
        }

        if (staged.Count == 0)
        {
            return;
        }

        var dispatcher = Application.Current.Dispatcher;

        // All first-show HWNDs exist but remain transparent. Let WPF measure/arrange/render the
        // complete set once instead of forcing Root.UpdateLayout separately for every host.
        dispatcher.Invoke(DispatcherPriority.Render, static () => { });

        foreach (var window in staged)
        {
            if (!window.RevealStartupDeepCapsulePresentation())
            {
                window.RecoverStartupDeepCapsulePresentation();
            }
        }

        // This is the only visible startup Render boundary for the staged hosts. DWM remains
        // asynchronous here; the external startup benchmark owns any DwmFlush measurement.
        dispatcher.Invoke(DispatcherPriority.Render, static () => { });
    }
}
