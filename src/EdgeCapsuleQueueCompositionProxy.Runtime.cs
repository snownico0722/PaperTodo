using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed record EdgeCapsuleQueueCompositionProxyMember(
    PaperWindow Window,
    EdgeCapsuleQueueProxyMemberPlan Plan,
    IntPtr SourceHandle);

/// <summary>
/// Dispatcher-shared DirectComposition device, queue-scoped output hosts and
/// generation-owned translation visuals. A queue host may have one current
/// generation and one staged successor.
/// </summary>
internal sealed partial class EdgeCapsuleQueueCompositionProxy : IDisposable
{
    private sealed class VisualState : IDisposable
    {
        public required EdgeCapsuleQueueCompositionProxyMember Member
            { get; init; }
        public required IntPtr PresentedSourceHandle { get; init; }
        public required DeviceScreenRect SourceBounds { get; init; }
        public required IUnknown Surface { get; init; }
        public required IDCompositionVisual Visual { get; init; }
        public required float StartOffsetX { get; set; }
        public required float StartOffsetY { get; set; }
        public required float TargetOffsetX { get; init; }
        public required float TargetOffsetY { get; init; }

        public IDCompositionAnimation? OffsetXAnimation { get; set; }
        public IDCompositionAnimation? OffsetYAnimation { get; set; }

        public void Dispose()
        {
            OffsetYAnimation?.Dispose();
            OffsetXAnimation?.Dispose();
            Visual.Dispose();
            Surface.Dispose();
        }
    }

    private sealed class StaticCoverResources : IDisposable
    {
        public required IDCompositionVisual Root { get; init; }
        public List<IUnknown> Surfaces { get; } = new();
        public List<IDCompositionVisual> Visuals { get; } = new();

        public void Dispose()
        {
            for (var index = Visuals.Count - 1; index >= 0; index--)
            {
                try { Visuals[index].Dispose(); } catch { }
            }
            Visuals.Clear();
            for (var index = Surfaces.Count - 1; index >= 0; index--)
            {
                try { Surfaces[index].Dispose(); } catch { }
            }
            Surfaces.Clear();
            try { Root.Dispose(); } catch { }
        }
    }

    private sealed class QueueHost : IDisposable
    {
        private readonly SharedRuntime _runtime;
        private bool _disposed;

        private QueueHost(
            SharedRuntime runtime,
            string queueKey,
            EdgeCapsuleQueueProxyWindow window,
            IDCompositionTarget target)
        {
            _runtime = runtime;
            QueueKey = queueKey;
            Window = window;
            Target = target;
        }

        public string QueueKey { get; private set; }
        public EdgeCapsuleQueueProxyWindow Window { get; }
        public IDCompositionTarget Target { get; }
        public EdgeCapsuleQueueCompositionProxy? Current { get; private set; }
        public EdgeCapsuleQueueCompositionProxy? Staged { get; private set; }
        public bool HasOwner => Current != null || Staged != null;
        public bool IsAvailable =>
            !_disposed &&
            !HasOwner &&
            Window.Handle != IntPtr.Zero;

        public static QueueHost? TryCreate(
            SharedRuntime runtime,
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds)
        {
            if (initialBounds.IsEmpty)
            {
                return null;
            }

            QueueHost? host = null;
            EdgeCapsuleQueueProxyWindow? window = null;
            IDCompositionTarget? target = null;
            try
            {
                window = EdgeCapsuleQueueProxyWindow.TryCreate(
                    initialBounds,
                    topmost,
                    point => host?.Current?.ContainsVisual(point) == true,
                    input => host?.Current?.HandleInteractionRequested(input),
                    () => host?.Current?.HandleEnvironmentChanged(),
                    () => host?.Current?.HandleCompositionPaint(),
                    () => host?.Current?.HandleOutputLost());
                if (window == null)
                {
                    return null;
                }

                runtime.Device.CreateTargetForHwnd(
                    window.Handle,
                    topmost: true,
                    out target).CheckError();
                host = new QueueHost(runtime, queueKey, window, target);
                window = null;
                target = null;
                return host;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "Edge capsule queue compositor host creation failed. Queue={0}; Exception={1}",
                    queueKey,
                    ex);
                target?.Dispose();
                window?.Dispose();
                return null;
            }
        }

        public bool AssignQueue(string queueKey)
        {
            if (_disposed ||
                Current != null ||
                Staged != null ||
                string.IsNullOrWhiteSpace(queueKey))
            {
                return false;
            }
            QueueKey = queueKey;
            return true;
        }

        public bool ReleaseQueue()
        {
            if (!IsAvailable)
            {
                return false;
            }
            QueueKey = string.Empty;
            return true;
        }

        public bool CanStage(
            EdgeCapsuleQueueCompositionProxy? predecessor) =>
            !_disposed &&
            Window.Handle != IntPtr.Zero &&
            Staged == null &&
            (predecessor == null
                ? Current == null
                : ReferenceEquals(Current, predecessor));

        public bool TryStage(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (!CanStage(predecessor))
            {
                return false;
            }
            Staged = proxy;
            return true;
        }

        public bool Promote(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (_disposed ||
                !ReferenceEquals(Staged, proxy) ||
                (predecessor == null
                    ? Current != null
                    : !ReferenceEquals(Current, predecessor)))
            {
                return false;
            }

            Current = proxy;
            Staged = null;
            return true;
        }

        public bool RollbackPromotion(
            EdgeCapsuleQueueCompositionProxy proxy,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            if (_disposed)
            {
                return false;
            }

            if (ReferenceEquals(Staged, proxy))
            {
                Staged = null;
            }

            if (ReferenceEquals(Current, proxy))
            {
                Current = predecessor;
            }
            else if (predecessor != null &&
                     !ReferenceEquals(Current, predecessor))
            {
                return false;
            }

            if (Current == null && Staged == null)
            {
                Window.Hide();
            }
            return true;
        }

        public void Detach(EdgeCapsuleQueueCompositionProxy proxy)
        {
            if (ReferenceEquals(Staged, proxy))
            {
                Staged = null;
            }
            if (ReferenceEquals(Current, proxy))
            {
                Current = null;
            }
            if (Current == null && Staged == null)
            {
                Window.Hide();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Current = null;
            Staged = null;
            try { Target.SetRoot(null!).CheckError(); } catch { }
            try
            {
#if DEBUG
                using (var edgeJournalNative = EdgeDiagnosticObservation.Begin("native.dcomp-commit"))
#endif
                    _runtime.Device.Commit().CheckError();
            }
            catch { }
            try { Target.Dispose(); } catch { }
            try { Window.Dispose(); } catch { }
        }
    }

    private sealed class SharedRuntime : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<string, QueueHost> _hosts =
            new(StringComparer.Ordinal);
        private readonly Stack<QueueHost> _spareHosts = new();
        private bool _disposed;
        private bool _invalid;

        internal SharedRuntime(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            var desktopDeviceId = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(
                IntPtr.Zero,
                ref desktopDeviceId,
                out var devicePointer));
            Device = new IDCompositionDesktopDevice(devicePointer);
            _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        }

        internal IDCompositionDesktopDevice Device { get; }
        internal bool IsUsable => !_disposed && !_invalid;

        internal void PrewarmOutputHost()
        {
            _dispatcher.VerifyAccess();
            if (!IsUsable || _spareHosts.Count > 0)
            {
                return;
            }

            var offscreen =
                new DeviceScreenRect(-32000, -32000, -31996, -31996);
            var host = QueueHost.TryCreate(
                this,
                string.Empty,
                topmost: true,
                offscreen);
            if (host != null)
            {
                _spareHosts.Push(host);
            }
        }

        private QueueHost? TakeOrCreateHost(
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds)
        {
            QueueHost? host = null;
            while (_spareHosts.Count > 0 && host == null)
            {
                var candidate = _spareHosts.Pop();
                if (candidate.IsAvailable &&
                    candidate.AssignQueue(queueKey))
                {
                    host = candidate;
                }
                else
                {
                    try { candidate.Dispose(); } catch { }
                }
            }
            return host ?? QueueHost.TryCreate(
                this,
                queueKey,
                topmost,
                initialBounds);
        }

        internal QueueHost? TryAcquire(
            string queueKey,
            bool topmost,
            DeviceScreenRect initialBounds,
            EdgeCapsuleQueueCompositionProxy? predecessor)
        {
            _dispatcher.VerifyAccess();
            if (!IsUsable)
            {
                return null;
            }
            if (!_hosts.TryGetValue(queueKey, out var host))
            {
                if (predecessor != null)
                {
                    return null;
                }
                host = TakeOrCreateHost(
                    queueKey,
                    topmost,
                    initialBounds);
                if (host == null)
                {
                    return null;
                }
                _hosts[queueKey] = host;
            }
            return host.CanStage(predecessor) ? host : null;
        }

        internal void Release(
            QueueHost host,
            EdgeCapsuleQueueCompositionProxy proxy,
            bool broken)
        {
            _dispatcher.VerifyAccess();
            host.Detach(proxy);
            if (_disposed)
            {
                return;
            }

            if (broken)
            {
                InvalidateAndDrain(host);
                return;
            }

            if (_invalid)
            {
                RetireInvalidHostIfIdle(host);
                TryDisposeDrainedRuntime();
                return;
            }

            ReturnIdleHost(host);
        }

        private void InvalidateAndDrain(QueueHost failedHost)
        {
            if (!_invalid)
            {
                _invalid = true;
                SharedRuntimes.Remove(_dispatcher);
            }

            while (_spareHosts.Count > 0)
            {
                try { _spareHosts.Pop().Dispose(); } catch { }
            }

            // A DComp device is shared by every monitor/edge queue on this dispatcher. Destroying
            // their output HWNDs here would strand still-cloaked real sources. Retire only idle
            // targets; active owners are told to reveal their own sources and drain themselves.
            foreach (var cached in _hosts.Values.ToArray())
            {
                RetireInvalidHostIfIdle(cached);
            }

            var affected = _hosts.Values
                .SelectMany(cached => new[] { cached.Current, cached.Staged })
                .Where(proxy => proxy != null)
                .Cast<EdgeCapsuleQueueCompositionProxy>()
                .Distinct()
                .ToArray();
            foreach (var proxy in affected)
            {
                proxy.HandleSharedRuntimeLost();
            }

            RetireInvalidHostIfIdle(failedHost);
            TryDisposeDrainedRuntime();
        }

        private void RetireInvalidHostIfIdle(QueueHost host)
        {
            if (!_invalid || host.HasOwner)
            {
                return;
            }

            foreach (var pair in _hosts
                         .Where(pair => ReferenceEquals(pair.Value, host))
                         .ToArray())
            {
                _hosts.Remove(pair.Key);
            }
            try { host.Dispose(); } catch { }
        }

        private void TryDisposeDrainedRuntime()
        {
            if (!_invalid || _disposed || _hosts.Count != 0)
            {
                return;
            }

            _disposed = true;
            _dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
            try { Device.Dispose(); } catch { }
        }

        internal void ReturnIdleHost(QueueHost host)
        {
            _dispatcher.VerifyAccess();
            if (_disposed || !host.IsAvailable)
            {
                return;
            }

            // Queue identity is an active-session concern, not a permanent cache key. Retain at
            // most one dispatcher-wide spare; this preserves a warm DComp target without growing
            // one transparent output HWND per monitor/edge queue.
            if (_hosts.TryGetValue(host.QueueKey, out var cached) &&
                ReferenceEquals(cached, host))
            {
                _hosts.Remove(host.QueueKey);
            }
            if (_spareHosts.Count == 0 && host.ReleaseQueue())
            {
                _spareHosts.Push(host);
            }
            else
            {
                try { host.Dispose(); } catch { }
            }
        }

        private void OnDispatcherShutdownStarted(
            object? sender,
            EventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
            foreach (var host in _hosts.Values.ToArray())
            {
                try { host.Dispose(); } catch { }
            }
            _hosts.Clear();
            while (_spareHosts.Count > 0)
            {
                try { _spareHosts.Pop().Dispose(); } catch { }
            }
            try { Device.Dispose(); } catch { }
        }
    }

    private static readonly ConditionalWeakTable<Dispatcher, SharedRuntime>
        SharedRuntimes = new();
    private static long _nextSessionOrdinal;
}
