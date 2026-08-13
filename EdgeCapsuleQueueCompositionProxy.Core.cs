using System.Diagnostics;
using System.Windows.Threading;
using Vortice.DirectComposition;

namespace PaperTodo;

/// <summary>
/// One compositor generation for one monitor/edge queue. The queue host, output HWND and target are
/// reused. A successor builds an off-target root, atomically replaces its predecessor on that same
/// HWND, and inherits the still-cloaked real sources.
/// </summary>
internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private readonly EdgeCapsuleQueueProxyPlan _plan;
    private readonly IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> _members;
    private readonly EdgeCapsuleQueueCompositionProxy? _predecessor;
    private readonly SharedRuntime _runtime;
    private readonly QueueHost _host;
    private readonly EdgeCapsuleQueueProxyWindow _window;
    private readonly IDCompositionDesktopDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _root;
    private readonly DeviceScreenRect _outputBounds;
    private readonly List<VisualState> _visuals = new();
    private readonly HashSet<IntPtr> _cloakedRealSourceHandles = new();
    private readonly DispatcherTimer _sampleTimer;
    private readonly DispatcherTimer _completionTimer;
    private readonly Action<DeviceScreenPoint, int> _interactionRequested;
    private readonly Action _environmentChanged;
    private readonly Func<EdgeCapsuleQueueCompositionProxy, bool> _coverReady;
    private readonly Action<EdgeCapsuleQueueCompositionProxy, bool> _completed;
    private readonly long _sessionOrdinal;
    private long _animationStartedAtTimestamp;
    private long _heldAtTimestamp;
    private bool _sourcesReleased;
    private bool _realEndpointMutationStarted;
    private bool _abortQueued;
    private bool _completionRetrySuccess = true;
    private int _completionRetryCount;
    private bool _finishing;
    private bool _disposed;
    private bool _starting = true;
    private bool _completionPendingDuringStart;
    private bool _pendingStartCompletionSuccess = true;
    private bool _coverLost;
    private bool _successorHeld;
    private bool _completionPendingDuringSuccessorHold;
    private bool _pendingSuccessorCompletionSuccess = true;
    private bool _coverPublished;
    private bool _targetRootInstalled;
    private bool _handoffRetirementPending;
    private bool _disposeAfterHandoffRetirement;
#if DEBUG
    private long _handoffRetirementStartedAtTimestamp;
#endif

    private EdgeCapsuleQueueCompositionProxy(
        long sessionOrdinal,
        EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members,
        EdgeCapsuleQueueCompositionProxy? predecessor,
        SharedRuntime runtime,
        QueueHost host,
        IDCompositionVisual root,
        DeviceScreenRect outputBounds,
        Action<DeviceScreenPoint, int> interactionRequested,
        Action environmentChanged,
        Func<EdgeCapsuleQueueCompositionProxy, bool> coverReady,
        Action<EdgeCapsuleQueueCompositionProxy, bool> completed)
    {
        _plan = plan;
        _members = members;
        _predecessor = predecessor;
        _runtime = runtime;
        _host = host;
        _window = host.Window;
        _device = runtime.Device;
        _target = host.Target;
        _root = root;
        _outputBounds = outputBounds;
        _interactionRequested = interactionRequested;
        _environmentChanged = environmentChanged;
        _coverReady = coverReady;
        _completed = completed;
        _sessionOrdinal = sessionOrdinal;

        var dispatcher = members[0].Window.Dispatcher;
        _sampleTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Input,
            OnSampleTimerTick,
            dispatcher);
        _completionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(plan.DurationMilliseconds + 34),
            DispatcherPriority.Render,
            OnCompletionTimerTick,
            dispatcher)
        {
            IsEnabled = false
        };
    }

    public string QueueKey => _plan.QueueKey;
    public IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> Members => _members;
    public long SessionOrdinal => _sessionOrdinal;
    public bool IsColdSession => _sessionOrdinal == 1;
    public bool CoverLost => _coverLost;
    public bool CoverPublished => _coverPublished;
    public IntPtr OutputHandle => _disposed ? IntPtr.Zero : _window.Handle;

    internal static void Prewarm(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => Prewarm(dispatcher)));
            return;
        }
        if (TryGetRuntime(dispatcher, out var runtime))
        {
            runtime.PrewarmOutputHost();
        }
    }

    internal static void PrewarmQueue(
        Dispatcher dispatcher,
        string queueKey,
        bool topmost,
        DeviceScreenRect initialBounds)
    {
        if (dispatcher.HasShutdownStarted ||
            string.IsNullOrWhiteSpace(queueKey) ||
            initialBounds.IsEmpty)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => PrewarmQueue(
                    dispatcher,
                    queueKey,
                    topmost,
                    initialBounds)));
            return;
        }
        if (TryGetRuntime(dispatcher, out var runtime))
        {
            runtime.PrewarmQueue(
                queueKey,
                topmost,
                initialBounds);
        }
    }

    private static bool TryGetRuntime(
        Dispatcher dispatcher,
        out SharedRuntime runtime)
    {
        try
        {
            runtime = SharedRuntimes.GetValue(
                dispatcher,
                static key => new SharedRuntime(key));
            return runtime.IsUsable;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule shared DirectComposition runtime creation failed. Exception={0}",
                ex);
            runtime = null!;
            return false;
        }
    }

    public static long ReserveSessionOrdinal() =>
        Interlocked.Increment(ref _nextSessionOrdinal);

    public static EdgeCapsuleQueueCompositionProxy? TryCreate(
        long sessionOrdinal,
        EdgeCapsuleQueueProxyPlan plan,
        IReadOnlyList<EdgeCapsuleQueueCompositionProxyMember> members,
        EdgeCapsuleQueueCompositionProxy? predecessor,
        Action<DeviceScreenPoint, int> interactionRequested,
        Action environmentChanged,
        Func<EdgeCapsuleQueueCompositionProxy, bool> coverReady,
        Action<EdgeCapsuleQueueCompositionProxy, bool> completed)
    {
        if (members.Count == 0 ||
            members.Count != plan.Members.Count ||
            members.Any(member => member.SourceHandle == IntPtr.Zero) ||
            (predecessor != null &&
             !string.Equals(
                 predecessor.QueueKey,
                 plan.QueueKey,
                 StringComparison.Ordinal)) ||
            !TryGetRuntime(
                members[0].Window.Dispatcher,
                out var runtime))
        {
            return null;
        }

        var requestedBounds =
            EdgeCapsuleQueueProxyGeometry.OutputBounds(plan.Envelope);
        var outputBounds = requestedBounds;
        if (predecessor != null)
        {
            // Repositioning the target HWND would move the still-visible predecessor root. A
            // successor is therefore admitted only when the current queue envelope already owns
            // the requested physical area. Normal queue browse transactions include every affected
            // member and satisfy this invariant.
            if (!EdgeCapsuleQueueProxyGeometry.Contains(
                    predecessor._outputBounds,
                    requestedBounds))
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"proxy.successor phase=admission queue={plan.QueueKey} " +
                    $"from={predecessor.SessionOrdinal} outcome=rejected " +
                    $"reason=output-growth current={predecessor._outputBounds.Left}," +
                    $"{predecessor._outputBounds.Top},{predecessor._outputBounds.Width}x" +
                    $"{predecessor._outputBounds.Height} requested={requestedBounds.Left}," +
                    $"{requestedBounds.Top},{requestedBounds.Width}x{requestedBounds.Height}");
#endif
                return null;
            }
            outputBounds = predecessor._outputBounds;
        }

        var host = runtime.TryAcquire(
            plan.QueueKey,
            plan.Topmost,
            outputBounds,
            predecessor);
        if (host == null)
        {
            return null;
        }

        IDCompositionVisual? root = null;
        EdgeCapsuleQueueCompositionProxy? proxy = null;
        try
        {
            runtime.Device.CreateVisual(
                out IDCompositionVisual2 rootVisual).CheckError();
            root = rootVisual;
            proxy = new EdgeCapsuleQueueCompositionProxy(
                sessionOrdinal,
                plan,
                members,
                predecessor,
                runtime,
                host,
                root,
                outputBounds,
                interactionRequested,
                environmentChanged,
                coverReady,
                completed);
            if (!host.TryStage(proxy, predecessor))
            {
                throw new InvalidOperationException(
                    "The queue compositor host cannot stage this generation.");
            }
            root = null;
            return proxy;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule queue DirectComposition proxy creation failed. Queue={0}; Exception={1}",
                plan.QueueKey,
                ex);
            root?.Dispose();
            if (proxy != null)
            {
                host.Detach(proxy);
            }
            return null;
        }
    }
}
