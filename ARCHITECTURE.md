# PaperTodo 架构

> 本文记录 **PaperTodo 当前有效的技术选型、架构结构和已经确立的技术方向**。
>
> - 它回答“系统现在按什么原则组织、各层由谁负责、后续实现应沿什么边界继续”。
> - 它不是代码目录、历史日志、PR 复盘或未来路线草案；任务入口与阅读顺序见 [`AGENTS.md`](AGENTS.md)，历史取舍和踩坑见 [`DECISIONS.md`](DECISIONS.md)。
> - 具体执行细节仍以当前代码为准。若本文、代码或 Decisions 冲突，先核对当前实现、提交历史和可观察行为，再统一修正。

## 1. 架构目标与当前方向

PaperTodo 是 Windows 桌面“纸片”应用。当前技术路线围绕几个长期方向组织：

- **paper 是主要对象和交互边界。** Todo、Markdown/Note、插件正文和 Edge Capsule 都围绕 `PaperData` / `PaperWindow` 组合；应用级能力由 `AppController` 协调，而不是默认把所有行为收束成一个中心主界面。
- **每个职责尽量只有一个 authority。** 状态、几何、队列 placement、presentation、持久化和外部 mutation 不各自复制第二套“近似真相”。
- **复杂 UI 状态优先走显式状态与单通道 reconcile。** Edge Capsule 使用 Intent → Reducer → Presenter；窗口和 controller 不通过并行 bool/临时 setter 绕过它。
- **WPF 是主 UI / shape owner，native/DirectComposition 只承担确有必要的 Windows 边界能力。** 不把 compositor 扩成第二套 UI renderer。
- **插件贡献内容/动作意图，宿主持有产品 chrome 与关键生命周期 authority。** Capsule、Edge Mini、Top Bar 都沿用这一方向；插件不能因为获得扩展点就接管 PaperWindow、Edge HWND 或顶栏 WPF tree。
- **持久化按数据生命周期和失败语义分域。** 核心状态、图片资产、插件状态分别由各自 store 管理；破坏性恢复/回收采用保守策略。
- **当前 Architecture 只记录已经确立的方向。** 未确认的未来方案、实验候选和一次性 workaround 不写成当前架构。

技术基础：

- .NET 10，目标 `net10.0-windows10.0.17763.0`。
- WPF 是主 UI；Windows Forms 只作为兼容依赖。
- 进程 DPI 策略：`PerMonitorV2,PerMonitor`。
- 主项目入口为根目录 `PaperTodo.csproj`。

## 2. 系统形态与 ownership

正常 GUI 模式由 `App` 建立一个单实例 WPF 主宿主；`AppController` 是应用级协调器。相同的 `PaperTodo.exe` 还支持独立 `--mcp` bridge 模式，该模式在 GUI 单实例协议之前分流，不拥有第二份 `AppState`。

高层关系：

```text
PaperTodo.exe
├─ --mcp
│   └─ McpBridge
│       └─ stdio MCP ↔ GUI-side MCP runtime
└─ GUI App
    └─ AppController
        ├─ AppState / StateStore
        ├─ NoteImageStore (LMDB)
        ├─ PaperBodyPluginRegistry / PaperBodyPluginDataStore
        ├─ PaperCommandService
        ├─ plugin Runtime[providerId] → logical Paper instances / Global Top Bar
        ├─ paper Top Bar session registry
        ├─ PaperWindow[paperId]
        │   ├─ paper shell / Todo / built-in Note
        │   ├─ PaperBodyHost
        │   ├─ host-owned Top Bar renderer
        │   └─ EdgeCapsulePresenter + EdgeCapsuleHost
        ├─ MasterCapsuleWindow[queue]
        ├─ EdgeCapsuleDragWindow (process-global pooled host)
        ├─ tray / hotkeys / reminders / fullscreen runtime
        └─ edge queue coordination / preview session / visual transaction /
           DirectComposition proxy lifecycle
```

主要 authority：

| 领域 | 当前 authority | 结构性职责 |
| --- | --- | --- |
| GUI 启动与进程生命周期 | `App` + `SingleInstanceHelper` | GUI 单实例、启动命令转发、全局异常边界、创建 `AppController` |
| 应用级业务协调 | `AppController` | `AppState`、窗口集合、保存调度、托盘、全局 runtime、跨纸片协调 |
| 核心持久化 | `StateStore` | `data.json` / backup 的加载、恢复和版本化写入 |
| 图片资产 | `NoteImageStore` | LMDB 生命周期、串行访问、图片编号、缓存和回收 |
| 插件状态 | `PaperBodyPluginDataStore` | provider settings、provider Runtime state 与 per-paper frontend state 的独立保存/恢复 |
| 外部 Paper/Todo/Note 命令 | `PaperCommandService` | 插件/MCP 共用的验证、mutation、同步提交/回滚和事件发布 |
| 单纸片 UI | `PaperWindow` | paper WPF shell、普通交互、provider 选择、子系统适配 |
| paper-body session | `PaperBodyHost` | 当前 `IPaperBodySession` 的 attach / invoke / commit / dispose |
| plugin Runtime | `AppController.PluginAppRuntime` | 每 provider 最多一个后端 Runtime；0→1 张实体插件 Paper 时启动、1→0 时释放，按 `paperId` 管理逻辑实例、后端 state、长期 presentation、Global Top Bar/Shortcuts 与 Workspace |
| 插件发现与合同 | `PaperBodyPluginRegistry` | builtin / Native / Web provider 发现、校验、激活 |
| 插件 Top Bar 注册 | `AppController.PluginTopBar` | Paper session action 与 Runtime Global action 的分域注册、输入校验 |
| 插件 Top Bar 绘制 | `PaperWindow.PluginTopBar` | 宿主按钮、字符/SVG 图标、主题/字体/响应式与 suppression reconcile |
| Edge 单纸片业务状态 | `EdgeCapsuleReducer` + `EdgeCapsuleModel` | 单纸片 typed intent 到完整 model 的原子变化 |
| Edge 单纸片呈现 | `EdgeCapsulePresenter` | desired model、target plan、transition、applied frame、reconcile |
| Edge 队列级协调 | `AppController` edge partials | preview owner/corridor、arrange、visual transaction、proxy lifecycle |
| Edge 队列 placement | `EdgeCapsuleQueueCoordinator` | queue index、master offset、slot count |
| Edge 物理几何 | `EdgeCapsuleGeometry` | monitor/edge/DIP 到 wall-pinned physical rectangles |
| docked Edge surface | `EdgeCapsuleHost` | 每纸片 bounded HWND 和完整 WPF visual tree |
| 同队列 compositor translation | `EdgeCapsuleQueueCompositionProxy` | live HWND surface 的 X/Y translation 与 visual-authority handoff |
| floating drag | `EdgeCapsuleDragWindow` | 独立 floating pill HWND |
| 同 Dispatcher 动画节拍 | `EdgeCapsuleFrameScheduler` | Rendering cadence、统一 pointer/time sample、liveness rescue |

## 3. 进程与运行时边界

### 3.1 GUI 单实例

正常 GUI 启动使用 `SingleInstanceHelper` 的 Mutex + named pipe。只有主 GUI 实例建立 `AppController`；后续 GUI 启动只把参数转发给主实例后退出。

`AppController` 尚未完成启动时收到的单实例命令先排队，待 controller 可用后再执行。普通纸片窗口全部关闭不等于退出应用，进程使用显式 shutdown 生命周期。

### 3.2 MCP

`--mcp` 是同一可执行文件的独立 bridge 模式。它在 GUI Mutex 之前分流，通过 stdio 暴露 MCP server；GUI 主宿主内部的 MCP runtime 由 `AppController` 管理。

MCP 的 transport、权限策略和 bridge 生命周期不拥有 Paper/Todo/Note 的第二套业务写入逻辑；真正的业务 mutation 仍回到 GUI 主宿主和共享命令边界。

### 3.3 辅助进程与插件 Runtime

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

## 4. 状态与持久化架构

### 4.1 三个数据域

当前长期数据按语义拆成三个主要域：

| 数据域 | 当前存储 | authority | 方向 |
| --- | --- | --- | --- |
| 核心应用与纸片状态 | `data.json` + `data.backup.json` | `StateStore` | 保持可迁移、可恢复的结构化业务状态 |
| Note 图片二进制 | `note-assets.lmdb` | `NoteImageStore` / `LmdbImageDatabase` | 大体积二进制与 JSON 分离，独立做引用/容量管理 |
| 插件 settings / Runtime state / per-paper frontend state | `plugins/data/*.json` | `PaperBodyPluginDataStore` | 插件后端、前端与核心状态解耦，独立迁移和恢复 |

这三类数据不能因为“都属于一张纸”就合并成一个写入协议。核心状态保存、图片回收和插件状态恢复具有不同失败语义，因此保持各自 authority。

### 4.2 核心状态

`AppState` 是核心持久化根；`PaperData` 是单纸片模型；Todo 行使用 `PaperItem`。

删除、隐藏、折叠是不同语义：

- 删除从 `State.Papers` 移除对象。
- 隐藏保留对象，仅改变可见性。
- 折叠仍是可见纸片，只切换到 capsule presentation。

普通窗口 `X/Y/Width/Height` 与 Edge Capsule 的 queue / expanded recovery geometry 不是同一套状态，不能由 parked/hidden shell 相互覆盖。

`StateStore` 的方向是保守恢复与版本化写入：主文件失败后可从 backup 恢复；需要保护失败源时先保留证据再允许正常保存覆盖。保存阶段只修复序列化无效值，不重新解释业务不变量。

全局 crash boundary 不执行普通“最后强行保存”。正常 durability 由常规保存、同步退出保存和 backup 提供。

### 4.3 图片资产

图片二进制不进入 `data.json`。`NoteImageStore` 统一串行化 LMDB 访问，外部业务代码不直接拥有 LMDB transaction authority。

Markdown 中的 Note 图片只通过 PaperTodo 内部 `i:` asset URI 引用宿主管理的图片；网络图片或任意外部图片不是当前 Note 图片资产协议的一部分。

图片 GC / id reuse 是破坏性操作，因此 reachability 采用 fail-closed：无法可靠证明当前状态和需要保护的 recovery snapshot 都可扫描时，本轮不回收。

### 4.4 插件状态

插件 settings 与 per-paper state 由 `PaperBodyPluginDataStore` 独立保存，不塞回 `data.json`。插件数据读失败时保留原始问题源，并通过受控 recovery 路径继续；插件数据故障不应把核心 Paper 数据变成不可加载。

## 5. Paper 与 paper-body 插件

### 5.1 Paper shell

`PaperWindow` 是单纸片 UI owner，负责普通 paper shell、Todo/Note 交互、标题/工具栏、窗口行为和各子系统适配。

Edge Capsule 启用后，一张纸的可见 surface 不再等价于一个 `PaperWindow` HWND：docked capsule 由 `EdgeCapsuleHost` 提供，跨队列/脱墙拖拽可以临时使用 `EdgeCapsuleDragWindow`；这些 surface 仍引用同一 `PaperData`，不复制业务对象。

内置 Markdown Note 的编辑态和浏览态复用同一个 `MarkdownTextBox`，通过 interaction/presentation 状态切换，而不是维护两套正文 surface。

### 5.2 Provider / session 分层

Provider 当前分三类：

- Built-in Markdown。
- fully trusted / unsandboxed Native .NET/WPF plugin。
- 本地 Web plugin，通过宿主 WebView2 运行。

`PaperBodyPluginRegistry` 负责 provider 发现和合同校验；`PaperBodyHost` 负责一张纸当前 session 的 attach / invoke / commit / dispose；`PaperWindow` 仍拥有窗口 placement、paper chrome 和 provider 选择。

插件文件不在当前进程中做热重载。安装、删除或修改插件目录后统一重启 PaperTodo，让下一进程重新完成 manifest discovery 和所需 runtime/DLL 激活。

### 5.3 外部读写

插件 `Workspace` 与 GUI 侧 MCP 对 Paper/Todo/Note 的共享业务 mutation 统一进入 `PaperCommandService`。该边界负责：

- 参数和类型约束；
- mutation 前提交仍停留在 UI/provider session 的待提交内容；
- 保存成功才完成外部 mutation；
- 保存失败回滚内存状态；
- 提交后刷新必要 UI 并发布外部变更事件。

transport 权限、Web/Native surface 生命周期、Top Bar presentation 和 MCP protocol 不下沉到 `PaperCommandService`；反过来，transport/presentation 层也不建立另一套核心 mutation 实现。

### 5.4 Protocol 2.0 Top Bar

Top Bar 是宿主 chrome/presentation capability，不是 Workspace 数据 API，而且 **Paper 与 Global 有不同 owner**：

- **Paper scope**：属于当前 `PaperBodyContext.TopBar` / paper-body session，只作用于承载该 session 的纸片。
- **Global scope**：属于 `PaperAppRuntimeContext.GlobalTopBar` / provider Runtime。该 runtime 只在 provider 当前至少有一张实体插件 paper 时存在；它不属于其中任意一张具体 paper，也不依赖 paper 的可见性、展开状态或 body session。

当前稳定边界：

- `startupPaper` 在启动阶段先决定是否创建/恢复真实插件 paper；之后才按最终实体 paper 集合 reconcile Global Runtime。
- 运行中 provider 从 0→1 张实体插件 paper 时启动 Runtime，从 1→0 时 Dispose；删除、隐藏、折叠非最后一张不会撤销 Global action。
- `PaperWindow` 始终拥有顶栏 WPF tree、按钮尺寸/位置、主题、Hover、DPI、字体缩放和 responsive layout；插件只提交 action descriptor。
- 图标只接受短字符或受限 SVG/WPF Path Data；Path 可以按宿主前景色 Fill 或 Stroke，不接受完整 SVG document、WebView 或任意 WPF tree。
- Paper scope 只作用于承载当前 session 的纸片；插件只能请求隐藏 `NewTodoPaper` / `NewNotePaper`，关闭、置顶、标题拖动和窗口生命周期不属于插件可删减区域。
- Global scope 每 provider 只有一个 Runtime owner；仅安装插件、但没有实体插件 paper 时不产生 Global UI。
- Global 点击带目标 `PaperId` / `Type` / `BodyProviderId`；需要读取或修改目标正文时仍走 Runtime Workspace → `PaperCommandService`，Top Bar 不复制业务 mutation。
- Runtime Workspace / GlobalTopBar facade 会把 Native 后台线程调用 marshal 回宿主 UI Dispatcher；paper session presentation 仍沿用自己的 WPF Dispatcher 生命周期。
- 用户设置决定宿主按钮 base visibility，插件 suppression 是最终 paper-local reconcile 层。
- Paper session Dispose 自动回收 Paper contribution。Web body 的 Paper contribution 进一步绑定当前 body document generation；导航、renderer failure 或 body document replacement 会撤销旧 Paper contribution。
- Web Global action 由独立 `runtime.html` app surface 注册。runtime document 导航、renderer failure、最后一张实体插件 paper 消失或 Runtime Dispose 都会撤销 Global contribution；Web Mini 不拥有 Top Bar 注册权。

为什么选择 host-rendered descriptor、Paper/session 与 Global/Runtime 分域，而不是插件直接拥有顶栏控件或把 Top Bar 塞进 Workspace，见 D-022。

### 5.5 Edge mini

插件可以提供专属 mini、允许迁移的纯 WPF 正文 View、custom/standard capsule presentation 或 plain-text fallback，但 **Edge 的窗口、queue placement、外层尺寸会话和输入 authority 始终属于宿主**。

当前技术方向是“插件贡献内容能力，宿主决定如何安全呈现”：

- Native mini 只接纳 fresh / unparented / pure-WPF tree。
- Web `miniEntry` 使用独立 Web mini surface；它自己的 ready/publication 时序属于 Web session 实现，不把 WebView2 当作可迁移 WPF child。
- 正文 View migration 只对 provider 明确声明且宿主可以安全接管的纯 WPF View 启用。
- 没有专属能力时由宿主降级到 capsule/plain text。

具体 fallback 次序、尺寸和 ready 时序属于当前 contract/代码实现；为什么形成这些边界见 D-018。

## 6. Edge Capsule V3 Lite

V3 Lite 的当前方向不是“再叠一个更聪明的代理”，而是保持 **单一 per-paper presentation authority + 极薄 native/compositor 边界**。

### 6.1 单纸片状态与呈现

主链：

```text
OS / WPF / controller event
        ↓
EdgeCapsuleIntent
        ↓
EdgeCapsuleReducer
        ↓
EdgeCapsuleModel
        ↓
EdgeCapsuleTargetPlanner
        ↓
EdgeCapsulePresentationPlan
        ↓
EdgeCapsulePresenter reconcile / transition
        ↓
EdgeCapsulePresentationFrame
        ↓
EdgeCapsuleHost.Apply(frame)
```

`EdgeCapsuleReducer` 决定单纸片业务状态；`EdgeCapsulePresenter` 是该纸 desired model、target、transition、applied presentation 和 dirty/deferred work 的唯一 presentation authority。

`EdgeCapsuleTargetPlanner` 是纯 desired-model → shape/layout planner，一次生成完整 `EdgeCapsulePresentationPlan`。Docked surface 与 `FloatingFree` 是互斥外形；floating 的宽度、圆角、关闭区和其他 shape 语义不由窗口构造参数或拖拽路径另行拼装。

`AppController` 可以协调跨纸片 session、向多张纸 dispatch intent、捕获事务 frame，但不维护第二份 per-paper desired model。

Measure / display-metrics 也是同一 presentation reconcile 的输入，而不是第二套状态入口：非拖拽时更新 layout snapshot 并从当前已呈现帧 retarget；正在 docked/floating drag 时相关 refresh 延后到 gesture 边界后处理，不反向改写 Hover / Active / slot / gesture 语义。

### 6.2 Queue placement 与 geometry

队列由 monitor + edge 标识。`EdgeCapsuleQueueCoordinator` 只负责 index、master offset 和 slot count；`EdgeCapsuleGeometry` 只负责 monitor/edge/DIP 到物理像素矩形。

`EdgeCapsuleLayoutSnapshot` 捕获的是**目标 monitor** 的 `MonitorGeometry` 与 DPI；docked 物理矩形必须基于这份目标显示器事实计算，不能退回主 `PaperWindow` 的当前 DPI 或在动画/measure 路径重新复制一套换算。共享 capsule 尺寸和队列布局参数从 `PaperLayoutDefaults` / `EdgeCapsuleLayout` 等统一来源进入 layout/planner。

队列保持完整顺序，不引入分页/自动隐藏 overflow。分页会把 placement 问题升级成另一套 visibility/state ownership，因此当前方向仍是连续完整队列。

Presentation contract 区分：

- `Bounds`：当前真正可见的 capsule rectangle。
- `HostBounds`：bounded docked HWND 的 native capacity。
- `InteractiveBounds`：当前真实输入区域。

透明 capacity 不属于交互区域。

### 6.3 Surface 切分

每张 docked capsule 由独立 `EdgeCapsuleHost` 长期拥有真实 HWND 和完整 WPF visual tree。Host 是 **bounded live host**：native capacity 稳定且有限，可见 shape 在其中由 WPF 变化。

跨队列/脱墙拖拽使用独立、进程级复用的 `EdgeCapsuleDragWindow`，不把 docked host 变形成自由 floating pill。

开启 collapse-all master 时，每个队列的 `MasterCapsuleWindow` 占 slot 0，只拥有自身 presentation/gesture，不持有真实 paper 的第二套 presenter state。

### 6.4 WPF 与 DirectComposition

当前明确的职责切分：

**WPF / bounded host owns shape；DirectComposition owns translation。**

WPF / Presenter 负责：

- Resting / Hover / Active / Preview 的可见宽高；
- rounded geometry；
- 内容布局与 opacity；
- `InteractiveBounds` 等 presentation contract。

DirectComposition queue proxy 负责：

- 从真实 HWND 建立 live surface；
- 保持 surface identity / size；
- 只做 X/Y translation；
- 在真实 HWND 已受 cover 保护时帮助 queue 成员完成位置移动和 visual-authority handoff。

Production translation backend 不承担 snapshot、clip/scale/effect resize 或另一套 deferred-resize presentation model。需要 shape/size 变化时，回到 WPF bounded host 或明确 native fallback 边界。

### 6.5 Visual authority 与 handoff

真实 docked HWND、queue compositor cover、floating drag HWND 是显式 visual authority。任何 publication、successor、handoff 或 rollback 边界都必须保证至少有一个可见 authority。

同队列 successor 继承 predecessor 当前 live authority 和可见 sample，而不是 dispose 后冷启动另一套互不相关 proxy。

Proxy 动画逻辑结束不等于 real WPF 已经可以接管。只有 terminal real/WPF presentation 已完成必要的 apply/layout/render/verify 边界后，cover 才能释放；completion timer 只负责发起完成尝试，不作为 correctness proof。

Display/DPI、z-order、drag 结束、隐藏/关闭 Edge 模式等生命周期边界如果会让现有 surface/queue 失效，先结束或恢复当前 visual authority，再清理 preview、retraction、临时 placement/transaction 等 transient state；这些临时状态不能跨失效边界残留到下一次显示或重新启用。

### 6.6 Pointer、Preview corridor 与帧节拍

Hover/Preview 的最终物理 truth 来自当前 presented/applied `InteractiveBounds`。WPF/native enter/leave 主要负责唤醒采样，透明 `HostBounds` 和 proxy envelope 不能扩大 hit area。

Preview session 建立后，当前 owner 是 queue-wide 的 pointer arbiter：owner、候选 target、transfer corridor 和 outside 都由同一 controller 路径解析，host/WPF 输入适配层只提供物理采样，不复制另一套 preview 状态机。owner 与可浏览候选的 `InteractiveBounds` 是真实命中区；连续可交互成员之间的 transfer corridor 只是允许指针跨空白移动的临时连续区域，不是新的 capsule hit area。指针真实离开合法 transfer region 时属于硬边界，预测逻辑不能把 outside 改写成 inside；pointer capture 期间则暂停这类离场判断，避免正在进行的交互被 corridor watcher 抢走。

首次没有 preview session 时，经过验证的真实物理命中可以直接建立 owner；已有 session 内的 A→B transfer 则继续使用当前 residence/stability/predictor policy。具体毫秒数和灵敏度属于实现参数，留在代码。

同一 Dispatcher 的 presenters 共用 `EdgeCapsuleFrameScheduler`。正常 transition 由 `CompositionTarget.Rendering` 推进；watchdog 只在 Rendering 没有及时推进 active transition 时做 demand-driven rescue，不成为第二套长期动画时钟。

这些原则的历史原因、失败路线和不可回退点见 D-005～D-014。

## 7. OS 与全局集成

`AppController` 还协调：

- Hardcodet tray icon / context menu；
- 全局快捷键；
- foreground fullscreen 检测和 topmost avoidance；
- display metrics / DPI 更新；
- Todo reminders；
- virtual desktop integration；
- 可选窗口 magnetism / tether 等实验 runtime；
- GUI 侧 MCP runtime。

全局 watcher 可以触发 visibility、z-order、monitor placement 等变化，但进入具体 Paper/Edge surface 后，仍应回到对应 subsystem authority，而不是在 watcher 中复制 geometry 或 presentation state。

托盘当前基于仓库固定的 `vendor/wpf-notifyicon` 和 WPF `IconSource`；选择该路线的历史原因见 D-017。

## 8. 仓库结构

- `src/`：主程序 C# 源码。
- `Resources/`：中文默认资源及 en/ja/ko 本地化 `.resx`。
- `PaperTodo.Plugin.Abstractions/`：插件 ABI / host contract。
- `plugins/`：可直接加载的插件产物；`plugins/data/` 保存宿主管理的插件状态。
- `plugin-samples/`：插件源码、示例和构建说明。
- `native/`：PaperTodo 自有 native 组件，例如 LMDB bridge。
- `vendor/`：固定版本 vendored dependency / submodule。
- `assets/`：图标和静态资源。
- `docs/`：GitHub Pages 站点资源，不作为内部架构文档默认目录。
- `.github/workflows/`：CI / Release。

根目录保留项目入口和仓库级知识入口：`README`、`CHANGELOG`、`AGENTS.md`、`ARCHITECTURE.md`、`DECISIONS.md`。
