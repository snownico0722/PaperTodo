from pathlib import Path
import re


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace(path, old, new, count=1):
    text = read(path)
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f'{path}: expected {count}, found {actual}: {old[:120]!r}')
    write(path, text.replace(old, new, count))


# ---------------------------------------------------------------------------
# Protocol policy checks: the old per-Paper Runtime is now forbidden rather than required.
# ---------------------------------------------------------------------------
path = 'tests/PaperTodo.ProtocolPolicyChecks/Program.cs'
text = read(path)
text = text.replace('            CheckWebPaperRuntimeAuthority(host);',
                    '            CheckUnifiedPluginRuntime(host, abstractions);')
old_method = re.search(
    r'''    private static void CheckWebPaperRuntimeAuthority\(Assembly host\)\n    \{.*?\n    \}\n\n    private static void CheckWebBodyNavigationIdentity''',
    text,
    flags=re.S)
if not old_method:
    raise SystemExit('policy checks: old WebPaperRuntime method not found')
new_method = '''    private static void CheckUnifiedPluginRuntime(Assembly host, Assembly abstractions)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        var webRuntime = RequireType(host, "PaperTodo.WebPluginAppRuntime");
        var manifest = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var context = RequireType(abstractions, "PaperTodo.Plugin.PaperAppRuntimeContext");
        var papers = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimePapers");
        var runtimeState = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeState");
        var bodyContext = RequireType(abstractions, "PaperTodo.Plugin.PaperBodyContext");
        var runtimeClient = RequireType(abstractions, "PaperTodo.Plugin.IPaperPluginRuntimeClient");

        Assert(
            controller.GetField("_pluginAppRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Provider Runtime must retain one provider-keyed lifecycle slot dictionary.");
        Assert(
            controller.GetField("_webPaperRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "Host-managed per-Paper Web Runtime slots must not return.");
        Assert(host.GetType("PaperTodo.WebPaperRuntime", throwOnError: false) == null,
            "WebPaperRuntime must not return; Web uses the one provider Runtime.");
        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "paperRuntime manifest fields must not return.");
        Assert(context.GetProperty("Papers")?.PropertyType == papers &&
               context.GetProperty("State")?.PropertyType == runtimeState,
            "The provider Runtime must own logical Paper routing and provider-scoped backend state.");
        Assert(bodyContext.GetProperty("Runtime")?.PropertyType == runtimeClient,
            "Body/Mini frontends must address the one provider Runtime through a thin client.");
        Assert(papers.GetMethod("List") != null &&
               papers.GetMethod("SetHeaderText") != null &&
               papers.GetMethod("SetCapsulePresentation") != null &&
               papers.GetMethod("PostToBody") != null,
            "Provider Runtime Paper routing is incomplete.");
        Assert(webRuntime.GetField("_papers", BindingFlags.Instance | BindingFlags.NonPublic) != null &&
               webRuntime.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "The Web provider Runtime must use the same logical Paper/state contract as Native.");
    }

    private static void CheckWebBodyNavigationIdentity'''
text = text[:old_method.start()] + new_method + text[old_method.end():]
text = text.replace(
'''        Assert(manifest.GetProperty("PaperRuntime") != null &&
               manifest.GetProperty("PaperRuntimePath") != null,
            "Web per-paper runtime manifest fields are missing.");''',
'''        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "Retired Web per-Paper runtime manifest fields must stay deleted.");''')
text = text.replace(
'''        Assert(settings.GetProperty("Json")?.PropertyType == typeof(string),
            "App runtime settings must expose the current normalized JSON.");''',
'''        Assert(settings.GetProperty("Json")?.PropertyType == typeof(string) &&
               settings.GetMethod("Subscribe") != null,
            "Provider Runtime settings must expose current JSON and change subscription.");''')
text = text.replace(
'''        Assert(context.GetProperty("Settings")?.PropertyType == settings,
            "PaperAppRuntimeContext.Settings was not found.");''',
'''        Assert(context.GetProperty("Settings")?.PropertyType == settings &&
               context.GetProperty("State") != null &&
               context.GetProperty("Papers") != null,
            "PaperAppRuntimeContext must expose Settings, backend State and logical Papers.");''')
write(path, text)

# ---------------------------------------------------------------------------
# Current architecture: one Runtime backend, N logical Paper instances, Body/Mini frontends.
# ---------------------------------------------------------------------------
path = 'ARCHITECTURE.md'
text = read(path)
text = text.replace('        ├─ plugin app runtime[providerId] → Global Top Bar',
                    '        ├─ plugin Runtime[providerId] → logical Paper instances / Global Top Bar')
text = text.replace(
'| 插件状态 | `PaperBodyPluginDataStore` | provider settings 与 per-paper plugin state 的独立保存/恢复 |',
'| 插件状态 | `PaperBodyPluginDataStore` | provider settings、provider Runtime state 与 per-paper frontend state 的独立保存/恢复 |')
text = text.replace(
'| plugin app runtime | `AppController.PluginAppRuntime` | `startupPaper` 处理后按最终实体插件 paper 集合 reconcile；provider 0→1 时启动、1→0 时释放，持有 Global Top Bar 与 app-runtime Workspace facade |',
'| plugin Runtime | `AppController.PluginAppRuntime` | 每 provider 最多一个后端 Runtime；0→1 张实体插件 Paper 时启动、1→0 时释放，按 `paperId` 管理逻辑实例、后端 state、长期 presentation、Global Top Bar/Shortcuts 与 Workspace |')

start = text.index('### 3.3 辅助进程与插件 app runtime')
end = text.index('## 4. 状态与持久化架构', start)
new_runtime_section = '''### 3.3 辅助进程与插件 Runtime

Web 插件使用 WebView2；Native 插件可以自行创建线程、Worker、子进程或第三方运行环境。这些实现细节属于插件内部，不成为 PaperTodo 的第二套 `AppState` authority。

插件协议当前只接受 **2.0**。需要在可见 Body/Mini 不存在时仍持续工作的插件声明 `appRuntime`：PaperTodo 对每个 provider **最多只创建一个 Runtime 后端**。`startupPaper` 先处理真实 Paper；之后只要最终至少有一张 `Note` Paper 的 `BodyProviderId` 指向该 provider，Runtime 就存在。0→1 启动，1→0 释放；隐藏、折叠、Body 重建、Mini 回收和当前没有 `PaperWindow` 都不改变 Runtime lifetime。

一张 Paper 不再对应一个后台 Runtime。多开插件仍然只有一个 provider Runtime，Runtime 通过 `PaperId` 管理 N 个逻辑实例；需要额外线程、Web Worker、子进程或隔离域时，由插件在自己的 Runtime 内部创建和回收，宿主不提供第二种“每 Paper 后台”协议。

Native 与 Web 使用同一生命周期语义：Native Runtime 是一个长期 C# 对象；Web Runtime 是一个隐藏 WebView/JS 页面。实现载体不同，但 `Settings`、provider `State`、`Papers`、Workspace、Global Top Bar/Shortcuts 和失败重启边界保持一致。

Runtime 的 provider `State` 与 Body/Mini 的 per-paper frontend state 分开保存。Runtime 可以在一份后端 JSON 中按 `paperId` 保存自己的业务实例；Body/Mini 的 `StateJson` 只保存前端/纸片 UI 状态。这样后台和前端不会争抢同一个持久化 writer。

当 provider 声明 Runtime 时，**长期 Paper presentation 由 Runtime 唯一负责**：标题、Header、胶囊通过 `Papers` 按 `paperId` 发布。Body/Mini 负责可见 UI，并通过 `Runtime.Post(...)` 发送用户操作；Runtime 可以通过 `Papers.PostToBody(...)` 向当前存在的 Body 前端推送消息。宿主只保证薄路由和明确失败，不提供业务消息总线、ACK、exactly-once、自动 retry 或状态冲突合并。

### Web 生命周期边界

**PaperTodo 管 Web Surface，不管 Web App。** 宿主负责单个 provider Runtime WebView、Body/Mini WebView 的创建/销毁、local origin 与 bridge、renderer 失败后的 surface 恢复以及粗粒度资源预算；插件负责 timer、网络连接、业务任务、内部并发和重试。

Body/Mini 是 Paper 的前端 surface，可以被隐藏、重建或回收；Web Mini 在离开预览一段时间后可由宿主释放并在下次使用时重建。Web Runtime 是 provider 的唯一后台 surface，不随某张 Paper 的 UI 生命周期创建第二份后台 WebView。

可见前端与后台 Runtime 不依赖共享 localStorage/cookie 作为业务协议；跨 surface 协作走 Runtime/Papers bridge。`commitRequested` 仍只是前端 best-effort 生命周期通知，可靠状态应在业务变化时及时保存。

PaperTodo 不提供插件热重载入口。插件 manifest、DLL、Web body/mini/runtime 等文件的安装、删除或修改统一在下次启动 PaperTodo 时重新发现并生效。

'''
text = text[:start] + new_runtime_section + text[end:]
text = text.replace(
'| 插件 settings / per-paper state | `plugins/data/*.json` | `PaperBodyPluginDataStore` | 插件生命周期与核心状态解耦，独立迁移和恢复 |',
'| 插件 settings / Runtime state / per-paper frontend state | `plugins/data/*.json` | `PaperBodyPluginDataStore` | 插件后端、前端与核心状态解耦，独立迁移和恢复 |')
write(path, text)

# ---------------------------------------------------------------------------
# Plugin developer manual: delete the dual-backend choice from the mental model.
# ---------------------------------------------------------------------------
path = 'plugin-samples/README.md'
text = read(path)
text = text.replace('可选 app runtime 入口', '可选 Runtime 后台入口')
text = text.replace('可选 `IPaperAppRuntimeProvider`', '可选 `IPaperAppRuntimeProvider`（单 provider Runtime）')
text = text.replace('`plugins/data/`：PaperTodo 代管的插件 settings 与 per-paper state；',
                    '`plugins/data/`：PaperTodo 代管的插件 settings、provider Runtime state 与 per-paper frontend state；')
text = text.replace('   │  ├─ runtime.html       # appRuntime 默认入口；manifest runtime 可改名\n   │  └─ paper-runtime.html # Web backgroundUpdates 的 per-Paper 后台入口',
                    '   │  └─ runtime.html       # provider Runtime 默认入口；manifest runtime 可改名')
text = text.replace('| `runtime` | 可选，仅 Web `appRuntime`；省略时默认 `entry` 同目录 `runtime.html` |\n| `paperRuntime` | Web 声明 `backgroundUpdates` 时必填；每张 Paper 独立的后台运行入口，必须位于 Web `entry` 静态目录内 |',
                    '| `runtime` | 可选，仅 Web `appRuntime`；一个 provider 最多一个 Runtime，省略时默认 `entry` 同目录 `runtime.html` |')
text = text.replace('| `requires` | 可选；当前支持 `backgroundUpdates` |\n', '')
text = text.replace('未知 `requires` 或 `permissions` 会拒绝加载。`appRuntime` 是 provider 生命周期声明，不会变成 `PaperBodyCapabilities` 的 body flag。',
                    '未知 `permissions` 会拒绝加载。`appRuntime` 是 provider 的单后台生命周期声明，不会变成 `PaperBodyCapabilities` 的 body flag。')

# Remove the obsolete requires section entirely.
text, n = re.subn(
    r'''### 3\.2 `requires`\n.*?(?=### 3\.3 `startupPaper`)''',
    '',
    text,
    count=1,
    flags=re.S)
if n != 1:
    raise SystemExit('README: requires section not found')

# Replace runtime model through the state section boundary with one coherent model.
start = text.index('### 3.4 `appRuntime`（2.0）')
end = text.index('## 5. 状态、设置与 `.runtime`', start)
new_model = '''### 3.4 `appRuntime`（2.0）

插件需要脱离 Body/Mini UI 生命周期持续运行时声明：

```json
"capabilities": ["appRuntime"]
```

规则只有一套：

- `startupPaper` 先按设置创建/恢复真实插件 Paper；
- provider 有至少一张真实 Paper 时启动 **一个** Runtime，最后一张离开 provider 时 Dispose；
- 隐藏、折叠、Body 重建、Mini 回收以及当前没有 `PaperWindow` 都不影响 Runtime；
- 不存在“每 Paper 后台”协议；多开仍然共用这一个 Runtime，通过 `PaperId` 区分逻辑实例；
- 插件如果确实需要多个 Worker、线程、子进程或隔离域，自己在 Runtime 内管理；
- Native 声明后实现 `IPaperAppRuntimeProvider`；Web 使用 manifest `runtime` 指定后台入口，省略时默认 `entry` 同目录 `runtime.html`；
- 修改插件文件后统一重启 PaperTodo 生效。

## 4. 插件运行模型

Native 与 Web 使用同一概念：

```text
Plugin provider
├─ Runtime ×0/1                 # 唯一后端
│  ├─ Settings
│  ├─ provider State
│  ├─ Papers[paperId]           # N 个逻辑实例
│  ├─ Workspace
│  └─ Global Top Bar / Shortcuts
└─ Paper ×N
   ├─ Body                      # 完整前端
   └─ Mini                      # 轻量前端
```

Runtime 是后端，Paper 是逻辑实例，Body/Mini 是同一 Paper 的两种前端。Body 与 Mini 之间不需要直接互相拥有状态；需要改变长期业务时向 Runtime 发消息。

### 4.1 `PaperBodyContext.Paper`

未声明 Runtime 的简单插件可以直接通过 `Paper` 设置标题/Header/胶囊。声明 `appRuntime` 后，长期 presentation 由 Runtime 的 `context.Papers` 唯一发布，Body/Mini 对这些长期 presentation 的写入不再成为 authority。

### 4.2 `PaperBodyContext.Body`

属于完整正文 surface：`Controls`、`Theme`、`SetInputClaims(...)`、`MarkDirty()`、`OpenExternal(...)`、`RequestReload()`。Body 是前端，折叠/隐藏后不应承担后台业务生命周期。

### 4.3 `PaperBodyContext.Runtime`

Body/Mini 使用同一条薄命令通道向 provider Runtime 发业务消息：

```csharp
context.Runtime.Post(message);
```

Web 对应：

```js
await papertodo.runtime.post(message);
```

调用只表示当前 Runtime 是否接受了消息。PaperTodo 不提供业务 ACK、持久消息总线、自动 retry 或 exactly-once。

### 4.4 `PaperBodyContext.TopBar` / `Workspace`

Paper Top Bar 属于当前 Body session；Global Top Bar 属于 provider Runtime。Workspace 是两边共用的受控 Paper/Todo/Note API，按 manifest `permissions` 授权。

### 4.5 Body/Mini 生命周期

Body/Mini 是前端，可以反复创建、隐藏、重建和销毁。Native `IPaperBodySession` 的 `OnVisibilityChanged` / `OnPresentationChanged` 只描述前端 surface，不再是后台保活协议。需要在 UI 不存在时继续工作的逻辑放进 Runtime。

### 4.6 Provider Runtime

Native：

```csharp
public sealed class MyPlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Body(context);

    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) =>
        new Runtime(context);
}
```

`PaperAppRuntimeContext` 提供：

- `Settings`：当前全局设置 + 变更订阅；
- `State`：provider Runtime 自己的一份持久 JSON；
- `Papers`：列出逻辑 Paper，按 `paperId` 设置长期 presentation、向 Body 发消息、接收 Paper 增删/前端消息；
- `Workspace`；
- `GlobalTopBar` / `GlobalShortcuts`。

Web Runtime 获得对应的 `papertodo.settings`、`papertodo.state`、`papertodo.papers`、`papertodo.workspace`。一个 provider 只创建一个隐藏 Runtime WebView。

Runtime state 与 Body/Mini 的 per-paper frontend state 是不同数据：Runtime 可以自己在 provider state 中维护 `instances[paperId]`；Body 的 `saveState` 只保存该 Paper 前端状态，不与 Runtime 抢 writer。

'''
text = text[:start] + new_model + text[end:]
# State section: describe the third stored member.
text = text.replace('- `settings`：该插件所有纸片共享；\n- `papers`：按 Paper ID 保存独立 state；',
                    '- `settings`：该插件所有纸片共享；\n- `runtime`：provider Runtime 的一份后端 state；\n- `papers`：按 Paper ID 保存 Body/Mini 前端 state；')
write(path, text)

# ---------------------------------------------------------------------------
# Official Web clock docs.
# ---------------------------------------------------------------------------
path = 'plugin-samples/PaperTodo.Plugin.OfficialClockWeb/README.md'
text = read(path)
text = text.replace('- Web Mini 默认把点击和拖拽交给 PaperTodo；网页需要自己处理指针的局部区域使用 `data-papertodo-interactive` 显式声明，本示例的“暂停 / 继续”按钮即为示例；\n',
                    '- Web Mini 默认把点击和拖拽交给 PaperTodo；网页需要自己处理指针的局部区域使用 `data-papertodo-interactive` 显式声明；\n')
text = text.replace('- `paper.setHeaderText` 与 `paper.setCapsulePresentation` 分别同步纸片顶栏和标准胶囊 presentation，胶囊按当前标题和日进度组件自动适配宽度；',
                    '- provider Runtime 通过 `papertodo.papers` 按 `paperId` 同步纸片顶栏和标准胶囊 presentation，胶囊按当前标题和日进度组件自动适配宽度；')
text = re.sub(
    r'''## Paper Runtime\n.*\Z''',
    '''## Plugin Runtime

时钟声明 `appRuntime`，整个 provider 只运行一个 `web/runtime.html` 后台。Runtime 收到当前逻辑 Paper 列表后维护一个定时器，并通过 `papertodo.papers.setHeaderText(...)` / `setCapsulePresentation(...)` 按 `paperId` 发布长期 presentation。

`web/index.html` 与 `web/mini.html` 都只是前端：它们可以重建或回收，不决定后台计时生命周期。即使未来允许多开时钟，也仍然只有一个 provider Runtime；不同 Paper 只是 Runtime 内按 `paperId` 区分的逻辑实例。需要额外 Worker/隔离时由插件自己创建，不由 PaperTodo 为每张 Paper 再生成隐藏 WebView。
''',
    text,
    count=1,
    flags=re.S)
write(path, text)

# ---------------------------------------------------------------------------
# Record the superseding architecture decision rather than rewriting history.
# ---------------------------------------------------------------------------
path = 'DECISIONS.md'
text = read(path)
index_row = '| D-023 | Lightweight Prewarm 保留一次性首用预热 | Accepted | Edge performance |'
if index_row not in text:
    raise SystemExit('DECISIONS: D-023 index row not found')
text = text.replace(index_row, index_row + '\n| D-024 | 插件后台统一为 provider 单 Runtime | Accepted | 插件 / 生命周期 |', 1)
if '## D-024 — 插件后台统一为 provider 单 Runtime' not in text:
    text += '''

---

## D-024 — 插件后台统一为 provider 单 Runtime

**Status:** Accepted

### Context

协议 2.0 一度同时存在 provider `appRuntime` 与 Web `paperRuntime`：前者一插件一个，后者一张 Paper 一个隐藏 WebView。随着后台状态、消息、失败重试和 presentation ownership 都开始在两层重复，插件作者需要先选择“哪个后台”，宿主也需要维护两套生命周期。

### Decision

PaperTodo 只提供 **一个 provider Runtime 后端**。一个插件无论有一张还是多张 Paper，宿主最多创建一个 Runtime；多张 Paper 是 Runtime 中以 `paperId` 区分的逻辑实例。Body 和 Mini 是前端 surface，不承担后台保活职责。

Web 与 Native 使用相同语义：Web Runtime 是一个隐藏 WebView/JS 页面，Native Runtime 是一个长期 C# 对象。插件如果需要多个 Worker、线程、子进程、浏览器实例或隔离域，由插件在自己的 Runtime 内创建和管理，宿主不提供第二种 per-Paper backend 协议。

Runtime 使用 provider-scoped state；Body/Mini 继续使用 per-paper frontend state。声明 Runtime 后，长期 Paper 标题/Header/胶囊由 Runtime 按 `paperId` 唯一发布，避免后台与前端双写。

### Why

- 常见插件后台天然是一插件一个；多实例通常是同一后台管理多个业务对象。
- 旧 Web per-Paper Runtime 并没有提供独立浏览器 Profile/Cookie 隔离，却为每张 Paper 额外创建 WebView、重试状态和消息桥。
- 插件数据文件本来就是“一 provider + 多 Paper”的结构，单 Runtime 与持久化模型更一致。
- 删除宿主的 per-Paper backend 后，Web/Native 的概念和生命周期一致，第三方插件不再需要理解两套后台。

### Rejected / Do not reintroduce

- 不恢复 manifest `paperRuntime` 或宿主管理的 `WebPaperRuntime[paperId]`。
- 不用 Body/Mini 的可见性作为长期业务后台生命周期。
- 不为“可能需要隔离”预建第二套后台协议；真实插件需要隔离时自己管理内部 Worker/进程。
- 不让 Runtime 与 Body/Mini 同时成为同一长期 presentation 的 authority。

### Consequences

一个 provider Runtime 故障会暂时影响该 provider 的所有逻辑 Paper，这是用更小、更明确的宿主模型换取的故障域扩大。需要更细隔离的插件自行在 Runtime 内拆 Worker/进程。PaperTodo 仍负责 Runtime 的 provider 生命周期、薄路由和粗粒度恢复，不升级成通用消息总线或进程编排器。

### Evidence

- `src/AppController.PluginAppRuntime.cs` / `src/PaperAppRuntimePapersApi.cs`。
- `src/WebPluginAppRuntime.cs`。
- `PaperTodo.Plugin.Abstractions/PluginRuntimeContracts.cs`。
- 删除的 `src/WebPaperRuntime.cs` / `src/AppController.WebPaperRuntime.cs`。
'''
write(path, text)

print('phase4 docs and policy checks complete')
