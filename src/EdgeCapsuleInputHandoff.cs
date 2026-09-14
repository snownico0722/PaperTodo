using System.Diagnostics;

namespace PaperTodo;

// One native press may wait only for the synchronous authority-release attempt that it triggered.
// If that attempt schedules any retry, the press is dropped; old input is never replayed later.
internal sealed class EdgeCapsuleInputHandoff
{
    private sealed record Pending(Func<bool> IsCurrent, Action Deliver);
    private Pending? _pending;
    internal int Count => _pending == null ? 0 : 1;

    internal void Enqueue(Func<bool> isCurrent, Action deliver) =>
        _pending = new Pending(isCurrent, deliver);

    internal void Complete()
    {
        var pending = _pending;
        _pending = null; // Remove ownership BEFORE callbacks can re-enter completion.
        if (pending == null)
        {
            return;
        }

        try
        {
            if (pending.IsCurrent()) pending.Deliver();
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Edge input transfer failed: {0}", error);
        }
    }

    // ScheduleCompletionRetry calls this before arming the retry. A press belongs only to the
    // original synchronous handoff attempt; any retry deliberately drops it instead of replaying it.
    internal void Prune() => Cancel();
    internal void Cancel() => _pending = null;
}

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private EdgeCapsuleInputHandoff? _inputHandoff;
    internal void DeferPointerDown(Func<bool> isCurrent, Action deliver) =>
        (_inputHandoff ??= new()).Enqueue(isCurrent, deliver);

    internal void CompleteDeferredPointerInput()
    {
        if (_coverLost)
        {
            _inputHandoff?.Cancel();
            return;
        }
        _inputHandoff?.Complete();
    }
}
