using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal enum EdgePrewarmOutcome { Prepared, Deferred, Skipped, Failed }

// Schedules preparation only. Queue/source authority and speculative body artifacts continue to
// belong to their existing owners; a Rendering notification is a scheduling barrier, not proof
// that a HWND is ready to take visual authority.
internal sealed class EdgePrewarmCoordinator : IDisposable
{
    private const int InteractionDelayMilliseconds = 180;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _canPrepare;
    private readonly Action _prepareGraphics;
    private readonly Func<string, EdgePrewarmOutcome> _prepareQueue;
    private readonly Action<bool> _suspendContent;
    private readonly Dictionary<string, Candidate> _candidates = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _interactionDelay;
    private PreparationDispatch? _pendingDispatch;
    private EventHandler? _renderingHandler;
    private Candidate? _preparingCandidate;
    private long _dispatchGeneration;
    private long _quietUntil;
    private bool _enabled;
    private bool _disposed;
    private bool _graphicsAttempted;
    private bool _interactionPending;
    private bool _contentSuspended;

    // Own the slot before BeginInvoke: OperationPosted may cancel or even run it before
    // BeginInvoke returns its operation handle. This is scheduling identity, not queue state.
    private sealed class PreparationDispatch
    {
        internal DispatcherOperation? Operation;
    }

    private enum Readiness { AwaitingFrame, Ready, Sleeping }
    private sealed class Candidate(string key, long version)
    {
        internal readonly string Key = key;
        internal readonly long Version = version;
        internal Readiness State = Readiness.AwaitingFrame;
    }

    internal EdgePrewarmCoordinator(Dispatcher dispatcher, Func<bool> canPrepare,
        Action prepareGraphics, Func<string, EdgePrewarmOutcome> prepareQueue,
        Action<bool> suspendContent)
    {
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();
        _canPrepare = canPrepare;
        _prepareGraphics = prepareGraphics;
        _prepareQueue = prepareQueue;
        _suspendContent = suspendContent;
        _interactionDelay = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(InteractionDelayMilliseconds) };
        _interactionDelay.Tick += OnInteractionDelay;
        _dispatcher.ShutdownStarted += OnShutdown;
    }

    internal long Version { get; private set; }
    internal bool IsPreparing { get; private set; }
    internal int PendingCount => _candidates.Count;
    internal int DeferredCount => _candidates.Values.Count(candidate => candidate.State == Readiness.Sleeping);
    internal bool HasScheduledWork => _renderingHandler != null || _pendingDispatch != null || _interactionPending;
    private bool Active => _enabled && !_disposed && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished;

    internal void SetEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        Version++;
        TraceState(enabled ? "enabled" : "disabled");
        if (!enabled) ClearPending();
        else Schedule();
    }

    internal void Request(string queueKey)
    {
        _dispatcher.VerifyAccess();
        if (!Active || string.IsNullOrEmpty(queueKey)) return;
        _candidates[queueKey] = new Candidate(queueKey, ++Version);
        TraceState("request", queueKey);
        Schedule();
    }

    // Readiness changes wake existing interest. They must not manufacture fresh work after a
    // permanent failure, cancellation, or successful preparation.
    internal void Wake(string queueKey)
    {
        _dispatcher.VerifyAccess();
        if (!Active || !_candidates.TryGetValue(queueKey, out var candidate) ||
            candidate.State != Readiness.Sleeping) return;
        _candidates[queueKey] = new Candidate(queueKey, ++Version);
        TraceState("wake", queueKey);
        Schedule();
    }

    internal void Cancel(string queueKey)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Version++;
        if (!_candidates.Remove(queueKey)) return;
        TraceState("cancel", queueKey);
        CancelDispatch();
        Schedule();
    }

    internal void CancelAll()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        Version++;
        TraceState("cancel-all");
        ClearPending();
    }

    internal void NotifyInteraction()
    {
        _dispatcher.VerifyAccess();
        if (!Active) return;
        Version++;
        // A native publication callback can pump input. Invalidate that attempt immediately,
        // while preserving its latest request for the next quiet period.
        if (_preparingCandidate != null && IsCurrent(_preparingCandidate))
            _preparingCandidate.State = Readiness.AwaitingFrame;
        _interactionPending = true;
        _quietUntil = Environment.TickCount64 + InteractionDelayMilliseconds;
        _interactionDelay.Stop();
        // Publish the pause before Abort can synchronously enter Dispatcher hooks.
        CancelDispatch();
        UpdateContentSuspension();
        // The content owner may synchronously cancel or disable this coordinator.
        if (!Active || !_interactionPending) return;
        _interactionDelay.Interval = TimeSpan.FromMilliseconds(InteractionDelayMilliseconds);
        _interactionDelay.Start();
    }

    private void OnInteractionDelay(object? sender, EventArgs e)
    {
        _interactionDelay.Stop();
        if (!Active || !_interactionPending) return;
        var remaining = _quietUntil - Environment.TickCount64;
        if (remaining > 0)
        {
            // A queued tick from before a reset cannot end the newer quiet period early.
            _interactionDelay.Interval = TimeSpan.FromMilliseconds(remaining);
            _interactionDelay.Start();
            return;
        }
        _interactionPending = false;
        // Input can be the readiness barrier (for example a pressed mouse button). Its actual
        // quiet boundary wakes existing deferred interest once; no timer polls for readiness.
        var wokeDeferred = false;
        foreach (var candidate in _candidates.Values)
        {
            if (candidate.State != Readiness.Sleeping) continue;
            candidate.State = Readiness.AwaitingFrame;
            wokeDeferred = true;
        }
        if (wokeDeferred)
        {
            Version++;
            TraceState("wake-after-interaction");
        }
        UpdateContentSuspension();
        Schedule();
    }

    private void Schedule()
    {
        if (!Active || IsPreparing || _interactionPending) return;
        var needsFrame = _candidates.Values.Any(candidate => candidate.State == Readiness.AwaitingFrame);
        if (needsFrame && _renderingHandler == null)
        {
            var generation = _dispatchGeneration;
            EventHandler? handler = null;
            handler = (_, _) => OnRendering(generation, handler!);
            _renderingHandler = handler;
            CompositionTarget.Rendering += handler;
        }
        // Rendering subscription can itself post work and re-enter through Dispatcher hooks.
        if (!Active || IsPreparing || _interactionPending || _pendingDispatch != null ||
            !_candidates.Values.Any(candidate => candidate.State == Readiness.Ready)) return;
        var pending = new PreparationDispatch();
        _pendingDispatch = pending;
        try
        {
            var operation = _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                (Action)(() => PrepareOne(pending)));
            if (ReferenceEquals(_pendingDispatch, pending) && operation.Status == DispatcherOperationStatus.Pending)
                pending.Operation = operation;
            else
            {
                if (ReferenceEquals(_pendingDispatch, pending)) _pendingDispatch = null;
                operation.Abort();
            }
        }
        catch
        {
            if (ReferenceEquals(_pendingDispatch, pending)) _pendingDispatch = null;
            throw;
        }
    }

    private void OnRendering(long generation, EventHandler handler)
    {
        // An unsubscribed delegate may still occur in an already captured multicast invocation.
        if (generation != _dispatchGeneration || !ReferenceEquals(_renderingHandler, handler)) return;
        CompositionTarget.Rendering -= handler;
        _renderingHandler = null;
        if (!Active || _interactionPending) return;
        foreach (var candidate in _candidates.Values)
            if (candidate.State == Readiness.AwaitingFrame) candidate.State = Readiness.Ready;
        // Never create native resources or publish a cover inside the Rendering callback.
        Schedule();
    }

    private void PrepareOne(PreparationDispatch pending)
    {
        if (!ReferenceEquals(_pendingDispatch, pending)) return;
        _pendingDispatch = null;
        if (!Active || IsPreparing || _interactionPending) return;
        Candidate? candidate = null;
        foreach (var entry in _candidates.Values)
            if (entry.State == Readiness.Ready && (candidate == null || entry.Version < candidate.Version))
                candidate = entry;
        if (candidate == null) return;

        var version = Version;
        candidate.State = Readiness.Sleeping;
        _preparingCandidate = candidate;
        IsPreparing = true;
        try
        {
            if (!_canPrepare() || !CanContinue(candidate, version))
            {
                TraceState("deferred-unavailable", candidate.Key);
                return;
            }
            // A native callback may pump messages or wait for publication. Pause only the
            // optional body preloader before entering it; current preview demand keeps running.
            UpdateContentSuspension();
            if (!CanContinue(candidate, version)) return;
            if (!_graphicsAttempted)
            {
                _graphicsAttempted = true;
                try { _prepareGraphics(); }
                catch (Exception ex) { Trace.WriteLine($"PaperTodo graphics prewarm failed: {ex}"); }
                if (!CanContinue(candidate, version)) return;
            }
            var outcome = _prepareQueue(candidate.Key);
            TraceState(outcome.ToString(), candidate.Key);
            // A callback may pump a newer Request, Wake, input, or shutdown. Its result cannot
            // erase that newer interest or resurrect cancelled work.
            if (CanContinue(candidate, version) && outcome != EdgePrewarmOutcome.Deferred)
                _candidates.Remove(candidate.Key);
        }
        catch (Exception ex)
        {
            // Preparation is optional: fail once and wait for fresh external interest.
            if (CanContinue(candidate, version)) _candidates.Remove(candidate.Key);
            Trace.WriteLine($"PaperTodo queue prewarm failed ({candidate.Key}): {ex}");
        }
        finally
        {
            IsPreparing = false;
            _preparingCandidate = null;
            // Do not undo an interaction pause that was established by a pumped callback.
            UpdateContentSuspension();
            Schedule();
        }
    }

    private bool IsCurrent(Candidate candidate) =>
        _candidates.TryGetValue(candidate.Key, out var current) && ReferenceEquals(current, candidate);

    private bool CanContinue(Candidate candidate, long version) =>
        Active && !_interactionPending && Version == version && IsCurrent(candidate);

    private void UpdateContentSuspension()
    {
        var suspended = Active && (IsPreparing || _interactionPending);
        if (_contentSuspended == suspended) return;
        _contentSuspended = suspended;
        try { _suspendContent(suspended); }
        catch (Exception ex) { Trace.WriteLine($"PaperTodo content prewarm suspension failed: {ex}"); }
    }

    private void CancelDispatch()
    {
        _dispatchGeneration++;
        var pending = _pendingDispatch;
        var rendering = _renderingHandler;
        _pendingDispatch = null;
        _renderingHandler = null;
        if (rendering != null) CompositionTarget.Rendering -= rendering;
        // Detach only the old work before Abort: OperationAborted may schedule new interest.
        pending?.Operation?.Abort();
    }

    private void ClearPending()
    {
        _candidates.Clear();
        _interactionPending = false;
        _interactionDelay.Stop();
        CancelDispatch();
        UpdateContentSuspension();
    }

    [Conditional("DEBUG")]
    private void TraceState(string phase, string? queueKey = null) =>
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"prewarm.coordinator phase={phase} queue={queueKey ?? "<none>"} version={Version} pending={PendingCount} deferred={DeferredCount}");

    private void OnShutdown(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        Version++;
        ClearPending();
        _interactionDelay.Tick -= OnInteractionDelay;
        _dispatcher.ShutdownStarted -= OnShutdown;
    }
}
