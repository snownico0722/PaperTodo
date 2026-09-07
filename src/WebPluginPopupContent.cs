using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// A visible Web frontend, not a fake Paper/Body and never another provider Runtime. It shares
/// the existing environment/local-origin and read-only image API. Navigation or renderer
/// failure cannot retain another document's bridge lease.
/// </summary>
internal sealed class WebPluginPopupContent : IPaperPluginPopupContent
{
    private readonly PaperBodyPluginManifest _manifest;
    private readonly string _entryPath;
    private readonly IPaperTodoHostApi _workspace;
    private readonly PaperPluginPopupContext _context;
    private readonly JsonElement _data;
    private readonly Action<JsonElement> _post;
    private readonly Action<string> _failed;
    private readonly WebView2CompositionControl _webView = new();
    private readonly CancellationTokenSource _lifetime = new();
    private PaperBodyTheme _theme;
    private string _origin = string.Empty;
    private string? _documentToken;
    private ulong _navigationId;
    private bool _navigating;
    private bool _started;
    private bool _ready;
    private bool _disposed;

    internal WebPluginPopupContent(PaperBodyPluginManifest manifest, string entryPath,
        IPaperTodoHostApi workspace, PaperPluginPopupContext context, JsonElement data,
        Action<JsonElement> post, Action<string> failed)
    {
        _manifest = manifest; _entryPath = entryPath; _workspace = workspace;
        _context = context; _theme = context.Theme; _data = data; _post = post; _failed = failed;
        _webView.Loaded += OnLoaded;
    }
    public FrameworkElement View => _webView;
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;
        _started = true;
        _ = InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var environment = await WebPaperBodySession.SharedSurfaceEnvironmentAsync(_manifest.DirectoryPath);
            token.ThrowIfCancellationRequested();
            await _webView.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            _origin = WebPluginRuntimeInfrastructure.Origin(_manifest.Id);
            var root = Path.GetDirectoryName(_manifest.EntryPath)!;
            core.SetVirtualHostNameToFolderMapping(WebPluginRuntimeInfrastructure.HostName(_manifest.Id),
                root, CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnMessage;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindow;
            core.ProcessFailed += OnProcessFailed;
            core.DownloadStarting += OnDownload;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript(_origin));
            token.ThrowIfCancellationRequested();
            if (!_disposed) _webView.Source = WebPluginRuntimeInfrastructure.LocalEntryUri(_origin, root, _entryPath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex.GetBaseException().Message); }
    }

    internal static string BridgeScript(string origin) => $$"""
        (() => {
          if (window !== window.top || location.origin !== {{JsonSerializer.Serialize(origin)}}) return;
          let token = null, sequence = 0, initialized;
          const ready = new Promise(resolve => initialized = resolve);
          const pending = new Map(), listeners = new Set();
          const request = async (method, params = {}) => {
            await ready;
            return new Promise((resolve, reject) => {
              const requestId = `r${++sequence}`;
              pending.set(requestId, {resolve, reject});
              chrome.webview.postMessage({token, requestId, method, params});
            });
          };
          const close = () => { if (token) chrome.webview.postMessage({token, method:'popup.close'}); };
          window.papertodo = Object.freeze({
            popup: Object.freeze({close, post: message => request('popup.post', {message})}),
            noteAssets: Object.freeze({readImage: (paperId, imageId) => request('noteAssets.readImage', {paperId, imageId})}),
            ready,
            onEvent(listener) { listeners.add(listener); return () => listeners.delete(listener); }
          });
          chrome.webview.addEventListener('message', e => {
            const value = e.data;
            if (value?.type === 'initialize') { token = value.token; initialized(value); }
            if (value?.theme) {
              const t = value.theme, css = document.documentElement.style;
              css.setProperty('--paper-background', t.paperColor);
              css.setProperty('--paper-text', t.textColor);
              css.setProperty('--paper-accent', t.accentColor);
              css.setProperty('--paper-border', t.borderColor);
              css.setProperty('--paper-font', t.fontFamily);
              css.setProperty('color-scheme', t.isDark ? 'dark' : 'light');
            }
            if (value?.type === 'response') {
              const p = pending.get(value.requestId);
              if (p) {
                pending.delete(value.requestId);
                if (value.ok) p.resolve(value.result);
                else p.reject(Object.assign(new Error(value.error.message), {code:value.error.code}));
              }
            }
            for (const listener of [...listeners]) { try { listener(value); } catch {} }
          });
        })();
        """;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_disposed) { e.Cancel = true; return; }
        if (!WebPluginRuntimeInfrastructure.IsSameOrigin(e.Uri, _origin))
        {
            e.Cancel = true;
            return;
        }
        _ready = false; _documentToken = null; _navigationId = e.NavigationId; _navigating = true;
    }
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed || !_navigating || e.NavigationId != _navigationId) return;
        _navigating = false;
        if (!e.IsSuccess) { Fail($"Web popup navigation failed ({e.WebErrorStatus})."); return; }
        _documentToken = Guid.NewGuid().ToString("N");
        _ready = true;
        Send(new { type = "initialize", token = _documentToken, theme = _theme, data = _data });
    }
    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !_ready || !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Source, _origin)) return;
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var value = document.RootElement;
            if (!value.TryGetProperty("token", out var token) || token.GetString() != _documentToken) return;
            var method = WebPluginRuntimeInfrastructure.RequiredString(value, "method");
            if (method == "popup.close") { _context.Close(); return; }
            requestId = WebPluginRuntimeInfrastructure.RequiredString(value, "requestId");
            if (requestId.Length > 64) return;
            var parameters = WebPluginRuntimeInfrastructure.ParametersOrEmpty(value);
            object? result;
            if (method == "popup.post")
            {
                var message = parameters.GetProperty("message").Clone();
                WebPluginPopupRequests.ValidateMessage(message);
                _post(message);
                result = new { delivered = true };
            }
            else if (method == "noteAssets.readImage")
                result = WebPluginWorkspaceRequests.Execute(_workspace, method, parameters);
            else throw new PaperTodoPluginException("method_not_found", "Unknown popup method.");
            Send(new { type = "response", requestId, ok = true, result });
        }
        catch (Exception ex)
        {
            var code = ex is PaperTodoPluginException pluginError ? pluginError.Code : "host_error";
            Send(new { type = "response", requestId, ok = false, error = new { code, message = ex.GetBaseException().Message } });
        }
    }
    private void Send(object message)
    {
        if (_disposed || !_ready) return;
        try { _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, WebPluginRuntimeInfrastructure.JsonOptions)); }
        catch (Exception ex) { Trace.TraceWarning("Web popup message failed: {0}", ex.GetBaseException()); }
    }
    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _theme = theme;
        Send(new { type = "themeChanged", theme });
    }
    private static void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;
    private static void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (WebPluginProcessFailurePolicy.Classify(e.ProcessFailedKind) != WebPluginProcessFailurePolicy.Recovery.None)
            Fail($"Web popup process failed ({e.ProcessFailedKind}).");
        else Trace.TraceWarning("Web popup recoverable process failure: {0}", e.ProcessFailedKind);
    }
    private void Fail(string message)
    {
        if (_disposed) return;
        try { _failed(message); }
        finally { _context.Close(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ready = false; _documentToken = null;
        _lifetime.Cancel();
        _webView.Loaded -= OnLoaded;
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnMessage;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NewWindowRequested -= OnNewWindow;
            core.ProcessFailed -= OnProcessFailed;
            core.DownloadStarting -= OnDownload;
        }
        _webView.Dispose();
        _lifetime.Dispose();
    }
}
