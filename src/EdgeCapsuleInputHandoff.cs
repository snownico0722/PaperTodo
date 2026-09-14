using System.Diagnostics;

namespace PaperTodo;

// Pending native presses belong to one proxy generation. This is not a mouse/gesture engine:
// it only retains the existing press transfer across that generation's verified handoff retry.
internal sealed class EdgeCapsuleInputHandoff
{
    private sealed record Pending(Func<bool> IsCurrent, Action Deliver, long Created);
    private readonly List<Pending> _pending = new();
    private readonly TimeProvider _clock;
    internal EdgeCapsuleInputHandoff(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal int Count => _pending.Count;

    internal void Enqueue(Func<bool> isCurrent, Action deliver)
    {
        Prune();
        _pending.Add(new Pending(isCurrent, deliver, _clock.GetTimestamp()));
    }

    // Called only once native/WPF authority has actually returned, not when a retry is scheduled.
    internal void Complete()
    {
        var pending = _pending.ToArray();
        _pending.Clear(); // Remove ownership BEFORE callbacks can re-enter completion.
        foreach (var item in pending)
        {
            try
            {
                if (Current(item)) item.Deliver();
            }
            catch (Exception error) { Trace.TraceWarning("Edge input transfer failed: {0}", error); }
        }
    }

    internal void Prune() => _pending.RemoveAll(item => !Current(item));
    internal void Cancel() => _pending.Clear();
    private bool Current(Pending item) =>
        _clock.GetElapsedTime(item.Created) <= TimeSpan.FromSeconds(1) && item.IsCurrent();
}

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private EdgeCapsuleInputHandoff? _inputHandoff;
    internal void DeferPointerDown(Func<bool> isCurrent, Action deliver) =>
        (_inputHandoff ??= new()).Enqueue(isCurrent, deliver);
    internal void CompleteDeferredPointerInput() => _inputHandoff?.Complete();
}
