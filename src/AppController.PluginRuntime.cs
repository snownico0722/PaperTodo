using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string PluginRuntimeCapability = "runtime";
    private static readonly TimeSpan[] PluginRuntimeRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10)
    ];
    private static readonly TimeSpan PluginRuntimeStableFailureResetAfter =
        TimeSpan.FromSeconds(30);

    private enum PluginRuntimeState
    {
        Stopped,
        Starting,
        Running,
        Backoff,
        Failed,
        Disposing
    }

    private static class PluginRuntimeTransitions
    {
        public static PluginRuntimeState BeginStart(PluginRuntimeState state) =>
            state == PluginRuntimeState.Stopped
                ? PluginRuntimeState.Starting
                : state;

        public static PluginRuntimeState StartSucceeded(PluginRuntimeState state) =>
            state == PluginRuntimeState.Starting
                ? PluginRuntimeState.Running
                : state;

        public static PluginRuntimeState StartFailed(int failureCount, int retryCount) =>
            failureCount <= retryCount
                ? PluginRuntimeState.Backoff
                : PluginRuntimeState.Failed;

        public static PluginRuntimeState RetryElapsed(PluginRuntimeState state) =>
            state == PluginRuntimeState.Backoff
                ? PluginRuntimeState.Stopped
                : state;

        public static bool RuntimeMatches(Guid currentRuntimeId, Guid callbackRuntimeId) =>
            currentRuntimeId == callbackRuntimeId;
    }

    private sealed class PluginRuntimeLifetime
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool TryDeactivate() =>
            Interlocked.Exchange(ref _active, 0) != 0;
    }

    private sealed class PluginRuntimeOwnershipCanceledException(string message)
        : Exception(message);

    private sealed class PluginRuntimeStateVersionException(string message)
        : Exception(message);

    private sealed class PluginRuntimeSlot
    {
        public required string ProviderId { get; init; }
        public PaperBodyPluginDescriptor? Descriptor { get; set; }
        public PluginRuntimeState State { get; set; }
        public Guid RuntimeId { get; set; }
        public PluginRuntimeLifetime? Lifetime { get; set; }
        public PluginRuntimeLease? Lease { get; set; }
        public int FailureCount { get; set; }
        public int RetryGeneration { get; set; }
        public bool RestartRequested { get; set; }
        public DateTimeOffset RunningSinceUtc { get; set; }
    }

    private sealed class PluginRuntimeLease : IDisposable
    {
        private int _disposed;

        public required Guid RuntimeId { get; init; }
        public required string ProviderId { get; init; }
        public required PluginRuntimeLifetime Lifetime { get; init; }
        public required PaperPluginRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperPluginRuntimeSettingsApi Settings { get; init; }
        public required PaperPluginRuntimeStateApi State { get; init; }
        public required PaperPluginRuntimePapersApi Papers { get; init; }
        public required PaperPluginRuntimeGlobalTopBarApi GlobalTopBar { get; init; }
        public required PaperPluginRuntimeGlobalShortcutApi GlobalShortcuts { get; init; }
        public IDisposable? Runtime { get; init; }
        public IPaperBodyPlugin? NativeFactory { get; init; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Lifetime.TryDeactivate();
            try { Runtime?.Dispose(); } catch { }
            try { Papers.Dispose(); } catch { }
            try { State.Dispose(); } catch { }
            try { Settings.Dispose(); } catch { }
            try { GlobalShortcuts.Dispose(); } catch { }
            try { GlobalTopBar.Dispose(); } catch { }
            try { Workspace.Dispose(); } catch { }
            if (NativeFactory is IDisposable disposable &&
                !ReferenceEquals(Runtime, NativeFactory))
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    private readonly Dictionary<string, PluginRuntimeSlot> _pluginRuntimeSlots =
        new(StringComparer.Ordinal);
    private bool _pluginRuntimeReconciliationEnabled;
    private bool _pluginRuntimeDisposing;

    internal void EnablePluginRuntimeReconciliation()
    {
        if (_pluginRuntimeDisposing || IsExiting)
        {
            return;
        }

        _pluginRuntimeReconciliationEnabled = true;
        RefreshPluginShortcuts();
        ReconcilePluginRuntimes();
    }

    internal void ReconcilePluginRuntimes()
    {
        if (!_pluginRuntimeReconciliationEnabled ||
            _pluginRuntimeDisposing ||
            IsExiting)
        {
            return;
        }

        var desired = PaperBodyPlugins.Descriptors
            .Where(DeclaresPluginRuntime)
            .Where(descriptor => HasEntityPluginPaper(descriptor.Id))
            .ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
        var statusChanged = false;

        foreach (var providerId in _pluginRuntimeSlots.Keys
                     .Where(providerId => !desired.ContainsKey(providerId))
                     .ToArray())
        {
            RemovePluginRuntimeSlot(providerId);
            statusChanged = true;
        }

        foreach (var descriptor in desired.Values)
        {
            if (!_pluginRuntimeSlots.TryGetValue(descriptor.Id, out var slot))
            {
                slot = new PluginRuntimeSlot
                {
                    ProviderId = descriptor.Id,
                    Descriptor = descriptor,
                    State = PluginRuntimeState.Stopped
                };
                _pluginRuntimeSlots.Add(descriptor.Id, slot);
                statusChanged = true;
            }
            else
            {
                slot.Descriptor = descriptor;
            }

            if (slot.State == PluginRuntimeState.Stopped)
            {
                StartPluginRuntimeSlot(slot, descriptor);
                statusChanged = true;
            }
        }

        foreach (var slot in _pluginRuntimeSlots.Values)
        {
            slot.Lease?.Papers.Reconcile();
        }

        if (statusChanged)
        {
            QueuePluginStatusUiRefresh();
        }
    }

    private bool HasEntityPluginPaper(string providerId) =>
        State.Papers.Any(paper =>
            paper.Type == PaperTypes.Note &&
            string.Equals(
                paper.BodyProviderId?.Trim(),
                providerId,
                StringComparison.Ordinal));

    private bool IsPluginRuntimeRunning(string providerId) =>
        _pluginRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State == PluginRuntimeState.Running &&
        slot.Lease != null;

    private bool HasPluginRuntimeFailure(string providerId) =>
        _pluginRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State is PluginRuntimeState.Backoff or PluginRuntimeState.Failed;

    private void StartPluginRuntimeSlot(
        PluginRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor)
    {
        if (slot.State != PluginRuntimeState.Stopped ||
            !IsPluginRuntimeDesired(descriptor.Id))
        {
            return;
        }

        slot.State = PluginRuntimeTransitions.BeginStart(slot.State);
        slot.Descriptor = descriptor;
        slot.RuntimeId = Guid.NewGuid();
        slot.RestartRequested = false;
        slot.RunningSinceUtc = default;
        var runtimeId = slot.RuntimeId;
        var lifetime = new PluginRuntimeLifetime();
        slot.Lifetime = lifetime;
        _ = StartPluginRuntimeSlotAsync(
            slot,
            descriptor,
            runtimeId,
            lifetime);
    }

    private async Task StartPluginRuntimeSlotAsync(
        PluginRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId,
        PluginRuntimeLifetime lifetime)
    {
        PluginRuntimeLease? lease = null;
        try
        {
            lease = await CreatePluginRuntimeLeaseAsync(
                slot,
                descriptor,
                runtimeId,
                lifetime);

            if (!IsCurrentPluginRuntimeSlot(slot, runtimeId) ||
                !IsPluginRuntimeDesired(descriptor.Id))
            {
                ClearPluginRuntimeLifetime(slot, lifetime);
                lease.Dispose();
                lease = null;
                if (IsCurrentPluginRuntimeSlot(slot, runtimeId))
                {
                    slot.State = PluginRuntimeState.Stopped;
                    ReconcilePluginRuntimes();
                }
                return;
            }

            if (slot.RestartRequested)
            {
                ClearPluginRuntimeLifetime(slot, lifetime);
                lease.Dispose();
                lease = null;
                slot.RestartRequested = false;
                HandlePluginRuntimeFailure(
                    slot,
                    descriptor,
                    new InvalidOperationException(
                        "The Web plugin runtime failed while completing startup."),
                    "startup-ready");
                return;
            }

            slot.Lease = lease;
            lease = null;
            slot.State = PluginRuntimeTransitions.StartSucceeded(slot.State);
            slot.RunningSinceUtc = DateTimeOffset.UtcNow;
            slot.RetryGeneration++;
            QueuePluginStatusUiRefresh();
        }
        catch (PluginRuntimeStateVersionException ex)
        {
            ClearPluginRuntimeLifetime(slot, lifetime);
            lifetime.TryDeactivate();
            lease?.Dispose();
            if (!IsCurrentPluginRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            HandlePluginRuntimeFailure(
                slot,
                descriptor,
                ex,
                "state-version",
                retry: false);
        }
        catch (PluginRuntimeOwnershipCanceledException)
        {
            ClearPluginRuntimeLifetime(slot, lifetime);
            lifetime.TryDeactivate();
            lease?.Dispose();
            if (!IsCurrentPluginRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            slot.RestartRequested = false;
            slot.State = PluginRuntimeState.Stopped;
            QueuePluginStatusUiRefresh();
            ReconcilePluginRuntimes();
        }
        catch (Exception ex)
        {
            ClearPluginRuntimeLifetime(slot, lifetime);
            lifetime.TryDeactivate();
            lease?.Dispose();
            if (!IsCurrentPluginRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            HandlePluginRuntimeFailure(slot, descriptor, ex, "start");
        }
    }

    private async Task<PluginRuntimeLease> CreatePluginRuntimeLeaseAsync(
        PluginRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId,
        PluginRuntimeLifetime lifetime)
    {
        if (!IsCurrentPluginRuntimeSlot(slot, runtimeId) ||
            !IsPluginRuntimeDesired(descriptor.Id) ||
            !lifetime.IsActive)
        {
            throw new PluginRuntimeOwnershipCanceledException(
                "The plugin runtime no longer has an entity-paper owner.");
        }

        var storedRuntimeState = PaperBodyPlugins.DataStore.ReadRuntimeState(descriptor.Id);
        if (!PluginRuntimeStateVersionIsSupported(
                storedRuntimeState.Version,
                descriptor.StateVersion))
        {
            throw new PluginRuntimeStateVersionException(
                $"Saved plugin Runtime state version {storedRuntimeState.Version} is newer than supported version {descriptor.StateVersion}.");
        }

        bool IsActive() => lifetime.IsActive;

        var workspace = new PaperPluginRuntimeWorkspaceApi(
            this,
            descriptor.Id,
            descriptor.Permissions,
            IsActive);
        var settings = new PaperPluginRuntimeSettingsApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var state = new PaperPluginRuntimeStateApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var papers = new PaperPluginRuntimePapersApi(
            this,
            descriptor.Id,
            IsActive);
        var globalTopBar = new PaperPluginRuntimeGlobalTopBarApi(
            this,
            runtimeId,
            descriptor.Id,
            IsActive);
        var globalShortcuts = new PaperPluginRuntimeGlobalShortcutApi(
            this,
            runtimeId,
            descriptor.Id,
            IsActive);

        IDisposable? runtime = null;
        IPaperBodyPlugin? nativeFactory = null;
        try
        {
            if (descriptor.Kind == PaperBodyPluginKind.Native)
            {
                var activation = PaperBodyPlugins.CreateNativePlugin(descriptor);
                nativeFactory = activation.Plugin;
                if (activation.Plugin is not IPaperPluginRuntimeProvider provider)
                {
                    throw new InvalidOperationException(
                        $"Native plugin '{descriptor.Id}' declares runtime but does not implement IPaperPluginRuntimeProvider.");
                }

                runtime = provider.CreatePluginRuntime(new PaperPluginRuntimeContext
                {
                    ProviderId = descriptor.Id,
                    ApiVersion = descriptor.ApiVersion,
                    GrantedPermissions = descriptor.Permissions,
                    Workspace = workspace,
                    Settings = settings,
                    State = state,
                    Papers = papers,
                    GlobalTopBar = globalTopBar,
                    GlobalShortcuts = globalShortcuts
                }) ?? throw new InvalidOperationException(
                    $"Native plugin '{descriptor.Id}' returned no plugin runtime.");
            }
            else if (descriptor.Kind == PaperBodyPluginKind.Web)
            {
                var webRuntime = new WebPluginRuntime(
                    descriptor,
                    workspace,
                    settings,
                    state,
                    papers,
                    globalTopBar,
                    globalShortcuts,
                    IsActive,
                    () => RequestPluginRuntimeRestart(runtimeId, descriptor.Id));
                runtime = webRuntime;
                await webRuntime.StartAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    "Built-in body providers cannot declare plugin runtime.");
            }

            if (!lifetime.IsActive)
            {
                throw new PluginRuntimeOwnershipCanceledException(
                    "The plugin runtime lost its entity-paper owner while starting.");
            }

            return new PluginRuntimeLease
            {
                RuntimeId = runtimeId,
                ProviderId = descriptor.Id,
                Lifetime = lifetime,
                Workspace = workspace,
                Settings = settings,
                State = state,
                Papers = papers,
                GlobalTopBar = globalTopBar,
                GlobalShortcuts = globalShortcuts,
                Runtime = runtime,
                NativeFactory = nativeFactory
            };
        }
        catch
        {
            lifetime.TryDeactivate();
            try { runtime?.Dispose(); } catch { }
            try { papers.Dispose(); } catch { }
            try { state.Dispose(); } catch { }
            try { settings.Dispose(); } catch { }
            try { globalShortcuts.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }
            if (nativeFactory is IDisposable disposable &&
                !ReferenceEquals(runtime, nativeFactory))
            {
                try { disposable.Dispose(); } catch { }
            }
            throw;
        }
    }

    private void HandlePluginRuntimeFailure(
        PluginRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Exception exception,
        string phase,
        bool retry = true)
    {
        slot.Lease = null;
        slot.Lifetime?.TryDeactivate();
        slot.Lifetime = null;
        slot.RestartRequested = false;
        slot.RunningSinceUtc = default;
        slot.FailureCount++;
        var attempt = slot.FailureCount;

        Trace.TraceWarning(
            "Plugin runtime failure. Provider={0}; Phase={1}; Attempt={2}; Exception={3}",
            descriptor.Id,
            phase,
            attempt,
            exception.GetBaseException());

        var nextState = retry
            ? PluginRuntimeTransitions.StartFailed(
                attempt,
                PluginRuntimeRetryDelays.Length)
            : PluginRuntimeState.Failed;
        if (nextState == PluginRuntimeState.Backoff &&
            IsPluginRuntimeDesired(descriptor.Id))
        {
            slot.State = nextState;
            SchedulePluginRuntimeRetry(
                slot,
                PluginRuntimeRetryDelays[attempt - 1]);
        }
        else
        {
            slot.State = PluginRuntimeState.Failed;
            ClearPluginRuntimePresentation(descriptor.Id);
        }

        QueuePluginStatusUiRefresh();
    }

    private void SchedulePluginRuntimeRetry(
        PluginRuntimeSlot slot,
        TimeSpan delay)
    {
        var generation = ++slot.RetryGeneration;
        _ = RetryPluginRuntimeAfterDelayAsync(
            slot.ProviderId,
            slot,
            generation,
            delay);
    }

    private async Task RetryPluginRuntimeAfterDelayAsync(
        string providerId,
        PluginRuntimeSlot slot,
        int generation,
        TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_pluginRuntimeDisposing ||
                    IsExiting ||
                    !_pluginRuntimeReconciliationEnabled ||
                    !_pluginRuntimeSlots.TryGetValue(providerId, out var current) ||
                    !ReferenceEquals(current, slot) ||
                    slot.RetryGeneration != generation ||
                    slot.State != PluginRuntimeState.Backoff)
                {
                    return;
                }

                if (!IsPluginRuntimeDesired(providerId))
                {
                    ReconcilePluginRuntimes();
                    return;
                }

                slot.State = PluginRuntimeTransitions.RetryElapsed(slot.State);
                ReconcilePluginRuntimes();
            }),
            DispatcherPriority.Background);
    }

    private bool IsCurrentPluginRuntimeSlot(
        PluginRuntimeSlot slot,
        Guid runtimeId) =>
        !_pluginRuntimeDisposing &&
        _pluginRuntimeReconciliationEnabled &&
        _pluginRuntimeSlots.TryGetValue(slot.ProviderId, out var current) &&
        ReferenceEquals(current, slot) &&
        PluginRuntimeTransitions.RuntimeMatches(slot.RuntimeId, runtimeId) &&
        slot.State != PluginRuntimeState.Disposing;

    private bool IsPluginRuntimeDesired(string providerId) =>
        _pluginRuntimeReconciliationEnabled &&
        !_pluginRuntimeDisposing &&
        !IsExiting &&
        HasEntityPluginPaper(providerId) &&
        PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
        DeclaresPluginRuntime(descriptor);

    private void RequestPluginRuntimeRestart(Guid runtimeId, string providerId)
    {
        if (_pluginRuntimeDisposing || IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_pluginRuntimeDisposing ||
                    IsExiting ||
                    !_pluginRuntimeSlots.TryGetValue(providerId, out var slot) ||
                    !PluginRuntimeTransitions.RuntimeMatches(slot.RuntimeId, runtimeId))
                {
                    return;
                }

                if (slot.State == PluginRuntimeState.Starting)
                {
                    slot.RestartRequested = true;
                    return;
                }

                if (slot.State != PluginRuntimeState.Running ||
                    slot.Lease?.RuntimeId != runtimeId ||
                    slot.Descriptor == null)
                {
                    return;
                }

                if (slot.RunningSinceUtc != default &&
                    DateTimeOffset.UtcNow - slot.RunningSinceUtc >=
                    PluginRuntimeStableFailureResetAfter)
                {
                    slot.FailureCount = 0;
                }

                var descriptor = slot.Descriptor;
                var lease = slot.Lease;
                slot.Lease = null;
                slot.Lifetime?.TryDeactivate();
                slot.Lifetime = null;
                slot.RetryGeneration++;
                slot.RestartRequested = false;
                lease.Dispose();
                HandlePluginRuntimeFailure(
                    slot,
                    descriptor,
                    new InvalidOperationException(
                        "The Web plugin runtime requested restart after a fatal navigation or browser-process failure."),
                    "running");
            }),
            DispatcherPriority.Background);
    }

    private void NotifyPluginRuntimeSettingsChanged(string providerId, string settingsJson)
    {
        if (_pluginRuntimeSlots.TryGetValue(providerId, out var slot) &&
            slot.State == PluginRuntimeState.Running &&
            slot.Lease != null)
        {
            slot.Lease.Settings.PublishChanged(settingsJson);
        }
    }

    private void RetryFailedPluginRuntimeAfterSettingsChanged(string providerId)
    {
        if (_pluginRuntimeDisposing ||
            IsExiting ||
            !_pluginRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State is not (PluginRuntimeState.Backoff or PluginRuntimeState.Failed))
        {
            return;
        }

        slot.RetryGeneration++;
        slot.RestartRequested = false;
        slot.FailureCount = 0;
        slot.RunningSinceUtc = default;
        slot.State = PluginRuntimeState.Stopped;
        QueuePluginStatusUiRefresh();
        ReconcilePluginRuntimes();
    }

    private static void ClearPluginRuntimeLifetime(
        PluginRuntimeSlot slot,
        PluginRuntimeLifetime lifetime)
    {
        if (ReferenceEquals(slot.Lifetime, lifetime))
        {
            slot.Lifetime = null;
        }
    }

    private void RemovePluginRuntimeSlot(string providerId)
    {
        if (!_pluginRuntimeSlots.Remove(providerId, out var slot))
        {
            return;
        }

        // Reconcile while the Runtime lifetime is still valid so deleting the final Paper emits
        // PaperRemoved and the plugin may persist its final provider-scoped state before teardown.
        try { slot.Lease?.Papers.Reconcile(); } catch { }

        slot.RetryGeneration++;
        slot.RestartRequested = false;
        slot.State = PluginRuntimeState.Disposing;
        slot.Lifetime?.TryDeactivate();
        slot.Lifetime = null;
        var lease = slot.Lease;
        slot.Lease = null;
        lease?.Dispose();
    }

    private static bool PluginRuntimeStateVersionIsSupported(
        int storedVersion,
        int targetVersion) =>
        storedVersion <= targetVersion;

    private static bool DeclaresPluginRuntime(PaperBodyPluginDescriptor descriptor) =>
        descriptor.Manifest?.Capabilities?.Contains(
            PluginRuntimeCapability,
            StringComparer.Ordinal) == true;

    private void DisposePluginRuntimes()
    {
        _pluginRuntimeDisposing = true;
        _pluginRuntimeReconciliationEnabled = false;
        foreach (var providerId in _pluginRuntimeSlots.Keys.ToArray())
        {
            RemovePluginRuntimeSlot(providerId);
        }
        _pluginRuntimeSlots.Clear();
    }
}
