using System.Diagnostics;
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

    private enum PluginAppRuntimeState
    {
        Stopped,
        Starting,
        Running,
        Backoff,
        Failed,
        Disposing
    }

    // Keep the lifecycle rules pure and deterministic so async WebView/native callbacks cannot
    // silently redefine the state machine. The policy is also directly exercised by protocol tests.
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
        public bool Active { get; set; } = true;
    }

    private sealed class PluginAppRuntimeOwnershipCanceledException(string message)
        : Exception(message);

    private sealed class PluginAppRuntimeSlot
    {
        public required string ProviderId { get; init; }
        public PaperBodyPluginDescriptor? Descriptor { get; set; }
        public PluginAppRuntimeState State { get; set; }
        public Guid RuntimeId { get; set; }
        public PluginAppRuntimeLease? Lease { get; set; }
        public int FailureCount { get; set; }
        public int RetryGeneration { get; set; }
        public bool RestartRequested { get; set; }
    }

    private sealed class PluginAppRuntimeLease : IDisposable
    {
        public required Guid RuntimeId { get; init; }
        public required string ProviderId { get; init; }
        public required PluginAppRuntimeLifetime Lifetime { get; init; }
        public required PaperAppRuntimeWorkspaceApi Workspace { get; init; }
        public required PaperAppRuntimeGlobalTopBarApi GlobalTopBar { get; init; }
        public required PaperAppRuntimeGlobalShortcutApi GlobalShortcuts { get; init; }
        public IDisposable? Runtime { get; init; }
        public IPaperBodyPlugin? NativeFactory { get; init; }

        public void Dispose()
        {
            if (!Lifetime.Active)
            {
                return;
            }

            Lifetime.Active = false;
            try { Runtime?.Dispose(); } catch { }
            try { GlobalShortcuts.Dispose(); } catch { }
            try { GlobalTopBar.Dispose(); } catch { }
            try { Workspace.Dispose(); } catch { }
            if (NativeFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    // One provider, one authoritative lifecycle record. Starting/running/backoff/failure/restart
    // state must never be inferred by combining several parallel dictionaries or hash sets.
    private readonly Dictionary<string, PluginAppRuntimeSlot> _pluginAppRuntimeSlots =
        new(StringComparer.Ordinal);
    private bool _pluginAppRuntimeReconciliationEnabled;
    private bool _pluginAppRuntimeDisposing;

    /// <summary>
    /// Enables provider-level runtimes only after startupPaper handling has settled. From this point
    /// the final entity-paper set is the authority: provider 0 -> 1 starts a runtime and 1 -> 0 ends
    /// it. Visibility, folding and live body-session state do not own this lifetime.
    /// </summary>
    internal void EnablePluginAppRuntimeReconciliation()
    {
        if (_pluginAppRuntimeDisposing || IsExiting)
        {
            return;
        }

        _pluginAppRuntimeReconciliationEnabled = true;
        // paper.* shortcuts depend on entity-paper ownership but not on appRuntime. Rebuild the
        // unified shortcut plan as soon as entity ownership becomes authoritative.
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
                    // Bounded automatic retries stay bounded. A real plugin rescan/content change is
                    // the explicit recovery signal that allows a previously failed provider to try
                    // again without requiring PaperTodo to restart.
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
        var runtimeId = slot.RuntimeId;
        _ = StartPluginAppRuntimeSlotAsync(slot, descriptor, runtimeId);
    }

    private async Task StartPluginAppRuntimeSlotAsync(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId)
    {
        PluginAppRuntimeLease? lease = null;
        try
        {
            lease = await CreatePluginAppRuntimeLeaseAsync(
                slot,
                descriptor,
                runtimeId);

            if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId) ||
                !IsPluginAppRuntimeDesired(descriptor.Id))
            {
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
                lease.Dispose();
                lease = null;
                slot.RestartRequested = false;
                slot.State = PluginAppRuntimeState.Stopped;
                slot.FailureCount = 0;
                QueuePluginStatusUiRefresh();
                ReconcilePluginAppRuntimes();
                return;
            }

            slot.Lease = lease;
            lease = null;
            slot.State = PluginAppRuntimeTransitions.StartSucceeded(slot.State);
            slot.FailureCount = 0;
            slot.RetryGeneration++;
            QueuePluginStatusUiRefresh();
        }
        catch (PluginAppRuntimeOwnershipCanceledException)
        {
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
            lease?.Dispose();
            if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId))
            {
                return;
            }

            HandlePluginAppRuntimeStartFailure(slot, descriptor, ex);
        }
    }

    private async Task<PluginAppRuntimeLease> CreatePluginAppRuntimeLeaseAsync(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Guid runtimeId)
    {
        if (!IsCurrentPluginAppRuntimeSlot(slot, runtimeId) ||
            !IsPluginAppRuntimeDesired(descriptor.Id))
        {
            throw new PluginAppRuntimeOwnershipCanceledException(
                "The plugin app runtime no longer has an entity-paper owner.");
        }

        var lifetime = new PluginAppRuntimeLifetime();
        bool IsActive() =>
            lifetime.Active &&
            IsRunning &&
            IsCurrentPluginAppRuntimeSlot(slot, runtimeId) &&
            slot.State is PluginAppRuntimeState.Starting or PluginAppRuntimeState.Running &&
            IsPluginAppRuntimeDesired(descriptor.Id);

        var workspace = new PaperAppRuntimeWorkspaceApi(
            this,
            descriptor.Id,
            descriptor.Permissions,
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

            if (!IsActive())
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
                GlobalTopBar = globalTopBar,
                GlobalShortcuts = globalShortcuts,
                Runtime = runtime,
                NativeFactory = nativeFactory
            };
        }
        catch
        {
            lifetime.Active = false;
            try { runtime?.Dispose(); } catch { }
            try { globalShortcuts.Dispose(); } catch { }
            try { globalTopBar.Dispose(); } catch { }
            try { workspace.Dispose(); } catch { }
            if (nativeFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            throw;
        }
    }

    private void HandlePluginAppRuntimeStartFailure(
        PluginAppRuntimeSlot slot,
        PaperBodyPluginDescriptor descriptor,
        Exception exception)
    {
        slot.Lease = null;
        slot.RestartRequested = false;
        slot.FailureCount++;
        var attempt = slot.FailureCount;

        Trace.TraceWarning(
            "Plugin app runtime failed to start. Provider={0}; Attempt={1}; Exception={2}",
            descriptor.Id,
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

        // WebView2 raises ProcessFailed from inside its own callback. Queue teardown so disposal is
        // never re-entrant into WebView2. RuntimeId prevents a stale callback from replacing a newer
        // provider runtime.
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
                    slot.Lease?.RuntimeId != runtimeId)
                {
                    return;
                }

                var lease = slot.Lease;
                slot.Lease = null;
                slot.RetryGeneration++;
                slot.RestartRequested = false;
                slot.FailureCount = 0;
                slot.State = PluginAppRuntimeState.Stopped;
                lease.Dispose();
                QueuePluginStatusUiRefresh();
                ReconcilePluginAppRuntimes();
            }),
            DispatcherPriority.Background);
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
