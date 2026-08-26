using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private const string PluginAppRuntimeCapability = "appRuntime";
    private static readonly TimeSpan[] PluginAppRuntimeRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10)
    ];
    private static readonly TimeSpan PluginAppRuntimeStableFailureResetAfter =
        TimeSpan.FromSeconds(30);

    private enum PluginAppRuntimeState
    {
        Stopped,
        Starting,
        Running,
        Backoff,
        Failed,
        Disposing
    }

    private static class PluginAppRuntimeTransitions
    {
        public static PluginAppRuntimeState BeginStart(PluginAppRuntimeState state) =>
            state == PluginAppRuntimeState.Stopped
                ? PluginAppRuntimeState.Starting
                : state;

        public static PluginAppRuntimeState StartSucceeded(PluginAppRuntimeState state) =>
            state == PluginAppRuntimeState.Starting
                ? PluginAppRuntimeState.Running
                : state;

        public static PluginAppRuntimeState StartFailed(int failureCount, int retryCount) =>
            failureCount <= retryCount
                ? PluginAppRuntimeState.Backoff
                : PluginAppRuntimeState.Failed;

        public static PluginAppRuntimeState RetryElapsed(PluginAppRuntimeState state) =>
            state == PluginAppRuntimeState.Backoff
                ? PluginAppRuntimeState.Stopped
                : state;

        public static PluginAppRuntimeState DescriptorChanged(PluginAppRuntimeState state) =>
            state == PluginAppRuntimeState.Failed
                ? PluginAppRuntimeState.Stopped
                : state;

        public static bool RuntimeMatches(Guid currentRuntimeId, Guid callbackRuntimeId) =>
            currentRuntimeId == callbackRuntimeId;
    }

    private sealed class PluginAppRuntimeLifetime
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool TryDeactivate() =>
            Interlocked.Exchange(ref _active, 0) != 0;
    }

    private sealed class PluginAppRuntimeOwnershipCanceledException(string message)
        : Exception(message);

    private sealed class PluginAppRuntimeSlot
    {
        public required string ProviderId { get; init; }
        public PaperBodyPluginDescriptor? Descriptor { get; set; }
        public PluginAppRuntimeState State { get; set; }
        public Guid RuntimeId { get; set; }
        public PluginAppRuntimeLifetime? Lifetime { get; set; }
        public PluginAppRuntimeLease? Lease { get; set; }
        public int FailureCount { get; set; }
        public int RetryGeneration { get; set; }
        public bool RestartRequested { get; set; }
        public DateTimeOffset RunningSinceUtc { get; set; }
    }

    private sealed class PluginAppRuntimeLease : IDisposable
    {
        private int _disposed;

        public required Guid RuntimeId { get; init; }
        public required string ProviderId { get; init; }
        public required PluginAppRuntimeLifetime Lifetime { get; init; }
        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeSettingsApi Settings { get; init; }
        public required PaperAppRuntimeStateApi State { get; init; }
        public required PaperAppRuntimePapersApi Papers { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }
        public required PaperAppRuntimeGlobalShortcutApi GlobalShortcuts { get; init; }
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

    private readonly Dictionary<string, PluginAppRuntimeSlot> _pluginAppRuntimeSlots =
        new(StringComparer.Ordinal);
    private bool _pluginAppRuntimeReconciliationEnabled;
    private bool _pluginAppRuntimeDisposing;

    internal void EnablePluginAppRuntimeReconciliation()
    {
        if (_pluginAppRuntimeDisposing || IsExiting)
        {
            return;
        }

        _pluginAppRuntimeReconciliationEnabled = true;
        RefreshPluginShortcuts();
        ReconcilePluginAppRuntimes();
    }

    internal void ReconcilePluginAppRuntimes()
    {
        if (!_pluginAppRuntimeReconciliationEnabled ||
            _pluginAppRuntimeDisposing ||
            IsExiting)
        {
            return;
        }

        var desired = PaperBodyPlugins.Descriptors
            .Where(DeclaresPluginAppRuntime)
            .Where(descriptor => HasEntityPluginPaper(descriptor.Id))
            .ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
        var statusChanged = false;

        foreach (var providerId in _pluginAppRuntimeSlots.Keys
                     .Where(providerId => !desired.ContainsKey(providerId))
                     .ToArray())
        {
            RemovePluginAppRuntimeSlot(providerId);
            statusChanged = true;
        }

        foreach (var descriptor in desired.Values)
        {
            if (!_pluginAppRuntimeSlots.TryGetValue(descriptor.Id, out var slot))
            {
                slot = new PluginAppRuntimeSlot
                {
                    ProviderId = descriptor.Id,
                    Descriptor = descriptor,
                    State = PluginAppRuntimeState.Stopped
                };
                _pluginAppRuntimeSlots.Add(descriptor.Id, slot);
                statusChanged = true;
            }
            else
            {
                var descriptorChanged = slot.Descriptor != null &&
                    !string.Equals(
                        slot.Descriptor.Fingerprint,
                        descriptor.Fingerprint,
                        StringComparison.Ordinal);
                slot.Descriptor = descriptor;
                if (descriptorChanged && slot.State == PluginAppRuntimeState.Failed)
                {
                    slot.State = PluginAppRuntimeTransitions.DescriptorChanged(slot.State);
                    slot.FailureCount = 0;
                    slot.RetryGeneration++;
                    slot.RestartRequested = false;
                    statusChanged = true;
                }
            }

            if (slot.State == PluginAppRuntimeState.Stopped)
            {
                StartPluginAppRuntimeSlot(slot, descriptor);
                statusChanged = true;
            }
        }

        foreach (var slot in _pluginAppRuntimeSlots.Values)
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

    private bool IsPluginAppRuntimeRunning(string providerId) =>
        _pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State == PluginAppRuntimeState.Running &&
        slot.Lease != null;

    private bool HasPluginAppRuntimeFailure(string providerId) =>
        _pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
        slot.State is PluginAppRuntimeState.Backoff or PluginAppRuntimeState.Failed;

    private void StartPluginAppRuntimeSlot(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor)
    {
        if (slot.State != PluginAppRuntimeState.Stopped ||
            !IsPluginAppRuntimeDesired(descriptor.Id))
        {
            return;
        }

        slot.State = PluginAppRuntimeTransitions.BeginStart(slot.State);
        slot.Descriptor = descriptor;
        slot.RuntimeId = Guid.NewGuid();
        slot.RestartRequested = false;
        slot.RunningSinceUtc = default;
        var runtimeId = slot.RuntimeId;
        var lifetime = new PluginAppRuntimeLifetime();
        slot.Lifetime = lifetime;
        _ = StartPluginAppRuntimeSlotAsync(
            slot,
            descriptor,
            runtimeId,
            lifetime);
    }

    private async Task StartPluginAppRuntimeSlotAsync(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId,
        PluginAppRuntimeLifetime lifetime)
    {
        PluginAppRuntimeLease? lease = null;
        try
        {
            lease = await CreatePluginAppRuntimeLeaseAsync(
                slot,
                descriptor,
                runtimeId,
                lifetime);

            if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId) ||
                !IsPluginAppRuntimeDesired(descriptor.Id))
            {
                ClearPluginAppRuntimeLifetime(slot, lifetime);
                lease.Dispose();
                lease = null;
                if (IsCurrentPluginAppRuntimeSlot(slot, runtimeId))
                {
                    slot.State = PluginAppRuntimeState.Stopped;
                    ReconcilePluginAppRuntimes();
                }
                return;
            }

            if (slot.RestartRequested)
            {
                ClearPluginAppRuntimeLifetime(slot, lifetime);
                lease.Dispose();
                lease = null;
                slot.RestartRequested = false;
                HandlePluginAppRuntimeFailure(
                    slot,
                    descriptor,
                    new InvalidOperationException(
                        "The Web plugin app runtime failed while completing startup."),
                    "startup-ready");
                return;
            }

            slot.Lease = lease;
            lease = null;
            slot.State = PluginAppRuntimeTransitions.StartSucceeded(slot.State);
            slot.RunningSinceUtc = DateTimeOffset.UtcNow;
            slot.RetryGeneration++;
            QueuePluginStatusUiRefresh();
        }
        catch (PluginAppRuntimeOwnershipCanceledException)
        {
            ClearPluginAppRuntimeLifetime(slot, lifetime);
            lifetime.TryDeactivate();
            lease?.Dispose();
            if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            slot.RestartRequested = false;
            slot.State = PluginAppRuntimeState.Stopped;
            QueuePluginStatusUiRefresh();
            ReconcilePluginAppRuntimes();
        }
        catch (Exception ex)
        {
            ClearPluginAppRuntimeLifetime(slot, lifetime);
            lifetime.TryDeactivate();
            lease?.Dispose();
            if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            HandlePluginAppRuntimeFailure(slot, descriptor, ex, "start");
        }
    }

    private async Task<PluginAppRuntimeLease> CreatePluginAppRuntimeLeaseAsync(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId,
        PluginAppRuntimeLifetime lifetime)
    {
        if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId) ||
            !IsPluginAppRuntimeDesired(descriptor.Id) ||
            !lifetime.IsActive)
        {
            throw new PluginAppRuntimeOwnershipCanceledException(
                "The plugin app runtime no longer has an entity-paper owner.");
        }

        bool IsActive() => lifetime.IsActive;

        var workspace = new PaperAppRuntimeWorkspaceApi(
            this,
            descriptor.Id,
            descriptor.Permissions,
            IsActive);
        var settings = new PaperAppRuntimeSettingsApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var state = new PaperAppRuntimeStateApi(
            PaperBodyPlugins.DataStore,
            descriptor,
            IsActive);
        var papers = new PaperAppRuntimePapersApi(
            this,
            descriptor.Id,
            IsActive);
        var globalTopBar = new PaperAppRuntimeGlobalTopBarApi(
            this,
            runtimeId,
            descriptor.Id,
            IsActive);
        var globalShortcuts = new PaperAppRuntimeGlobalShortcutApi(
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
                if (activation.Plugin is not IPaperAppRuntimeProvider provider)
                {
                    throw new InvalidOperationException(
                        $"Native plugin '{descriptor.Id}' declares appRuntime but does not implement IPaperAppRuntimeProvider.");
                }

                runtime = provider.CreateAppRuntime(new PaperAppRuntimeContext
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
                    $"Native plugin '{descriptor.Id}' returned no app runtime.");
            }
            else if (descriptor.Kind == PaperBodyPluginKind.Web)
            {
                var webRuntime = new WebPluginAppRuntime(
                    descriptor,
                    workspace,
                    settings,
                    state,
                    papers,
                    globalTopBar,
                    globalShortcuts,
                    IsActive,
                    () => RequestPluginAppRuntimeRestart(runtimeId, descriptor.Id));
                runtime = webRuntime;
                await webRuntime.StartAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    "Built-in body providers cannot declare plugin appRuntime.");
            }

            if (!lifetime.IsActive)
            {
                throw new PluginAppRuntimeOwnershipCanceledException(
                    "The plugin app runtime lost its entity-paper owner while starting.");
            }

            return new PluginAppRuntimeLease
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

    private void HandlePluginAppRuntimeFailure(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Exception exception,
        string phase)
    {
        slot.Lease = null;
        slot.Lifetime?.TryDeactivate();
        slot.Lifetime = null;
        slot.RestartRequested = false;
        slot.RunningSinceUtc = default;
        slot.FailureCount++;
        var attempt = slot.FailureCount;

        Trace.TraceWarning(
            "Plugin app runtime failure. Provider={0}; Phase={1}; Attempt={2}; Exception={3}",
            descriptor.Id,
            phase,
            attempt,
            exception.GetBaseException());

        var nextState = PluginAppRuntimeTransitions.StartFailed(
            attempt,
            PluginAppRuntimeRetryDelays.Length);
        if (nextState == PluginAppRuntimeState.Backoff &&
            IsPluginAppRuntimeDesired(descriptor.Id))
        {
            slot.State = nextState;
            SchedulePluginAppRuntimeRetry(
                slot,
                PluginAppRuntimeRetryDelays[attempt - 1]);
        }
        else
        {
            slot.State = PluginAppRuntimeState.Failed;
        }

        QueuePluginStatusUiRefresh();
    }

    private void SchedulePluginAppRuntimeRetry(
        PluginAppRuntimeSlot slot,
        TimeSpan delay)
    {
        var generation = ++slot.RetryGeneration;
        _ = RetryPluginAppRuntimeAfterDelayAsync(
            slot.ProviderId,
            slot,
            generation,
            delay);
    }

    private async Task RetryPluginAppRuntimeAfterDelayAsync(
        string providerId,
        PluginAppRuntimeSlot slot,
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
                if (_pluginAppRuntimeDisposing ||
                    IsExiting ||
                    !_pluginAppRuntimeReconciliationEnabled ||
                    !_pluginAppRuntimeSlots.TryGetValue(providerId, out var current) ||
                    !ReferenceEquals(current, slot) ||
                    slot.RetryGeneration != generation ||
                    slot.State != PluginAppRuntimeState.Backoff)
                {
                    return;
                }

                if (!IsPluginAppRuntimeDesired(providerId))
                {
                    ReconcilePluginAppRuntimes();
                    return;
                }

                slot.State = PluginAppRuntimeTransitions.RetryElapsed(slot.State);
                ReconcilePluginAppRuntimes();
            }),
            DispatcherPriority.Background);
    }

    private bool IsCurrentPluginAppRuntimeSlot(
        PluginAppRuntimeSlot slot,
        Guid runtimeId) =>
        !_pluginAppRuntimeDisposing &&
        _pluginAppRuntimeReconciliationEnabled &&
        _pluginAppRuntimeSlots.TryGetValue(slot.ProviderId, out var current) &&
        ReferenceEquals(current, slot) &&
        PluginAppRuntimeTransitions.RuntimeMatches(slot.RuntimeId, runtimeId) &&
        slot.State != PluginAppRuntimeState.Disposing;

    private bool IsPluginAppRuntimeDesired(string providerId) =>
        _pluginAppRuntimeReconciliationEnabled &&
        !_pluginAppRuntimeDisposing &&
        !IsExiting &&
        HasEntityPluginPaper(providerId) &&
        PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
        DeclaresPluginAppRuntime(descriptor);

    private void RequestPluginAppRuntimeRestart(Guid runtimeId, string providerId)
    {
        if (_pluginAppRuntimeDisposing || IsExiting)
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
                if (_pluginAppRuntimeDisposing ||
                    IsExiting ||
                    !_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) ||
                    !PluginAppRuntimeTransitions.RuntimeMatches(slot.RuntimeId, runtimeId))
                {
                    return;
                }

                if (slot.State == PluginAppRuntimeState.Starting)
                {
                    slot.RestartRequested = true;
                    return;
                }

                if (slot.State != PluginAppRuntimeState.Running ||
                    slot.Lease?.RuntimeId != runtimeId ||
                    slot.Descriptor == null)
                {
                    return;
                }

                if (slot.RunningSinceUtc != default &&
                    DateTimeOffset.UtcNow - slot.RunningSinceUtc >=
                    PluginAppRuntimeStableFailureResetAfter)
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
                HandlePluginAppRuntimeFailure(
                    slot,
                    descriptor,
                    new InvalidOperationException(
                        "The Web plugin app runtime requested restart after a fatal navigation or browser-process failure."),
                    "running");
            }),
            DispatcherPriority.Background);
    }

    private void NotifyPluginAppRuntimeSettingsChanged(string providerId, string settingsJson)
    {
        if (_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) &&
            slot.State == PluginAppRuntimeState.Running &&
            slot.Lease != null)
        {
            slot.Lease.Settings.PublishChanged(settingsJson);
        }
    }

    private void RetryFailedPluginAppRuntimeAfterSettingsChanged(string providerId)
    {
        if (_pluginAppRuntimeDisposing ||
            IsExiting ||
            !_pluginAppRuntimeSlots.TryGetValue(providerId, out var slot) ||
            slot.State is not (PluginAppRuntimeState.Backoff or PluginAppRuntimeState.Failed))
        {
            return;
        }

        slot.RetryGeneration++;
        slot.RestartRequested = false;
        slot.FailureCount = 0;
        slot.RunningSinceUtc = default;
        slot.State = PluginAppRuntimeState.Stopped;
        QueuePluginStatusUiRefresh();
        ReconcilePluginAppRuntimes();
    }

    private static void ClearPluginAppRuntimeLifetime(
        PluginAppRuntimeSlot slot,
        PluginAppRuntimeLifetime lifetime)
    {
        if (ReferenceEquals(slot.Lifetime, lifetime))
        {
            slot.Lifetime = null;
        }
    }

    private void RemovePluginAppRuntimeSlot(string providerId)
    {
        if (!_pluginAppRuntimeSlots.Remove(providerId, out var slot))
        {
            return;
        }

        slot.RetryGeneration++;
        slot.RestartRequested = false;
        slot.State = PluginAppRuntimeState.Disposing;
        slot.Lifetime?.TryDeactivate();
        slot.Lifetime = null;
        var lease = slot.Lease;
        slot.Lease = null;
        lease?.Dispose();
    }

    private static bool DeclaresPluginAppRuntime(PaperBodyPluginDescriptor descriptor) =>
        descriptor.Manifest?.Capabilities?.Contains(
            PluginAppRuntimeCapability,
            StringComparer.Ordinal) == true;

    private void DisposePluginAppRuntimes()
    {
        _pluginAppRuntimeDisposing = true;
        _pluginAppRuntimeReconciliationEnabled = false;
        foreach (var providerId in _pluginAppRuntimeSlots.Keys.ToArray())
        {
            RemovePluginAppRuntimeSlot(providerId);
        }
        _pluginAppRuntimeSlots.Clear();
    }
}
