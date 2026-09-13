#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

/// <summary>
/// Opt-in observations of existing callbacks. Never subscribes to Rendering, advances a
/// transition, changes a frame, or samples geometry by forcing layout/native synchronization.
/// The same source and event schema are used by historical diagnostic builds.
/// </summary>
internal static class EdgeDiagnosticObservation
{
    private sealed class Identity
    {
        internal readonly long Id = Interlocked.Increment(ref _nextId);
        internal long Transition;
        internal bool Mapped;
        internal long NativeHandle;
    }

    private static readonly ConditionalWeakTable<object, Identity> Identities = new();
    private static long _nextId;
    private static bool _inputInstalled;
    private static readonly bool DetailedEnabled = Environment.GetEnvironmentVariable("PAPERTODO_EDGE_OBSERVATIONS") != "0";
    private static bool Enabled => EdgeDiagnosticJournal.Enabled && DetailedEnabled;
    [ThreadStatic] private static long _parentSpan;
    [ThreadStatic] private static long _frameSpan;

    internal static long Id(object? owner) => owner == null ? 0 :
        Identities.GetValue(owner, static _ => new Identity()).Id;

    internal readonly struct Scope : IDisposable
    {
        private readonly long _id, _parent, _started, _allocated;
        private readonly string? _stage;
        private readonly int _gen0, _gen1, _gen2;
        internal Scope(string stage, object? owner, long value1, long value2)
        {
            _id = Interlocked.Increment(ref _nextId);
            _parent = _parentSpan;
            _started = Stopwatch.GetTimestamp();
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _gen0 = GC.CollectionCount(0); _gen1 = GC.CollectionCount(1); _gen2 = GC.CollectionCount(2);
            _stage = stage;
            EdgeCapsulePerformanceDiagnostics.Event("span.begin", _id,
                Id(owner), _parent, value1, value2, detail: stage);
            _parentSpan = _id;
        }
        public void Dispose()
        {
            if (_stage == null) return;
            var ended = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            _parentSpan = _parent;
            EdgeCapsulePerformanceDiagnostics.Event("span.end", _id,
                ended - _started, allocated, GC.CollectionCount(0) - _gen0,
                ((long)(GC.CollectionCount(1) - _gen1) << 32) | (uint)(GC.CollectionCount(2) - _gen2),
                detail: _stage);
        }
    }

    internal static Scope Begin(string stage, object? owner = null, long value1 = 0, long value2 = 0) =>
        Enabled ? new Scope(stage, owner, value1, value2) : default;

    internal static void MapPresenter(object presenter, string paperId)
    {
        if (!Enabled) return;
        var identity = Identities.GetValue(presenter, static _ => new Identity());
        if (identity.Mapped) return;
        identity.Mapped = true;
        EdgeCapsulePerformanceDiagnostics.Event("presenter.map", identity.Id, detail: paperId);
    }

    internal static void Rendering(object scheduler, EventArgs args, int presenters, bool ticking, int pending)
    {
        if (!Enabled) return;
        EdgeCapsulePerformanceDiagnostics.Event("render.callback", Id(scheduler),
            args is RenderingEventArgs rendering ? rendering.RenderingTime.Ticks : -1,
            presenters, pending, ticking ? 1 : 0);
    }

    internal static void Subscription(object scheduler, bool subscribed)
    {
        if (!Enabled) return;
        EdgeCapsulePerformanceDiagnostics.Event("render.subscription", Id(scheduler), subscribed ? 1 : 0);
    }

    internal readonly struct FrameScope : IDisposable
    {
        private readonly long _previous;
        private readonly bool _enabled;
        internal FrameScope(long previous) { _previous = previous; _enabled = true; }
        public void Dispose() { if (_enabled) _frameSpan = _previous; }
    }

    internal static FrameScope FrameAccepted(object scheduler, TimeSpan? renderingTime, string source, int presenters, int pending)
    {
        if (!Enabled) return default;
        var previous = _frameSpan;
        _frameSpan = Interlocked.Increment(ref _nextId);
        EdgeCapsulePerformanceDiagnostics.Event("frame.dispatch", _frameSpan,
            Id(scheduler), renderingTime?.Ticks ?? -1, presenters, pending, detail: source);
        return new FrameScope(previous);
    }

    internal static void TransitionChanged(object presenter, EdgeCapsuleTransition? previous, EdgeCapsuleTransition? next)
    {
        if (!Enabled || previous == next) return;
        var identity = Identities.GetValue(presenter, static _ => new Identity());
        if (next is { } transition)
        {
            if (previous is { } old && old.Start == transition.Start && old.Target == transition.Target &&
                old.DurationTimestampTicks == transition.DurationTimestampTicks && identity.Transition != 0)
            {
                EdgeCapsulePerformanceDiagnostics.Event("transition.rebase", identity.Transition,
                    identity.Id, transition.StartedAtTimestamp, transition.DurationTimestampTicks, (long)transition.Reason);
                return;
            }
            if (identity.Transition != 0)
                EdgeCapsulePerformanceDiagnostics.Event("transition.replaced", identity.Transition, identity.Id);
            identity.Transition = Interlocked.Increment(ref _nextId);
            EdgeCapsulePerformanceDiagnostics.Event("transition.begin", identity.Transition,
                identity.Id, transition.StartedAtTimestamp, transition.DurationTimestampTicks, (long)transition.Reason);
            Frame("transition.from", identity.Id, identity.Transition, transition.Start);
            Frame("transition.to", identity.Id, identity.Transition, transition.Target.ToFrame());
        }
        else if (identity.Transition != 0)
        {
            // Null can mean completion OR cancellation. The surrounding scope/endpoint events
            // distinguish them; this event alone does not assert that the endpoint was displayed.
            EdgeCapsulePerformanceDiagnostics.Event("transition.cleared", identity.Transition, identity.Id);
            identity.Transition = 0;
        }
    }

    internal static void Applied(object presenter, EdgeCapsulePresentationFrame frame)
    {
        if (!Enabled) return;
        var identity = Identities.GetValue(presenter, static _ => new Identity());
        Frame("shape.applied", identity.Id, identity.Transition, frame);
    }

    private static void Frame(string kind, long presenter, long transition, EdgeCapsulePresentationFrame frame)
    {
        EdgeCapsulePerformanceDiagnostics.Event(kind, presenter,
            transition, _frameSpan, Pack(frame.Bounds.Left, frame.Bounds.Top), Pack(frame.Bounds.Width, frame.Bounds.Height),
            frame.Opacity, frame.ContentOpacity);
        EdgeCapsulePerformanceDiagnostics.Event("shape.host", presenter,
            transition, (long)frame.Surface, Pack(frame.HostBounds.Left, frame.HostBounds.Top), Pack(frame.HostBounds.Width, frame.HostBounds.Height),
            frame.DpiScaleX, frame.DpiScaleY, detail: kind);
        EdgeCapsulePerformanceDiagnostics.Event("shape.hit", presenter,
            transition, (frame.Visible ? 1 : 0) | (frame.IsHitTestVisible ? 2 : 0) | (frame.OutlineVisible ? 4 : 0),
            Pack(frame.InteractiveBounds.Left, frame.InteractiveBounds.Top), Pack(frame.InteractiveBounds.Width, frame.InteractiveBounds.Height),
            frame.BodyWindowWidthDevice, frame.MaximumCloseWidthDip, detail: kind);
    }

    internal static void HostApplied(object host, Window window, CornerRadius corners)
    {
        if (!Enabled) return;
        var identity = Identities.GetValue(host, static _ => new Identity());
        if (identity.NativeHandle == 0)
        {
            identity.NativeHandle = new WindowInteropHelper(window).Handle.ToInt64();
            EdgeCapsulePerformanceDiagnostics.Event("host.hwnd", identity.Id, identity.NativeHandle);
        }
        EdgeCapsulePerformanceDiagnostics.Event("host.corners", identity.Id,
            _frameSpan, BitConverter.DoubleToInt64Bits(corners.BottomLeft), BitConverter.DoubleToInt64Bits(corners.BottomRight),
            number1: corners.TopLeft, number2: corners.TopRight);
    }

    internal static long Pack(int first, int second) => ((long)first << 32) | (uint)second;

    internal static void InstallInput()
    {
        if (!Enabled || _inputInstalled) return;
        _inputInstalled = true;
        ComponentDispatcher.ThreadPreprocessMessage += OnMessage;
        EdgeCapsulePerformanceDiagnostics.Event("input.installed");
    }

    internal static void RemoveInput()
    {
        if (!_inputInstalled) return;
        ComponentDispatcher.ThreadPreprocessMessage -= OnMessage;
        _inputInstalled = false;
    }

    private static void OnMessage(ref MSG message, ref bool handled)
    {
        // Observe the existing thread message path; never alter handled or query live HWND state.
        if (message.message is >= 0x0200 and <= 0x020E or 0x00A0 or 0x02A3)
            EdgeCapsulePerformanceDiagnostics.Event("input.native", message.hwnd.ToInt64(),
                message.message, message.lParam.ToInt64(), message.wParam.ToInt64(), message.time,
                message.pt_x, message.pt_y);
    }
}
#endif
