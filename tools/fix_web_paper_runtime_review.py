from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def write(path, text):
    (ROOT / path).write_text(text.replace("\r\n", "\n"), encoding="utf-8")


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected 1 match, got {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


# 1) PaperRuntime state authority must track its own successful save/version.
replace_once(
    "src/WebPaperRuntime.cs",
    """    private string _stateJson;\n    private readonly int _stateVersion;\n    private readonly int _targetStateVersion;\n""",
    """    private string _stateJson;\n    private int _stateVersion;\n    private readonly int _targetStateVersion;\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """    private ulong _documentNavigationId;\n    private bool _hasDocumentNavigation;\n""",
    """    private ulong _documentNavigationId;\n    private bool _hasDocumentNavigation;\n    private string? _activeDocumentToken;\n    private string? _departingDocumentToken;\n    private int _documentGeneration;\n""",
)

# 2) Give PaperRuntime the same document-ready/token rules as Body: ordinary posts wait for
# initialize; saveState may flush from the departing document with its old token.
replace_once(
    "src/WebPaperRuntime.cs",
    """              const pending = new Map();\n              const queuedRequests = [];\n              let sequence = 0;\n              let hostReady = false;\n              let stateProvider = null;\n              const post = (type, payload = null) => window.chrome.webview.postMessage({ type, payload });\n              const request = (method, params = {}) => {\n                const requestId = `p${++sequence}`;\n                const payload = { requestId, method: String(method ?? ''), params: params ?? {} };\n                return new Promise((resolve, reject) => {\n                  pending.set(requestId, { resolve, reject });\n                  if (hostReady) post('hostRequest', payload);\n                  else queuedRequests.push(payload);\n                });\n              };\n              const saveState = state => post('saveState', state ?? {});\n""",
    """              const pending = new Map();\n              let sequence = 0;\n              let stateProvider = null;\n              let documentToken = null;\n              let pendingState = null;\n              let hasPendingState = false;\n              let markHostReady;\n              const hostReady = new Promise(resolve => { markHostReady = resolve; });\n              const rawPost = (type, payload = null) => {\n                window.chrome.webview.postMessage({ type, payload, documentToken });\n              };\n              const post = (type, payload = null) => {\n                void hostReady.then(() => rawPost(type, payload));\n              };\n              const request = async (method, params = {}) => {\n                await hostReady;\n                const requestId = `p${++sequence}`;\n                const payload = { requestId, method: String(method ?? ''), params: params ?? {} };\n                return new Promise((resolve, reject) => {\n                  pending.set(requestId, { resolve, reject });\n                  rawPost('hostRequest', payload);\n                });\n              };\n              const saveState = state => {\n                const nextState = state ?? {};\n                if (documentToken) {\n                  rawPost('saveState', nextState);\n                  return;\n                }\n                pendingState = nextState;\n                hasPendingState = true;\n              };\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """                if (message?.type === 'initialize' && !hostReady) {\n                  hostReady = true;\n                  for (const payload of queuedRequests.splice(0)) post('hostRequest', payload);\n                }\n""",
    """                if (message?.type === 'initialize') {\n                  documentToken = typeof message.documentToken === 'string'\n                    ? message.documentToken\n                    : null;\n                  if (documentToken && hasPendingState) {\n                    rawPost('saveState', pendingState);\n                  }\n                  pendingState = null;\n                  hasPendingState = false;\n                  markHostReady();\n                }\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        _documentNavigationId = e.NavigationId;\n        _hasDocumentNavigation = true;\n        _documentReady = false;\n        ClearHostSubscriptions();\n""",
    """        _documentNavigationId = e.NavigationId;\n        _hasDocumentNavigation = true;\n        if (!string.IsNullOrWhiteSpace(_activeDocumentToken))\n        {\n            _departingDocumentToken = _activeDocumentToken;\n            _activeDocumentToken = null;\n        }\n        _documentGeneration++;\n        _documentReady = false;\n        ClearHostSubscriptions();\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """            _reloadRecoveryPending = false;\n            _documentReady = false;\n            FailStartupOrRestart(\n""",
    """            _reloadRecoveryPending = false;\n            _documentReady = false;\n            _activeDocumentToken = null;\n            _departingDocumentToken = null;\n            FailStartupOrRestart(\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        _reloadRecoveryPending = false;\n        _documentReady = true;\n        SendInitialize();\n""",
    """        _reloadRecoveryPending = false;\n        _documentReady = true;\n        _activeDocumentToken = Guid.NewGuid().ToString("N");\n        _departingDocumentToken = null;\n        SendInitialize();\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """            type = "initialize",\n            surface = "paperRuntime",\n            paperId = _paperId,\n""",
    """            type = "initialize",\n            surface = "paperRuntime",\n            documentToken = _activeDocumentToken,\n            paperId = _paperId,\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||\n            !_documentReady ||\n            !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Source, _expectedOrigin))\n""",
    """        if (!ReferenceEquals(sender, _webView.CoreWebView2) ||\n            !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Source, _expectedOrigin))\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """            var payload = root.TryGetProperty("payload", out var payloadValue)\n                ? payloadValue\n                : default;\n            switch (typeValue.GetString())\n            {\n                case "saveState":\n                    _saveState(payload.ValueKind == JsonValueKind.Undefined\n                        ? "{}"\n                        : payload.GetRawText());\n                    break;\n""",
    """            var type = typeValue.GetString() ?? string.Empty;\n            if (!CanAcceptDocumentMessage(type, _documentReady))\n            {\n                return;\n            }\n            var documentToken = root.TryGetProperty("documentToken", out var tokenValue) &&\n                                tokenValue.ValueKind == JsonValueKind.String\n                ? tokenValue.GetString()\n                : null;\n            if (!HasDocumentAuthority(type, documentToken))\n            {\n                return;\n            }\n\n            var payload = root.TryGetProperty("payload", out var payloadValue)\n                ? payloadValue\n                : default;\n            switch (type)\n            {\n                case "saveState":\n                    SaveStateFromRuntime(payload);\n                    break;\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """    private void HandleHostRequest(JsonElement payload)\n    {\n        var requestId = WebPluginRuntimeInfrastructure.RequiredString(payload, "requestId");\n        try\n""",
    """    private static bool CanAcceptDocumentMessage(string type, bool documentReady) =>\n        string.Equals(type, "saveState", StringComparison.Ordinal) || documentReady;\n\n    private bool HasDocumentAuthority(string type, string? documentToken)\n    {\n        if (string.IsNullOrWhiteSpace(documentToken))\n        {\n            return false;\n        }\n        if (_documentReady &&\n            string.Equals(documentToken, _activeDocumentToken, StringComparison.Ordinal))\n        {\n            return true;\n        }\n        return string.Equals(type, "saveState", StringComparison.Ordinal) &&\n            !_documentReady &&\n            string.Equals(documentToken, _departingDocumentToken, StringComparison.Ordinal);\n    }\n\n    private void SaveStateFromRuntime(JsonElement payload)\n    {\n        var normalized = PaperBodyPluginDataStore.NormalizeStateJson(\n            payload.ValueKind == JsonValueKind.Undefined ? "{}" : payload.GetRawText());\n        _stateJson = normalized;\n        _stateVersion = _targetStateVersion;\n        _saveState(normalized);\n    }\n\n    private void HandleHostRequest(JsonElement payload)\n    {\n        var requestId = WebPluginRuntimeInfrastructure.RequiredString(payload, "requestId");\n        var documentGeneration = _documentGeneration;\n        try\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """            Send(new { type = "hostResponse", requestId, ok = true, result });\n        }\n        catch (PaperTodoPluginException ex)\n        {\n            Send(new\n""",
    """            if (documentGeneration != _documentGeneration) return;\n            Send(new { type = "hostResponse", requestId, ok = true, result });\n        }\n        catch (PaperTodoPluginException ex)\n        {\n            if (documentGeneration != _documentGeneration) return;\n            Send(new\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        catch\n        {\n            Send(new\n            {\n                type = "hostResponse",\n""",
    """        catch\n        {\n            if (documentGeneration != _documentGeneration) return;\n            Send(new\n            {\n                type = "hostResponse",\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        var subscriptionId = WebPluginRuntimeInfrastructure.RequiredString(\n            payload,\n            "subscriptionId");\n        try\n""",
    """        var subscriptionId = WebPluginRuntimeInfrastructure.RequiredString(\n            payload,\n            "subscriptionId");\n        var documentGeneration = _documentGeneration;\n        try\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """                value =>\n                {\n                    var eventJson = JsonSerializer.SerializeToElement(\n""",
    """                value =>\n                {\n                    if (documentGeneration != _documentGeneration) return;\n                    var eventJson = JsonSerializer.SerializeToElement(\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        _stateJson = normalized;\n        Send(new\n        {\n            type = "stateChanged",\n""",
    """        _stateJson = normalized;\n        _stateVersion = _targetStateVersion;\n        Send(new\n        {\n            type = "stateChanged",\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        _hasDocumentNavigation = false;\n        _reloadRecoveryPending = false;\n        _documentReady = false;\n        ClearHostSubscriptions();\n""",
    """        _hasDocumentNavigation = false;\n        _reloadRecoveryPending = false;\n        _documentReady = false;\n        _activeDocumentToken = null;\n        _departingDocumentToken = null;\n        _documentGeneration++;\n        ClearHostSubscriptions();\n""",
)
replace_once(
    "src/WebPaperRuntime.cs",
    """        _documentReady = false;\n        _disposed = true;\n        _startupReady.TrySetCanceled();\n""",
    """        _documentReady = false;\n        _activeDocumentToken = null;\n        _departingDocumentToken = null;\n        _documentGeneration++;\n        _disposed = true;\n        _startupReady.TrySetCanceled();\n""",
)

# 3) Runtime -> Body messages get the same bounded pre-ready protection as the other direction.
replace_once(
    "src/WebPaperBodySession.cs",
    """    private readonly Action<JsonElement>? _postRuntimeMessage;\n    private readonly bool _paperRuntimeOwnsPresentation;\n""",
    """    private readonly Action<JsonElement>? _postRuntimeMessage;\n    private readonly bool _paperRuntimeOwnsPresentation;\n    private readonly Queue<JsonElement> _pendingRuntimeMessages = new();\n""",
)
replace_once(
    "src/WebPaperBodySession.cs",
    """            _activeDocumentToken = Guid.NewGuid().ToString("N");\n            _departingDocumentToken = null;\n            SendInitialize();\n""",
    """            _activeDocumentToken = Guid.NewGuid().ToString("N");\n            _departingDocumentToken = null;\n            SendInitialize();\n            FlushPendingRuntimeMessages();\n""",
)
replace_once(
    "src/WebPaperBodySession.cs",
    """    internal void ReceiveRuntimeMessage(JsonElement payload)\n    {\n        Send(new\n        {\n            type = "runtimeMessage",\n            payload\n        });\n    }\n""",
    """    internal void ReceiveRuntimeMessage(JsonElement payload)\n    {\n        if (_disposed || _webViewFailed)\n        {\n            return;\n        }\n        if (!_documentReady || !_pluginDocumentReady)\n        {\n            while (_pendingRuntimeMessages.Count >= 32)\n            {\n                _pendingRuntimeMessages.Dequeue();\n            }\n            _pendingRuntimeMessages.Enqueue(payload.Clone());\n            return;\n        }\n        SendRuntimeMessage(payload);\n    }\n\n    private void FlushPendingRuntimeMessages()\n    {\n        while (_documentReady && _pluginDocumentReady && _pendingRuntimeMessages.Count > 0)\n        {\n            SendRuntimeMessage(_pendingRuntimeMessages.Dequeue());\n        }\n    }\n\n    private void SendRuntimeMessage(JsonElement payload)\n    {\n        Send(new\n        {\n            type = "runtimeMessage",\n            payload\n        });\n    }\n""",
)
replace_once(
    "src/WebPaperBodySession.cs",
    """        _documentGeneration++;\n        ClearHostSubscriptions();\n        _lifetime.Cancel();\n""",
    """        _documentGeneration++;\n        ClearHostSubscriptions();\n        _pendingRuntimeMessages.Clear();\n        _lifetime.Cancel();\n""",
)

# 4) Start persistent per-paper runtime ownership before restoring any visible Body surface.
replace_once(
    "src/AppController.cs",
    """        var rescuedPapers = EnsurePapersOnScreen();\n\n        // Respect persisted IsVisible: hide closes the paper surface, delete removes it.\n""",
    """        var rescuedPapers = EnsurePapersOnScreen();\n\n        // Establish entity-paper background ownership before any visible Web body can initialize\n        // and emit Body -> PaperRuntime messages during restore.\n        EnableWebPaperRuntimeReconciliation();\n\n        // Respect persisted IsVisible: hide closes the paper surface, delete removes it.\n""",
)

# 5) Do not accumulate unrelated transient runtime crashes forever.
replace_once(
    "src/AppController.WebPaperRuntime.cs",
    """        public int FailureCount { get; set; }\n        public int RetryGeneration { get; set; }\n        public bool HasHeaderValue { get; set; }\n""",
    """        public int FailureCount { get; set; }\n        public int RetryGeneration { get; set; }\n        public DateTimeOffset RunningSinceUtc { get; set; }\n        public bool HasHeaderValue { get; set; }\n""",
)
replace_once(
    "src/AppController.WebPaperRuntime.cs",
    """            slot.State = WebPaperRuntimeState.Running;\n            ApplyWebPaperRuntimePresentationToWindowForSlot(slot);\n""",
    """            slot.State = WebPaperRuntimeState.Running;\n            slot.RunningSinceUtc = DateTimeOffset.UtcNow;\n            ApplyWebPaperRuntimePresentationToWindowForSlot(slot);\n""",
)
replace_once(
    "src/AppController.WebPaperRuntime.cs",
    """                if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))\n                {\n                    return;\n                }\n                HandleWebPaperRuntimeFailure(\n""",
    """                if (!IsCurrentWebPaperRuntimeSlot(slot, runtimeId))\n                {\n                    return;\n                }\n                if (slot.RunningSinceUtc != default &&\n                    DateTimeOffset.UtcNow - slot.RunningSinceUtc >=\n                    PluginAppRuntimeStableFailureResetAfter)\n                {\n                    slot.FailureCount = 0;\n                }\n                HandleWebPaperRuntimeFailure(\n""",
)
replace_once(
    "src/AppController.WebPaperRuntime.cs",
    """        DisposeWebPaperRuntimeLease(slot);\n        slot.FailureCount++;\n""",
    """        DisposeWebPaperRuntimeLease(slot);\n        slot.RunningSinceUtc = default;\n        slot.FailureCount++;\n""",
)
replace_once(
    "src/AppController.WebPaperRuntime.cs",
    """            slot.RetryGeneration++;\n            slot.FailureCount = 0;\n            StartWebPaperRuntimeSlot(slot);\n""",
    """            slot.RetryGeneration++;\n            slot.FailureCount = 0;\n            slot.RunningSinceUtc = default;\n            StartWebPaperRuntimeSlot(slot);\n""",
)

# 6) Make the public plugin manual match the newly-enforced manifest contract.
replace_once(
    "plugin-samples/README.md",
    """| `runtime` | 可选，仅 Web `appRuntime`；省略时默认 `entry` 同目录 `runtime.html` |\n| `capabilities` | 可选：`textZoom`、`noteLinks`；2.0 还支持生命周期能力 `appRuntime` |\n""",
    """| `runtime` | 可选，仅 Web `appRuntime`；省略时默认 `entry` 同目录 `runtime.html` |\n| `paperRuntime` | Web 声明 `backgroundUpdates` 时必填；每张 Paper 独立的后台运行入口，必须位于 Web `entry` 静态目录内 |\n| `capabilities` | 可选：`textZoom`、`noteLinks`；2.0 还支持生命周期能力 `appRuntime` |\n""",
)
replace_once(
    "plugin-samples/README.md",
    """只有当插件在完整正文没有呈现时仍需要保持业务运行，才声明它。不要把它当成普通能力标记；长期计时、后台状态同步等场景才需要。\n""",
    """只有当插件在完整正文没有呈现时仍需要保持业务运行，才声明它。不要把它当成普通能力标记；长期计时、后台状态同步等场景才需要。\n\nWeb 插件声明 `backgroundUpdates` 时必须同时声明 `paperRuntime`，例如 `\"paperRuntime\": \"web/paper-runtime.html\"`。宿主会为每张真实 Paper 创建一份独立后台 WebView；它与可见 `entry` Body 分离，折叠、隐藏、Body reload/失败以及当前没有 `PaperWindow` 都不会结束它。Native `backgroundUpdates` 仍沿用 per-paper Body Session 运行语义，不需要 `paperRuntime`。\n""",
)
replace_once(
    "plugin-samples/README.md",
    """   │  ├─ mini.html\n   │  └─ runtime.html       # appRuntime 默认入口；manifest runtime 可改名\n""",
    """   │  ├─ mini.html\n   │  ├─ runtime.html       # appRuntime 默认入口；manifest runtime 可改名\n   │  └─ paper-runtime.html # Web backgroundUpdates 的 per-Paper 后台入口\n""",
)
replace_once(
    "plugin-samples/README.md",
    """    ├─ plugin app runtime (可选，provider 级)\n    │   ├─ Workspace\n    │   ├─ Settings（只读、按需读取当前值）\n    │   ├─ Global Top Bar\n    │   └─ Global Shortcuts\n    └─ paper body session[paperId] (0..N live)\n        ├─ Paper / Body\n        ├─ Paper Top Bar\n        ├─ Workspace\n        └─ Mini / capsule capability\n""",
    """    ├─ plugin app runtime (可选，provider 级)\n    │   ├─ Workspace\n    │   ├─ Settings（只读、按需读取当前值）\n    │   ├─ Global Top Bar\n    │   └─ Global Shortcuts\n    ├─ paper runtime[paperId] (Web backgroundUpdates 时每 Paper 1 个)\n    │   └─ 独立后台 JS / state / timer / network；不拥有可见 UI\n    └─ paper body session[paperId] (0..N live)\n        ├─ Paper / Body\n        ├─ Paper Top Bar\n        ├─ Workspace\n        └─ Mini / capsule capability\n""",
)

# 7) Extend structural protocol checks so future edits cannot silently remove these boundaries.
replace_once(
    "tests/PaperTodo.ProtocolPolicyChecks/Program.cs",
    """        Assert(\n            runtime.GetField("_pendingBodyMessages", BindingFlags.Instance | BindingFlags.NonPublic) != null,\n            "Body-to-paper-runtime startup messages need a bounded pre-ready queue.");\n        Assert(manifest.GetProperty("PaperRuntime") != null,\n""",
    """        Assert(\n            runtime.GetField("_pendingBodyMessages", BindingFlags.Instance | BindingFlags.NonPublic) != null,\n            "Body-to-paper-runtime startup messages need a bounded pre-ready queue.");\n        Assert(\n            runtime.GetField("_activeDocumentToken", BindingFlags.Instance | BindingFlags.NonPublic) != null &&\n            runtime.GetField("_departingDocumentToken", BindingFlags.Instance | BindingFlags.NonPublic) != null &&\n            runtime.GetField("_documentGeneration", BindingFlags.Instance | BindingFlags.NonPublic) != null,\n            "Web paper runtime must reject stale documents across reload/recovery.");\n        var stateVersion = runtime.GetField("_stateVersion", BindingFlags.Instance | BindingFlags.NonPublic);\n        Assert(stateVersion != null && !stateVersion.IsInitOnly,\n            "Web paper runtime must advance its in-memory state version after a successful save.");\n        var body = RequireType(host, "PaperTodo.WebPaperBodySession");\n        Assert(\n            body.GetField("_pendingRuntimeMessages", BindingFlags.Instance | BindingFlags.NonPublic) != null,\n            "Paper-runtime-to-body startup messages need a bounded pre-ready queue.");\n        Assert(manifest.GetProperty("PaperRuntime") != null,\n""",
)

print("Web paper runtime review fixes applied")
