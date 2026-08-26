using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// One persistent background Web runtime for one Paper. It never owns visible UI and never moves
/// between HWNDs; the matching WebPaperBodySession may be created, hidden, failed or rebuilt without
/// changing this runtime's JS world.
/// </summary>
internal sealed class WebPaperRuntime : IDisposable
{
    private const int MaximumConcurrentColdStarts = 3;
    private static readonly SemaphoreSlim StartupGate = new(
        MaximumConcurrentColdStarts,
        MaximumConcurrentColdStarts);

    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly string _paperId;
    private readonly PaperBodyPluginHostApi _workspace;
    private readonly Action<string> _setTitle;
    private readonly Action<string> _setHeaderText;
    private readonly Action<PaperCapsulePresentation?> _setCapsulePresentation;
    private readonly Action<string> _saveState;
    private readonly Func<JsonElement, bool> _postBodyMessage;
    private readonly Action _requestRestart;
    private readonly Func<bool> _isActive;
    private readonly WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _startupReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, IDisposable> _hostSubscriptions =
        new(StringComparer.Ordinal);

    private string _expectedOrigin = string.Empty;
    private string _stateJson;
    private int _stateVersion;
    private readonly int _targetStateVersion;
    private string _settingsJson;
    private readonly Queue<JsonElement> _pendingBodyMessages = new();
    private bool _documentReady;
    private bool _startupCompleted;
    private bool _reloadRecoveryPending;
    private bool _restartRequested;
    private bool _disposed;
    private ulong _documentNavigationId;
    private bool _hasDocumentNavigation;
    private string? _activeDocumentToken;
    private string? _departingDocumentToken;
    private int _documentGeneration;

    public WebPaperRuntime(
        PaperBodyPluginDescriptor descriptor,
        string paperId,
        string stateJson,
        int stateVersion,
        int targetStateVersion,
        string settingsJson,
        PaperBodyPluginHostApi workspace,
        Func<bool> isActive,
        Action<string> setTitle,
        Action<string> setHeaderText,
        Action<PaperCapsulePresentation?> setCapsulePresentation,
        Action<string> saveState,
        Func<JsonElement, bool> postBodyMessage,
        Action requestRestart)
    {
        _descriptor = descriptor;
        _paperId = paperId;
        _stateJson = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;
        _stateVersion = Math.Max(1, stateVersion);
        _targetStateVersion = Math.Max(_stateVersion, targetStateVersion);
        _settingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        _workspace = workspace;
        _isActive = isActive;
        _setTitle = setTitle;
        _setHeaderText = setHeaderText;
        _setCapsulePresentation = setCapsulePresentation;
        _saveState = saveState;
        _postBodyMessage = postBodyMessage;
        _requestRestart = requestRestart;

        _webView = new WebView2CompositionControl
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _webView.SetValue(UIElement.OpacityProperty, 0.0);
        if (!WebPluginRuntimeInfrastructure.AttachBackground(_webView))
        {
            throw new InvalidOperationException(
                "PaperTodo could not attach the Web paper runtime to its background host.");
        }
    }

    public async Task StartAsync()
    {
        await StartupGate.WaitAsync(_lifetime.Token);
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            StartupGate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        ThrowIfInactive();
        var manifest = _descriptor.Manifest
            ?? throw new InvalidOperationException("Web plugin manifest is unavailable.");
        if (string.IsNullOrWhiteSpace(manifest.PaperRuntimePath))
        {
            throw new InvalidOperationException("Web paper runtime entry is unavailable.");
        }
        var webRoot = Path.GetDirectoryName(manifest.EntryPath)
            ?? throw new InvalidOperationException("Web plugin entry has no containing directory.");

        var environment = await WebPluginRuntimeInfrastructure.EnvironmentAsync(
            manifest.DirectoryPath);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();
        await _webView.EnsureCoreWebView2Async(environment);
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();

        var core = _webView.CoreWebView2
            ?? throw new InvalidOperationException(
                "WebView2 initialization returned no CoreWebView2 instance.");
        WebPluginRuntimeInfrastructure.ConfigureBackgroundCore(core);

        var hostName = WebPluginRuntimeInfrastructure.HostName(_descriptor.Id);
        _expectedOrigin = WebPluginRuntimeInfrastructure.Origin(_descriptor.Id);
        var runtimeUri = WebPluginRuntimeInfrastructure.LocalEntryUri(
            _expectedOrigin,
            webRoot,
            manifest.PaperRuntimePath);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            BuildBridgeScript(_expectedOrigin));
        _lifetime.Token.ThrowIfCancellationRequested();
        ThrowIfInactive();
        core.SetVirtualHostNameToFolderMapping(
            hostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        _webView.Source = runtimeUri;

        await _startupReady.Task.WaitAsync(_lifetime.Token);
        _startupCompleted = true;
        ThrowIfInactive();
    }

    private static string BuildBridgeScript(string expectedOrigin)
    {
        var originJson = JsonSerializer.Serialize(expectedOrigin);
        return $$"""
            (() => {
              const expectedOrigin = {{originJson}};
              if (window !== window.top || location.origin !== expectedOrigin || window.papertodo) return;
              const listeners = new Set();
              const hostEventListeners = new Map();
              const pending = new Map();
              let sequence = 0;
              let stateProvider = null;
              let documentToken = null;
              let pendingState = null;
              let hasPendingState = false;
              let markHostReady;
              const hostReady = new Promise(resolve => { markHostReady = resolve; });
              const rawPost = (type, payload = null) => {
                window.chrome.webview.postMessage({ type, payload, documentToken });
              };
              const post = (type, payload = null) => {
                void hostReady.then(() => rawPost(type, payload));
              };
              const request = async (method, params = {}) => {
                await hostReady;
                const requestId = `p${++sequence}`;
                const payload = { requestId, method: String(method ?? ''), params: params ?? {} };
                return new Promise((resolve, reject) => {
                  pending.set(requestId, { resolve, reject });
                  rawPost('hostRequest', payload);
                });
              };
              const saveState = state => {
                const nextState = state ?? {};
                if (documentToken) {
                  rawPost('saveState', nextState);
                  return;
                }
                pendingState = nextState;
                hasPendingState = true;
              };
              const flushState = () => {
                if (typeof stateProvider !== 'function') return;
                try { saveState(stateProvider()); } catch { }
              };
              const paper = Object.freeze({
                setTitle(title) { post('setTitle', String(title ?? '')); },
                setHeaderText(text) { post('setHeaderText', String(text ?? '')); },
                setCapsulePresentation(presentation) {
                  post('setCapsulePresentation', presentation ?? null);
                },
                show(options = {}) { return request('paper.show', options); },
                hide() { return request('paper.hide'); },
                toggle(options = {}) { return request('paper.toggle', options); },
                expand(options = {}) { return request('paper.expand', options); },
                collapse() { return request('paper.collapse'); },
                toggleCollapsed(options = {}) { return request('paper.toggleCollapsed', options); },
                activate() { return request('paper.activate'); }
              });
              const body = Object.freeze({
                post(message) { return request('body.post', { message: message ?? null }); }
              });
              const workspace = Object.freeze({ request });
              window.papertodo = Object.freeze({
                surface: 'paperRuntime',
                paper,
                body,
                workspace,
                request,
                saveState,
                flushState,
                registerStateProvider(provider) {
                  stateProvider = typeof provider === 'function' ? provider : null;
                  return () => { if (stateProvider === provider) stateProvider = null; };
                },
                onHostEvent(types, listener, options = {}) {
                  if (typeof listener !== 'function') return () => {};
                  const values = Array.isArray(types)
                    ? types.map(value => String(value ?? '')).filter(Boolean)
                    : [];
                  if (values.length === 0) return () => {};
                  const subscriptionId = `s${++sequence}`;
                  hostEventListeners.set(subscriptionId, listener);
                  post('subscribeHostEvents', {
                    subscriptionId,
                    types: values,
                    paperIds: Array.isArray(options.paperIds)
                      ? options.paperIds.map(value => String(value ?? '')).filter(Boolean)
                      : null,
                    excludeOwnOperations: options.excludeOwnOperations !== false
                  });
                  return () => {
                    if (!hostEventListeners.delete(subscriptionId)) return;
                    post('unsubscribeHostEvents', { subscriptionId });
                  };
                },
                onEvent(listener) {
                  if (typeof listener !== 'function') return () => {};
                  listeners.add(listener);
                  return () => listeners.delete(listener);
                }
              });
              window.chrome.webview.addEventListener('message', event => {
                const message = event.data;
                if (message?.type === 'initialize') {
                  documentToken = typeof message.documentToken === 'string'
                    ? message.documentToken
                    : null;
                  if (documentToken && hasPendingState) {
                    rawPost('saveState', pendingState);
                  }
                  pendingState = null;
                  hasPendingState = false;
                  markHostReady();
                }
                if (message?.type === 'commitRequested') flushState();
                if (message?.type === 'hostResponse') {
                  const waiter = pending.get(message.requestId);
                  if (waiter) {
                    pending.delete(message.requestId);
                    if (message.ok) waiter.resolve(message.result);
                    else {
                      const error = new Error(message.error?.message ?? 'PaperTodo paper-runtime request failed.');
                      error.code = message.error?.code ?? 'host_error';
                      waiter.reject(error);
                    }
                  }
                } else if (message?.type === 'hostEvent') {
                  const listener = hostEventListeners.get(message.subscriptionId);
                  if (listener) {
                    try { listener(message.event); } catch { }
                  }
                } else if (message?.type === 'hostSubscriptionError') {
                  hostEventListeners.delete(message.subscriptionId);
                }
                for (const listener of [...listeners]) {
                  try { listener(message); } catch { }
                }
                window.dispatchEvent(new CustomEvent('papertodo', { detail: message }));
              });
              window.addEventListener('beforeunload', flushState);
            })();
            """;
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }
        if (!string.IsNullOrEmpty(_expectedOrigin) &&
            !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Uri, _expectedOrigin))
        {
            e.Cancel = true;
            return;
        }

        _documentNavigationId = e.NavigationId;
        _hasDocumentNavigation = true;
        if (!string.IsNullOrWhiteSpace(_activeDocumentToken))
        {
            _departingDocumentToken = _activeDocumentToken;
            _activeDocumentToken = null;
        }
        _documentGeneration++;
        _documentReady = false;
        ClearHostSubscriptions();
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !_hasDocumentNavigation ||
            e.NavigationId != _documentNavigationId)
        {
            return;
        }

        _hasDocumentNavigation = false;
        if (!e.IsSuccess ||
            !WebPluginRuntimeInfrastructure.IsSameOrigin(
                _webView.Source?.AbsoluteUri,
                _expectedOrigin))
        {
            _reloadRecoveryPending = false;
            _documentReady = false;
            _activeDocumentToken = null;
            _departingDocumentToken = null;
            FailStartupOrRestart(
                $"Web paper runtime navigation failed ({e.WebErrorStatus}).");
            return;
        }

        _reloadRecoveryPending = false;
        _documentReady = true;
        _activeDocumentToken = Guid.NewGuid().ToString("N");
        _departingDocumentToken = null;
        SendInitialize();
        FlushPendingBodyMessages();
        _startupReady.TrySetResult(true);
    }

    private void SendInitialize()
    {
        Send(new
        {
            type = "initialize",
            surface = "paperRuntime",
            documentToken = _activeDocumentToken,
            paperId = _paperId,
            providerId = _descriptor.Id,
            apiVersion = _descriptor.ApiVersion,
            state = ParseState(_stateJson),
            stateVersion = _stateVersion,
            targetStateVersion = _targetStateVersion,
            settings = ParseState(_settingsJson),
            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray()
        });
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Source, _expectedOrigin))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeValue) ||
                typeValue.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeValue.GetString() ?? string.Empty;
            if (!CanAcceptDocumentMessage(type, _documentReady))
            {
                return;
            }
            var documentToken = root.TryGetProperty("documentToken", out var tokenValue) &&
                                tokenValue.ValueKind == JsonValueKind.String
                ? tokenValue.GetString()
                : null;
            if (!HasDocumentAuthority(type, documentToken))
            {
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadValue)
                ? payloadValue
                : default;
            switch (type)
            {
                case "saveState":
                    SaveStateFromRuntime(payload);
                    break;
                case "setTitle":
                    _setTitle(ReadPayloadString(payload));
                    break;
                case "setHeaderText":
                    _setHeaderText(ReadPayloadString(payload));
                    break;
                case "setCapsulePresentation":
                    _setCapsulePresentation(
                        payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                            ? null
                            : payload.Deserialize<PaperCapsulePresentation>(
                                WebPluginRuntimeInfrastructure.JsonOptions));
                    break;
                case "hostRequest":
                    HandleHostRequest(payload);
                    break;
                case "subscribeHostEvents":
                    HandleSubscribeHostEvents(payload);
                    break;
                case "unsubscribeHostEvents":
                    HandleUnsubscribeHostEvents(payload);
                    break;
            }
        }
        catch
        {
            // Malformed or failed plugin messages stay isolated to this paper runtime.
        }
    }

    private static bool CanAcceptDocumentMessage(string type, bool documentReady) =>
        string.Equals(type, "saveState", StringComparison.Ordinal) || documentReady;

    private bool HasDocumentAuthority(string type, string? documentToken)
    {
        if (string.IsNullOrWhiteSpace(documentToken))
        {
            return false;
        }
        if (_documentReady &&
            string.Equals(documentToken, _activeDocumentToken, StringComparison.Ordinal))
        {
            return true;
        }
        return string.Equals(type, "saveState", StringComparison.Ordinal) &&
            !_documentReady &&
            string.Equals(documentToken, _departingDocumentToken, StringComparison.Ordinal);
    }

    private void SaveStateFromRuntime(JsonElement payload)
    {
        var normalized = PaperBodyPluginDataStore.NormalizeStateJson(
            payload.ValueKind == JsonValueKind.Undefined ? "{}" : payload.GetRawText());
        _stateJson = normalized;
        _stateVersion = _targetStateVersion;
        _saveState(normalized);
    }

    private void HandleHostRequest(JsonElement payload)
    {
        var requestId = WebPluginRuntimeInfrastructure.RequiredString(payload, "requestId");
        var documentGeneration = _documentGeneration;
        try
        {
            var method = WebPluginRuntimeInfrastructure.RequiredString(payload, "method");
            var parameters = WebPluginRuntimeInfrastructure.ParametersOrEmpty(payload);
            object? result;
            if (string.Equals(method, "settings.get", StringComparison.Ordinal))
            {
                result = ParseState(_settingsJson);
            }
            else if (string.Equals(method, "body.post", StringComparison.Ordinal))
            {
                var message = parameters.ValueKind == JsonValueKind.Object &&
                              parameters.TryGetProperty("message", out var messageValue)
                    ? messageValue
                    : default;
                if (!_postBodyMessage(
                        message.ValueKind == JsonValueKind.Undefined
                            ? JsonSerializer.SerializeToElement<object?>(null)
                            : message.Clone()))
                {
                    throw new PaperTodoPluginException(
                        "body_unavailable",
                        "The paper body is not ready to accept this message.");
                }
                result = null;
            }
            else if (method.StartsWith("paper.", StringComparison.Ordinal))
            {
                result = ExecutePaperPresentationRequest(method, parameters);
            }
            else
            {
                result = WebPluginWorkspaceRequests.Execute(
                    _workspace,
                    method,
                    parameters);
            }
            if (documentGeneration != _documentGeneration) return;
            Send(new { type = "hostResponse", requestId, ok = true, result });
        }
        catch (PaperTodoPluginException ex)
        {
            if (documentGeneration != _documentGeneration) return;
            Send(new
            {
                type = "hostResponse",
                requestId,
                ok = false,
                error = new { code = ex.Code, message = ex.Message }
            });
        }
        catch
        {
            if (documentGeneration != _documentGeneration) return;
            Send(new
            {
                type = "hostResponse",
                requestId,
                ok = false,
                error = new
                {
                    code = "host_error",
                    message = "PaperTodo could not complete the paper-runtime request."
                }
            });
        }
    }

    private object? ExecutePaperPresentationRequest(
        string method,
        JsonElement parameters)
    {
        var activate = OptionalBoolean(parameters, "activate") ?? true;
        switch (method)
        {
            case "paper.show":
                _workspace.Show(activate);
                break;
            case "paper.hide":
                _workspace.Hide();
                break;
            case "paper.toggle":
                _workspace.ToggleVisibility(activate);
                break;
            case "paper.expand":
                _workspace.Expand(activate);
                break;
            case "paper.collapse":
                _workspace.Collapse();
                break;
            case "paper.toggleCollapsed":
                _workspace.ToggleCollapsed(activate);
                break;
            case "paper.activate":
                _workspace.Activate();
                break;
            default:
                throw new PaperTodoPluginException(
                    "method_not_found",
                    $"Unknown PaperTodo paper presentation method: {method}");
        }
        return null;
    }

    private void HandleSubscribeHostEvents(JsonElement payload)
    {
        var subscriptionId = WebPluginRuntimeInfrastructure.RequiredString(
            payload,
            "subscriptionId");
        var documentGeneration = _documentGeneration;
        try
        {
            if (_hostSubscriptions.Remove(subscriptionId, out var existing))
            {
                existing.Dispose();
            }
            _hostSubscriptions[subscriptionId] = _workspace.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = ReadEventKinds(payload),
                    PaperIds = ReadStringSet(payload, "paperIds"),
                    ExcludeOwnOperations = OptionalBoolean(
                        payload,
                        "excludeOwnOperations") ?? true
                },
                value =>
                {
                    if (documentGeneration != _documentGeneration) return;
                    var eventJson = JsonSerializer.SerializeToElement(
                        value,
                        value.GetType(),
                        WebPluginRuntimeInfrastructure.JsonOptions);
                    Send(new
                    {
                        type = "hostEvent",
                        subscriptionId,
                        @event = eventJson
                    });
                });
        }
        catch (PaperTodoPluginException ex)
        {
            Send(new
            {
                type = "hostSubscriptionError",
                subscriptionId,
                error = new { code = ex.Code, message = ex.Message }
            });
        }
    }

    private void HandleUnsubscribeHostEvents(JsonElement payload)
    {
        var subscriptionId = WebPluginRuntimeInfrastructure.RequiredString(
            payload,
            "subscriptionId");
        if (_hostSubscriptions.Remove(subscriptionId, out var subscription))
        {
            subscription.Dispose();
        }
    }

    private void ClearHostSubscriptions()
    {
        foreach (var subscription in _hostSubscriptions.Values)
        {
            try { subscription.Dispose(); } catch { }
        }
        _hostSubscriptions.Clear();
    }

    private static HashSet<PaperTodoEventKind> ReadEventKinds(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("types", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new PaperTodoPluginException("invalid_params", "types must be an array.");
        }
        var result = new HashSet<PaperTodoEventKind>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            result.Add(value.GetString() switch
            {
                "paper.created" => PaperTodoEventKind.PaperCreated,
                "paper.changed" => PaperTodoEventKind.PaperChanged,
                "paper.deleted" => PaperTodoEventKind.PaperDeleted,
                "todo.created" => PaperTodoEventKind.TodoCreated,
                "todo.changed" => PaperTodoEventKind.TodoChanged,
                "todo.deleted" => PaperTodoEventKind.TodoDeleted,
                "note.changed" => PaperTodoEventKind.NoteChanged,
                var unknown => throw new PaperTodoPluginException(
                    "invalid_params",
                    $"Unknown event type: {unknown}")
            });
        }
        if (result.Count == 0)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                "types must contain at least one event type.");
        }
        return result;
    }

    private static HashSet<string>? ReadStringSet(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var values) ||
            values.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} must be an array.");
        }
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool? OptionalBoolean(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static string ReadPayloadString(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String
            ? payload.GetString() ?? string.Empty
            : string.Empty;

    private static JsonElement ParseState(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    public void OnStateChanged(string stateJson)
    {
        var normalized = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;
        if (string.Equals(normalized, _stateJson, StringComparison.Ordinal))
        {
            return;
        }
        _stateJson = normalized;
        _stateVersion = _targetStateVersion;
        Send(new
        {
            type = "stateChanged",
            state = ParseState(_stateJson),
            stateVersion = _descriptor.StateVersion
        });
    }

    public void OnSettingsChanged(string settingsJson)
    {
        var normalized = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        if (string.Equals(normalized, _settingsJson, StringComparison.Ordinal))
        {
            return;
        }
        _settingsJson = normalized;
        Send(new
        {
            type = "settingsChanged",
            settings = ParseState(_settingsJson)
        });
    }

    public bool PostBodyMessage(JsonElement payload)
    {
        if (_disposed)
        {
            return false;
        }
        if (!_documentReady)
        {
            if (_pendingBodyMessages.Count >= 32)
            {
                return false;
            }
            _pendingBodyMessages.Enqueue(payload.Clone());
            return true;
        }
        return SendBodyMessage(payload);
    }

    private void FlushPendingBodyMessages()
    {
        while (_documentReady && _pendingBodyMessages.Count > 0)
        {
            if (!SendBodyMessage(_pendingBodyMessages.Peek()))
            {
                break;
            }
            _pendingBodyMessages.Dequeue();
        }
    }

    private bool SendBodyMessage(JsonElement payload) =>
        Send(new
        {
            type = "bodyMessage",
            payload
        });

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) || _disposed)
        {
            return;
        }

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                FailStartupOrRestart("The WebView2 browser process exited.");
                return;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                RecoverRendererByReload();
                return;
            default:
                return;
        }
    }

    private void RecoverRendererByReload()
    {
        if (_disposed || _restartRequested || _reloadRecoveryPending)
        {
            return;
        }
        _reloadRecoveryPending = true;
        _documentReady = false;
        ClearHostSubscriptions();

        var dispatcher = _webView.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            FailStartupOrRestart("The Web paper runtime dispatcher is shutting down.");
            return;
        }

        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (_disposed || _restartRequested || !_reloadRecoveryPending)
                {
                    return;
                }
                try
                {
                    var core = _webView.CoreWebView2;
                    if (core == null)
                    {
                        FailStartupOrRestart(
                            "The Web paper runtime renderer could not be reloaded.");
                        return;
                    }
                    core.Reload();
                }
                catch (Exception ex)
                {
                    FailStartupOrRestart(
                        $"The Web paper runtime renderer reload failed: {ex.GetBaseException().Message}");
                }
            }),
            DispatcherPriority.Background);
    }

    private void FailStartupOrRestart(string message)
    {
        _hasDocumentNavigation = false;
        _reloadRecoveryPending = false;
        _documentReady = false;
        _activeDocumentToken = null;
        _departingDocumentToken = null;
        _documentGeneration++;
        ClearHostSubscriptions();
        if (!_startupCompleted &&
            _startupReady.TrySetException(new InvalidOperationException(message)))
        {
            return;
        }
        RequestRestart();
    }

    private void RequestRestart()
    {
        if (_disposed || _restartRequested)
        {
            return;
        }
        _restartRequested = true;
        try { _requestRestart(); } catch { }
    }

    private bool Send(object value)
    {
        if (!_documentReady || _disposed || !_isActive() || _webView.CoreWebView2 == null)
        {
            return false;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(
                    value,
                    WebPluginRuntimeInfrastructure.JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ThrowIfInactive()
    {
        if (_disposed || !_isActive())
        {
            throw new InvalidOperationException("The Web paper runtime is no longer active.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        // Best effort only; reliable durability is saveState-at-mutation time.
        Send(new { type = "commitRequested" });
        _documentReady = false;
        _activeDocumentToken = null;
        _departingDocumentToken = null;
        _documentGeneration++;
        _disposed = true;
        _startupReady.TrySetCanceled();
        _lifetime.Cancel();
        ClearHostSubscriptions();
        _pendingBodyMessages.Clear();
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }
        WebPluginRuntimeInfrastructure.DetachBackground(_webView);
        try { _webView.Dispose(); } catch { }
        _lifetime.Dispose();
    }
}
