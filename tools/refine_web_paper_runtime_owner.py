from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text.replace("\r\n", "\n"), encoding="utf-8")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected 1 match, got {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


# -----------------------------------------------------------------------------
# The per-paper runtime belongs to AppController / PaperData lifetime, not to a
# PaperWindow. A hidden paper can have no Window at all, so Window ownership
# would still couple background lifetime to presentation construction.
# -----------------------------------------------------------------------------
old_owner = ROOT / "src/PaperWindow.WebPaperRuntime.cs"
if not old_owner.exists():
    raise RuntimeError("PaperWindow.WebPaperRuntime.cs not found")
old_owner.unlink()

write(
    "src/AppController.WebPaperRuntime.cs",
    r'''using System.Diagnostics;
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

    internal void PostBodyMessageToWebPaperRuntime(
        string paperId,
        string providerId,
        JsonElement payload)
    {
        if (_webPaperRuntimeSlots.TryGetValue(paperId, out var slot) &&
            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal))
        {
            slot.Runtime?.PostBodyMessage(payload);
        }
    }

    private void PostWebPaperRuntimeMessageToBody(
        WebPaperRuntimeSlot slot,
        Guid runtimeId,
        JsonElement payload)
    {
        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))
        {
            return;
        }
        if (_windows.TryGetValue(slot.PaperId, out var window) && !window.IsClosed)
        {
            window.ReceiveWebPaperRuntimeMessage(slot.ProviderId, payload);
        }
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
''')

write(
    "src/PaperWindow.WebPaperRuntimePresentation.cs",
    r'''using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool HasWebPaperRuntimePresentationOwner =>
        _controller.HasWebPaperRuntimeOwnership(
            _paper.Id,
            NormalizeBodyProviderId(_paper.BodyProviderId));

    internal void ApplyWebPaperRuntimePresentation(
        string providerId,
        bool hasHeaderValue,
        string headerText,
        bool hasCapsuleValue,
        PaperCapsulePresentation? capsulePresentation)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (hasHeaderValue)
        {
            ApplyWebPaperRuntimeHeader(providerId, headerText);
        }
        if (hasCapsuleValue)
        {
            ApplyWebPaperRuntimeCapsule(providerId, capsulePresentation);
        }
    }

    internal void ApplyWebPaperRuntimeHeader(string providerId, string headerText)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        _pluginDisplayTitle = headerText;
        _paper.BodyHeaderText = headerText;
        if (_isShellBuilt)
        {
            RefreshPaperTitle();
        }
    }

    internal void ApplyWebPaperRuntimeCapsule(
        string providerId,
        PaperCapsulePresentation? presentation)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!_isShellBuilt)
        {
            _pluginCapsulePresentation = presentation;
            return;
        }
        SetPluginCapsulePresentation(presentation);
    }

    internal void ApplyExternalWebPaperRuntimeState(
        string providerId,
        string stateJson)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }
        if (_paperBodyHost.Current is WebPaperBodySession body)
        {
            body.ApplyExternalState(stateJson);
        }
    }

    internal void ReceiveWebPaperRuntimeMessage(
        string providerId,
        JsonElement payload)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }
        if (_paperBodyHost.Current is WebPaperBodySession body)
        {
            body.ReceiveRuntimeMessage(payload);
        }
    }

    private void ClearWebPaperRuntimePresentation(string providerId)
    {
        if (!string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        var hadHeader = !string.IsNullOrEmpty(_pluginDisplayTitle) ||
            !string.IsNullOrEmpty(_paper.BodyHeaderText);
        var hadCapsule = _pluginCapsulePresentation != null ||
            !string.IsNullOrEmpty(_paper.BodyCapsuleText);
        _pluginDisplayTitle = string.Empty;
        _pluginCapsulePresentation = null;
        _paper.BodyHeaderText = string.Empty;
        _paper.BodyCapsuleText = string.Empty;
        ResetPluginCapsuleCustomViews();
        if (_isShellBuilt)
        {
            if (hadHeader)
            {
                RefreshPaperTitle();
            }
            if (hadCapsule)
            {
                RefreshCapsuleLabel();
                ApplyCurrentCollapsedCapsuleWidth();
            }
        }
    }
}
''')

# -----------------------------------------------------------------------------
# Window body: it no longer owns/restarts/disposes PaperRuntime. It only bridges
# its visible body to the controller-owned per-paper runtime.
# -----------------------------------------------------------------------------
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasLiveWebPaperRuntime(\n                NormalizeBodyProviderId(_paper.BodyProviderId))) &&\n            !string.IsNullOrWhiteSpace(title);\n''',
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasWebPaperRuntimePresentationOwner) &&\n            !string.IsNullOrWhiteSpace(title);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasLiveWebPaperRuntime(\n                NormalizeBodyProviderId(_paper.BodyProviderId))) &&\n            !string.IsNullOrWhiteSpace(title);\n''',
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasWebPaperRuntimePresentationOwner) &&\n            !string.IsNullOrWhiteSpace(title);\n''')

for snippet in [
    "            DisposeWebPaperRuntime();\n            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);\n",
    "            DisposeWebPaperRuntime();\n            _bodyDescriptor = null;\n",
    "                DisposeWebPaperRuntime();\n                var stored = ReadPluginState(descriptor.Id);\n",
    "                EnsureWebPaperRuntime(descriptor);\n                var stored = ReadPluginState(descriptor.Id);\n",
]:
    text = read("src/PaperWindow.PluginBodies.cs")
    if snippet not in text:
        raise RuntimeError(f"PluginBodies snippet missing: {snippet!r}")
    if "markdownDescriptor" in snippet:
        replacement = "            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);\n"
    elif "_bodyDescriptor = null" in snippet:
        replacement = "            _bodyDescriptor = null;\n"
    elif "CreateNative" not in snippet and "var stored" in snippet:
        replacement = "                var stored = ReadPluginState(descriptor.Id);\n"
    else:
        replacement = snippet
    write("src/PaperWindow.PluginBodies.cs", text.replace(snippet, replacement, 1))

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''                    payload => PostBodyMessageToWebPaperRuntime(\n                        descriptor.Id,\n                        payload));\n''',
    '''                    payload => _controller.PostBodyMessageToWebPaperRuntime(\n                        _paper.Id,\n                        descriptor.Id,\n                        payload));\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        NotifyWebPaperRuntimeStateChanged(providerId, normalized);\n''',
    '''        _controller.NotifyWebPaperRuntimeStateChanged(\n            _paper.Id,\n            providerId,\n            normalized);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        var preservePaperPresentation = HasLiveWebPaperRuntime(\n            NormalizeBodyProviderId(_paper.BodyProviderId));\n''',
    '''        var preservePaperPresentation = HasWebPaperRuntimePresentationOwner;\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        CommitPendingEditsForSave();\n        DisposeWebPaperRuntime();\n        RemoveCurrentPaperBody();\n        _paper.BodyProviderId = normalized;\n''',
    '''        CommitPendingEditsForSave();\n        var previousProviderId = NormalizeBodyProviderId(_paper.BodyProviderId);\n        ClearWebPaperRuntimePresentation(previousProviderId);\n        RemoveCurrentPaperBody();\n        _paper.BodyProviderId = normalized;\n        _controller.ReconcileWebPaperRuntimes();\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        if (HasLiveWebPaperRuntime(NormalizeBodyProviderId(_paper.BodyProviderId)))\n''',
    '''        if (HasWebPaperRuntimePresentationOwner)\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        NotifyWebPaperRuntimeSettingsChanged(providerId, settingsJson);\n        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));\n''',
    '''        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);\n        DisposeWebPaperRuntime();\n        _bodyDescriptor = null;\n''',
    '''        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);\n        _bodyDescriptor = null;\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''    private static string NormalizePluginDisplayText(string? text)\n''',
    '''    internal static string NormalizePluginDisplayText(string? text)\n''')

# Plugin capsule normalization is needed by controller even when no window exists.
replace_once(
    "src/PaperWindow.PluginCapsule.cs",
    '''    private static PaperCapsulePresentation? NormalizePluginCapsulePresentation(\n''',
    '''    internal static PaperCapsulePresentation? NormalizePluginCapsulePresentation(\n''')
replace_once(
    "src/PaperWindow.PluginCapsule.cs",
    '''    private static string CapsulePresentationFallbackText(PaperCapsulePresentation presentation)\n''',
    '''    internal static string CapsulePresentationFallbackText(PaperCapsulePresentation presentation)\n''')
replace_once(
    "src/PaperWindow.PluginCapsule.cs",
    '''        if (_pluginCapsulePresentation == null || IsCurrentBodyProviderMarkdown || _bodyFailed)\n''',
    '''        if (_pluginCapsulePresentation == null ||\n            IsCurrentBodyProviderMarkdown ||\n            (_bodyFailed && !HasWebPaperRuntimePresentationOwner))\n''')
replace_once(
    "src/PaperWindow.PluginCapsule.cs",
    '''        if (presentation == null || IsCurrentBodyProviderMarkdown || _bodyFailed)\n''',
    '''        if (presentation == null ||\n            IsCurrentBodyProviderMarkdown ||\n            (_bodyFailed && !HasWebPaperRuntimePresentationOwner))\n''')
replace_once(
    "src/PaperWindow.PluginCapsule.cs",
    '''        if (presentation == null || IsCurrentBodyProviderMarkdown || _bodyFailed)\n''',
    '''        if (presentation == null ||\n            IsCurrentBodyProviderMarkdown ||\n            (_bodyFailed && !HasWebPaperRuntimePresentationOwner))\n''')
replace_once(
    "src/PaperWindow.EdgeCapsulePreviewContent.cs",
    '''        if (_pluginCapsulePresentation != null &&\n            !IsCurrentBodyProviderMarkdown &&\n            !_bodyFailed)\n''',
    '''        if (_pluginCapsulePresentation != null &&\n            !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasWebPaperRuntimePresentationOwner))\n''')

# Re-apply persistent runtime presentation after a deferred shell is finally built.
replace_once(
    "src/PaperWindow.cs",
    '''        BuildShell();\n        _isShellBuilt = true;\n        UpdateToolTipSetting();\n''',
    '''        BuildShell();\n        _isShellBuilt = true;\n        _controller.ApplyWebPaperRuntimePresentationToWindow(this);\n        UpdateToolTipSetting();\n''')

# Window plugin status becomes body-only; controller owns paper-runtime status.
write(
    "src/PaperWindow.PluginStatus.cs",
    r'''namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal bool HasFailedPluginBody(string providerId)
    {
        return _paper.Type == PaperTypes.Note &&
            _bodyFailed &&
            string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal);
    }

    internal bool HasRunningPluginBody(string providerId)
    {
        return _paper.Type == PaperTypes.Note &&
            !_bodyFailed &&
            _paperBodyHost.HasCurrent &&
            _bodyRuntimeVisible &&
            string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal);
    }
}
''')

# -----------------------------------------------------------------------------
# Reconcile per-paper runtimes from entity PaperData, independently of Window
# construction/visibility. Provider app runtime keeps its own delayed startup rule.
# -----------------------------------------------------------------------------
replace_once(
    "src/AppController.PluginStartup.cs",
    '''        if (IsExiting)\n        {\n            return;\n        }\n\n        // Explicit --hide keeps the existing startup-paper behavior''',
    '''        if (IsExiting)\n        {\n            return;\n        }\n\n        // Per-paper Web runtimes are owned by real PaperData, including papers that start hidden.\n        // They do not wait for visible shell construction or startupPaper presentation readiness.\n        EnableWebPaperRuntimeReconciliation();\n\n        // Explicit --hide keeps the existing startup-paper behavior''')

replace_once(
    "src/AppController.PluginStatus.cs",
    '''        if (hasDataIssue ||\n            HasPluginAppRuntimeFailure(descriptor.Id) ||\n''',
    '''        if (hasDataIssue ||\n            HasPluginAppRuntimeFailure(descriptor.Id) ||\n            HasWebPaperRuntimeFailure(descriptor.Id) ||\n''')
replace_once(
    "src/AppController.PluginStatus.cs",
    '''        return IsPluginAppRuntimeRunning(descriptor.Id) ||\n               _windows.Values.Any(window =>\n''',
    '''        return IsPluginAppRuntimeRunning(descriptor.Id) ||\n               HasRunningWebPaperRuntime(descriptor.Id) ||\n               _windows.Values.Any(window =>\n''')
replace_once(
    "src/AppController.PluginStatus.cs",
    '''        ReconcilePluginAppRuntimes();\n        QueuePluginStatusUiRefresh();\n''',
    '''        ReconcilePluginAppRuntimes();\n        ReconcileWebPaperRuntimes();\n        QueuePluginStatusUiRefresh();\n''')

replace_once(
    "src/AppController.PluginApi.cs",
    '''        // Deletion is committed before this cleanup pass. Reconcile from the final entity-paper\n        // set so deleting the last paper of a provider also ends its process-level app runtime.\n        ReconcilePluginAppRuntimes();\n''',
    '''        // Deletion is committed before this cleanup pass. Reconcile from the final entity-paper\n        // set so provider-level and per-paper runtimes lose deleted owners promptly.\n        ReconcilePluginAppRuntimes();\n        ReconcileWebPaperRuntimes();\n''')
replace_once(
    "src/AppController.PluginApi.cs",
    '''    internal void DisposePaperPluginHostRuntime()\n    {\n        DisposePluginAppRuntimes();\n''',
    '''    internal void DisposePaperPluginHostRuntime()\n    {\n        DisposeWebPaperRuntimes();\n        DisposePluginAppRuntimes();\n''')

# Settings propagate to hidden paper runtimes too, and a changed setting is an
# explicit recovery signal for failed/backoff runtimes.
replace_once(
    "src/AppController.Plugins.cs",
    '''        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);\n        RetryFailedPluginAppRuntimeAfterSettingsChanged(providerId);\n        foreach (var window in _windows.Values.ToList())\n''',
    '''        var settingsJson = _paperBodyPlugins.DataStore.GetSettingsJson(descriptor);\n        RetryFailedPluginAppRuntimeAfterSettingsChanged(providerId);\n        RetryFailedWebPaperRuntimesAfterSettingsChanged(providerId);\n        NotifyWebPaperRuntimeSettingsChanged(providerId, settingsJson);\n        foreach (var window in _windows.Values.ToList())\n''')

# User deletion and external/MCP creation/deletion are entity ownership changes,
# including when no PaperWindow exists.
replace_once(
    "src/AppController.cs",
    '''        State.Papers.RemoveAll(p => p.Id == paper.Id);\n        QueuePluginPaperStateDeletion(paper.Id);\n''',
    '''        State.Papers.RemoveAll(p => p.Id == paper.Id);\n        ReconcileWebPaperRuntimes();\n        QueuePluginPaperStateDeletion(paper.Id);\n''')
replace_once(
    "src/AppController.Mcp.cs",
    '''    internal void RollbackMcpCreatedPaper(PaperData paper)\n    {\n        State.Papers.Remove(paper);\n''',
    '''    internal void RollbackMcpCreatedPaper(PaperData paper)\n    {\n        State.Papers.Remove(paper);\n        ReconcileWebPaperRuntimes();\n''')
replace_once(
    "src/AppController.Mcp.cs",
    '''    internal void FinalizeMcpPaperCreated(PaperData paper, bool show)\n    {\n        paper.IsVisible = show;\n        RefreshTrayMenu();\n''',
    '''    internal void FinalizeMcpPaperCreated(PaperData paper, bool show)\n    {\n        paper.IsVisible = show;\n        ReconcileWebPaperRuntimes();\n        RefreshTrayMenu();\n''')
replace_once(
    "src/AppController.Mcp.cs",
    '''        _visibilityAnimationVersions.Remove(deleted.Id);\n        NotifyTodoReminderCollectionChanged();\n''',
    '''        _visibilityAnimationVersions.Remove(deleted.Id);\n        ReconcileWebPaperRuntimes();\n        NotifyTodoReminderCollectionChanged();\n''')

# -----------------------------------------------------------------------------
# Runtime transport: preserve state-version identity and queue a small bounded
# set of body->runtime messages while the hidden document is still starting.
# -----------------------------------------------------------------------------
replace_once(
    "src/WebPaperRuntime.cs",
    '''    private string _stateJson;\n    private string _settingsJson;\n''',
    '''    private string _stateJson;\n    private readonly int _stateVersion;\n    private readonly int _targetStateVersion;\n    private string _settingsJson;\n    private readonly Queue<JsonElement> _pendingBodyMessages = new();\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''        string paperId,\n        string stateJson,\n        string settingsJson,\n''',
    '''        string paperId,\n        string stateJson,\n        int stateVersion,\n        int targetStateVersion,\n        string settingsJson,\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''        _paperId = paperId;\n        _stateJson = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;\n        _settingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;\n''',
    '''        _paperId = paperId;\n        _stateJson = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;\n        _stateVersion = Math.Max(1, stateVersion);\n        _targetStateVersion = Math.Max(_stateVersion, targetStateVersion);\n        _settingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''        SendInitialize();\n        _startupReady.TrySetResult(true);\n''',
    '''        SendInitialize();\n        FlushPendingBodyMessages();\n        _startupReady.TrySetResult(true);\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''            state = ParseState(_stateJson),\n            stateVersion = _descriptor.StateVersion,\n            settings = ParseState(_settingsJson),\n''',
    '''            state = ParseState(_stateJson),\n            stateVersion = _stateVersion,\n            targetStateVersion = _targetStateVersion,\n            settings = ParseState(_settingsJson),\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''    public void PostBodyMessage(JsonElement payload)\n    {\n        Send(new\n        {\n            type = "bodyMessage",\n            payload\n        });\n    }\n''',
    '''    public void PostBodyMessage(JsonElement payload)\n    {\n        if (_disposed)\n        {\n            return;\n        }\n        if (!_documentReady)\n        {\n            while (_pendingBodyMessages.Count >= 32)\n            {\n                _pendingBodyMessages.Dequeue();\n            }\n            _pendingBodyMessages.Enqueue(payload.Clone());\n            return;\n        }\n        SendBodyMessage(payload);\n    }\n\n    private void FlushPendingBodyMessages()\n    {\n        while (_documentReady && _pendingBodyMessages.Count > 0)\n        {\n            SendBodyMessage(_pendingBodyMessages.Dequeue());\n        }\n    }\n\n    private void SendBodyMessage(JsonElement payload)\n    {\n        Send(new\n        {\n            type = "bodyMessage",\n            payload\n        });\n    }\n''')
replace_once(
    "src/WebPaperRuntime.cs",
    '''        ClearHostSubscriptions();\n        if (_webView.CoreWebView2 is { } core)\n''',
    '''        ClearHostSubscriptions();\n        _pendingBodyMessages.Clear();\n        if (_webView.CoreWebView2 is { } core)\n''')

# -----------------------------------------------------------------------------
# Documentation and policy checks.
# -----------------------------------------------------------------------------
replace_once(
    "ARCHITECTURE.md",
    '''Web 插件把三种生命周期分开：`appRuntime` 是 Provider 级单例；`paperRuntime` 是声明 `backgroundUpdates` 时每张 Paper 独立的后台 WebView；`WebPaperBodySession` 只负责完整纸片 UI。PaperRuntime 从创建起固定挂在后台宿主，不跨 HWND；Body 的折叠、失败、reload 或重建都不会销毁对应 PaperRuntime。删除 Paper、切换 Provider 或 PaperWindow 最终销毁时才结束 PaperRuntime。\n''',
    '''Web 插件把三种生命周期分开：`appRuntime` 是 Provider 级单例；`paperRuntime` 是声明 `backgroundUpdates` 时每张 Paper 独立的后台 WebView；`WebPaperBodySession` 只负责完整纸片 UI。`AppController` 以真实 `PaperData` / `paperId` 为 PaperRuntime authority，因此隐藏纸片即使没有 `PaperWindow` 也继续拥有自己的后台实例。PaperRuntime 从创建起固定挂在后台宿主，不跨 HWND；Body 的折叠、失败、reload、重建以及 Window 是否存在都不会销毁它。删除 Paper、切换 Provider 或应用退出才结束对应 PaperRuntime。\n''')

with (ROOT / "DECISIONS.md").open("a", encoding="utf-8") as f:
    f.write(r'''

---

## D-024 — Web `backgroundUpdates` 使用 per-Paper Runtime，不借 Body WebView 保活

**Status:** Accepted

### Decision

Web provider 声明 `backgroundUpdates` 时必须同时声明 `paperRuntime` 入口。宿主由 `AppController` 按真实 `PaperData.Id` 持有一份独立后台 WebView；它从创建起固定挂在后台 runtime host，不进入 `PaperWindow`，也不因 Paper 隐藏、折叠、Body reload/失败/重建或当前没有 Window 而结束。

`WebPaperBodySession` 只负责完整正文 UI；provider 级 `appRuntime` 继续保持每 provider 0/1 的全局生命周期。Native `backgroundUpdates` 保持现有 body-session 语义，因为它没有 WebView 跨 HWND 的 controller 搬运问题。

### Why

旧实现让同一个 WebView 同时承担前台 UI 和后台 JS runtime。未展示的 WebView 先挂在隐藏 HWND，第一次进入真实 PaperWindow 时又必须 Dispose/Recreate，导致 timer、Promise、WebSocket、closure 和内存状态被 UI 宿主切换误杀。把 runtime 仅移到 `PaperWindow` 仍不完整，因为启动时本来就隐藏的 Paper 可以没有 Window。

### Rejected / Do not reintroduce

- 不把已初始化的 Body WebView 在隐藏 HWND 与 PaperWindow HWND 之间搬运。
- 不用 provider 级 `appRuntime` 模拟多 Paper 实例；每张 Paper 的后台实例必须独立。
- 不让 PaperRuntime lifetime 依赖 `PaperWindow` 是否已经构造。
- 不用保存 JSON 假装能恢复 WebSocket、Promise、timer 或 JS 闭包的连续 runtime。
''')

replace_once(
    "tests/PaperTodo.ProtocolPolicyChecks/Program.cs",
    '''            CheckSharedWebInfrastructure(host);\n            CheckWebBodyNavigationIdentity(host);\n''',
    '''            CheckSharedWebInfrastructure(host);\n            CheckWebPaperRuntimeAuthority(host);\n            CheckWebBodyNavigationIdentity(host);\n''')
replace_once(
    "tests/PaperTodo.ProtocolPolicyChecks/Program.cs",
    '''    private static void CheckWebBodyNavigationIdentity(Assembly host)\n''',
    r'''    private static void CheckWebPaperRuntimeAuthority(Assembly host)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        var window = RequireType(host, "PaperTodo.PaperWindow");
        var runtime = RequireType(host, "PaperTodo.WebPaperRuntime");
        var manifest = RequireType(host, "PaperTodo.PaperBodyPluginManifest");

        Assert(
            controller.GetField("_webPaperRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Per-paper Web runtime ownership must live on AppController by paper id.");
        Assert(
            window.GetField("_webPaperRuntime", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "PaperWindow must not own the persistent Web paper runtime.");
        Assert(
            runtime.GetField("_startupReady", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Web paper runtime must wait for its hidden document to become ready.");
        Assert(
            runtime.GetField("_pendingBodyMessages", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Body-to-paper-runtime startup messages need a bounded pre-ready queue.");
        Assert(manifest.GetProperty("PaperRuntime") != null,
            "Web per-paper runtime entry is not represented in the parsed manifest.");
        Assert(manifest.GetProperty("PaperRuntimePath") != null,
            "Web per-paper runtime resolved path is not cached by discovery.");
    }

    private static void CheckWebBodyNavigationIdentity(Assembly host)
''')
replace_once(
    "tests/PaperTodo.ProtocolPolicyChecks/Program.cs",
    '''        Assert(manifest.GetProperty("RuntimePath") != null,\n            "Web app runtime resolved path is not cached by plugin discovery.");\n        Assert(manifest.GetProperty("MiniMaxSize") != null,\n''',
    '''        Assert(manifest.GetProperty("RuntimePath") != null,\n            "Web app runtime resolved path is not cached by plugin discovery.");\n        Assert(manifest.GetProperty("PaperRuntime") != null &&\n               manifest.GetProperty("PaperRuntimePath") != null,\n            "Web per-paper runtime manifest fields are missing.");\n        Assert(manifest.GetProperty("MiniMaxSize") != null,\n''')

print("paper runtime ownership audit fixes applied")
