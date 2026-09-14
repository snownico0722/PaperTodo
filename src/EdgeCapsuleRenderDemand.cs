using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// Requests WPF work when a ready animation group has not been sampled recently. This owns no
/// presentation state and never advances a frame. One Dispatcher owns all deadlines; the timer
/// only transfers one generation-tagged demand back to that Dispatcher.
/// </summary>
internal sealed class EdgeCapsuleRenderDemand : IDisposable
{
    // A measured request-latency reference, not a display period or a frame-rate guarantee.
    internal static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(12);

    private sealed class Wake(long generation, long deadline, long fired)
    {
        internal readonly long Generation = generation;
        internal readonly long Deadline = deadline;
        internal readonly long Fired = fired;
        internal DispatcherOperation? Operation;
    }

    private readonly Dispatcher _dispatcher;
    private readonly Func<EdgeCapsuleNativeBatchGroup, bool> _ready;
    private readonly Action _request;
    private readonly TimeProvider _clock;
    private readonly long _delayTicks;
    private readonly ITimer? _timer;
    private readonly Dictionary<EdgeCapsuleNativeBatchGroup, long> _deadlines = new();
    private readonly HashSet<EdgeCapsuleNativeBatchGroup> _nextReady = new();
    // Only the immutable deadline/generation and the single posting slot cross threads.
    private readonly object _gate = new();
    private Wake? _wake;
    private long _deadline;
    private long _generation;
    private bool _disposed;

    internal bool Enabled { get; }
    internal int ReadyGroupCount => _deadlines.Count;
    internal bool HasScheduledDemand { get { lock (_gate) return _deadline != 0; } }
    internal bool HasPendingOperation { get { lock (_gate) return _wake != null; } }

    internal EdgeCapsuleRenderDemand(Dispatcher dispatcher,
        Func<EdgeCapsuleNativeBatchGroup, bool> ready, Action request,
        bool enabled = true, TimeProvider? clock = null)
    {
        _dispatcher = dispatcher;
        _ready = ready;
        _request = request;
        _clock = clock ?? TimeProvider.System;
        _delayTicks = Math.Max(1, (long)Math.Ceiling(RequestDelay.TotalSeconds * _clock.TimestampFrequency));
        Enabled = enabled;
        if (enabled)
        {
            _timer = _clock.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            dispatcher.ShutdownStarted += OnShutdown;
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Event("scheduler.demand.mode", value1: enabled ? 1 : 0);
#endif
    }

    internal void ReadyGroupsChanged(IEnumerable<EdgeCapsuleNativeBatchGroup> groups)
    {
        _dispatcher.VerifyAccess();
        if (!Enabled || _disposed) return;
        _nextReady.Clear();
        _nextReady.UnionWith(groups);
        foreach (var group in _deadlines.Keys.ToArray())
            if (!_nextReady.Contains(group)) _deadlines.Remove(group);
        var now = _clock.GetTimestamp();
        foreach (var group in _nextReady)
            _deadlines.TryAdd(group, now + _delayTicks);
        _nextReady.Clear();
        // Finish all caller-owned enumeration/state changes before Abort can enter Hooks.
        PublishDeadline();
    }

    internal void GroupSampled(EdgeCapsuleNativeBatchGroup group, long sampledAtTimestamp)
    {
        _dispatcher.VerifyAccess();
        if (!Enabled || _disposed || !_deadlines.ContainsKey(group)) return;
        // The scheduler supplies the very QPC sampled by the presenters, before native commit.
        _deadlines[group] = sampledAtTimestamp + _delayTicks;
        PublishDeadline();
    }

    private void PublishDeadline()
    {
        var next = _deadlines.Count == 0 ? 0 : _deadlines.Values.Min();
        DispatcherOperation? retired = null;
        long generation;
        lock (_gate)
        {
            if (_disposed || next == _deadline) return;
            _deadline = next;
            generation = ++_generation;
            retired = _wake?.Operation;
            ScheduleLocked();
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Event("scheduler.demand.deadline", value1: _deadlines.Count,
            value2: next, value3: generation);
#endif
        RetireOperation(retired);
        // No state derived before Abort is written after it: a Hook may have restarted demand.
    }

    private void ScheduleLocked()
    {
        if (_timer == null || _disposed) return;
        if (_deadline == 0 || _wake != null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var remaining = _deadline - _clock.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(Math.Max(1,
            Math.Ceiling(remaining * 1000.0 / _clock.TimestampFrequency)));
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        Wake wake;
        lock (_gate)
        {
            if (_disposed || _deadline == 0 || _wake != null) return;
            var now = _clock.GetTimestamp();
            // A callback already in flight from an older Change must not consume a restarted
            // deadline early. The current deadline remains authoritative, even on this thread.
            if (now < _deadline) { ScheduleLocked(); return; }
            wake = new Wake(_generation, _deadline, now);
            _wake = wake;
            ScheduleLocked();
        }
#if DEBUG
        EdgeCapsulePerformanceDiagnostics.Event("scheduler.demand.fire", value1: wake.Generation,
            value2: wake.Deadline, value3: wake.Fired);
#endif
        DispatcherOperation operation;
        try
        {
            operation = _dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() => Run(wake)));
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            FinishWake(wake);
            return;
        }
        bool retired;
        lock (_gate)
        {
            wake.Operation = operation;
            retired = _disposed || !ReferenceEquals(_wake, wake) || wake.Generation != _generation;
        }
        if (retired || operation.Status == DispatcherOperationStatus.Aborted)
            RetireOperation(operation);
    }

    private void Run(Wake wake)
    {
        _dispatcher.VerifyAccess();
        try
        {
            lock (_gate)
                if (_disposed || !ReferenceEquals(_wake, wake) || wake.Generation != _generation ||
                    _clock.GetTimestamp() < wake.Deadline) return;
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            var now = _clock.GetTimestamp();
            var dueCount = 0;
            foreach (var group in _deadlines.Keys.ToArray())
            {
                if (!_ready(group)) { _deadlines.Remove(group); continue; }
                if (_deadlines[group] > now) continue;
                // One request, never a catch-up loop. A missing callback can trigger a later
                // bounded request while the same animation remains active and ready.
                _deadlines[group] = now + _delayTicks;
                dueCount++;
            }
            PublishDeadline();
            if (dueCount == 0 || _disposed) return;
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Event("scheduler.demand.request", value1: dueCount,
                value2: now - wake.Fired, value3: now - wake.Deadline);
#endif
            _request();
        }
        finally { FinishWake(wake); }
    }

    private void RetireOperation(DispatcherOperation? operation)
    {
        if (operation == null) return;
        if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort();
        if (operation.Status != DispatcherOperationStatus.Aborted) return;
        lock (_gate)
        {
            if (_wake?.Operation != operation) return;
            _wake = null;
            ScheduleLocked();
        }
    }

    private void FinishWake(Wake wake)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_wake, wake)) return;
            _wake = null;
            ScheduleLocked();
        }
    }

    private void OnShutdown(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        DispatcherOperation? retired;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _deadline = 0;
            _generation++;
            retired = _wake?.Operation;
            _timer?.Dispose();
        }
        _deadlines.Clear();
        _nextReady.Clear();
        _dispatcher.ShutdownStarted -= OnShutdown;
        RetireOperation(retired);
    }
}
