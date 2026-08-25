from __future__ import annotations

import json
import shutil
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
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def replace_between(path: str, start_marker: str, end_marker: str, replacement: str) -> None:
    text = read(path)
    start = text.find(start_marker)
    if start < 0:
        raise RuntimeError(f"{path}: start marker not found: {start_marker!r}")
    end = text.find(end_marker, start)
    if end < 0:
        raise RuntimeError(f"{path}: end marker not found: {end_marker!r}")
    write(path, text[:start] + replacement + text[end:])


# -----------------------------------------------------------------------------
# Manifest: Web backgroundUpdates now requires a per-paper runtime entry.
# Native backgroundUpdates keeps its existing body-session semantics.
# -----------------------------------------------------------------------------
replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '    public string Runtime { get; set; } = "";\n',
    '    public string Runtime { get; set; } = "";\n'
    '    public string PaperRuntime { get; set; } = "";\n')
replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '    public string RuntimePath { get; internal set; } = "";\n',
    '    public string RuntimePath { get; internal set; } = "";\n'
    '    public string PaperRuntimePath { get; internal set; } = "";\n')

replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '''        if (kind == PaperBodyPluginKind.Web && hasAppRuntime)\n        {\n            manifest.RuntimePath = string.IsNullOrWhiteSpace(manifest.Runtime)\n                ? Path.Combine(webRoot!, "runtime.html")\n                : ResolveContainedPath(directory, manifest.Runtime);\n            EnsurePathInsideDirectory(webRoot!, manifest.RuntimePath, "runtime");\n            if (!File.Exists(manifest.RuntimePath))\n            {\n                throw new FileNotFoundException(\n                    "Plugin app runtime entry was not found.",\n                    manifest.RuntimePath);\n            }\n        }\n\n        return manifest;\n''',
    '''        if (kind == PaperBodyPluginKind.Web && hasAppRuntime)\n        {\n            manifest.RuntimePath = string.IsNullOrWhiteSpace(manifest.Runtime)\n                ? Path.Combine(webRoot!, "runtime.html")\n                : ResolveContainedPath(directory, manifest.Runtime);\n            EnsurePathInsideDirectory(webRoot!, manifest.RuntimePath, "runtime");\n            if (!File.Exists(manifest.RuntimePath))\n            {\n                throw new FileNotFoundException(\n                    "Plugin app runtime entry was not found.",\n                    manifest.RuntimePath);\n            }\n        }\n\n        var requiresBackgroundUpdates =\n            (ParseRuntimeRequirements(manifest.Requires) &\n             PaperBodyRuntimeRequirements.BackgroundUpdates) != 0;\n        if (!string.IsNullOrWhiteSpace(manifest.PaperRuntime) &&\n            kind != PaperBodyPluginKind.Web)\n        {\n            throw new InvalidDataException(\n                "paperRuntime is only valid for Web plugins.");\n        }\n        if (kind == PaperBodyPluginKind.Web && requiresBackgroundUpdates)\n        {\n            if (string.IsNullOrWhiteSpace(manifest.PaperRuntime))\n            {\n                throw new InvalidDataException(\n                    "Web plugins that require backgroundUpdates must declare paperRuntime.");\n            }\n            manifest.PaperRuntimePath = ResolveContainedPath(\n                directory,\n                manifest.PaperRuntime);\n            EnsurePathInsideDirectory(\n                webRoot!,\n                manifest.PaperRuntimePath,\n                "paperRuntime");\n            if (!File.Exists(manifest.PaperRuntimePath))\n            {\n                throw new FileNotFoundException(\n                    "Plugin paper runtime entry was not found.",\n                    manifest.PaperRuntimePath);\n            }\n        }\n        else if (!string.IsNullOrWhiteSpace(manifest.PaperRuntime))\n        {\n            throw new InvalidDataException(\n                "paperRuntime requires the backgroundUpdates runtime requirement.");\n        }\n\n        return manifest;\n''')

replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '''                manifest.EntryPath,\n                manifest.MiniEntryPath,\n                manifest.RuntimePath);\n''',
    '''                manifest.EntryPath,\n                manifest.MiniEntryPath,\n                manifest.RuntimePath,\n                manifest.PaperRuntimePath);\n''')

replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '''    private static string DiscoveryFingerprint(\n        string manifestPath,\n        string entryPath,\n        string? miniEntryPath = null,\n        string? runtimePath = null)\n''',
    '''    private static string DiscoveryFingerprint(\n        string manifestPath,\n        string entryPath,\n        string? miniEntryPath = null,\n        string? runtimePath = null,\n        string? paperRuntimePath = null)\n''')
replace_once(
    "src/PaperBodyPluginRegistry.cs",
    '''        if (!string.IsNullOrWhiteSpace(runtimePath))\n        {\n            var runtime = new FileInfo(runtimePath);\n            value += $":{runtime.Length}:{runtime.LastWriteTimeUtc.Ticks}";\n        }\n        return value;\n''',
    '''        if (!string.IsNullOrWhiteSpace(runtimePath))\n        {\n            var runtime = new FileInfo(runtimePath);\n            value += $":{runtime.Length}:{runtime.LastWriteTimeUtc.Ticks}";\n        }\n        if (!string.IsNullOrWhiteSpace(paperRuntimePath))\n        {\n            var paperRuntime = new FileInfo(paperRuntimePath);\n            value += $":{paperRuntime.Length}:{paperRuntime.LastWriteTimeUtc.Ticks}";\n        }\n        return value;\n''')


# -----------------------------------------------------------------------------
# Shared Web infrastructure owns the hidden runtime host. Body sessions no
# longer know about or move through that host.
# -----------------------------------------------------------------------------
write(
    "src/WebPaperBodySession.SharedRuntime.cs",
    r'''using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PaperTodo;

// These narrow accessors keep the body-session implementation private while allowing the
// provider/paper runtime infrastructure to share one environment pool and local-origin policy.
internal sealed partial class WebPaperBodySession
{
    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(
        string pluginDirectory) =>
        GetPluginEnvironmentAsync(pluginDirectory);

    internal static string SharedWebHostName(string pluginId) =>
        WebHostName(pluginId);
}

/// <summary>
/// Common non-surface-specific Web plugin runtime services. Body sessions own only visible UI;
/// provider and per-paper runtimes use the hidden host below and never move their WebViews into a
/// PaperWindow.
/// </summary>
internal static class WebPluginRuntimeInfrastructure
{
    private static class BackgroundWebViewHost
    {
        private static Window? _window;
        private static Grid? _root;

        public static bool TryAttach(WebView2CompositionControl webView)
        {
            try
            {
                Application.Current.Dispatcher.VerifyAccess();
                if (webView.Parent is Panel parent)
                {
                    parent.Children.Remove(webView);
                }
                else if (webView.Parent != null)
                {
                    return false;
                }

                EnsureWindow();
                _root!.Children.Add(webView);
                webView.Width = 1;
                webView.Height = 1;
                webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                webView.VerticalAlignment = VerticalAlignment.Stretch;
                if (_window!.IsVisible == false)
                {
                    _window.Show();
                }
                return true;
            }
            catch
            {
                if (_root?.Children.Contains(webView) == true)
                {
                    _root.Children.Remove(webView);
                }
                return false;
            }
        }

        public static void Detach(WebView2CompositionControl webView)
        {
            if (_root?.Children.Contains(webView) == true)
            {
                _root.Children.Remove(webView);
            }
            if (_root?.Children.Count == 0 && _window?.IsVisible == true)
            {
                _window.Hide();
            }
        }

        private static void EnsureWindow()
        {
            if (_window != null)
            {
                return;
            }

            _root = new Grid
            {
                Width = 1,
                Height = 1,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };
            _window = new Window
            {
                Content = _root,
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Opacity = 0.01,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false
            };
        }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Task<CoreWebView2Environment> EnvironmentAsync(string pluginDirectory) =>
        WebPaperBodySession.SharedPluginEnvironmentAsync(pluginDirectory);

    public static bool AttachBackground(WebView2CompositionControl webView) =>
        BackgroundWebViewHost.TryAttach(webView);

    public static void DetachBackground(WebView2CompositionControl webView) =>
        BackgroundWebViewHost.Detach(webView);

    public static string Origin(string pluginId) =>
        $"https://{WebPaperBodySession.SharedWebHostName(pluginId)}";

    public static string HostName(string pluginId) =>
        WebPaperBodySession.SharedWebHostName(pluginId);

    public static Uri LocalEntryUri(
        string expectedOrigin,
        string webRoot,
        string entryPath)
    {
        var relative = Path.GetRelativePath(webRoot, entryPath).Replace('\\', '/');
        return new Uri(
            $"{expectedOrigin}/{Uri.EscapeDataString(relative).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");
    }

    public static bool IsSameOrigin(string? value, string expectedOrigin) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority),
            expectedOrigin,
            StringComparison.OrdinalIgnoreCase);

    public static void ConfigureBackgroundCore(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
    }

    public static string RequiredString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new PaperTodo.Plugin.PaperTodoPluginException(
                "invalid_params",
                $"{name} is required.");
        }
        return value.GetString()!;
    }

    public static JsonElement ParametersOrEmpty(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("params", out var paramsValue)
            ? paramsValue
            : JsonSerializer.SerializeToElement(new { });
}
''')


# -----------------------------------------------------------------------------
# Web body sessions become presentation-only. They initialize only when the
# full body is presented and never move into the hidden runtime HWND.
# -----------------------------------------------------------------------------
replace_between(
    "src/WebPaperBodySession.cs",
    "    // A dedicated, non-activating runtime surface is used only for Web plugins that explicitly\n",
    "    private readonly PaperBodyContext _context;\n",
    "")
replace_once(
    "src/WebPaperBodySession.cs",
    "    private readonly PaperBodyPluginManifest _manifest;\n",
    "    private readonly PaperBodyPluginManifest _manifest;\n"
    "    private readonly Action<JsonElement>? _postRuntimeMessage;\n")
replace_once(
    "src/WebPaperBodySession.cs",
    "    private bool _everPresented;\n",
    "")
replace_once(
    "src/WebPaperBodySession.cs",
    '''    public WebPaperBodySession(\n        PaperBodyContext context,\n        PaperBodyPluginManifest manifest)\n    {\n        _context = context;\n        _manifest = manifest;\n''',
    '''    public WebPaperBodySession(\n        PaperBodyContext context,\n        PaperBodyPluginManifest manifest,\n        Action<JsonElement>? postRuntimeMessage = null)\n    {\n        _context = context;\n        _manifest = manifest;\n        _postRuntimeMessage = postRuntimeMessage;\n''')
replace_once(
    "src/WebPaperBodySession.cs",
    "            !_runtimeVisible ||\n",
    "            !_presentationVisible ||\n")
replace_once(
    "src/WebPaperBodySession.cs",
    '''              const workspace = Object.freeze({ request });\n              window.papertodo = Object.freeze({\n                surface: 'body',\n                paper,\n                body,\n                workspace,\n                post,\n''',
    '''              const workspace = Object.freeze({ request });\n              const runtime = Object.freeze({\n                post(message) { post('runtimeMessage', message ?? null); }\n              });\n              window.papertodo = Object.freeze({\n                surface: 'body',\n                paper,\n                body,\n                workspace,\n                runtime,\n                post,\n''')
replace_once(
    "src/WebPaperBodySession.cs",
    '''                case "openExternal":\n                    _context.OpenExternal(ReadPayloadString(payload));\n                    break;\n                case "hostRequest":\n''',
    '''                case "openExternal":\n                    _context.OpenExternal(ReadPayloadString(payload));\n                    break;\n                case "runtimeMessage":\n                    _postRuntimeMessage?.Invoke(\n                        payload.ValueKind == JsonValueKind.Undefined\n                            ? JsonSerializer.SerializeToElement<object?>(null)\n                            : payload.Clone());\n                    break;\n                case "hostRequest":\n''')

replace_between(
    "src/WebPaperBodySession.cs",
    "    private void ShowWebView()\n",
    "    private void DisposeWebView(WebView2CompositionControl webView)\n",
    r'''    private void ShowWebView()
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

''')
replace_once(
    "src/WebPaperBodySession.cs",
    "        BackgroundWebViewHost.Detach(webView);\n",
    "")
replace_once(
    "src/WebPaperBodySession.cs",
    '''    public void RefreshFromModel()\n    {\n        SendStateChanged();\n        _miniViewHost?.SendStateChanged();\n    }\n''',
    '''    internal void ApplyExternalState(string stateJson)\n    {\n        var normalized = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;\n        if (string.Equals(_stateJson, normalized, StringComparison.Ordinal))\n        {\n            return;\n        }\n        _stateJson = normalized;\n        SendStateChanged();\n        _miniViewHost?.SendStateChanged();\n    }\n\n    internal void ReceiveRuntimeMessage(JsonElement payload)\n    {\n        Send(new\n        {\n            type = "runtimeMessage",\n            payload\n        });\n    }\n\n    public void RefreshFromModel()\n    {\n        SendStateChanged();\n        _miniViewHost?.SendStateChanged();\n    }\n''')


# -----------------------------------------------------------------------------
# Per-paper runtime: one hidden WebView per Paper, separate from its body UI.
# -----------------------------------------------------------------------------
write(
    "src/WebPaperRuntime.cs",
    r'''using System.IO;
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
    private readonly PaperBodyPluginDescriptor _descriptor;
    private readonly string _paperId;
    private readonly PaperBodyPluginHostApi _workspace;
    private readonly Action<string> _setTitle;
    private readonly Action<string> _setHeaderText;
    private readonly Action<PaperCapsulePresentation?> _setCapsulePresentation;
    private readonly Action<string> _saveState;
    private readonly Action<JsonElement> _postBodyMessage;
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
    private string _settingsJson;
    private bool _documentReady;
    private bool _startupCompleted;
    private bool _reloadRecoveryPending;
    private bool _restartRequested;
    private bool _disposed;
    private ulong _documentNavigationId;
    private bool _hasDocumentNavigation;

    public WebPaperRuntime(
        PaperBodyPluginDescriptor descriptor,
        string paperId,
        string stateJson,
        string settingsJson,
        PaperBodyPluginHostApi workspace,
        Func<bool> isActive,
        Action<string> setTitle,
        Action<string> setHeaderText,
        Action<PaperCapsulePresentation?> setCapsulePresentation,
        Action<string> saveState,
        Action<JsonElement> postBodyMessage,
        Action requestRestart)
    {
        _descriptor = descriptor;
        _paperId = paperId;
        _stateJson = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;
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
              const queuedRequests = [];
              let sequence = 0;
              let hostReady = false;
              let stateProvider = null;
              const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });
              const request = (method, params = {}) => {
                const requestId = `p${++sequence}`;
                const payload = { requestId, method: String(method ?? ''), params: params ?? {} };
                return new Promise((resolve, reject) => {
                  pending.set(requestId, { resolve, reject });
                  if (hostReady) post('hostRequest', payload);
                  else queuedRequests.push(payload);
                });
              };
              const saveState = state => post('saveState', state ?? {});
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
                post(message) { post('bodyMessage', message ?? null); }
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
                if (message?.type === 'initialize' && !hostReady) {
                  hostReady = true;
                  for (const payload of queuedRequests.splice(0)) post('hostRequest', payload);
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
            FailStartupOrRestart(
                $"Web paper runtime navigation failed ({e.WebErrorStatus}).");
            return;
        }

        _reloadRecoveryPending = false;
        _documentReady = true;
        SendInitialize();
        _startupReady.TrySetResult(true);
    }

    private void SendInitialize()
    {
        Send(new
        {
            type = "initialize",
            surface = "paperRuntime",
            paperId = _paperId,
            providerId = _descriptor.Id,
            apiVersion = _descriptor.ApiVersion,
            state = ParseState(_stateJson),
            stateVersion = _descriptor.StateVersion,
            settings = ParseState(_settingsJson),
            permissions = _workspace.GrantedPermissions.OrderBy(value => value).ToArray()
        });
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||
            !_documentReady ||
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

            var payload = root.TryGetProperty("payload", out var payloadValue)
                ? payloadValue
                : default;
            switch (typeValue.GetString())
            {
                case "saveState":
                    _saveState(payload.ValueKind == JsonValueKind.Undefined
                        ? "{}"
                        : payload.GetRawText());
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
                case "bodyMessage":
                    _postBodyMessage(
                        payload.ValueKind == JsonValueKind.Undefined
                            ? JsonSerializer.SerializeToElement<object?>(null)
                            : payload.Clone());
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

    private void HandleHostRequest(JsonElement payload)
    {
        var requestId = WebPluginRuntimeInfrastructure.RequiredString(payload, "requestId");
        try
        {
            var method = WebPluginRuntimeInfrastructure.RequiredString(payload, "method");
            var parameters = WebPluginRuntimeInfrastructure.ParametersOrEmpty(payload);
            object? result;
            if (string.Equals(method, "settings.get", StringComparison.Ordinal))
            {
                result = ParseState(_settingsJson);
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
            Send(new { type = "hostResponse", requestId, ok = true, result });
        }
        catch (PaperTodoPluginException ex)
        {
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

    public void PostBodyMessage(JsonElement payload)
    {
        Send(new
        {
            type = "bodyMessage",
            payload
        });
    }

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

    private void Send(object value)
    {
        if (!_documentReady || _disposed || !_isActive() || _webView.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(
                    value,
                    WebPluginRuntimeInfrastructure.JsonOptions));
        }
        catch
        {
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
        Send(new { type = "commitRequested" });
        _documentReady = false;
        _disposed = true;
        _startupReady.TrySetCanceled();
        _lifetime.Cancel();
        ClearHostSubscriptions();
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
''')

write(
    "src/PaperWindow.WebPaperRuntime.cs",
    r'''using System.Diagnostics;
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
''')


# -----------------------------------------------------------------------------
# PaperWindow lifecycle: Web background work is owned by WebPaperRuntime; Native
# keeps the existing BackgroundUpdates body-session contract.
# -----------------------------------------------------------------------------
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        return !IsCurrentBodyProviderMarkdown &&\n            !_bodyFailed &&\n            !string.IsNullOrWhiteSpace(title);\n''',
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasLiveWebPaperRuntime(\n                NormalizeBodyProviderId(_paper.BodyProviderId))) &&\n            !string.IsNullOrWhiteSpace(title);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        return !IsCurrentBodyProviderMarkdown &&\n            !_bodyFailed &&\n            !string.IsNullOrWhiteSpace(title);\n''',
    '''        return !IsCurrentBodyProviderMarkdown &&\n            (!_bodyFailed || HasLiveWebPaperRuntime(\n                NormalizeBodyProviderId(_paper.BodyProviderId))) &&\n            !string.IsNullOrWhiteSpace(title);\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        if (string.Equals(providerId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))\n        {\n            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);\n''',
    '''        if (string.Equals(providerId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))\n        {\n            DisposeWebPaperRuntime();\n            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        if (!_controller.PaperBodyPlugins.TryGet(providerId, out var descriptor))\n        {\n            _bodyDescriptor = null;\n''',
    '''        if (!_controller.PaperBodyPlugins.TryGet(providerId, out var descriptor))\n        {\n            DisposeWebPaperRuntime();\n            _bodyDescriptor = null;\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''            if (descriptor.Kind == PaperBodyPluginKind.Native)\n            {\n                var stored = ReadPluginState(descriptor.Id);\n''',
    '''            if (descriptor.Kind == PaperBodyPluginKind.Native)\n            {\n                DisposeWebPaperRuntime();\n                var stored = ReadPluginState(descriptor.Id);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''            if (descriptor.Kind == PaperBodyPluginKind.Web && descriptor.Manifest != null)\n            {\n                var stored = ReadPluginState(descriptor.Id);\n''',
    '''            if (descriptor.Kind == PaperBodyPluginKind.Web && descriptor.Manifest != null)\n            {\n                EnsureWebPaperRuntime(descriptor);\n                var stored = ReadPluginState(descriptor.Id);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''                var context = CreatePluginContext(descriptor, generation, stored);\n                return new WebPaperBodySession(context, descriptor.Manifest);\n''',
    '''                var context = CreatePluginContext(descriptor, generation, stored);\n                return new WebPaperBodySession(\n                    context,\n                    descriptor.Manifest,\n                    payload => PostBodyMessageToWebPaperRuntime(\n                        descriptor.Id,\n                        payload));\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        _controller.PaperBodyPlugins.DataStore.SavePaperState(\n            providerId,\n            _paper.Id,\n            stateVersion,\n            normalized);\n''',
    '''        _controller.PaperBodyPlugins.DataStore.SavePaperState(\n            providerId,\n            _paper.Id,\n            stateVersion,\n            normalized);\n        NotifyWebPaperRuntimeStateChanged(providerId, normalized);\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''    private void ResetPluginRuntimeState(bool refreshTitle)\n    {\n        var hadDisplayTitle = !string.IsNullOrEmpty(_pluginDisplayTitle);\n        var hadCapsulePresentation = _pluginCapsulePresentation != null;\n        _pluginDisplayTitle = "";\n        _pluginCapsulePresentation = null;\n        ResetPluginCapsuleCustomViews();\n''',
    '''    private void ResetPluginRuntimeState(bool refreshTitle)\n    {\n        var preservePaperPresentation = HasLiveWebPaperRuntime(\n            NormalizeBodyProviderId(_paper.BodyProviderId));\n        var hadDisplayTitle =\n            !preservePaperPresentation &&\n            !string.IsNullOrEmpty(_pluginDisplayTitle);\n        var hadCapsulePresentation =\n            !preservePaperPresentation &&\n            _pluginCapsulePresentation != null;\n        if (!preservePaperPresentation)\n        {\n            _pluginDisplayTitle = "";\n            _pluginCapsulePresentation = null;\n        }\n        ResetPluginCapsuleCustomViews();\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        CommitPendingEditsForSave();\n        RemoveCurrentPaperBody();\n        _paper.BodyProviderId = normalized;\n''',
    '''        CommitPendingEditsForSave();\n        DisposeWebPaperRuntime();\n        RemoveCurrentPaperBody();\n        _paper.BodyProviderId = normalized;\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        var runtimeVisible = _paper.IsVisible &&\n            (visible ||\n             BodyRequires(PaperBodyRuntimeRequirements.BackgroundUpdates));\n''',
    '''        var keepNativeBodyRuntimeAlive =\n            _bodyDescriptor?.Kind == PaperBodyPluginKind.Native &&\n            BodyRequires(PaperBodyRuntimeRequirements.BackgroundUpdates);\n        var runtimeVisible = _paper.IsVisible &&\n            (visible || keepNativeBodyRuntimeAlive);\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        InvokeBodySession(item =>\n        {\n            // Presentation first avoids briefly starting a cold background controller when a\n            // folded paper is being expanded in the same dispatcher turn.\n            item.OnPresentationChanged(visible);\n            item.OnVisibilityChanged(runtimeVisible);\n        });\n''',
    '''        InvokeBodySession(item =>\n        {\n            item.OnPresentationChanged(visible);\n            item.OnVisibilityChanged(runtimeVisible);\n        });\n''')
replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        if (!visible)\n        {\n            }\n''',
    "")

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));\n''',
    '''        NotifyWebPaperRuntimeSettingsChanged(providerId, settingsJson);\n        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''    private void ClearPluginPresentationOnFailure()\n    {\n        var hadHeader = !string.IsNullOrEmpty(_paper.BodyHeaderText);\n''',
    '''    private void ClearPluginPresentationOnFailure()\n    {\n        if (HasLiveWebPaperRuntime(NormalizeBodyProviderId(_paper.BodyProviderId)))\n        {\n            return;\n        }\n\n        var hadHeader = !string.IsNullOrEmpty(_paper.BodyHeaderText);\n''')

replace_once(
    "src/PaperWindow.PluginBodies.cs",
    '''    internal void DisposeCurrentPaperBody()\n    {\n        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);\n        _bodyDescriptor = null;\n''',
    '''    internal void DisposeCurrentPaperBody()\n    {\n        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);\n        DisposeWebPaperRuntime();\n        _bodyDescriptor = null;\n''')

replace_once(
    "src/PaperWindow.PluginStatus.cs",
    '''    internal bool HasRunningPluginBody(string providerId)\n    {\n        return _paper.Type == PaperTypes.Note &&\n            !_bodyFailed &&\n            _paperBodyHost.HasCurrent &&\n            _bodyRuntimeVisible &&\n            string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                providerId,\n                StringComparison.Ordinal);\n    }\n''',
    '''    internal bool HasRunningPluginBody(string providerId)\n    {\n        if (_paper.Type != PaperTypes.Note ||\n            !string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                providerId,\n                StringComparison.Ordinal))\n        {\n            return false;\n        }\n\n        return HasLiveWebPaperRuntime(providerId) ||\n            (!_bodyFailed && _paperBodyHost.HasCurrent && _bodyRuntimeVisible);\n    }\n''')


# -----------------------------------------------------------------------------
# Official Web clock: move background title/capsule work into paper-runtime.html.
# The body remains pure visible UI. Mirror the installed plugin and sample source.
# -----------------------------------------------------------------------------
for manifest_path in [
    "plugins/official.clock.web/plugin.json",
    "plugin-samples/PaperTodo.Plugin.OfficialClockWeb/plugin.json",
]:
    data = json.loads(read(manifest_path))
    data["version"] = "1.5.0"
    reordered = {}
    for key, value in data.items():
        reordered[key] = value
        if key == "entry":
            reordered["paperRuntime"] = "web/paper-runtime.html"
    write(manifest_path, json.dumps(reordered, ensure_ascii=False, indent=2) + "\n")

runtime_html = r'''<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <title>PaperTodo Clock Runtime</title>
</head>
<body>
<script>
  const defaults = Object.freeze({
    showDayProgress: true,
    hourCycle: '24',
    dateFormat: 'long',
    timeZone: 'local',
    titleMode: 'time',
    customTitle: ''
  });

  const zoneMap = Object.freeze({
    local: undefined,
    utc: 'UTC',
    beijing: 'Asia/Shanghai',
    tokyo: 'Asia/Tokyo',
    london: 'Europe/London',
    newYork: 'America/New_York',
    losAngeles: 'America/Los_Angeles'
  });
  const zoneLabels = Object.freeze({
    local: '本地时间',
    utc: 'UTC',
    beijing: '北京时间',
    tokyo: '东京时间',
    london: '伦敦时间',
    newYork: '纽约时间',
    losAngeles: '洛杉矶时间'
  });

  let settings = { ...defaults };
  let timer = 0;
  let lastHeaderText = '';
  let lastCapsuleSignature = '';

  function applySettings(value) {
    settings = { ...defaults, ...(value || {}) };
  }

  function zonedParts(date) {
    const timeZone = zoneMap[settings.timeZone];
    const parts = new Intl.DateTimeFormat(undefined, {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      weekday: 'long',
      hour12: settings.hourCycle === '12'
    }).formatToParts(date);
    return Object.fromEntries(parts.map(part => [part.type, part.value]));
  }

  function formatDate(parts) {
    const y = parts.year;
    const m = parts.month;
    const d = parts.day;
    return settings.dateFormat === 'short' ? `${y}-${m}-${d}`
      : settings.dateFormat === 'slash' ? `${y}/${m}/${d}`
      : settings.dateFormat === 'us' ? `${m}/${d}/${y}`
      : settings.dateFormat === 'eu' ? `${d}/${m}/${y}`
      : `${y}年${Number(m)}月${Number(d)}日`;
  }

  function zonedClockParts(date) {
    const timeZone = zoneMap[settings.timeZone];
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hourCycle: 'h23'
    }).formatToParts(date);
    return Object.fromEntries(parts.map(part => [part.type, part.value]));
  }

  function titleText(timeText, dateText) {
    if (settings.titleMode === 'date') return dateText || timeText;
    if (settings.titleMode === 'zone') {
      return `${zoneLabels[settings.timeZone] || '本地时间'} · ${timeText}`;
    }
    if (settings.titleMode === 'fixed') return '时钟';
    if (settings.titleMode === 'custom' && String(settings.customTitle || '').trim()) {
      return String(settings.customTitle).trim();
    }
    return timeText;
  }

  function publish() {
    const now = new Date();
    const parts = zonedParts(now);
    const timeText = `${parts.hour}:${parts.minute}`;
    const title = titleText(timeText, formatDate(parts));
    const paper = window.papertodo?.paper;
    if (!paper) return;

    if (title !== lastHeaderText) {
      paper.setHeaderText(title);
      lastHeaderText = title;
    }

    const clock = zonedClockParts(now);
    const elapsed = Number(clock.hour) * 3600 + Number(clock.minute) * 60 + Number(clock.second);
    const percent = Math.min(100, Math.max(0, elapsed / 864));
    const progressStep = Math.round(percent * 10);
    const signature = `${title}\u001f${progressStep}\u001f${settings.showDayProgress ? 1 : 0}`;
    if (signature !== lastCapsuleSignature) {
      paper.setCapsulePresentation({
        preferredWidth: 0,
        plainText: title,
        toolTip: title,
        components: settings.showDayProgress
          ? [
              { kind: 'progressRing', value: percent / 100, tone: 'accent' },
              { kind: 'text', text: title, fill: true }
            ]
          : [{ kind: 'text', text: title, fill: true }]
      });
      lastCapsuleSignature = signature;
    }
  }

  function restartTimer() {
    clearInterval(timer);
    timer = setInterval(publish, 1000);
    publish();
  }

  window.addEventListener('papertodo', event => {
    const message = event.detail || {};
    if (message.type === 'initialize') {
      applySettings(message.settings);
      restartTimer();
    } else if (message.type === 'settingsChanged') {
      applySettings(message.settings);
      lastHeaderText = '';
      lastCapsuleSignature = '';
      restartTimer();
    }
  });
</script>
</body>
</html>
'''
write("plugins/official.clock.web/web/paper-runtime.html", runtime_html)
write("plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/paper-runtime.html", runtime_html)

for body_path in [
    "plugins/official.clock.web/web/index.html",
    "plugin-samples/PaperTodo.Plugin.OfficialClockWeb/web/index.html",
]:
    text = read(body_path)
    text = text.replace("    let lastHeaderText = '';\n    let lastCapsuleSignature = '';\n", "")
    start = text.find("    function titleText(timeText, dateText) {\n")
    end = text.find("    function render() {\n", start)
    if start < 0 or end < 0:
        raise RuntimeError(f"{body_path}: titleText/render markers not found")
    text = text[:start] + text[end:]
    old = '''      const timeText = `${parts.hour}:${parts.minute}`;\n      const title = titleText(timeText, dateText);\n      const paperApi = window.papertodo?.paper;\n      if (paperApi && title !== lastHeaderText) {\n        paperApi.setHeaderText(title);\n        lastHeaderText = title;\n      }\n\n      // The body may render at 4 Hz for a stable second boundary, but host capsule templates do\n      // not need to be rebuilt at that cadence. Update on title changes or each 0.1% of the day.\n      const capsuleProgressStep = Math.round(percent * 10);\n      const capsuleSignature =\n        `${title}\\u001f${capsuleProgressStep}\\u001f${settings.showDayProgress ? 1 : 0}`;\n      if (paperApi && capsuleSignature !== lastCapsuleSignature) {\n        paperApi.setCapsulePresentation({\n          preferredWidth: 0,\n          plainText: title,\n          toolTip: title,\n          components: settings.showDayProgress\n            ? [\n                { kind: 'progressRing', value: percent / 100, tone: 'accent' },\n                { kind: 'text', text: title, fill: true }\n              ]\n            : [\n                { kind: 'text', text: title, fill: true }\n              ]\n        });\n        lastCapsuleSignature = capsuleSignature;\n      }\n'''
    if old not in text:
        raise RuntimeError(f"{body_path}: body-owned presentation block not found")
    text = text.replace(old, "", 1)
    write(body_path, text)

readme_path = "plugin-samples/PaperTodo.Plugin.OfficialClockWeb/README.md"
readme = read(readme_path)
if "paperRuntime" not in readme:
    readme += "\n## Paper Runtime\n\n`requires: [\"backgroundUpdates\"]` 的 Web 插件通过 `paperRuntime` 声明每张 Paper 独立的后台入口。时钟的定时器、标题与胶囊更新运行在 `web/paper-runtime.html`；`web/index.html` 只负责展开后的可见 UI，因此 Body 重建不会重启后台计时。\n"
write(readme_path, readme)


# Architecture note: keep this short and factual.
architecture_path = "ARCHITECTURE.md"
architecture = read(architecture_path)
marker = "## Web Paper Runtime 生命周期"
if marker not in architecture:
    architecture += r'''

## Web Paper Runtime 生命周期

Web 插件把三种生命周期分开：`appRuntime` 是 Provider 级单例；`paperRuntime` 是声明 `backgroundUpdates` 时每张 Paper 独立的后台 WebView；`WebPaperBodySession` 只负责完整纸片 UI。PaperRuntime 从创建起固定挂在后台宿主，不跨 HWND；Body 的折叠、失败、reload 或重建都不会销毁对应 PaperRuntime。删除 Paper、切换 Provider 或 PaperWindow 最终销毁时才结束 PaperRuntime。
'''
write(architecture_path, architecture)

print("web paper runtime refactor applied")
