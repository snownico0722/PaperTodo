using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private enum WebPaperRuntimeState
    {
        Starting,
        Running,
        Backoff,
        Failed
    }

    private sealed class WebPaperRuntimeSlot
    {
        public required string PaperId { get; init; }
        public required string ProviderId { get; init; }
        public required PaperData Paper { get; init; }
        public required PaperBodyPluginDescriptor Descriptor { get; set; }
        public WebPaperRuntimeState State { get; set; }
        public Guid RuntimeId { get; set; }
        public WebPaperRuntime? Runtime { get; set; }
        public PaperBodyPluginHostApi? HostApi { get; set; }
        public int FailureCount { get; set; }
        public int RetryGeneration { get; set; }
        public DateTimeOffset RunningSinceUtc { get; set; }
        public bool HasHeaderValue { get; set; }
        public string HeaderText { get; set; } = string.Empty;
        public bool HasCapsuleValue { get; set; }
        public PaperCapsulePresentation? CapsulePresentation { get; set; }
    }

    private readonly Dictionary<string, WebPaperRuntimeSlot> _webPaperRuntimeSlots =
        new(StringComparer.Ordinal);
    private bool _webPaperRuntimeReconciliationEnabled;
    private bool _webPaperRuntimeDisposing;

    internal void EnableWebPaperRuntimeReconciliation()
    {
        if (_webPaperRuntimeDisposing || IsExiting)
        {
            return;
        }

        _webPaperRuntimeReconciliationEnabled = true;
        ReconcileWebPaperRuntimes();
    }

    internal void ReconcileWebPaperRuntimes()
    {
        if (!_webPaperRuntimeReconciliationEnabled ||
            _webPaperRuntimeDisposing ||
            IsExiting)
        {
            return;
        }

        var desired = new Dictionary<string, (PaperData Paper, PaperBodyPluginDescriptor Descriptor)>(
            StringComparer.Ordinal);
        foreach (var paper in State.Papers)
        {
            if (TryGetDesiredWebPaperRuntime(paper, out var descriptor))
            {
                desired[paper.Id] = (paper, descriptor);
            }
        }

        foreach (var pair in _webPaperRuntimeSlots.ToArray())
        {
            var slot = pair.Value;
            if (!desired.TryGetValue(pair.Key, out var wanted))
            {
                RemoveWebPaperRuntimeSlot(slot, clearPresentation: true);
                continue;
            }

            if (!string.Equals(slot.ProviderId, wanted.Descriptor.Id, StringComparison.Ordinal) ||
                !string.Equals(
                    slot.Descriptor.Fingerprint,
                    wanted.Descriptor.Fingerprint,
                    StringComparison.Ordinal))
            {
                // A replacement runtime owns the same paper. Keep the last painted presentation
                // until the new runtime publishes its first value so reloads do not flash empty.
                RemoveWebPaperRuntimeSlot(slot, clearPresentation: false);
            }
        }

        foreach (var wanted in desired.Values)
        {
            if (_webPaperRuntimeSlots.ContainsKey(wanted.Paper.Id))
            {
                continue;
            }

            var slot = new WebPaperRuntimeSlot
            {
                PaperId = wanted.Paper.Id,
                ProviderId = wanted.Descriptor.Id,
                Paper = wanted.Paper,
                Descriptor = wanted.Descriptor,
                State = WebPaperRuntimeState.Starting
            };
            _webPaperRuntimeSlots.Add(slot.PaperId, slot);
            StartWebPaperRuntimeSlot(slot);
        }
    }

    private bool TryGetDesiredWebPaperRuntime(
        PaperData paper,
        out PaperBodyPluginDescriptor descriptor)
    {
        descriptor = null!;
        if (paper.Type != PaperTypes.Note ||
            string.IsNullOrWhiteSpace(paper.BodyProviderId) ||
            !PaperBodyPlugins.TryGet(paper.BodyProviderId, out var candidate) ||
            candidate.Kind != PaperBodyPluginKind.Web ||
            candidate.Manifest == null ||
            string.IsNullOrWhiteSpace(candidate.Manifest.PaperRuntimePath) ||
            (candidate.RuntimeRequirements & PaperBodyRuntimeRequirements.BackgroundUpdates) == 0)
        {
            return false;
        }

        descriptor = candidate;
        return true;
    }

    internal bool HasWebPaperRuntimeOwnership(string paperId, string providerId)
    {
        return _webPaperRuntimeSlots.TryGetValue(paperId, out var slot) &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal) &&
            string.Equals(slot.Paper.BodyProviderId, providerId, StringComparison.Ordinal) &&
            State.Papers.Contains(slot.Paper);
    }

    private bool HasRunningWebPaperRuntime(string providerId) =>
        _webPaperRuntimeSlots.Values.Any(slot =>
            slot.State == WebPaperRuntimeState.Running &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal));

    private bool HasWebPaperRuntimeFailure(string providerId) =>
        _webPaperRuntimeSlots.Values.Any(slot =>
            slot.State is WebPaperRuntimeState.Backoff or WebPaperRuntimeState.Failed &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal));

    private bool IsCurrentWebPaperRuntimeSlot(
        WebPaperRuntimeSlot slot,
        Guid runtimeId) =>
        !_webPaperRuntimeDisposing &&
        _webPaperRuntimeReconciliationEnabled &&
        slot.RuntimeId == runtimeId &&
        slot.State is WebPaperRuntimeState.Starting or WebPaperRuntimeState.Running &&
        _webPaperRuntimeSlots.TryGetValue(slot.PaperId, out var current) &&
        ReferenceEquals(current, slot) &&
        string.Equals(slot.Paper.BodyProviderId, slot.ProviderId, StringComparison.Ordinal) &&
        State.Papers.Contains(slot.Paper);

    private void StartWebPaperRuntimeSlot(WebPaperRuntimeSlot slot)
    {
        if (_webPaperRuntimeDisposing ||
            IsExiting ||
            !_webPaperRuntimeReconciliationEnabled ||
            !_webPaperRuntimeSlots.TryGetValue(slot.PaperId, out var current) ||
            !ReferenceEquals(current, slot) ||
            !TryGetDesiredWebPaperRuntime(slot.Paper, out var descriptor) ||
            !string.Equals(descriptor.Id, slot.ProviderId, StringComparison.Ordinal))
        {
            return;
        }

        slot.Descriptor = descriptor;
        slot.State = WebPaperRuntimeState.Starting;
        slot.RuntimeId = Guid.NewGuid();
        slot.RetryGeneration++;
        var runtimeId = slot.RuntimeId;
        bool IsActive() => IsCurrentWebPaperRuntimeSlot(slot, runtimeId);

        PaperBodyPluginHostApi? hostApi = null;
        WebPaperRuntime? runtime = null;
        try
        {
            var stored = PaperBodyPlugins.DataStore.ReadPaperState(
                slot.ProviderId,
                slot.PaperId);
            if (stored.Version > descriptor.StateVersion)
            {
                throw new InvalidOperationException(
                    $"Saved plugin state version {stored.Version} is newer than supported version {descriptor.StateVersion}.");
            }

            hostApi = new PaperBodyPluginHostApi(
                this,
                PaperCommands,
                slot.PaperId,
                slot.ProviderId,
                descriptor.Permissions,
                IsActive,
                IsActive);
            runtime = new WebPaperRuntime(
                descriptor,
                slot.PaperId,
                stored.Json ?? "{}",
                stored.Version,
                descriptor.StateVersion,
                PaperBodyPlugins.DataStore.GetSettingsJson(descriptor),
                hostApi,
                IsActive,
                title =>
                {
                    if (IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
                    {
                        UpdatePaperTitleFromPlugin(slot.Paper, title, slot.ProviderId);
                    }
                },
                text => SetWebPaperRuntimeHeader(slot, runtimeId, text),
                presentation => SetWebPaperRuntimeCapsule(slot, runtimeId, presentation),
                json => SaveWebPaperRuntimeState(slot, runtimeId, json),
                payload => PostWebPaperRuntimeMessageToBody(slot, runtimeId, payload),
                () => RequestWebPaperRuntimeRestart(slot, runtimeId));
            slot.HostApi = hostApi;
            slot.Runtime = runtime;
            _ = StartWebPaperRuntimeSlotAsync(slot, runtime, runtimeId);
        }
        catch (Exception ex)
        {
            try { runtime?.Dispose(); } catch { }
            try { hostApi?.Dispose(); } catch { }
            slot.Runtime = null;
            slot.HostApi = null;
            HandleWebPaperRuntimeFailure(slot, runtimeId, ex, "create");
        }
    }

    private async Task StartWebPaperRuntimeSlotAsync(
        WebPaperRuntimeSlot slot,
        WebPaperRuntime runtime,
        Guid runtimeId)
    {
        try
        {
            await runtime.StartAsync();
            if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId) ||
                !ReferenceEquals(slot.Runtime, runtime))
            {
                return;
            }

            slot.State = WebPaperRuntimeState.Running;
            slot.RunningSinceUtc = DateTimeOffset.UtcNow;
            ApplyWebPaperRuntimePresentationToWindowForSlot(slot);
            QueuePluginStatusUiRefresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId) ||
                !ReferenceEquals(slot.Runtime, runtime))
            {
                return;
            }
            HandleWebPaperRuntimeFailure(slot, runtimeId, ex, "start");
        }
    }

    private void RequestWebPaperRuntimeRestart(
        WebPaperRuntimeSlot slot,
        Guid runtimeId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
                {
                    return;
                }
                if (slot.RunningSinceUtc != default &&
                    DateTimeOffset.UtcNow - slot.RunningSinceUtc >=
                    PluginAppRuntimeStableFailureResetAfter)
                {
                    slot.FailureCount = 0;
                }
                HandleWebPaperRuntimeFailure(
                    slot,
                    runtimeId,
                    new InvalidOperationException("The Web paper runtime requested a restart."),
                    "runtime");
            }),
            DispatcherPriority.Background);
    }

    private void HandleWebPaperRuntimeFailure(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        Exception exception,
        string phase)
    {
        if (!_webPaperRuntimeSlots.TryGetValue(slot.PaperId, out var current) ||
            !ReferenceEquals(current, slot) ||
            slot.RuntimeId != runtimeId)
        {
            return;
        }

        Trace.TraceWarning(
            "Web paper runtime failure. Paper={0}; Provider={1}; Phase={2}; Attempt={3}; Exception={4}",
            slot.PaperId,
            slot.ProviderId,
            phase,
            slot.FailureCount + 1,
            exception.GetBaseException());

        DisposeWebPaperRuntimeLease(slot);
        slot.RunningSinceUtc = default;
        slot.FailureCount++;
        if (slot.FailureCount <= PluginAppRuntimeRetryDelays.Length &&
            TryGetDesiredWebPaperRuntime(slot.Paper, out var descriptor) &&
            string.Equals(descriptor.Id, slot.ProviderId, StringComparison.Ordinal))
        {
            slot.State = WebPaperRuntimeState.Backoff;
            ScheduleWebPaperRuntimeRetry(
                slot,
                PluginAppRuntimeRetryDelays[slot.FailureCount - 1]);
        }
        else
        {
            slot.State = WebPaperRuntimeState.Failed;
        }
        QueuePluginStatusUiRefresh();
    }

    private void ScheduleWebPaperRuntimeRetry(
        WebPaperRuntimeSlot slot,
        TimeSpan delay)
    {
        var generation = ++slot.RetryGeneration;
        _ = RetryWebPaperRuntimeAfterDelayAsync(slot, generation, delay);
    }

    private async Task RetryWebPaperRuntimeAfterDelayAsync(
        WebPaperRuntimeSlot slot,
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
                if (_webPaperRuntimeDisposing ||
                    IsExiting ||
                    !_webPaperRuntimeReconciliationEnabled ||
                    slot.RetryGeneration != generation ||
                    slot.State != WebPaperRuntimeState.Backoff ||
                    !_webPaperRuntimeSlots.TryGetValue(slot.PaperId, out var current) ||
                    !ReferenceEquals(current, slot))
                {
                    return;
                }
                StartWebPaperRuntimeSlot(slot);
            }),
            DispatcherPriority.Background);
    }

    private void RetryFailedWebPaperRuntimesAfterSettingsChanged(string providerId)
    {
        foreach (var slot in _webPaperRuntimeSlots.Values
                     .Where(slot =>
                         string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal) &&
                         slot.State is WebPaperRuntimeState.Backoff or WebPaperRuntimeState.Failed)
                     .ToArray())
        {
            slot.RetryGeneration++;
            slot.FailureCount = 0;
            slot.RunningSinceUtc = default;
            StartWebPaperRuntimeSlot(slot);
        }
    }

    private void SetWebPaperRuntimeHeader(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        string? text)
    {
        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
        {
            return;
        }

        var normalized = PaperWindow.NormalizePluginDisplayText(text);
        slot.HasHeaderValue = true;
        slot.HeaderText = normalized;
        slot.Paper.BodyHeaderText = normalized;
        if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
        {
            window.ApplyWebPaperRuntimeHeader(slot.ProviderId, normalized);
        }
        NotifyPaperDisplayTitleChanged(slot.PaperId);
    }

    private void SetWebPaperRuntimeCapsule(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        PaperCapsulePresentation? presentation)
    {
        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
        {
            return;
        }

        var normalized = PaperWindow.NormalizePluginCapsulePresentation(presentation);
        slot.HasCapsuleValue = true;
        slot.CapsulePresentation = normalized;
        slot.Paper.BodyCapsuleText = normalized == null
            ? string.Empty
            : PaperWindow.CapsulePresentationFallbackText(normalized);
        if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
        {
            window.ApplyWebPaperRuntimeCapsule(slot.ProviderId, normalized);
        }
    }

    private void SaveWebPaperRuntimeState(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        string? json)
    {
        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
        {
            return;
        }

        var normalized = PaperBodyPluginDataStore.NormalizeStateJson(json);
        PaperBodyPlugins.DataStore.SavePaperState(
            slot.ProviderId,
            slot.PaperId,
            slot.Descriptor.StateVersion,
            normalized);
        if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
        {
            window.ApplyExternalWebPaperRuntimeState(slot.ProviderId, normalized);
        }
    }

    internal void NotifyWebPaperRuntimeStateChanged(
        string paperId,
        string providerId,
        string stateJson)
    {
        if (_webPaperRuntimeSlots.TryGetValue(paperId, out var slot) &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal))
        {
            slot.Runtime?.OnStateChanged(stateJson);
        }
    }

    internal void NotifyWebPaperRuntimeSettingsChanged(
        string providerId,
        string settingsJson)
    {
        foreach (var slot in _webPaperRuntimeSlots.Values
                     .Where(slot => string.Equals(
                         slot.ProviderId,
                         providerId,
                         StringComparison.Ordinal)))
        {
            slot.Runtime?.OnSettingsChanged(settingsJson);
        }
    }

    internal bool PostBodyMessageToWebPaperRuntime(
        string paperId,
        string providerId,
        JsonElement payload)
    {
        return _webPaperRuntimeSlots.TryGetValue(paperId, out var slot) &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal) &&
            slot.Runtime?.PostBodyMessage(payload) == true;
    }

    private bool PostWebPaperRuntimeMessageToBody(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        JsonElement payload)
    {
        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
        {
            return false;
        }
        return _windows.TryGetValue(slot.PaperId, out var window) &&
            !window.IsClosed &&
            window.ReceiveWebPaperRuntimeMessage(slot.ProviderId, payload);
    }

    internal void ApplyWebPaperRuntimePresentationToWindow(PaperWindow window)
    {
        if (!_webPaperRuntimeSlots.TryGetValue(window.PaperId, out var slot) ||
            !string.Equals(
                slot.Paper.BodyProviderId,
                slot.ProviderId,
                StringComparison.Ordinal))
        {
            return;
        }

        window.ApplyWebPaperRuntimePresentation(
            slot.ProviderId,
            slot.HasHeaderValue,
            slot.HeaderText,
            slot.HasCapsuleValue,
            slot.CapsulePresentation);
    }

    private void ApplyWebPaperRuntimePresentationToWindowForSlot(
        WebPaperRuntimeSlot slot)
    {
        if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
        {
            ApplyWebPaperRuntimePresentationToWindow(window);
        }
    }

    private void DisposeWebPaperRuntimeLease(WebPaperRuntimeSlot slot)
    {
        var runtime = slot.Runtime;
        var hostApi = slot.HostApi;
        slot.Runtime = null;
        slot.HostApi = null;
        try { runtime?.Dispose(); } catch { }
        try { hostApi?.Dispose(); } catch { }
    }

    private void RemoveWebPaperRuntimeSlot(
        WebPaperRuntimeSlot slot,
        bool clearPresentation)
    {
        if (!_webPaperRuntimeSlots.TryGetValue(slot.PaperId, out var current) ||
            !ReferenceEquals(current, slot))
        {
            return;
        }

        _webPaperRuntimeSlots.Remove(slot.PaperId);
        slot.RetryGeneration++;
        DisposeWebPaperRuntimeLease(slot);
        if (clearPresentation &&
            string.Equals(slot.Paper.BodyProviderId, slot.ProviderId, StringComparison.Ordinal) &&
            State.Papers.Contains(slot.Paper))
        {
            slot.Paper.BodyHeaderText = string.Empty;
            slot.Paper.BodyCapsuleText = string.Empty;
            if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
            {
                window.ClearWebPaperRuntimePresentation(slot.ProviderId);
            }
            NotifyPaperDisplayTitleChanged(slot.PaperId);
        }
    }

    private void DisposeWebPaperRuntimes()
    {
        if (_webPaperRuntimeDisposing)
        {
            return;
        }
        _webPaperRuntimeDisposing = true;
        _webPaperRuntimeReconciliationEnabled = false;
        foreach (var slot in _webPaperRuntimeSlots.Values.ToArray())
        {
            slot.RetryGeneration++;
            DisposeWebPaperRuntimeLease(slot);
        }
        _webPaperRuntimeSlots.Clear();
    }
}
