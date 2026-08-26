using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

// Web plugins are trusted. Top-level body navigation stays on the plugin's local origin; normal
// http/https/mailto external navigation and external new-window requests are handed to the system
// shell. Same-origin frames/popups and permission behavior remain WebView2-owned, and PaperTodo's
// host bridge is restricted to the local top-level origin.
internal sealed partial class WebPaperBodySession : IPaperBodySession
{
    private static readonly object EnvironmentGate = new();
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> VisibleEnvironmentTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> RuntimeEnvironmentTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly PaperBodyContext _context;
    private readonly PaperBodyPluginManifest _manifest;
    private readonly bool _runtimeOwnsPresentation;
    private readonly Queue<JsonElement> _pendingRuntimeMessages = new();
    private readonly Grid _root;
    private WebView2CompositionControl _webView;
    private readonly CancellationTokenSource _lifetime = new();
    private int _webViewGeneration;
    private PaperBodyTheme _theme;
    private string _stateJson;
    private string _settingsJson;
    private string _expectedOrigin = "";
    private Uri? _entryUri;
    private bool _initializationStarted;
    private bool _initialized;
    private bool _documentReady;
    private bool _pluginDocumentReady;
    private ulong _documentNavigationId;
    private bool _hasDocumentNavigation;
    private string? _activeDocumentToken;
    private string? _departingDocumentToken;
    private bool _disposed;
    private bool _runtimeVisible;
    private bool _presentationVisible;
    private bool _webViewFailed;
    private int _documentGeneration;
    private readonly Dictionary<string, IDisposable> _hostSubscriptions =
        new(StringComparer.Ordinal);

    public WebPaperBodySession(
        PaperBodyContext context,
        PaperBodyPluginManifest manifest,
        bool runtimeOwnsPresentation = false)
    {
        _context = context;
        _manifest = manifest;
        _runtimeOwnsPresentation = runtimeOwnsPresentation;
        _theme = context.Theme;
        _stateJson = context.StateJson;
        _settingsJson = context.SettingsJson;
        _root = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        _webView = CreateWebView();
        _root.Children.Add(BuildStatusView(Strings.Get("PluginsWebLoading")));
        _root.Children.Add(_webView);
        Panel.SetZIndex(_webView, 1);
    }

    public FrameworkElement View => _root;

    public bool OnRuntimeMessage(JsonElement message) => ReceiveRuntimeMessage(message);

    private WebView2CompositionControl CreateWebView()
    {
        var webView = new WebView2CompositionControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        webView.SetValue(UIElement.OpacityProperty, 0.0);
        webView.Loaded += OnWebViewLoaded;
        webView.SizeChanged += OnWebViewSizeChanged;
        return webView;
    }

    private void OnWebViewLoaded(object sender, RoutedEventArgs e) =>
        TryStartInitialization();

    private void OnWebViewSizeChanged(object sender, SizeChangedEventArgs e) =>
        TryStartInitialization();

    private void TryStartInitialization()
    {
        var webView = _webView;
        var generation = _webViewGeneration;
        if (_initializationStarted ||
            _webViewFailed ||
            !_presentationVisible ||
            _disposed ||
            !webView.IsLoaded ||
            webView.ActualWidth <= 0 ||
            webView.ActualHeight <= 0)
        {
            return;
        }

        _initializationStarted = true;
        _ = InitializeAsync(webView, generation, _lifetime.Token);
    }

    private async Task InitializeAsync(
        WebView2CompositionControl webView,
        int generation,
        CancellationToken token)
    {
        try
        {
            var environment = await GetPluginEnvironmentAsync(_manifest.DirectoryPath);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }
            await webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            var core = webView.CoreWebView2
                ?? throw new InvalidOperationException(
                    "WebView2 initialization returned no CoreWebView2 instance.");
            core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            var hostName = WebHostName(_manifest.Id);
            _expectedOrigin = $"https://{hostName}";
            var webRoot = Path.GetDirectoryName(_manifest.EntryPath)
                ?? throw new InvalidOperationException("Web plugin entry has no containing directory.");
            var relativeEntry = Path.GetRelativePath(
                    webRoot,
                    _manifest.EntryPath)
                .Replace('\\', '/');
            _entryUri = new Uri(
                $"{_expectedOrigin}/{Uri.EscapeDataString(relativeEntry).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");

            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DownloadStarting += OnDownloadStarting;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildBridgeScript(
                    _expectedOrigin,
                    persistentStateWritable: true));
            token.ThrowIfCancellationRequested();
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                hostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Set readiness before navigation. A tiny local document can complete synchronously
            // enough for NavigationCompleted to run before the line after Source assignment.
            _initialized = true;
            _documentReady = false;
            webView.Source = _entryUri;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentWebView(webView, generation))
            {
                return;
            }

            _initializationStarted = false;
            ShowFailure(ex.GetBaseException().Message);
        }
    }

    private bool IsCurrentWebView(
        WebView2CompositionControl webView,
        int generation) =>
        !_disposed &&
        generation == _webViewGeneration &&
        ReferenceEquals(webView, _webView);

    private static string BuildBridgeScript(
        string expectedOrigin,
        bool persistentStateWritable)
    {
        var originJson = JsonSerializer.Serialize(expectedOrigin);
        var stateWritableJson = persistentStateWritable ? "true" : "false";
        return $$"""
            (() => {
              const expectedOrigin = {{originJson}};
              const persistentStateWritable = {{stateWritableJson}};
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
              const saveState = state => {
                if (!persistentStateWritable) {
                  throw new Error('Persistent frontend state is unavailable.');
                }
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
              const request = async (method, params = {}) => {
                await hostReady;
                const requestId = `r${++sequence}`;
                return new Promise((resolve, reject) => {
                  pending.set(requestId, { resolve, reject });
                  post('hostRequest', {
                    requestId,
                    method: String(method ?? ''),
                    params: params ?? {}
                  });
                });
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
                setInputClaims(claims) {
                  const values = Array.isArray(claims)
                    ? claims.map(value => String(value ?? '')).filter(Boolean)
                    : [];
                  post('setInputClaims', values);
                },
                markDirty() { post('markDirty'); },
                openExternal(url) { post('openExternal', String(url ?? '')); }
              });
              const workspace = Object.freeze({ request });
              const runtime = Object.freeze({
                post(message) { return request('runtime.post', { message: message ?? null }); }
              });
              window.papertodo = Object.freeze({
                surface: 'body',
                paper,
                body,
                workspace,
                runtime,
                post,
                request,
                saveState,
                flushState,
                registerStateProvider(provider) {
                  if (!persistentStateWritable) {
                    throw new Error('Persistent frontend state providers are unavailable.');
                  }
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
                      const error = new Error(message.error?.message ?? 'PaperTodo host request failed.');
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
              document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'hidden') flushState();
              });
            })();
            """;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_expectedOrigin) &&
            !IsAllowedDocumentUri(e.Uri))
        {
            e.Cancel = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var external))
            {
                TryOpenExternalNavigation(external);
            }
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
        ClearHostSubscriptions();
        _documentReady = false;
        _pluginDocumentReady = false;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !_hasDocumentNavigation ||
            e.NavigationId != _documentNavigationId)
        {
            return;
        }

        _hasDocumentNavigation = false;
        if (!e.IsSuccess)
        {
            ShowFailure(
                $"{Strings.Get("PluginsWebNavigationFailed")} ({e.WebErrorStatus})");
            return;
        }

        _documentReady = true;
        _pluginDocumentReady = IsAllowedDocumentUri(_webView.Source?.AbsoluteUri);
        ShowWebView();
        if (_pluginDocumentReady)
        {
            _activeDocumentToken = Guid.NewGuid().ToString("N");
            _departingDocumentToken = null;
            SendInitialize();
            FlushPendingRuntimeMessages();
        }
        else
        {
            _activeDocumentToken = null;
            _departingDocumentToken = null;
        }
    }

    private void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            IsAllowedDocumentUri(e.Uri))
        {
            return;
        }

        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            _ = TryOpenExternalNavigation(uri);
        }
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var value = e.DownloadOperation.Uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            // blob:, data: and other session-local downloads stay inside WebView2.
            return;
        }

        if (TryOpenExternalNavigation(uri))
        {
            e.Cancel = true;
        }
    }

    private bool IsAllowedDocumentUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.GetLeftPart(UriPartial.Authority), _expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenExternalNavigation(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ShowWebView()
    {
        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        UpdateWebViewPresentation();
    }

    private void UpdateWebViewHost()
    {
        if (_disposed || _webViewFailed)
        {
            return;
        }

        UpdateWebViewPresentation();
        TryStartInitialization();
    }

    private void UpdateWebViewPresentation()
    {
        var show = _presentationVisible &&
            _documentReady &&
            !_disposed &&
            ReferenceEquals(_webView.Parent, _root);
        _webView.SetValue(UIElement.OpacityProperty, show ? 1.0 : 0.0);
        _webView.IsHitTestVisible = show;
    }

    private void DisposeWebView(WebView2CompositionControl webView)
    {
        if (ReferenceEquals(webView, _webView))
        {
            _hasDocumentNavigation = false;
            _activeDocumentToken = null;
            _departingDocumentToken = null;
            _documentGeneration++;
            ClearHostSubscriptions();
        }
        webView.Loaded -= OnWebViewLoaded;
        webView.SizeChanged -= OnWebViewSizeChanged;
        if (webView.Parent is Panel parent)
        {
            parent.Children.Remove(webView);
        }

        if (webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.DownloadStarting -= OnDownloadStarting;
        }
        try { webView.Dispose(); } catch { }
    }

    private void SendInitialize()
    {
        Send(new
        {
            type = "initialize",
            surface = "body",
            documentToken = _activeDocumentToken,
            paperId = _context.PaperId,
            providerId = _context.ProviderId,
            apiVersion = _context.ApiVersion,
            state = ParseState(_stateJson),
            stateVersion = _context.StateVersion,
            targetStateVersion = _context.TargetStateVersion,
            settings = ParseState(_settingsJson),
            permissions = _context.GrantedPermissions.OrderBy(value => value).ToArray(),
            theme = ThemePayload(_theme),
            visible = _runtimeVisible,
            presentationVisible = _presentationVisible
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !IsAllowedDocumentUri(e.Source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeElement.GetString() ?? "";
            if (!CanAcceptDocumentMessage(
                    type,
                    _documentReady,
                    _pluginDocumentReady))
            {
                return;
            }

            var documentToken = root.TryGetProperty("documentToken", out var tokenElement) &&
                                tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (!HasDocumentAuthority(type, documentToken))
            {
                return;
            }

            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : default;
            switch (type)
            {
                case "saveState":
                    UpdateStateFromWebSurface(payload, sourceMini: null);
                    break;
                case "setTitle":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.SetTitle(ReadPayloadString(payload));
                    }
                    break;
                case "setHeaderText":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.Paper.SetHeaderText(ReadPayloadString(payload));
                    }
                    break;
                case "setCapsulePresentation":
                    if (!_runtimeOwnsPresentation)
                    {
                        _context.Paper.SetCapsulePresentation(
                            payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                ? null
                                : JsonSerializer.Deserialize<PaperCapsulePresentation>(
                                    payload.GetRawText(),
                                    BridgeJsonOptions));
                    }
                    break;
                case "setInputClaims":
                    _context.SetInputClaims(ReadInputClaims(payload));
                    break;
                case "markDirty":
                    _context.MarkDirty();
                    break;
                case "openExternal":
                    _context.OpenExternal(ReadPayloadString(payload));
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
            // A malformed plugin message is isolated to the plugin body.
        }
    }

    private static bool CanAcceptDocumentMessage(
        string type,
        bool documentReady,
        bool pluginDocumentReady) =>
        string.Equals(type, "saveState", StringComparison.Ordinal) ||
        (documentReady && pluginDocumentReady);

    private bool HasDocumentAuthority(string type, string? documentToken)
    {
        if (string.IsNullOrWhiteSpace(documentToken))
        {
            return false;
        }

        if (_documentReady &&
            _pluginDocumentReady &&
            string.Equals(documentToken, _activeDocumentToken, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(type, "saveState", StringComparison.Ordinal) &&
            !_documentReady &&
            string.Equals(documentToken, _departingDocumentToken, StringComparison.Ordinal);
    }

    private void HandleHostRequest(JsonElement payload)
    {
        var requestId = PayloadString(payload, "requestId");
        var documentGeneration = _documentGeneration;
        try
        {
            var method = PayloadString(payload, "method");
            var parameters = payload.ValueKind == JsonValueKind.Object &&
                             payload.TryGetProperty("params", out var paramsValue)
                ? paramsValue
                : JsonSerializer.SerializeToElement(new { });
            var result = ExecuteHostRequest(method, parameters);
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
                    message = "PaperTodo could not complete the plugin request."
                }
            });
        }
    }

    private object? ExecuteHostRequest(string method, JsonElement parameters)
    {
        if (string.Equals(method, "runtime.post", StringComparison.Ordinal))
        {
            var message = parameters.ValueKind == JsonValueKind.Object &&
                          parameters.TryGetProperty("message", out var messageValue)
                ? messageValue
                : default;
            if (!_context.Runtime.Post(
                    message.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : message.Clone()))
            {
                throw new PaperTodoPluginException(
                    "runtime_unavailable",
                    "The plugin Runtime is not ready to accept this message.");
            }
            return null;
        }

        return method switch
        {
        "papers.list" => _context.Host.ListPapers(OptionalPayloadString(parameters, "type")),
        "papers.get" => _context.Host.GetPaper(PayloadString(parameters, "paperId")),
        "todos.list" => _context.Host.ListTodos(
            OptionalPayloadString(parameters, "paperId"),
            OptionalPayloadBoolean(parameters, "includeBlank") ?? false),
        "notes.get" => _context.Host.GetNote(PayloadString(parameters, "paperId")),
        "papers.create" => _context.Host.CreatePaper(
            DeserializePayload<CreatePaperRequest>(parameters)),
        "todos.append" => _context.Host.AppendTodos(
            DeserializePayload<AppendTodosRequest>(parameters)),
        "todos.update" => _context.Host.UpdateTodo(
            DeserializePayload<UpdateTodoRequest>(parameters)),
        "todos.setReminder" => _context.Host.SetTodoReminder(
            DeserializePayload<SetTodoReminderRequest>(parameters)),
        "notes.write" => _context.Host.WriteNote(
            DeserializePayload<WriteNoteRequest>(parameters)),
        "todos.delete" => _context.Host.DeleteTodo(
            DeserializePayload<DeleteTodoRequest>(parameters)),
        "papers.delete" => _context.Host.DeletePaper(
            PayloadString(parameters, "paperId")),
        "paper.show" or
        "paper.hide" or
        "paper.toggle" or
        "paper.expand" or
        "paper.collapse" or
        "paper.toggleCollapsed" or
        "paper.activate" => ExecutePaperPresentationHostRequest(method, parameters),
        "topbar.paper.set" => SetPaperTopBarActionsFromWeb(parameters),
        "topbar.global.set" => SetGlobalTopBarActionsFromWeb(parameters),
            _ => throw new PaperTodoPluginException(
                "method_not_found",
                $"Unknown PaperTodo plugin host method: {method}")
        };
    }

    private void HandleSubscribeHostEvents(JsonElement payload)
    {
        var subscriptionId = PayloadString(payload, "subscriptionId");
        var documentGeneration = _documentGeneration;
        try
        {
            if (_hostSubscriptions.Remove(subscriptionId, out var existing))
            {
                existing.Dispose();
            }
            _hostSubscriptions[subscriptionId] = _context.Host.Subscribe(
                new PaperTodoEventFilter
                {
                    Kinds = ReadEventKinds(payload),
                    PaperIds = ReadStringSet(payload, "paperIds"),
                    ExcludeOwnOperations = OptionalPayloadBoolean(
                        payload,
                        "excludeOwnOperations") ?? true
                },
                value =>
                {
                    if (documentGeneration != _documentGeneration) return;
                    var eventJson = JsonSerializer.SerializeToElement(
                        value,
                        value.GetType(),
                        BridgeJsonOptions);
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
        var subscriptionId = PayloadString(payload, "subscriptionId");
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

    private static T DeserializePayload<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(BridgeJsonOptions)
                ?? throw new JsonException("Payload deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                ex.GetBaseException().Message);
        }
    }

    private static string PayloadString(JsonElement payload, string name)
    {
        var value = OptionalPayloadString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} is required.");
        }
        return value;
    }

    private static string? OptionalPayloadString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a string.");
        }
        return value.GetString();
    }

    private static bool? OptionalPayloadBoolean(JsonElement payload, string name)
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

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2))
        {
            return;
        }

        _hasDocumentNavigation = false;
        _activeDocumentToken = null;
        _departingDocumentToken = null;
        _documentGeneration++;
        ClearHostSubscriptions();
        ShowFailure(Strings.Format("PluginsWebProcessFailedFormat", e.ProcessFailedKind));
    }

    private static string ReadPayloadString(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.String
            ? payload.GetString() ?? ""
            : "";

    private static PaperBodyInputClaims ReadInputClaims(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            return PaperBodyInputClaims.None;
        }

        var claims = PaperBodyInputClaims.None;
        foreach (var item in payload.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            claims |= item.GetString() switch
            {
                "escapeKey" => PaperBodyInputClaims.EscapeKey,
                "contextMenu" => PaperBodyInputClaims.ContextMenu,
                _ => PaperBodyInputClaims.None
            };
        }
        return claims;
    }

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

    private static object ThemePayload(PaperBodyTheme theme) => new
    {
        isDark = theme.IsDark,
        paperColor = theme.PaperColor,
        textColor = theme.TextColor,
        weakTextColor = theme.WeakTextColor,
        accentColor = theme.AccentColor,
        borderColor = theme.BorderColor,
        fontFamily = theme.FontFamily,
        fontScale = theme.FontScale
    };

    private bool Send(object value)
    {
        if (!_initialized ||
            !_documentReady ||
            !_pluginDocumentReady ||
            _disposed ||
            _webView.CoreWebView2 == null)
        {
            return false;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(value, BridgeJsonOptions));
            return true;
        }
        catch
        {
            // Renderer teardown can race with paper close.
            return false;
        }
    }

    private void ShowFailure(string message)
    {
        if (_disposed)
        {
            return;
        }

        _hasDocumentNavigation = false;
        _activeDocumentToken = null;
        _departingDocumentToken = null;
        _documentGeneration++;
        ClearHostSubscriptions();
        _documentReady = false;
        _pluginDocumentReady = false;
        _webViewFailed = true;
        UpdateWebViewPresentation();
        if (!_runtimeOwnsPresentation)
        {
            _context.Paper.SetHeaderText("");
            _context.Paper.SetCapsulePresentation(null);
        }
        _context.SetInputClaims(PaperBodyInputClaims.None);
        for (var index = _root.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_root.Children[index], _webView))
            {
                _root.Children.RemoveAt(index);
            }
        }
        _root.Children.Insert(0, BuildStatusView(
            Strings.Format("PluginBodyFailureMessageFormat", _manifest.Name, message),
            isError: true,
            retry: _context.RequestReload));
    }

    private static FrameworkElement BuildStatusView(
        string text,
        bool isError = false,
        Action? retry = null)
    {
        var layout = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420
        };
        layout.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = isError ? Theme.DangerBrush : Theme.WeakTextBrush,
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        if (retry != null)
        {
            var button = new Button
            {
                Content = Strings.Get("PluginBodyRetry"),
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 76,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Theme.Tint(28),
                Foreground = Theme.TextBrush,
                BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12)
            };
            button.Click += (_, _) => retry();
            layout.Children.Add(button);
        }

        return new Border
        {
            Padding = new Thickness(18),
            Background = Brushes.Transparent,
            Child = layout
        };
    }

    internal static async Task<CoreWebView2Environment> GetPluginEnvironmentAsync(
        string pluginDirectory,
        bool backgroundRuntime = false)
    {
        var key = Path.GetFullPath(pluginDirectory);
        var environmentTasks = backgroundRuntime
            ? RuntimeEnvironmentTasks
            : VisibleEnvironmentTasks;
        Task<CoreWebView2Environment> task;
        lock (EnvironmentGate)
        {
            if (!environmentTasks.TryGetValue(key, out task!))
            {
                task = CreateEnvironmentAsync(key, backgroundRuntime);
                environmentTasks.Add(key, task);
            }
        }

        try
        {
            return await task;
        }
        catch
        {
            lock (EnvironmentGate)
            {
                if (environmentTasks.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, task))
                {
                    environmentTasks.Remove(key);
                }
            }
            throw;
        }
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(
        string pluginDirectory,
        bool backgroundRuntime)
    {
        var userDataFolder = Path.Combine(
            pluginDirectory,
            ".runtime",
            backgroundRuntime ? "webview2-runtime" : "webview2");
        Directory.CreateDirectory(userDataFolder);
        var options = backgroundRuntime
            ? new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling " +
                "--disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows")
            : new CoreWebView2EnvironmentOptions();
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: options);
    }

    private static string WebHostName(string id)
    {
        var safe = new string(id
            .ToLowerInvariant()
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'
                    ? character
                    : '-')
            .ToArray())
            .Trim('-');
        if (safe.Length == 0)
        {
            safe = "plugin";
        }
        return $"{safe}.papertodo.local";
    }

    internal void ApplyExternalState(string stateJson)
    {
        var normalized = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;
        if (string.Equals(_stateJson, normalized, StringComparison.Ordinal))
        {
            return;
        }
        _stateJson = normalized;
        SendStateChanged();
        _miniViewHost?.SendStateChanged();
    }

    internal bool ReceiveRuntimeMessage(JsonElement payload)
    {
        if (_disposed || _webViewFailed)
        {
            return false;
        }
        if (!_documentReady || !_pluginDocumentReady)
        {
            if (_pendingRuntimeMessages.Count >= 32)
            {
                return false;
            }
            _pendingRuntimeMessages.Enqueue(payload.Clone());
            return true;
        }
        return SendRuntimeMessage(payload);
    }

    private void FlushPendingRuntimeMessages()
    {
        while (_documentReady && _pluginDocumentReady && _pendingRuntimeMessages.Count > 0)
        {
            if (!SendRuntimeMessage(_pendingRuntimeMessages.Peek()))
            {
                break;
            }
            _pendingRuntimeMessages.Dequeue();
        }
    }

    private bool SendRuntimeMessage(JsonElement payload) =>
        Send(new
        {
            type = "runtimeMessage",
            payload
        });

    public void RefreshFromModel()
    {
        SendStateChanged();
        _miniViewHost?.SendStateChanged();
    }

    public void OnActivated() => Send(new { type = "activated" });
    public void OnDeactivated() => Send(new { type = "deactivated" });

    public void OnVisibilityChanged(bool visible)
    {
        _runtimeVisible = visible;
        UpdateWebViewHost();
        Send(new { type = "visibilityChanged", visible });
    }

    public void OnPresentationChanged(bool visible)
    {
        _presentationVisible = visible;
        UpdateWebViewHost();
        Send(new { type = "presentationChanged", visible });
    }

    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        Send(new { type = "themeChanged", theme = ThemePayload(theme) });
        _miniViewHost?.SendThemeChanged("themeChanged");
    }

    public void OnTypographyChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        Send(new { type = "typographyChanged", theme = ThemePayload(theme) });
        _miniViewHost?.SendThemeChanged("typographyChanged");
    }

    public void OnSettingsChanged(string settingsJson)
    {
        _settingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        Send(new
        {
            type = "settingsChanged",
            settings = ParseState(_settingsJson)
        });
        _miniViewHost?.SendSettingsChanged();
    }

    public void Commit()
    {
        // Best-effort lifecycle notification only. Persistent state must be saved at the
        // moment it changes; Dispose does not wait for a JavaScript acknowledgement.
        Send(new { type = "commitRequested" });
    }

    public void CancelInteractions() => Send(new { type = "cancelInteractions" });
    public void OnDpiChanged() => Send(new { type = "dpiChanged" });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Commit();
        _disposed = true;
        _miniViewHost?.Dispose();
        _miniViewHost = null;
        _hasDocumentNavigation = false;
        _activeDocumentToken = null;
        _departingDocumentToken = null;
        _documentGeneration++;
        ClearHostSubscriptions();
        _pendingRuntimeMessages.Clear();
        _lifetime.Cancel();
        _webViewGeneration++;
        DisposeWebView(_webView);
        _lifetime.Dispose();
    }
}
