using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private WebPaperRuntime? _webPaperRuntime;
    private PaperBodyPluginHostApi? _webPaperRuntimeHostApi;
    private string _webPaperRuntimeProviderId = string.Empty;
    private string _webPaperRuntimeFingerprint = string.Empty;
    private int _webPaperRuntimeGeneration;

    private bool HasLiveWebPaperRuntime(string? providerId = null) =>
        _webPaperRuntime != null &&
        (string.IsNullOrWhiteSpace(providerId) ||
         string.Equals(
             _webPaperRuntimeProviderId,
             providerId,
             StringComparison.Ordinal));

    private bool IsCurrentWebPaperRuntime(int generation, string providerId) =>
        _windowLifecycle == PaperWindowLifecycleState.Alive &&
        _webPaperRuntime != null &&
        generation == _webPaperRuntimeGeneration &&
        string.Equals(_webPaperRuntimeProviderId, providerId, StringComparison.Ordinal) &&
        string.Equals(
            NormalizeBodyProviderId(_paper.BodyProviderId),
            providerId,
            StringComparison.Ordinal);

    private void EnsureWebPaperRuntime(PaperBodyPluginDescriptor descriptor)
    {
        var requiresPaperRuntime =
            descriptor.Kind == PaperBodyPluginKind.Web &&
            descriptor.Manifest != null &&
            !string.IsNullOrWhiteSpace(descriptor.Manifest.PaperRuntimePath) &&
            (descriptor.RuntimeRequirements & PaperBodyRuntimeRequirements.BackgroundUpdates) != 0;
        if (!requiresPaperRuntime)
        {
            DisposeWebPaperRuntime();
            return;
        }

        if (_webPaperRuntime != null &&
            string.Equals(_webPaperRuntimeProviderId, descriptor.Id, StringComparison.Ordinal) &&
            string.Equals(
                _webPaperRuntimeFingerprint,
                descriptor.Fingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        DisposeWebPaperRuntime();
        var generation = ++_webPaperRuntimeGeneration;
        var providerId = descriptor.Id;
        bool IsActive() => IsCurrentWebPaperRuntime(generation, providerId);

        var hostApi = new PaperBodyPluginHostApi(
            _controller,
            _controller.PaperCommands,
            _paper.Id,
            providerId,
            descriptor.Permissions,
            IsActive,
            IsActive);
        var stored = ReadPluginState(providerId);
        WebPaperRuntime runtime;
        try
        {
            runtime = new WebPaperRuntime(
                descriptor,
                _paper.Id,
                stored.Json ?? "{}",
                _controller.PaperBodyPlugins.DataStore.GetSettingsJson(descriptor),
                hostApi,
                IsActive,
                title => InvokeWebPaperRuntimeCallback(
                    generation,
                    providerId,
                    () => _controller.UpdatePaperTitleFromPlugin(
                        _paper,
                        title,
                        providerId)),
                text => InvokeWebPaperRuntimeCallback(
                    generation,
                    providerId,
                    () => SetPluginHeaderText(text)),
                presentation => InvokeWebPaperRuntimeCallback(
                    generation,
                    providerId,
                    () => SetPluginCapsulePresentation(presentation)),
                json => InvokeWebPaperRuntimeCallback(
                    generation,
                    providerId,
                    () => SaveWebPaperRuntimeState(
                        generation,
                        providerId,
                        descriptor.StateVersion,
                        json)),
                payload => InvokeWebPaperRuntimeCallback(
                    generation,
                    providerId,
                    () => PostRuntimeMessageToCurrentWebBody(providerId, payload)),
                () => RequestWebPaperRuntimeRestart(generation, providerId));
        }
        catch
        {
            hostApi.Dispose();
            throw;
        }

        _webPaperRuntimeHostApi = hostApi;
        _webPaperRuntime = runtime;
        _webPaperRuntimeProviderId = providerId;
        _webPaperRuntimeFingerprint = descriptor.Fingerprint;
        _controller.QueuePluginStatusRefresh();
        _ = StartWebPaperRuntimeAsync(runtime, generation, providerId);
    }

    private async Task StartWebPaperRuntimeAsync(
        WebPaperRuntime runtime,
        int generation,
        string providerId)
    {
        try
        {
            await runtime.StartAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (!IsCurrentWebPaperRuntime(generation, providerId) ||
                        !ReferenceEquals(runtime, _webPaperRuntime))
                    {
                        return;
                    }
                    Trace.TraceWarning(
                        "Web paper runtime failed to start. Paper={0}; Provider={1}; Exception={2}",
                        _paper.Id,
                        providerId,
                        ex.GetBaseException());
                    DisposeWebPaperRuntime();
                }),
                DispatcherPriority.Background);
        }
    }

    private void InvokeWebPaperRuntimeCallback(
        int generation,
        string providerId,
        Action callback)
    {
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!IsCurrentWebPaperRuntime(generation, providerId))
                {
                    return;
                }
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning(
                        "Web paper runtime callback failed. Paper={0}; Provider={1}; Exception={2}",
                        _paper.Id,
                        providerId,
                        ex.GetBaseException());
                }
            }),
            DispatcherPriority.Background);
    }

    private void SaveWebPaperRuntimeState(
        int generation,
        string providerId,
        int stateVersion,
        string? json)
    {
        if (!IsCurrentWebPaperRuntime(generation, providerId))
        {
            return;
        }
        var normalized = NormalizePluginStateJson(json);
        SavePluginStateValidated(
            providerId,
            Math.Max(1, stateVersion),
            normalized);
        if (_paperBodyHost.Current is WebPaperBodySession body)
        {
            body.ApplyExternalState(normalized);
        }
    }

    private void NotifyWebPaperRuntimeStateChanged(string providerId, string stateJson)
    {
        if (_webPaperRuntime != null &&
            string.Equals(_webPaperRuntimeProviderId, providerId, StringComparison.Ordinal))
        {
            _webPaperRuntime.OnStateChanged(stateJson);
        }
    }

    private void NotifyWebPaperRuntimeSettingsChanged(
        string providerId,
        string settingsJson)
    {
        if (_webPaperRuntime != null &&
            string.Equals(_webPaperRuntimeProviderId, providerId, StringComparison.Ordinal))
        {
            _webPaperRuntime.OnSettingsChanged(settingsJson);
        }
    }

    private void PostBodyMessageToWebPaperRuntime(
        string providerId,
        JsonElement payload)
    {
        if (_webPaperRuntime != null &&
            string.Equals(_webPaperRuntimeProviderId, providerId, StringComparison.Ordinal))
        {
            _webPaperRuntime.PostBodyMessage(payload);
        }
    }

    private void PostRuntimeMessageToCurrentWebBody(
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

    private void RequestWebPaperRuntimeRestart(int generation, string providerId)
    {
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!IsCurrentWebPaperRuntime(generation, providerId))
                {
                    return;
                }
                if (!_controller.PaperBodyPlugins.TryGet(providerId, out var descriptor))
                {
                    DisposeWebPaperRuntime();
                    return;
                }

                Trace.TraceWarning(
                    "Restarting Web paper runtime. Paper={0}; Provider={1}",
                    _paper.Id,
                    providerId);
                DisposeWebPaperRuntime();
                EnsureWebPaperRuntime(descriptor);
            }),
            DispatcherPriority.Background);
    }

    private void DisposeWebPaperRuntime()
    {
        if (_webPaperRuntime == null && _webPaperRuntimeHostApi == null)
        {
            return;
        }

        _webPaperRuntimeGeneration++;
        var runtime = _webPaperRuntime;
        var hostApi = _webPaperRuntimeHostApi;
        _webPaperRuntime = null;
        _webPaperRuntimeHostApi = null;
        _webPaperRuntimeProviderId = string.Empty;
        _webPaperRuntimeFingerprint = string.Empty;
        try { runtime?.Dispose(); } catch { }
        try { hostApi?.Dispose(); } catch { }
        if (_windowLifecycle == PaperWindowLifecycleState.Alive)
        {
            _controller.QueuePluginStatusRefresh();
        }
    }
}
