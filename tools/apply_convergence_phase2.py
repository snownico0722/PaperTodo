from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, value):
    (ROOT / path).write_text(value, encoding='utf-8', newline='')

def replace_once(path, old, new):
    value = read(path)
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:100]!r}')
    write(path, value.replace(old, new, 1))

def sub_once(path, pattern, repl, flags=re.S):
    value = read(path)
    updated, count = re.subn(pattern, repl, value, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f'{path}: expected one regex match, got {count}: {pattern[:120]!r}')
    write(path, updated)

# -----------------------------------------------------------------------------
# 1. Resource policy: visible Body/Mini use normal browser scheduling;
#    AppRuntime/PaperRuntime use a separate runtime environment with background
#    throttling disabled. The two environment groups deliberately use separate
#    user-data folders: plugins must communicate through the host bridge rather
#    than assuming browser storage is shared across lifecycle scopes.
# -----------------------------------------------------------------------------
replace_once(
    'src/WebPaperBodySession.cs',
    '''    private static readonly object EnvironmentGate = new();\n    private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks =\n        new(StringComparer.OrdinalIgnoreCase);''',
    '''    private static readonly object EnvironmentGate = new();\n    private static readonly Dictionary<string, Task<CoreWebView2Environment>> VisibleEnvironmentTasks =\n        new(StringComparer.OrdinalIgnoreCase);\n    private static readonly Dictionary<string, Task<CoreWebView2Environment>> RuntimeEnvironmentTasks =\n        new(StringComparer.OrdinalIgnoreCase);''')

sub_once(
    'src/WebPaperBodySession.cs',
    r'''    private static async Task<CoreWebView2Environment> GetPluginEnvironmentAsync\(\n        string pluginDirectory\)\n    \{.*?\n    private static string WebHostName''',
    '''    internal static async Task<CoreWebView2Environment> GetPluginEnvironmentAsync(\n        string pluginDirectory,\n        bool backgroundRuntime = false)\n    {\n        var key = Path.GetFullPath(pluginDirectory);\n        var environmentTasks = backgroundRuntime\n            ? RuntimeEnvironmentTasks\n            : VisibleEnvironmentTasks;\n        Task<CoreWebView2Environment> task;\n        lock (EnvironmentGate)\n        {\n            if (!environmentTasks.TryGetValue(key, out task!))\n            {\n                task = CreateEnvironmentAsync(key, backgroundRuntime);\n                environmentTasks.Add(key, task);\n            }\n        }\n\n        try\n        {\n            return await task;\n        }\n        catch\n        {\n            lock (EnvironmentGate)\n            {\n                if (environmentTasks.TryGetValue(key, out var current) &&\n                    ReferenceEquals(current, task))\n                {\n                    environmentTasks.Remove(key);\n                }\n            }\n            throw;\n        }\n    }\n\n    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(\n        string pluginDirectory,\n        bool backgroundRuntime)\n    {\n        var userDataFolder = Path.Combine(\n            pluginDirectory,\n            ".runtime",\n            backgroundRuntime ? "webview2-runtime" : "webview2");\n        Directory.CreateDirectory(userDataFolder);\n        var options = backgroundRuntime\n            ? new CoreWebView2EnvironmentOptions(\n                "--disable-background-timer-throttling " +\n                "--disable-renderer-backgrounding " +\n                "--disable-backgrounding-occluded-windows")\n            : new CoreWebView2EnvironmentOptions();\n        return CoreWebView2Environment.CreateAsync(\n            browserExecutableFolder: null,\n            userDataFolder: userDataFolder,\n            options: options);\n    }\n\n    private static string WebHostName''')

replace_once(
    'src/WebPaperBodySession.SharedRuntime.cs',
    '''    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(\n        string pluginDirectory) =>\n        GetPluginEnvironmentAsync(pluginDirectory);''',
    '''    internal static Task<CoreWebView2Environment> SharedPluginEnvironmentAsync(\n        string pluginDirectory) =>\n        GetPluginEnvironmentAsync(pluginDirectory, backgroundRuntime: true);''')

# PaperRuntime cold startup: only the expensive CoreWebView2 bootstrap is globally bounded.
replace_once(
    'src/WebPaperRuntime.cs',
    '''internal sealed class WebPaperRuntime : IDisposable\n{\n    private readonly PaperBodyPluginDescriptor _descriptor;''',
    '''internal sealed class WebPaperRuntime : IDisposable\n{\n    private const int MaximumConcurrentColdStarts = 3;\n    private static readonly SemaphoreSlim StartupGate = new(\n        MaximumConcurrentColdStarts,\n        MaximumConcurrentColdStarts);\n\n    private readonly PaperBodyPluginDescriptor _descriptor;''')
sub_once(
    'src/WebPaperRuntime.cs',
    r'''    public async Task StartAsync\(\)\n    \{\n(.*?)\n    \}\n\n    private static string BuildBridgeScript''',
    '''    public async Task StartAsync()\n    {\n        await StartupGate.WaitAsync(_lifetime.Token);\n        try\n        {\n            await StartCoreAsync();\n        }\n        finally\n        {\n            StartupGate.Release();\n        }\n    }\n\n    private async Task StartCoreAsync()\n    {\n\\1\n    }\n\n    private static string BuildBridgeScript''')
# re.sub replacement above intentionally used a group marker; materialize it if Python left it literal.
v = read('src/WebPaperRuntime.cs')
if '\\1' in v:
    raise RuntimeError('WebPaperRuntime StartAsync capture was not expanded')

# -----------------------------------------------------------------------------
# 2. Persistent per-paper state has one writer. With paperRuntime present, only
#    PaperRuntime can save persistent state. Body/Mini remain UI surfaces.
# -----------------------------------------------------------------------------
replace_once(
    'src/WebPaperBodySession.cs',
    '''    private readonly Action<JsonElement>? _postRuntimeMessage;\n    private readonly bool _paperRuntimeOwnsPresentation;''',
    '''    private readonly Func<JsonElement, bool>? _postRuntimeMessage;\n    private readonly bool _paperRuntimeOwnsPresentation;\n    private readonly bool _paperRuntimeOwnsState;''')
replace_once(
    'src/WebPaperBodySession.cs',
    '''        PaperBodyContext context,\n        PaperBodyPluginManifest manifest,\n        Action<JsonElement>? postRuntimeMessage = null,\n        bool paperRuntimeOwnsPresentation = false)''',
    '''        PaperBodyContext context,\n        PaperBodyPluginManifest manifest,\n        Func<JsonElement, bool>? postRuntimeMessage = null,\n        bool paperRuntimeOwnsPresentation = false,\n        bool paperRuntimeOwnsState = false)''')
replace_once(
    'src/WebPaperBodySession.cs',
    '''        _postRuntimeMessage = postRuntimeMessage;\n        _paperRuntimeOwnsPresentation = paperRuntimeOwnsPresentation;''',
    '''        _postRuntimeMessage = postRuntimeMessage;\n        _paperRuntimeOwnsPresentation = paperRuntimeOwnsPresentation;\n        _paperRuntimeOwnsState = paperRuntimeOwnsState;''')

# Body bridge knows whether this surface may use persistent saveState.
replace_once(
    'src/WebPaperBodySession.cs',
    '''            await core.AddScriptToExecuteOnDocumentCreatedAsync(\n                BuildBridgeScript(_expectedOrigin));''',
    '''            await core.AddScriptToExecuteOnDocumentCreatedAsync(\n                BuildBridgeScript(\n                    _expectedOrigin,\n                    persistentStateWritable: !_paperRuntimeOwnsState));''')
replace_once(
    'src/WebPaperBodySession.cs',
    '''    private static string BuildBridgeScript(string expectedOrigin)\n    {\n        var originJson = JsonSerializer.Serialize(expectedOrigin);\n        return $$"""\n            (() => {\n              const expectedOrigin = {{originJson}};''',
    '''    private static string BuildBridgeScript(\n        string expectedOrigin,\n        bool persistentStateWritable)\n    {\n        var originJson = JsonSerializer.Serialize(expectedOrigin);\n        var stateWritableJson = persistentStateWritable ? "true" : "false";\n        return $$"""\n            (() => {\n              const expectedOrigin = {{originJson}};\n              const persistentStateWritable = {{stateWritableJson}};''')
replace_once(
    'src/WebPaperBodySession.cs',
    '''              const saveState = state => {\n                const nextState = state ?? {};''',
    '''              const saveState = state => {\n                if (!persistentStateWritable) {\n                  throw new Error('Persistent paper state is owned by paperRuntime; send a runtime command instead.');\n                }\n                const nextState = state ?? {};''')
replace_once(
    'src/WebPaperBodySession.cs',
    '''                registerStateProvider(provider) {\n                  stateProvider = typeof provider === 'function' ? provider : null;''',
    '''                registerStateProvider(provider) {\n                  if (!persistentStateWritable) {\n                    throw new Error('Persistent paper state providers belong to paperRuntime for this plugin.');\n                  }\n                  stateProvider = typeof provider === 'function' ? provider : null;''')
# Host is the final authority even if a page bypasses the injected bridge.
replace_once(
    'src/WebPaperBodySession.cs',
    '''                case "saveState":\n                    UpdateStateFromWebSurface(payload, sourceMini: null);\n                    break;''',
    '''                case "saveState":\n                    if (!_paperRuntimeOwnsState)\n                    {\n                        UpdateStateFromWebSurface(payload, sourceMini: null);\n                    }\n                    break;''')

# Mini is another visual surface and obeys the same writer rule.
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''    private void UpdateStateFromWebSurface(\n        JsonElement payload,\n        WebPluginMiniViewHost? sourceMini)\n    {\n        var nextStateJson''',
    '''    private void UpdateStateFromWebSurface(\n        JsonElement payload,\n        WebPluginMiniViewHost? sourceMini)\n    {\n        if (_paperRuntimeOwnsState)\n        {\n            return;\n        }\n\n        var nextStateJson''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                await core.AddScriptToExecuteOnDocumentCreatedAsync(\n                    BuildMiniBridgeScript(_expectedOrigin));''',
    '''                await core.AddScriptToExecuteOnDocumentCreatedAsync(\n                    BuildMiniBridgeScript(\n                        _expectedOrigin,\n                        persistentStateWritable: !_owner._paperRuntimeOwnsState));''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''        private static string BuildMiniBridgeScript(string expectedOrigin)\n        {\n            var originJson = JsonSerializer.Serialize(expectedOrigin);\n            return $$"""\n                (() => {\n                  const expectedOrigin = {{originJson}};''',
    '''        private static string BuildMiniBridgeScript(\n            string expectedOrigin,\n            bool persistentStateWritable)\n        {\n            var originJson = JsonSerializer.Serialize(expectedOrigin);\n            var stateWritableJson = persistentStateWritable ? "true" : "false";\n            return $$"""\n                (() => {\n                  const expectedOrigin = {{originJson}};\n                  const persistentStateWritable = {{stateWritableJson}};''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                  const saveState = state => post('saveState', state ?? {});''',
    '''                  const saveState = state => {\n                    if (!persistentStateWritable) {\n                      throw new Error('Persistent paper state is owned by paperRuntime; send a runtime command instead.');\n                    }\n                    post('saveState', state ?? {});\n                  };''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                    registerStateProvider(provider) {\n                      stateProvider = typeof provider === 'function' ? provider : null;''',
    '''                    registerStateProvider(provider) {\n                      if (!persistentStateWritable) {\n                        throw new Error('Persistent paper state providers belong to paperRuntime for this plugin.');\n                      }\n                      stateProvider = typeof provider === 'function' ? provider : null;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                    case "saveState":\n                        _owner.UpdateStateFromWebSurface(payload, this);\n                        break;''',
    '''                    case "saveState":\n                        if (!_owner._paperRuntimeOwnsState)\n                        {\n                            _owner.UpdateStateFromWebSurface(payload, this);\n                        }\n                        break;''')

# Calculate one paperRuntime semantic flag at Body creation and use it for both
# presentation and persistent-state ownership.
replace_once(
    'src/PaperWindow.PluginBodies.cs',
    '''                var context = CreatePluginContext(descriptor, generation, stored);\n                return new WebPaperBodySession(\n                    context,\n                    descriptor.Manifest,\n                    payload => _controller.PostBodyMessageToWebPaperRuntime(\n                        _paper.Id,\n                        descriptor.Id,\n                        payload),\n                    paperRuntimeOwnsPresentation:\n                        (descriptor.RuntimeRequirements &\n                         PaperBodyRuntimeRequirements.BackgroundUpdates) != 0 &&\n                        !string.IsNullOrWhiteSpace(\n                            descriptor.Manifest.PaperRuntimePath));''',
    '''                var context = CreatePluginContext(descriptor, generation, stored);\n                var hasPaperRuntime =\n                    (descriptor.RuntimeRequirements &\n                     PaperBodyRuntimeRequirements.BackgroundUpdates) != 0 &&\n                    !string.IsNullOrWhiteSpace(\n                        descriptor.Manifest.PaperRuntimePath);\n                return new WebPaperBodySession(\n                    context,\n                    descriptor.Manifest,\n                    payload => _controller.PostBodyMessageToWebPaperRuntime(\n                        _paper.Id,\n                        descriptor.Id,\n                        payload),\n                    paperRuntimeOwnsPresentation: hasPaperRuntime,\n                    paperRuntimeOwnsState: hasPaperRuntime);''')

# -----------------------------------------------------------------------------
# 3. Thin ordered transport. Reuse existing request/response so send failure is
#    explicit to plugin code. No ACK/retry/message-bus semantics are added.
# -----------------------------------------------------------------------------
# Body JS: runtime.post is now an awaitable send request.
replace_once(
    'src/WebPaperBodySession.cs',
    '''              const runtime = Object.freeze({\n                post(message) { post('runtimeMessage', message ?? null); }\n              });''',
    '''              const runtime = Object.freeze({\n                post(message) { return request('runtime.post', { message: message ?? null }); }\n              });''')
# Remove old direct one-way ingress case.
sub_once(
    'src/WebPaperBodySession.cs',
    r'''                case "runtimeMessage":\n                    _postRuntimeMessage\?\.Invoke\(\n                        payload\.ValueKind == JsonValueKind\.Undefined\n                            \? JsonSerializer\.SerializeToElement<object\?>\(null\)\n                            : payload\.Clone\(\)\);\n                    break;\n''',
    '')
# Body ExecuteHostRequest: runtime.post is handled before workspace switch.
sub_once(
    'src/WebPaperBodySession.cs',
    r'''    private object\? ExecuteHostRequest\(string method, JsonElement parameters\) => method switch\n    \{''',
    '''    private object? ExecuteHostRequest(string method, JsonElement parameters)\n    {\n        if (string.Equals(method, "runtime.post", StringComparison.Ordinal))\n        {\n            var message = parameters.ValueKind == JsonValueKind.Object &&\n                          parameters.TryGetProperty("message", out var messageValue)\n                ? messageValue\n                : default;\n            if (_postRuntimeMessage == null ||\n                !_postRuntimeMessage(\n                    message.ValueKind == JsonValueKind.Undefined\n                        ? JsonSerializer.SerializeToElement<object?>(null)\n                        : message.Clone()))\n            {\n                throw new PaperTodoPluginException(\n                    "runtime_unavailable",\n                    "The paper runtime is not ready to accept this message.");\n            }\n            return null;\n        }\n\n        return method switch\n        {''')
# Close the new method after the switch expression.
replace_once(
    'src/WebPaperBodySession.cs',
    '''        _ => throw new PaperTodoPluginException(\n            "method_not_found",\n            $"Unknown PaperTodo plugin host method: {method}")\n    };''',
    '''            _ => throw new PaperTodoPluginException(\n                "method_not_found",\n                $"Unknown PaperTodo plugin host method: {method}")\n        };\n    }''')

# Runtime JS: body.post is the same thin request boundary.
replace_once(
    'src/WebPaperRuntime.cs',
    '''              const body = Object.freeze({\n                post(message) { post('bodyMessage', message ?? null); }\n              });''',
    '''              const body = Object.freeze({\n                post(message) { return request('body.post', { message: message ?? null }); }\n              });''')
# Runtime delegate returns accepted/not accepted.
replace_once(
    'src/WebPaperRuntime.cs',
    '''    private readonly Action<string> _saveState;\n    private readonly Action<JsonElement> _postBodyMessage;''',
    '''    private readonly Action<string> _saveState;\n    private readonly Func<JsonElement, bool> _postBodyMessage;''')
replace_once(
    'src/WebPaperRuntime.cs',
    '''        Action<string> saveState,\n        Action<JsonElement> postBodyMessage,\n        Action requestRestart)''',
    '''        Action<string> saveState,\n        Func<JsonElement, bool> postBodyMessage,\n        Action requestRestart)''')
# Remove direct one-way bodyMessage input.
sub_once(
    'src/WebPaperRuntime.cs',
    r'''                case "bodyMessage":\n                    _postBodyMessage\(\n                        payload\.ValueKind == JsonValueKind\.Undefined\n                            \? JsonSerializer\.SerializeToElement<object\?>\(null\)\n                            : payload\.Clone\(\)\);\n                    break;\n''',
    '')
# Runtime hostRequest handles body.post before other request families.
replace_once(
    'src/WebPaperRuntime.cs',
    '''            if (string.Equals(method, "settings.get", StringComparison.Ordinal))\n            {\n                result = ParseState(_settingsJson);\n            }\n            else if (method.StartsWith("paper.", StringComparison.Ordinal))''',
    '''            if (string.Equals(method, "settings.get", StringComparison.Ordinal))\n            {\n                result = ParseState(_settingsJson);\n            }\n            else if (string.Equals(method, "body.post", StringComparison.Ordinal))\n            {\n                var message = parameters.ValueKind == JsonValueKind.Object &&\n                              parameters.TryGetProperty("message", out var messageValue)\n                    ? messageValue\n                    : default;\n                if (!_postBodyMessage(\n                        message.ValueKind == JsonValueKind.Undefined\n                            ? JsonSerializer.SerializeToElement<object?>(null)\n                            : message.Clone()))\n                {\n                    throw new PaperTodoPluginException(\n                        "body_unavailable",\n                        "The paper body is not ready to accept this message.");\n                }\n                result = null;\n            }\n            else if (method.StartsWith("paper.", StringComparison.Ordinal))''')

# Target queues preserve order and reject overflow instead of deleting old commands.
sub_once(
    'src/WebPaperRuntime.cs',
    r'''    public void PostBodyMessage\(JsonElement payload\)\n    \{.*?\n    private void SendBodyMessage\(JsonElement payload\)\n    \{\n        Send\(new\n        \{\n            type = "bodyMessage",\n            payload\n        \}\);\n    \}''',
    '''    public bool PostBodyMessage(JsonElement payload)\n    {\n        if (_disposed)\n        {\n            return false;\n        }\n        if (!_documentReady)\n        {\n            if (_pendingBodyMessages.Count >= 32)\n            {\n                return false;\n            }\n            _pendingBodyMessages.Enqueue(payload.Clone());\n            return true;\n        }\n        return SendBodyMessage(payload);\n    }\n\n    private void FlushPendingBodyMessages()\n    {\n        while (_documentReady && _pendingBodyMessages.Count > 0)\n        {\n            if (!SendBodyMessage(_pendingBodyMessages.Peek()))\n            {\n                break;\n            }\n            _pendingBodyMessages.Dequeue();\n        }\n    }\n\n    private bool SendBodyMessage(JsonElement payload) =>\n        Send(new\n        {\n            type = "bodyMessage",\n            payload\n        });''')

sub_once(
    'src/WebPaperBodySession.cs',
    r'''    internal void ReceiveRuntimeMessage\(JsonElement payload\)\n    \{.*?\n    private void SendRuntimeMessage\(JsonElement payload\)\n    \{\n        Send\(new\n        \{\n            type = "runtimeMessage",\n            payload\n        \}\);\n    \}''',
    '''    internal bool ReceiveRuntimeMessage(JsonElement payload)\n    {\n        if (_disposed || _webViewFailed)\n        {\n            return false;\n        }\n        if (!_documentReady || !_pluginDocumentReady)\n        {\n            if (_pendingRuntimeMessages.Count >= 32)\n            {\n                return false;\n            }\n            _pendingRuntimeMessages.Enqueue(payload.Clone());\n            return true;\n        }\n        return SendRuntimeMessage(payload);\n    }\n\n    private void FlushPendingRuntimeMessages()\n    {\n        while (_documentReady && _pluginDocumentReady && _pendingRuntimeMessages.Count > 0)\n        {\n            if (!SendRuntimeMessage(_pendingRuntimeMessages.Peek()))\n            {\n                break;\n            }\n            _pendingRuntimeMessages.Dequeue();\n        }\n    }\n\n    private bool SendRuntimeMessage(JsonElement payload) =>\n        Send(new\n        {\n            type = "runtimeMessage",\n            payload\n        });''')

# Send returns real acceptance so a renderer teardown race does not masquerade as delivery.
sub_once(
    'src/WebPaperRuntime.cs',
    r'''    private void Send\(object value\)\n    \{\n        if \(!_documentReady \|\| _disposed \|\| !_isActive\(\) \|\| _webView\.CoreWebView2 == null\)\n        \{\n            return;\n        \}\n        try\n        \{\n            _webView\.CoreWebView2\.PostWebMessageAsJson\(\n                JsonSerializer\.Serialize\(\n                    value,\n                    WebPluginRuntimeInfrastructure\.JsonOptions\)\);\n        \}\n        catch\n        \{\n        \}\n    \}''',
    '''    private bool Send(object value)\n    {\n        if (!_documentReady || _disposed || !_isActive() || _webView.CoreWebView2 == null)\n        {\n            return false;\n        }\n        try\n        {\n            _webView.CoreWebView2.PostWebMessageAsJson(\n                JsonSerializer.Serialize(\n                    value,\n                    WebPluginRuntimeInfrastructure.JsonOptions));\n            return true;\n        }\n        catch\n        {\n            return false;\n        }\n    }''')
sub_once(
    'src/WebPaperBodySession.cs',
    r'''    private void Send\(object value\)\n    \{\n        if \(!_initialized \|\|\n            !_documentReady \|\|\n            !_pluginDocumentReady \|\|\n            _disposed \|\|\n            _webView\.CoreWebView2 == null\)\n        \{\n            return;\n        \}\n        try\n        \{\n            _webView\.CoreWebView2\.PostWebMessageAsJson\(JsonSerializer\.Serialize\(value, BridgeJsonOptions\)\);\n        \}\n        catch\n        \{\n            // Renderer teardown can race with paper close\.\n        \}\n    \}''',
    '''    private bool Send(object value)\n    {\n        if (!_initialized ||\n            !_documentReady ||\n            !_pluginDocumentReady ||\n            _disposed ||\n            _webView.CoreWebView2 == null)\n        {\n            return false;\n        }\n        try\n        {\n            _webView.CoreWebView2.PostWebMessageAsJson(\n                JsonSerializer.Serialize(value, BridgeJsonOptions));\n            return true;\n        }\n        catch\n        {\n            // Renderer teardown can race with paper close.\n            return false;\n        }\n    }''')

# Controller/window surfaces propagate send acceptance rather than swallowing it.
sub_once(
    'src/AppController.WebPaperRuntime.cs',
    r'''    internal void PostBodyMessageToWebPaperRuntime\(\n        string paperId,\n        string providerId,\n        JsonElement payload\)\n    \{.*?\n    \}\n\n    private void PostWebPaperRuntimeMessageToBody\(\n        WebPaperRuntimeSlot slot,\n        Guid runtimeId,\n        JsonElement payload\)\n    \{.*?\n    \}''',
    '''    internal bool PostBodyMessageToWebPaperRuntime(\n        string paperId,\n        string providerId,\n        JsonElement payload)\n    {\n        return _webPaperRuntimeSlots.TryGetValue(paperId, out var slot) &&\n            string.Equals(slot.ProviderId, providerId, StringComparison.Ordinal) &&\n            slot.Runtime?.PostBodyMessage(payload) == true;\n    }\n\n    private bool PostWebPaperRuntimeMessageToBody(\n        WebPaperRuntimeSlot slot,\n        Guid runtimeId,\n        JsonElement payload)\n    {\n        if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))\n        {\n            return false;\n        }\n        return _windows.TryGetValue(slot.PaperId, out var window) &&\n            !window.IsClosed &&\n            window.ReceiveWebPaperRuntimeMessage(slot.ProviderId, payload);\n    }''')
replace_once(
    'src/PaperWindow.WebPaperRuntimePresentation.cs',
    '''    internal void ReceiveWebPaperRuntimeMessage(\n        string providerId,\n        JsonElement payload)\n    {\n        if (!string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                providerId,\n                StringComparison.Ordinal))\n        {\n            return;\n        }\n        if (_paperBodyHost.Current is WebPaperBodySession body)\n        {\n            body.ReceiveRuntimeMessage(payload);\n        }\n    }''',
    '''    internal bool ReceiveWebPaperRuntimeMessage(\n        string providerId,\n        JsonElement payload)\n    {\n        if (!string.Equals(\n                NormalizeBodyProviderId(_paper.BodyProviderId),\n                providerId,\n                StringComparison.Ordinal))\n        {\n            return false;\n        }\n        return _paperBodyHost.Current is WebPaperBodySession body &&\n            body.ReceiveRuntimeMessage(payload);\n    }''')

# -----------------------------------------------------------------------------
# 4. Mini idle release: one local 45 s timer, no global cache manager/LRU.
# -----------------------------------------------------------------------------
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''    private WebPluginMiniViewHost? _miniViewHost;\n\n    internal bool HasMiniEntry''',
    '''    private WebPluginMiniViewHost? _miniViewHost;\n\n    private void ReleaseIdleMiniView(WebPluginMiniViewHost host)\n    {\n        if (!ReferenceEquals(_miniViewHost, host) || host.IsPreviewVisible)\n        {\n            return;\n        }\n        _miniViewHost = null;\n        host.Dispose();\n    }\n\n    internal bool HasMiniEntry''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''        private const int MaximumInteractiveRegions = 128;\n\n        private readonly WebPaperBodySession _owner;''',
    '''        private const int MaximumInteractiveRegions = 128;\n        private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromSeconds(45);\n\n        private readonly WebPaperBodySession _owner;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''        private readonly CancellationTokenSource _lifetime = new();\n        private FrameworkElement _fallback;''',
    '''        private readonly CancellationTokenSource _lifetime = new();\n        private DispatcherTimer? _idleReleaseTimer;\n        private FrameworkElement _fallback;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''        public bool Matches(EdgeCapsulePreviewSize size) =>\n            Math.Abs(_size.WidthDip - size.WidthDip) <= 0.001 &&\n            Math.Abs(_size.HeightDip - size.HeightDip) <= 0.001;''',
    '''        public bool IsPreviewVisible => _visible;\n\n        public bool Matches(EdgeCapsulePreviewSize size) =>\n            Math.Abs(_size.WidthDip - size.WidthDip) <= 0.001 &&\n            Math.Abs(_size.HeightDip - size.HeightDip) <= 0.001;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''            if (visible)\n            {\n                QueueInitialization();\n            }\n            else\n            {''',
    '''            if (visible)\n            {\n                StopIdleReleaseTimer();\n                QueueInitialization();\n            }\n            else\n            {''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''                _initializationDeferralGeneration++;\n                Send(new { type = "commitRequested" });\n            }\n\n            // Deliver the public lifecycle event''',
    '''                _initializationDeferralGeneration++;\n                Send(new { type = "commitRequested" });\n                RestartIdleReleaseTimer();\n            }\n\n            // Deliver the public lifecycle event''')
# Timer helpers before SendStateChanged.
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''        public void SendStateChanged() => Send(new\n        {''',
    '''        private void RestartIdleReleaseTimer()\n        {\n            if (_disposed)\n            {\n                return;\n            }\n            _idleReleaseTimer ??= new DispatcherTimer(DispatcherPriority.Background)\n            {\n                Interval = IdleReleaseDelay\n            };\n            _idleReleaseTimer.Tick -= OnIdleReleaseTimerTick;\n            _idleReleaseTimer.Tick += OnIdleReleaseTimerTick;\n            _idleReleaseTimer.Stop();\n            _idleReleaseTimer.Start();\n        }\n\n        private void StopIdleReleaseTimer()\n        {\n            _idleReleaseTimer?.Stop();\n        }\n\n        private void OnIdleReleaseTimerTick(object? sender, EventArgs e)\n        {\n            StopIdleReleaseTimer();\n            if (!_disposed && !_visible)\n            {\n                _owner.ReleaseIdleMiniView(this);\n            }\n        }\n\n        public void SendStateChanged() => Send(new\n        {''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''            Send(new { type = "commitRequested" });\n            AdvancePresentationGeneration();''',
    '''            Send(new { type = "commitRequested" });\n            StopIdleReleaseTimer();\n            if (_idleReleaseTimer != null)\n            {\n                _idleReleaseTimer.Tick -= OnIdleReleaseTimerTick;\n                _idleReleaseTimer = null;\n            }\n            AdvancePresentationGeneration();''')

# -----------------------------------------------------------------------------
# 5. Commit is explicitly best-effort. saveState is the durability primitive.
# -----------------------------------------------------------------------------
replace_once(
    'src/WebPaperBodySession.cs',
    '''        // Web state persistence is immediate by contract. This message only asks a registered\n        // state provider to flush a final snapshot while the renderer is still alive.\n        Send(new { type = "commitRequested" });''',
    '''        // Best-effort lifecycle notification only. Persistent state must be saved at the\n        // moment it changes; Dispose does not wait for a JavaScript acknowledgement.\n        Send(new { type = "commitRequested" });''')
# Runtime and Mini get matching comments immediately before their best-effort send.
replace_once(
    'src/WebPaperRuntime.cs',
    '''        Send(new { type = "commitRequested" });\n        _documentReady = false;''',
    '''        // Best effort only; reliable durability is saveState-at-mutation time.\n        Send(new { type = "commitRequested" });\n        _documentReady = false;''')
replace_once(
    'src/WebPaperBodySession.Mini.cs',
    '''            Send(new { type = "commitRequested" });\n            StopIdleReleaseTimer();''',
    '''            // Best effort only; reliable durability is saveState-at-mutation time.\n            Send(new { type = "commitRequested" });\n            StopIdleReleaseTimer();''')

# -----------------------------------------------------------------------------
# 6. Documentation: host owns surfaces/resources, plugin owns the Web app/business
#    lifecycle. Explicitly document writer and transport boundaries.
# -----------------------------------------------------------------------------
v = read('ARCHITECTURE.md')
anchor = 'PaperTodo 不提供插件热重载入口。'
if 'PaperTodo 管 Web Surface，不管 Web App' not in v:
    block = '''\n### Web 生命周期边界\n\n**PaperTodo 管 Web Surface，不管 Web App。** 宿主负责 WebView 的创建/销毁、Body/Mini/后台 Runtime surface 是否存在、local origin 与 bridge、renderer 失败后的 surface 恢复以及粗粒度资源预算；插件自己负责 timer、网络连接、任务生命周期、业务重试、Body ↔ PaperRuntime 消息内容和业务状态结构。宿主不提供消息总线、exactly-once、业务事务或状态冲突合并。\n\n可见 Body/Mini 使用浏览器正常的后台调度策略；AppRuntime/PaperRuntime 使用独立的后台 runtime 环境。两个环境不承诺共享 localStorage/cookie 等浏览器存储，跨 surface 协作必须走宿主 bridge。PaperRuntime 冷启动只做有限并发，Web Mini 在离开预览一段时间后可被宿主回收并于下次使用时重建。\n\n声明 `paperRuntime` 后，**PaperRuntime 是该 Paper 插件持久 state 的唯一 writer**。Body/Mini 可以消费宿主广播的 state snapshot，并通过 `runtime.post(...)` 向 PaperRuntime 发送业务消息，但不能直接 `saveState()` 覆盖持久状态。反向 `body.post(...)` 同样只是薄的有序消息发送边界：目标尚未 ready 时宿主可短暂排队，无法接受或队列达到上限时调用明确失败；如何重试、去重或解释消息由插件自己决定。\n\n`commitRequested` 只是 best-effort 生命周期通知，Dispose 不等待 JavaScript ACK。需要可靠保存的业务状态必须在状态变化时立即调用 `saveState()`，不能依赖退出前最后一次 flush。\n\n'''
    v = v.replace(anchor, block + anchor)
write('ARCHITECTURE.md', v)

v = read('plugin-samples/README.md')
marker = '### 4.6 App runtime 生命周期'
if '持久 state 的唯一 writer' not in v:
    block = '''### 4.6 Web PaperRuntime 的 state 与消息\n\nWeb 插件声明 `backgroundUpdates` + `paperRuntime` 后，PaperRuntime 是这张 Paper **持久 state 的唯一 writer**。`paper-runtime.html` 在业务状态变化时直接调用 `papertodo.saveState(...)`；Body/Mini 只消费 `initialize` / `stateChanged` snapshot，需要改变业务状态时通过 `papertodo.runtime.post(message)` 把消息交给 PaperRuntime。Body/Mini 上的 `saveState` / `registerStateProvider` 不再承担持久状态写入。\n\n`papertodo.runtime.post(...)` 与 PaperRuntime 的 `papertodo.body.post(...)` 返回 Promise，只表示宿主是否接受这次发送。目标正在短暂初始化/reload 时宿主保持一个有界有序队列；目标不存在、失败或队列已满时 Promise 以 `runtime_unavailable` / `body_unavailable` 明确失败。PaperTodo 不理解消息的业务语义，也不提供自动 retry、ACK、exactly-once 或 durable message bus；这些属于插件自己的 Web App。\n\n`commitRequested` 是 best-effort 生命周期通知，不保证 Dispose 前完成。可靠持久化只依赖状态变化时主动 `saveState()`。可见 Body/Mini 与后台 AppRuntime/PaperRuntime 也不应依赖共享 localStorage/cookie；跨 surface 数据流使用 state snapshot 与 bridge 消息。\n\n'''
    v = v.replace(marker, block + marker)
write('plugin-samples/README.md', v)

print('phase2 patches applied')
