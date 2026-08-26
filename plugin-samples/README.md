# PaperTodo 插件开发

本文是 **当前 PaperTodo 插件开发手册**。只描述现在可用的插件合同、运行边界、构建方式和示例，不记录协议演进历史。

新插件使用：

```json
"apiVersion": "2.0"
```

当前宿主只接受 `2.0` 插件；旧 `1.8` manifest 不再兼容加载。

插件公开类型以 [`../PaperTodo.Plugin.Abstractions/`](../PaperTodo.Plugin.Abstractions/) 为编译期合同；宿主实际校验和运行行为以当前代码为准。需要理解 PaperTodo 内部 ownership 时再看 [`../ARCHITECTURE.md`](../ARCHITECTURE.md)，插件作者不需要先阅读主程序架构才能开始开发。

> **信任边界：PaperTodo 不为插件提供安全沙箱。** Native 与 Web 插件都应视为可信代码，只安装可信来源的插件。

## 1. 快速开始

PaperTodo 支持两种插件：

| 类型 | 适合 | 入口 | 构建 |
| --- | --- | --- | --- |
| Web | HTML/CSS/JS、本地状态面板、轻量交互 | 本地 `entry` 页面；可选 Runtime 后台入口（manifest `runtime`，省略时默认 `entry` 同目录 `runtime.html`） | 不需要编译 |
| Native | .NET/WPF、复杂本地 UI、原生依赖、自定义 WPF capsule/mini | 实现 `IPaperBodyPlugin` 的 DLL；可选 `IPaperAppRuntimeProvider`（单 provider Runtime） | `dotnet publish`，推荐使用仓库脚本 |

两种插件最终都安装到：

```text
plugins/<插件 ID>/
```

目录名必须与 `plugin.json` 的 `id` 一致。

### 1.1 最小 Web 插件

目录：

```text
plugins/com.example.hello/
├─ plugin.json
└─ web/
   └─ index.html
```

`plugin.json`：

```json
{
  "kind": "web",
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "web/index.html"
}
```

页面在 PaperTodo 的本地顶层 origin 中运行时会获得 `window.papertodo`：

```html
<!doctype html>
<meta charset="utf-8">
<button id="hello">Hello</button>
<script>
  papertodo.paper.setTitle('Hello');
  papertodo.paper.setHeaderText('Hello 插件');
  papertodo.paper.setCapsulePresentation({
    preferredWidth: 0,
    plainText: 'Hello',
    components: [{ kind: 'text', text: 'Hello', fill: true }]
  });

  document.querySelector('#hello').addEventListener('click', () => {
    papertodo.saveState({ clickedAt: Date.now() });
  });
</script>
```

开发时把 `plugin.json` 和 `web/` 复制到对应 `plugins/<id>/`。**PaperTodo 不提供插件 Reload；安装、删除或修改插件文件后统一重启 PaperTodo 生效。**

### 1.2 最小 Native 插件

Native 项目使用 .NET 10 + WPF，并引用：

```text
PaperTodo.Plugin.Abstractions/PaperTodo.Plugin.Abstractions.csproj
```

示例项目配置：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

入口程序集必须包含且只包含一个公开、非抽象、带 public 无参构造函数的 `IPaperBodyPlugin` 实现：

```csharp
using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

public sealed class HelloPlugin : IPaperBodyPlugin
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new Session(context);

    private sealed class Session : IPaperBodySession
    {
        public Session(PaperBodyContext context)
        {
            View = new TextBlock
            {
                Text = "Hello PaperTodo",
                Margin = new Thickness(16)
            };
            context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "Hello",
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = "Hello",
                        Fill = true
                    }
                ]
            });
        }

        public FrameworkElement View { get; }
        public void Dispose() { }
    }
}
```

Native `plugin.json`：

```json
{
  "kind": "native",
  "id": "com.example.hello-native",
  "name": "Hello Native",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "HelloPlugin.dll"
}
```

`plugin.json` 是 Native 插件元数据的唯一来源；入口 DLL 不再重复声明 ID、名称、版本、协议版本、状态版本、能力或后台需求，只实现插件行为。

### 1.3 构建并安装 Native 插件

仓库提供统一脚本：

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

脚本会：

- 执行 Release / `win-x64` / framework-dependent publish；
- 把同目录 `plugin.json` 放入最终包；
- 移除 PDB、XML、WebView2 loader 以及宿主已经提供的共享程序集；
- 保留目标插件现有 `.runtime/`；
- 安装到 `plugins/<插件 ID>/`。

替换 Native 插件前必须退出 PaperTodo。已经载入 CLR 的 Native 插件不会在当前进程中安全热替换，修改或删除后应重启 PaperTodo。

## 2. 目录与部署边界

仓库中的目录职责：

- `plugin-samples/`：插件源码、源码侧 `plugin.json`、示例和构建脚本；
- `plugins/`：已经构建、可由 PaperTodo 直接加载的最终插件；
- `plugins/data/`：PaperTodo 代管的插件 settings、provider Runtime state 与 per-paper frontend state；
- `plugins/<id>/.runtime/`：插件自己管理的缓存或独立长期数据。

PaperTodo 的本地 publish 和 GitHub Release 都不捆绑插件，插件独立分发。

典型目录：

```text
plugins/
├─ data/
│  └─ com.example.weather.json
└─ com.example.weather/
   ├─ plugin.json
   ├─ web/
   │  ├─ index.html
   │  ├─ mini.html
   │  └─ runtime.html       # provider Runtime 默认入口；manifest runtime 可改名
   ├─ WeatherPlugin.dll
   ├─ WeatherPlugin.deps.json
   ├─ 插件私有依赖 / 原生库
   └─ .runtime/
```

`data` 是宿主保留 ID。插件 ID 必须由 3～120 个 ASCII 字母、数字、`.`、`_`、`-` 组成。

Native 最终目录只保留运行所需内容。不要分发无必要的 PDB/XML，也不要重复携带宿主共享的 `PaperTodo.Plugin.Abstractions`、Windows SDK / WinRT 或 WebView2 共享程序集。

## 3. `plugin.json`

当前 manifest 支持：

| 字段 | 说明 |
| --- | --- |
| `kind` | `web` 或 `native` |
| `id` | 插件唯一 ID；目录名必须一致 |
| `name` | 显示名称；为空时回退到 ID |
| `description` | 插件说明 |
| `version` | 插件版本，必须能解析为 `Version` |
| `apiVersion` | 必须为 `"2.0"` |
| `stateVersion` | per-paper state 版本，至少为 1 |
| `maxPaperInstances` | 可选；同一 Provider 最多允许存在的真实 Paper 数。省略默认 `1`，`0` 表示不限制；隐藏/折叠 Paper 仍计数 |
| `entry` | Web 主页面或 Native 入口 DLL，必须位于插件目录内 |
| `miniEntry` | 可选，仅 Web；专属 Edge Mini 页面 |
| `miniSize` | 可选，仅与 `miniEntry` 一起使用；Mini 首选尺寸 |
| `miniMaxSize` | 可选，2.0；插件承诺的 Mini 最大容量，用于宿主 bounded capacity 规划 |
| `runtime` | 可选，仅 Web `appRuntime`；一个 provider 最多一个 Runtime，省略时默认 `entry` 同目录 `runtime.html` |
| `capabilities` | 可选：`textZoom`、`noteLinks`；2.0 还支持生命周期能力 `appRuntime` |
| `permissions` | 可选；Paper/Todo/Note Workspace 权限 |
| `advancedSettings` | 可选，默认 `false`；声明 `true` 后启用独立完整设置页 |
| `primarySettings` | 可选；仅 `advancedSettings: true` 时有效，插件卡片直接显示前 1～3 个设置，省略时默认 3 |
| `settingCategories` | 可选；仅 `advancedSettings: true` 时有效，声明完整设置页分类及可选 `left` / `right` 列位置 |
| `settings` | 可选；由宿主绘制和保存的全局设置；高级模式下设置项可写 `category` |
| `startupPaper` | 可选；按用户设置自动创建/恢复一张插件纸片 |

`maxPaperInstances` 是 Paper/provider 级产品约束，对 Native 与 Web 一致生效；插件更新后如果已有实例超过新上限，宿主不会删除现有 Paper，只会阻止继续新增。

未知 `permissions` 会拒绝加载。`appRuntime` 是 provider 的单后台生命周期声明，不会变成 `PaperBodyCapabilities` 的 body flag。

### 3.1 Web `entry` / `miniEntry`

`entry` 和 `miniEntry` 都必须留在插件目录中；`miniEntry` 还必须位于 Web `entry` 所在静态目录内。

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "version": "1.0.0",
  "apiVersion": "2.0",
  "stateVersion": 1,
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 },
  "miniMaxSize": { "width": 360, "height": 240 }
}
```

没有 `miniEntry` 时不能声明 `miniSize`；Web 插件声明 `miniMaxSize` 时也必须有 `miniEntry`。`miniSize` 不能超过 `miniMaxSize`。

### 3.3 `startupPaper`

插件可以让一个 boolean setting 控制“启动后自动创建或恢复一张插件纸片”：

```json
{
  "startupPaper": {
    "enabledSetting": "autoStart",
    "instanceKey": "main",
    "presentation": "capsule",
    "title": "天气"
  },
  "settings": [
    {
      "id": "autoStart",
      "type": "boolean",
      "name": "启动后自动显示",
      "default": false
    }
  ]
}
```

约束：

- `enabledSetting` 必须引用同一 manifest 中的 boolean setting；
- `instanceKey` 为 1～80 个 ASCII 字母、数字、`.`、`_`、`-`；
- `presentation` 只能是 `capsule` 或 `expanded`；
- `title` 最长 120 个字符；
- 创建时机、去重和恢复由宿主管理；插件只声明意图；
- 如果用户已经把原自动创建纸片改造成其他 provider/type，宿主不会强行接管或偷偷再创建副本。

### 3.4 `appRuntime`（2.0）

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

## 5. 状态、设置与 `.runtime`

### 5.1 Per-paper state

每个插件的宿主管理状态位于：

```text
plugins/data/<插件 ID>.json
```

其中：

- `settings`：该插件所有纸片共享；
- `runtime`：provider Runtime 的一份后端 state；
- `papers`：按 Paper ID 保存 Body/Mini 前端 state；
- 每张纸片 state 的保存上限是 **1 MiB UTF-8 JSON**。

Native 使用：

```csharp
context.StateJson
context.StateVersion
context.TargetStateVersion
context.SaveStateJson(json)
```

状态变化后应立即提交给宿主，不要只依赖 session `Commit()`。如果 Native 已保存版本低于当前 `StateVersion`，通过 `IPaperBodyPlugin.MigrateState(...)` 转换后再创建 session；保存数据比插件声明版本更新时，宿主不会猜测降级。

Web 使用：

```js
papertodo.saveState(nextState);
papertodo.registerStateProvider(() => currentState);
```

`initialize` 同时提供 `state`、`stateVersion`、`targetStateVersion`。Web 插件自己负责把旧 shape 归一化为当前 shape，并在真实迁移后保存；不要因为解析失败直接用空对象覆盖仍可能有价值的状态。

### 5.2 恢复行为

宿主读取正常数据文件失败时：

- 保留原文件；
- 当前进程从空插件状态继续；
- 后续写入稳定的 `<插件 ID>.json.recovered`；
- `.recovered` 存在时后续优先使用它。

插件数据故障不会让 PaperTodo 核心 `data.json` 失效。

### 5.3 全局 settings

宿主支持：`boolean`、`string`、`number`、`select`、`shortcut`。设置仍只有一份存储和读写协议，下面两种只是宿主展示方式。

默认不声明 `advancedSettings`（或为 `false`）时，行为保持原样：最多三个 `quick: true` 设置直接显示在插件卡片上，其余设置通过“更多设置”在**当前卡片内**展开/收起。没有 `quick` 时不会自动猜主要设置。

声明 `"advancedSettings": true` 后才启用新的高级设置模式：插件卡片自动直接显示 `settings` 前 3 项，超过后“更多设置”打开独立完整设置页；可用 `primarySettings: 1..3` 覆盖直接显示数量。完整页的设置项可写 `category`，同名分类自动归组；顶层 `settingCategories` 可以给分类指定 `column: "left"` 或 `"right"`，不写列就交给宿主自动安排。宿主先尝试单列，纵向放不下且确实有多个可分配块时才自动分成左右两列；同一分类不会被拆开。

```json
{
  "advancedSettings": true,
  "primarySettings": 2,
  "settingCategories": [
    { "name": "常规", "column": "left" },
    { "name": "网络", "column": "right" },
    { "name": "调试" }
  ],
  "settings": [
    { "id": "enabled", "type": "boolean", "name": "启用", "category": "常规" },
    { "id": "mode", "type": "select", "name": "模式", "category": "常规", "options": [
      { "value": "auto", "name": "自动" },
      { "value": "manual", "name": "手动" }
    ] },
    { "id": "timeout", "type": "number", "name": "超时", "category": "网络" },
    { "id": "debug", "type": "boolean", "name": "调试日志", "category": "调试" }
  ]
}
```

Native paper session 从 `SettingsJson` 读取初始设置，并通过 `OnSettingsChanged` 接收更新。Web body 从 `initialize.settings` 读取，并接收 `settingsChanged`。

Runtime 不借用 paper-session settings 生命周期：Native 随时读取 `PaperAppRuntimeContext.Settings.Json`；Web Runtime 的 `initialize.settings` 提供启动快照，需要最新值时调用 `await papertodo.settings.get()`。当前不为 Runtime 复制一套 `settingsChanged` 事件状态机。

### 5.4 `.runtime/`

`.runtime/` 不属于宿主管理的 per-paper state 协议。它适合：

- WebView2 Profile；
- 可重建缓存；
- 大型本地索引；
- 必须独立于单张 paper 生命周期的插件私有数据。

插件自己负责 `.runtime/` 的格式版本、原子写入、损坏恢复和容量控制。普通单纸片 UI/业务状态不要同时写进 `.runtime/` 和 `plugins/data/`，否则会产生两份 authoritative state。

## 6. Workspace 权限与数据 API

manifest 可声明：

```text
papers.read
papers.observe
papers.create
papers.delete

todos.read
todos.observe
todos.append
todos.update
todos.delete

notes.read
notes.observe
notes.append
notes.replace
```

Native paper session 使用 `PaperBodyContext.Workspace`；Native Runtime 使用 `PaperAppRuntimeContext.Workspace`；Web body/mini/Runtime 都通过各自 bridge 的 `papertodo.workspace.request(method, params)`。

Web 数据 method：

```text
papers.list
papers.get
papers.create
papers.delete

todos.list
todos.append
todos.update
todos.setReminder
todos.delete

notes.get
notes.write
```

几个容易遗漏的权限组合：

- 创建带正文的 Note：除了 `papers.create` 还需要 `notes.append`；
- 创建/追加带完成状态、提醒或 `linkedPaperId` 的 Todo：还需要 `todos.update`；
- `todos.setReminder` 使用 `todos.update`；
- `notes.write` 的 append/fill-blank 使用 `notes.append`，replace 使用 `notes.replace`；
- paper session 插件不能删除承载当前 active session 的 paper；Runtime 没有 host paper，因此不受这条单纸片自删除限制。

Observe 权限独立于 Read 权限。没有对应 read 权限时，事件仍可按 observe 权限投递，但敏感字段会被宿主裁剪。

Native paper session：

```csharp
using var subscription = context.Workspace.Subscribe(
    new PaperTodoEventFilter
    {
        Kinds = new HashSet<PaperTodoEventKind>
        {
            PaperTodoEventKind.TodoChanged
        },
        ExcludeOwnOperations = true
    },
    evt => { /* refresh model */ });
```

Web body：

```js
const dispose = papertodo.onHostEvent(
  ['todo.changed'],
  event => console.log(event),
  { excludeOwnOperations: true }
);
```

可订阅：`paper.created`、`paper.changed`、`paper.deleted`、`todo.created`、`todo.changed`、`todo.deleted`、`note.changed`。会话失效或销毁后订阅自动失效；插件自己也应及时 unsubscribe 不再需要的监听。

### 6.1 正文读写边界

Top Bar 不提供另一套 `GetBodyText/SetBodyText`。需要读写目标纸片时继续使用 Workspace：

- Markdown Note：`notes.get` + `notes.write`，受 `notes.read` / `notes.append` / `notes.replace` 权限约束；
- Todo：使用结构化 `todos.*` API，不把 Todo 伪装成 Markdown 字符串；
- 自定义插件正文：正文数据仍由对应 provider 的 state/capability 拥有，宿主不会假装所有正文都是文本。

插件 Workspace 与 MCP 共用 `PaperCommandService` 业务边界，因此保存、失败回滚、UI reconcile 和事件顺序不因为入口不同而复制第二套实现。

## 7. Top Bar 扩展（2.0）

**PaperTodo 始终拥有顶栏 WPF tree、按钮尺寸、位置、主题、Hover、DPI 和 responsive layout；插件只贡献 action descriptor。** 不接受插件直接塞 `FrameworkElement`、Button、WebView 或任意顶栏控件。

Top Bar 有两个明确 owner：

- **Paper**：`PaperBodyContext.TopBar` / body session；只显示在承载当前 session 的插件纸片，每 session 最多 4 个；
- **Global**：`PaperAppRuntimeContext.GlobalTopBar` / provider Runtime；显示在所有 PaperTodo 纸片，每个 provider Runtime 最多声明 256 个 Global action。Global runtime 要求该 provider 当前至少有一张实体插件 paper，但不要求任何 paper 可见、展开或拥有 live body session。

Global action 使用 `Priority` 排序，数值越大越靠前；同优先级先按 provider runtime 注册顺序，再按插件声明顺序，保证稳定。**PaperTodo 自己的宿主 action 不进入这个数值空间，拥有不可被插件覆盖的最高优先级。** 窗口宽度不足时插件 contribution 先让位，不会因为插件声明很多 Global action 而先把宿主 action 挤掉。

启动时先处理 `startupPaper`。它可能先创建/恢复实体插件 paper；随后宿主按最终实体 paper 集合启动对应 Global runtime。运行中第一张实体 paper 出现会启动，最后一张被删除或切走 provider 会 Dispose；删除/隐藏/折叠非最后一张不会撤销 Global action。

Global 点击包含：`TargetPaperId`、`TargetPaperType`、`TargetBodyProviderId`。`TargetBodyProviderId` 只对 Note 有意义；Todo 等非 Note 目标返回空字符串。插件据此通过 Runtime Workspace 读取或修改目标 Markdown/Todo；Top Bar 自己不拥有业务数据接口。

### 7.1 图标

支持两类宿主绘制图标：

1. `Character`：1～8 个 UTF-16 字符，不允许控制字符；
2. `SvgPath`：单份 SVG/WPF Path Data，最长 4096 字符。

不接受完整 `<svg>`、`filter`、`image`、脚本或任意 SVG DOM。

SVG 有两种绘制模式：

- `Fill`：宿主用当前按钮前景色填充；
- `Stroke`：宿主用当前按钮前景色描边，`strokeWidth` 允许 0.1～4.0。

按钮外框、点击区域、Hover、Disabled、主题色和响应式收起始终由 PaperTodo 控制。

### 7.2 Native Paper action

```csharp
context.TopBar.SetActionHandler(invocation =>
{
    // 当前 paper 的按钮回调。
});

context.TopBar.SetPaperActions(
    [
        new PaperTopBarAction
        {
            Id = "refresh",
            Icon = PaperTopBarIcon.Character("↻"),
            ToolTip = "刷新"
        }
    ],
    PaperHostTopBarActions.NewNotePaper);
```

自己的插件纸片只允许请求隐藏：

```text
NewTodoPaper
NewNotePaper
```

这两个值表示宿主的“创建 Todo / 创建 Note”动作，不要求未来永远对应两枚独立物理按钮。关闭、置顶、标题拖动、窗口生命周期等宿主生命线不能被插件删除。

### 7.3 Native Global action

Global 不从 `PaperBodyContext` 注册。声明 `appRuntime` 后：

```csharp
public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context)
{
    context.GlobalTopBar.SetActionHandler(invocation =>
    {
        var target = context.Workspace.GetPaper(invocation.TargetPaperId);
        // 根据目标 paper 执行操作。
    });

    context.GlobalTopBar.SetActions([
        new PaperTopBarAction
        {
            Id = "inspect-current",
            Icon = PaperTopBarIcon.SvgPath(
                "M3,3 L13,3 13,13 3,13 Z M6,8 L10,8",
                PaperTopBarSvgRenderMode.Stroke,
                1.5),
            ToolTip = "读取当前纸片",
            Priority = 100
        }
    ]);

    return new Runtime();
}
```

`SetPaperActions(...)` / `GlobalTopBar.SetActions(...)` 都是 replace 语义；传空数组得到空 action set。Paper session Dispose 自动撤掉 Paper contribution；provider Runtime Dispose 自动撤掉 Global contribution。

### 7.4 Web Paper action

Web body 只注册 Paper scope：

```js
await papertodo.request('topbar.paper.set', {
  actions: [
    {
      id: 'refresh',
      icon: { kind: 'character', value: '↻' },
      toolTip: '刷新'
    }
  ],
  hiddenHostActions: ['newNotePaper']
});
```

当前 ready body document 才能注册；贡献绑定 document generation。页面导航、renderer failure、body WebView 被替换或 session Dispose 时旧 Paper contribution 自动撤销。Body 调用 `topbar.global.set` 会得到 `global_topbar_app_runtime_only`。

### 7.5 Web Global Runtime

manifest：

```json
{
  "apiVersion": "2.0",
  "entry": "web/index.html",
  "runtime": "web/background.html",
  "capabilities": ["appRuntime"]
}
```

`runtime` 可省略；省略时默认使用 `entry` 同目录的 `runtime.html`。显式路径仍以插件目录为基准，并且必须位于 Web `entry` 的静态目录内。

当 provider 至少有一张实体插件 paper 时，该 app 页面获得：

```js
papertodo.surface;                    // 'app'
papertodo.workspace.request(method, params);
papertodo.settings.get();
papertodo.globalTopBar.setActions(actions);
papertodo.onEvent(listener);
```

示例：

```js
const settings = await papertodo.settings.get();

await papertodo.globalTopBar.setActions([
  {
    id: 'inspect-current',
    icon: {
      kind: 'svgPath',
      value: 'M3,3 L13,3 13,13 3,13 Z M6,8 L10,8',
      renderMode: 'stroke',
      strokeWidth: 1.5
    },
    toolTip: '读取当前纸片',
    priority: 100
  }
]);

papertodo.onEvent(async message => {
  if (message.type !== 'topBarActionInvoked') return;
  const paper = await papertodo.workspace.request('papers.get', {
    paperId: message.action.targetPaperId
  });
});
```

Runtime 是独立 app surface，不获得 `paper`、`body`、`mini` presentation API。runtime document 导航、renderer failure、最后一张实体插件 paper 消失或 Runtime Dispose 都会撤掉 Global action。Web Mini 也不能注册 Global Top Bar。

完整可运行示例见 `PaperTodo.Plugin.TopBarWeb`。

## 8. 胶囊 presentation

### 8.1 宿主绘制的标准胶囊

插件可以提交 `PaperCapsulePresentation`。外壳、关闭区、Hover、拖动、贴边、跨屏、DPI 和输入始终由 PaperTodo 管理。

标准组件最多三个，按声明顺序排列：

- `text`
- `glyph`
- `statusDot`
- `progressRing`
- `progressBar`

组件支持 `fill`、固定 `width`、`tone` 和自定义 `color`。

宽度：

- Native：`PreferredWidth = PaperCapsulePresentation.AutomaticWidth`
- Web：`preferredWidth: 0`

表示让宿主按内容测量自然宽度。正数表示插件希望的完整内容段宽度（DIP），宿主仍会限制到合法范围。

`plainText` 应始终提供有意义的纯文字表示，用于只接受文本的临时 surface 和安全回退。

### 8.2 Native 自定义 WPF 胶囊

Native session 可实现 `IPaperCapsuleViewProvider`，由 `CreateCapsuleView(PaperCapsuleViewContext)` 分别为 `Regular`、`Docked` 创建 WPF 内容 View。

规则：

- 两种 surface 必须返回不同的 WPF 对象；
- View 必须 fresh、未挂载、pure-WPF；
- 不接受 `Window`、`HwndHost`、`WindowsFormsHost`、WebView2 或已有 parent 的控件；
- 自定义胶囊内容本身不拥有鼠标输入；
- 宿主仍拥有外壳、关闭区、点击、右键、拖动、Hover、贴边和 DPI；
- 创建失败或返回 `null` 时回退到标准胶囊；
- 自动宽度先由标准 presentation 解析，再把最终槽尺寸传给自定义 View；
- 同一 session/geometry 下宿主缓存 View，实时状态应原地刷新，不要靠持续重建 View。

Web 插件不提供 WPF 自定义胶囊，只使用宿主绘制的标准 presentation。

## 9. Edge Mini

Edge Mini 是快速浏览 surface。**插件贡献内容，PaperTodo 始终拥有 Edge 窗口、队列 placement、卡片外框、尺寸归一化和输入路由。** 插件不要创建自己的 Edge HWND，也不要复制宿主 queue/geometry 算法。

当前路径：

1. Native dedicated mini：`IPaperMiniViewProvider`；
2. Web dedicated mini：manifest `miniEntry`；
3. 没有 dedicated mini 时，宿主根据 custom/standard capsule 或 `plainText` 构造只读 preview。

### 9.1 Mini 尺寸

`PaperMiniViewSize` / `miniSize` 描述**包含宿主外框和关闭区的完整卡片尺寸**，单位 DIP。

协议没有固定的 120×90 下限或 480×420 上限。插件声明的 `width` / `height` 必须是**正且有限的数值**；宿主只按当前显示器可用工作区约束最终尺寸。

`miniMaxSize` 是 2.0 的可选**容量上界声明**：插件承诺该 Mini 在当前协议下不会请求超过它的宽高，宿主可据此准备 bounded host，而不是无理由预留一个很大的 WebView/HWND/承载面。它不是插件取得窗口尺寸 authority；最终尺寸仍由 PaperTodo 的显示器工作区和宿主规则限制。Web 插件声明 `miniMaxSize` 时必须有 `miniEntry`，且 `miniSize` 不能大于它。Native 也可以在 manifest 中声明同一上界；省略时宿主使用兼容容量策略。

默认首选尺寸：

```text
320 × 220 DIP
```

内置 Todo / Markdown 可以继续使用自己的 renderer envelope 和视觉默认尺寸；这些值不是插件协议限制。Native `PreferredMiniViewSize` 可以随会话状态变化；宿主在没有活动 queue-proxy 事务时可以直接调整 bounded host，如果尺寸变化正好发生在 queue translation 中，增长可能短暂延后到该事务结束。**不推荐在 Mini 已显示时高频改变尺寸，也不要把 Preferred Size 当作动画参数**，因为尺寸变化可能触发宿主/native 重新布局并造成短暂卡顿。

### 9.2 Native dedicated mini

实现 `IPaperMiniViewProvider`。dedicated mini 与正文可以共享同一业务 model，但必须是不同 WPF 控件实例。

规则：

- `CreateMiniView` 必须返回 fresh / unparented / pure-WPF tree；
- 不接受 `Window`、`HwndHost`、`WindowsFormsHost`、WebView2；
- 返回 `null` 或创建失败不会让正文 session 失败；
- `OnMiniViewVisibilityChanged(false)` 从收起开始发送；可暂停刷新和输入，但保留最后绘制内容完成离场；
- Edge host 不取得键盘焦点，mini 不应依赖文本输入。

标准 WPF Button、选择器、滚动条、Thumb、Hyperlink 等可以取得 pointer input。其他自定义元素可声明：

```csharp
PaperMiniViewInteraction.SetConsumesPointer(element, true);
```

### 9.3 Web dedicated mini

Web manifest：

```json
{
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 },
  "miniMaxSize": { "width": 360, "height": 240 }
}
```

`miniEntry` 使用独立 WebView2，应保持本地、轻量，不要再次加载完整远程应用。

publication 流程：透明内容占位 → 延后 cold WebView2 初始化 → 当前 document `initialize` → 页面首轮真实布局后 `papertodo.mini.ready()` → 当前 generation challenge → `CompositionTarget.Rendering` publication boundary → generation/visibility 仍匹配才发布 Web surface。

因此不要假设 `mini.ready()` 一调用就同步可见，也不要依赖旧胶囊替 Web 页面占位。

Web Mini 的 pointer 默认属于 PaperTodo。局部控件确实需要网页自己处理点击/按下/拖动时声明：

```html
<button type="button" data-papertodo-interactive>暂停</button>
```

宿主把这些元素的当前 DOM 矩形镜像到 WPF 输入层；未标记区域继续用于打开完整 paper、拖动 Edge Mini 等宿主交互。不要把整个页面根节点无差别标记为 interactive。

正文与 mini 获得同一个宿主管理 state/settings。任一 surface `saveState` 后，另一侧收到 `stateChanged`；接收方不要原样再次 `saveState`，避免回声。

Web mini 不取得键盘焦点，也**不拥有 Top Bar 注册权**。


## 10. Web 插件

### 10.1 本地 origin 与 bridge

Web `entry` 所在目录是本地静态根，建议固定为 `web/`，避免把 `.runtime/` 暴露进页面资源映射。

插件自己的本地顶层页面运行在：

```text
https://<plugin-id>.papertodo.local/
```

只有该插件的本地 **top-level document** 获得 `window.papertodo`。远程页面、iframe 或其他 origin 不获得宿主 bridge。

PaperTodo 把 Web 插件视为可信内容；同源 frame/popup 和 permission 保持 WebView2 默认行为，外部顶层导航及外部新窗口请求交给系统默认程序。普通 HTTP/HTTPS 下载优先交给系统默认浏览器；`blob:`、`data:` 等 session-local download 保留 WebView2 默认行为。

### 10.2 Body bridge

正文页可用：

```js
papertodo.surface;                    // 'body'
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.setInputClaims(['escapeKey', 'contextMenu']);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.workspace.request(method, params);
papertodo.request(method, params);           // Paper Top Bar 使用 root transport
papertodo.onHostEvent(types, listener, options);
papertodo.onEvent(listener);
```

宿主会发送：

```text
initialize
stateChanged
settingsChanged
activated
deactivated
visibilityChanged
presentationChanged
themeChanged
typographyChanged
dpiChanged
commitRequested
cancelInteractions
hostResponse
hostEvent
hostSubscriptionError
topBarActionInvoked
```

`initialize` 包含当前 surface、paper/provider ID、API/state 版本、state、settings、permissions、theme、runtime visibility 和 presentation visibility。

### 10.3 Mini bridge

`miniEntry` 页可用：

```js
papertodo.surface;                    // 'mini'
papertodo.mini.ready();
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.workspace.request(method, params);
papertodo.onEvent(listener);
```

Mini 没有正文的 `setInputClaims`，也不能注册 Top Bar。键盘焦点始终不属于 Edge Mini；pointer 默认归宿主，只有 `data-papertodo-interactive` 局部区域交给网页。Mini 的 host-request 路由只接受当前列出的 Workspace 数据方法，不按方法名前缀自动继承未来宿主能力。

### 10.4 Plugin runtime bridge

声明 `appRuntime` 的 Web 插件在 provider 至少有一张实体插件 paper 时创建独立 app surface。manifest 的 `runtime` 可自定义入口；省略时默认 `entry` 同目录 `runtime.html`：

```js
papertodo.surface;                    // 'app'
papertodo.workspace.request(method, params);
papertodo.settings.get();
papertodo.globalTopBar.setActions(actions);
papertodo.onEvent(listener);
```

Runtime 的 `initialize` 包含当前 `settings` 快照；需要读取最新宿主管理设置时使用 `await papertodo.settings.get()`。它没有 `paper`、`body`、`mini`、`saveState` 等 paper-session API，也不复制 body 的 `settingsChanged` / Web `onHostEvent` bridge。

### 10.5 状态写入

每次真实 paper-session state mutation 后尽快 `saveState`。`registerStateProvider` 只是让宿主在 `commitRequested`、页面隐藏/卸载等边界尽量 flush 当前状态，不应被当作唯一 durability 机制。

## 11. Native 插件

Native 插件是 fully trusted / unsandboxed .NET/WPF 代码，与 PaperTodo 当前用户权限一致。

关键规则：

- `IPaperBodyPlugin` 是 factory，不保存某一张 paper 的 session state；
- 每个 paper body session 使用新的 plugin object / `IPaperBodySession`；
- 没有 `appRuntime` 时，manifest-only discovery 不会仅因启动而加载 Native DLL；
- 声明 `appRuntime` 时，只有 provider 当前至少有一张实体插件 paper 才会创建 provider runtime；
- Runtime 与 paper session 是不同对象/lifetime，不能把某张具体 paper session 当作 Global runtime authority；
- entry assembly 必须只有一个有效 `IPaperBodyPlugin` 实现；
- 插件文件变化/删除统一重启 PaperTodo 生效；
- 私有依赖和 native library 放在插件自包含目录；
- 不重复携带宿主共享程序集；
- timer、task、subscription、Top Bar contribution、外部资源都必须跟随各自 session/runtime 生命周期结束。

需要宿主统一视觉的 select 可使用 `PaperBodyContext.Body.Controls`，不要复制 PaperTodo 内部 popup/theme/DPI 细节。

## 12. 示例项目怎么选

| 示例 | 重点 |
| --- | --- |
| `PaperTodo.Plugin.TopBarWeb` | **Protocol 2.0 Top Bar 专项示例**：body Paper action + Web Runtime Global action、字符/Stroke SVG、目标 Paper context、Workspace 复用 |
| `PaperTodo.Plugin.SampleClock` | Native 主示例：settings、background updates、标准 capsule、自定义 WPF capsule、dedicated WPF mini |
| `PaperTodo.Plugin.OfficialClockWeb` | Web 主示例：body/mini 双页面、`miniEntry`、state/settings 同步、startup paper、background updates |
| `PaperTodo.Plugin.FocusTimer` | Native 有状态交互：正文与 dedicated mini 共享计时 model，mini 内直接开始/暂停/继续 |
| `PaperTodo.Plugin.ReviewArchive` | Workspace 数据读取/observe、插件 state 与长期数据的组合使用 |
| `PaperTodo.Plugin.CloudGenshin` | 正文含 WebView2/native child 时：完整远程应用留正文，Edge Mini 使用独立 pure-WPF 状态面板 |

开发新插件时优先从与目标最接近的示例复制最小结构，不要一次合并所有示例能力。

## 13. 常见错误

### Manifest / Runtime

- 新插件仍以 `apiVersion: "1.8"` 为目标，导致无法使用 2.0 Top Bar / Runtime；
- 插件目录名和 `id` 不一致；
- `id` 使用非法字符或保留 ID `data`；
- Web 声明 `appRuntime`，但默认 `runtime.html` 不存在，或显式 `runtime` 路径不存在/跑出 Web `entry` 静态目录；
- Native 声明 `appRuntime` 却没有实现 `IPaperAppRuntimeProvider`；
- 以为只安装插件、零实体插件 paper 时也会启动 Runtime；
- 把 `appRuntime` 当成 `startupPaper`；前者不负责创建 paper，后者才负责自启动实体 paper；
- 修改插件文件后期待当前进程自动重新扫描/热替换；当前规则是重启 PaperTodo；
- `miniSize` 没有对应 `miniEntry`；
- `miniSize` 超过 `miniMaxSize`，或 Web 声明 `miniMaxSize` 却没有 `miniEntry`；
- Web `miniEntry` 跑出 `entry` 静态目录；
- Native manifest 与 DLL 的 id/version/API/state/runtime requirements 不一致；
- `quick: true` 超过三个；
- `startupPaper.enabledSetting` 没有指向 boolean setting；
- 声明未知 `requires` / `permissions`。

### Top Bar

- 把 Top Bar 当 Workspace 数据 API；
- 给 PaperTodo 传 `FrameworkElement` / Button / 完整 SVG，而不是 action descriptor；
- action ID 重复、超过 64 字符或含非法字符；
- 一个 session 超过 4 个 Paper action；每个 provider Runtime 最多 256 个 Global action；
- 误以为插件 `Priority` 可以超过宿主按钮；宿主 action 永远拥有更高优先级；
- SVG 传完整 `<svg>` 而不是 Path Data；
- `Stroke` 使用非有限或 0.1～4.0 之外的 `strokeWidth`；
- 想隐藏关闭/置顶/拖动等宿主生命线；
- 为 Top Bar 另写正文 mutation，而不是复用 Workspace；
- 从 paper body / Web Mini 注册 Global action，而不是 Runtime；
- 把 Global contribution 绑到某个具体 body session、paper 可见性或展开状态，而不是 provider 的实体 paper 存在性；
- Web body reload 后仍假设上一 document 的 Paper contribution 有效。

### WPF surface

- 把同一个 WPF 元素同时返回给正文、Regular capsule、Docked capsule 或 mini；
- 返回已有 parent 的控件；
- 把 `Window`、`HwndHost`、WindowsFormsHost、WebView2 当成可迁移/custom mini tree；
- 在只读 custom capsule 中放需要点击的按钮；
- 让 Edge Mini 依赖键盘焦点。

### Web Mini

- 认为 `miniSize` 仍有固定 120×90～480×420 协议范围；
- 把 `miniMaxSize` 当成插件取得窗口最终尺寸控制权；
- 需要网页处理点击的局部控件没有 `data-papertodo-interactive`；
- 为接管输入把整个页面根节点无差别标记 interactive；
- 假设 `mini.ready()` 后 Web surface 同步立即显示。

### 状态

- 只在 `Commit()` 或页面卸载时保存；
- 收到 `stateChanged` 后原样 `saveState` 造成 body/mini 回声；
- 把普通 per-paper state 同时写进 `plugins/data` 和 `.runtime/`；
- state 迁移失败时写空对象覆盖旧数据；
- 单张 paper state 超过 1 MiB。

### Workspace / 生命周期

- 没 permission 就调用 Workspace；
- 用 observe 权限误当 read 权限；
- paper session 尝试删除承载自己的 active paper；
- 不需要后台运行却声明 `appRuntime`；
- session/runtime Dispose 后仍让 timer/task/subscription 继续；
- 在 Native Runtime 顶栏回调里直接长时间阻塞 UI 线程；
- 让插件自己接管 Edge HWND、queue placement、外框或 geometry。

## 14. 提交插件前

- `plugin.json` 使用当前目标 `apiVersion: "2.0"`；
- Native manifest 与入口 DLL metadata/runtime requirements 一致；
- 声明 `appRuntime` 时：Native 实现 `IPaperAppRuntimeProvider`；Web 默认提供 `entry` 同目录 `runtime.html`，或用 `runtime` 指定同一 Web 静态目录内的其他入口；
- Runtime 需要插件设置时只读取自己的 `context.Settings.Json` / `papertodo.settings.get()`，不借用隐藏 paper session；
- startupPaper 先决定是否创建/恢复实体插件 paper；Runtime 再按最终实体 paper 数量启动；
- Global Top Bar 只由 Runtime 注册：删除非最后一张不应消失，删除/改造最后一张必须撤销；Global action 用 `Priority` 表达插件内部优先级，宿主 action 始终更高；
- Native 使用统一 build/install 脚本跑通；
- 最终 `plugins/<id>/` 不包含 PDB/XML/重复 shared assemblies；
- `.runtime/` 不被构建脚本误删；
- Web body 与 mini 的 state/settings 同步没有回声；
- Web mini 只有真正需要 pointer 的局部元素声明 `data-papertodo-interactive`；
- `miniMaxSize` 如声明，应真实覆盖 Mini 可能请求的最大尺寸，且 `miniSize` 不超过它；
- Top Bar 只提交 host-rendered descriptor；Paper contribution 随 session 撤销，Global contribution随 Runtime 撤销；
- capsule 提供合理 `plainText`；
- custom WPF surface 均为 fresh / unparented / pure-WPF；
- Edge Mini 不依赖键盘输入；
- 只声明实际需要的 permissions / `appRuntime`；
- 切换 provider、删除 paper 时 0↔1 Runtime ownership 正确；退出 PaperTodo 后 Runtime 与 Global Top Bar 完整撤销。
