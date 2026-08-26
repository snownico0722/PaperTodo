# PaperTodo 架构决策

> 本文记录 **PaperTodo 重要技术选择的历史背景、取舍、被淘汰的路线和值得防止重复踩坑的结论**。
>
> 它不是当前架构说明、changelog 或验收清单。当前技术选型与架构方向见 [`ARCHITECTURE.md`](ARCHITECTURE.md)；Agent 的任务路由和执行规则见 [`AGENTS.md`](AGENTS.md)。
>
> - 当前代码和可观察行为描述实现事实；commit/PR 是历史证据，不替代代码阅读。
> - 本文首次建立时以 `main@626fe60d` 为代码基线，并独立回读 PR #94 / V3 Lite 及其后续收敛提交。

## 决策索引

索引只用于定位，不替代各条目的 Context / Why / Rejected / Evidence。

| ID | 主题 | Status | 领域 |
| --- | --- | --- | --- |
| D-001 | “桌面纸片”作为主要交互和对象边界 | Accepted | 产品边界 |
| D-002 | `data.json` 与 LMDB 分域 | Accepted | 持久化 |
| D-003 | Crash boundary 不做强制最终保存 | Accepted | 持久化 / 恢复 |
| D-004 | Paper body 通过 session 边界接入 | Accepted | 插件 |
| D-005 | Edge typed Intent → Reducer → Presenter | Accepted | Edge 状态 |
| D-006 | Queue placement 与物理几何单一 authority | Accepted | Edge 几何 |
| D-007 | per-paper bounded live host | Accepted | Edge surface |
| D-008 | WPF owns shape；DComp translation-only | Accepted | Edge composition |
| D-009 | Visual authority 显式交接 | Accepted | Edge handoff |
| D-010 | Successor 继承 predecessor live authority | Accepted | Edge transaction |
| D-011 | Floating drag 使用独立持久 HWND | Accepted | Edge drag |
| D-012 | Rendering cadence + rescue-only watchdog | Accepted | Edge animation |
| D-013 | Proxy handoff 等待真实 WPF terminal presentation | Accepted | Edge handoff |
| D-014 | Pointer truth 来自 `InteractiveBounds` | Accepted | Edge input |
| D-015 | AGENTS / Architecture / Decisions / 注释分工 | Accepted | 文档体系 |
| D-016 | V3 Lite 收敛后删除一次性验证脚手架 | Accepted | 验证 / 工具 |
| D-017 | Hardcodet WPF `IconSource` + 本地 popup lifecycle | Accepted | 托盘 |
| D-018 | Edge plugin mini 由宿主持有关键 authority | Accepted | 插件 / Edge |
| D-019 | Note 编辑与浏览共享 `MarkdownTextBox` | Accepted | Note |
| D-020 | 插件状态与核心 `data.json` 分域持久化 | Accepted | 插件 / 持久化 |
| D-021 | 插件与 MCP 共用 `PaperCommandService` | Accepted | 外部命令 / 一致性 |
| D-022 | Plugin Top Bar 使用宿主绘制 descriptor + Paper/app-runtime 分域 | Accepted | 插件 / UI ownership |
| D-023 | Lightweight Prewarm 保留一次性首用预热 | Accepted | Edge performance |
| D-024 | 插件后台统一为 provider 单 Runtime | Accepted | 插件 / 生命周期 |

## 维护规则

Decisions 的核心问题是：**“为什么今天会这样设计，以及哪些已经付过代价的路不要轻易重走？”**

新条目优先回答：

1. **Context**：当时面对什么问题/约束，为什么需要做选择。
2. **Decision**：最终选择了什么。
3. **Why**：核心 trade-off 和判断依据。
4. **Rejected / Pitfalls**：哪些路线已经试过、证明危险或不符合当前目标。
5. **Consequences**：这个选择长期带来什么约束或成本。
6. **Evidence**：当前代码入口、关键 commit / PR。

维护时遵守：

- Decisions 是**历史记录**，不是当前状态镜像。已 Accepted 的条目可以修正事实错误、补证据或澄清原意，但不要为了让它“看起来一直正确”而抹掉当时的背景。
- 如果一个旧选择真正失效，优先新增下一条 D-xxx，并把旧条目标为 `Superseded by D-xxx`；不要直接把旧 Decision 改写成后来完全不同的路线。
- 普通 bugfix、参数调整、UI 调整、测试结果和 PR 逐步试错不自动产生 Decision；只有最终形成可复用的技术取舍/踩坑结论时才提炼。
- `Rejected / Pitfalls` 只记录有证据的失败/高风险路线，不把所有“没选中”的想法升级成永久禁令。
- Evidence 优先指向当前代码文件/类型，并在历史因果重要时补关键 commit/PR；聊天记录不作为长期证据。

---

## D-001 — 保持“桌面纸片”作为主要交互和对象边界

**Status:** Accepted

### Decision

PaperTodo 当前的主要交互单元仍是一张可独立存在、独立显示和独立交互的桌面纸片。Todo、Markdown/Note、插件 body、胶囊等能力以 paper 为自然组合边界；应用级能力由 controller 协调，但不会在没有新产品决策的情况下自动把所有 paper 行为收束成一个中心主界面。

这条选择约束的是对象与交互边界，不是永久禁止新的后端、索引、提醒、外部数据源或知识管理能力。后续如果产品方向明确扩展，应通过新的 decision 更新边界，而不是让旧结论反过来否决已经明确的新路线。

### Why

大量窗口生命周期、持久化几何、跨纸片链接、托盘恢复、边缘胶囊和插件 session 都以 `PaperData` / `PaperWindow` 为自然边界。保持这个边界能让新增能力组合到纸片上，而不是默认把所有功能提升到全局 controller。

### Consequences

- `AppController` 可以全局协调，但不吸走所有单纸片业务行为。
- `PaperWindow`、`PaperBodyHost`、edge per-paper presenter/host 保留清晰的 paper ownership。
- 新功能先判断它属于 paper、paper body 还是应用级 runtime；若要改变这个分层，记录新的明确决策。

### Evidence

- 当前 `AppState -> PaperData[] -> PaperWindow` 主结构。
- `ARCHITECTURE.md` 的 ownership/边界说明。

---

## D-002 — 业务状态使用 `data.json`，图片资产独立进入 LMDB

**Status:** Accepted

### Decision

`data.json` 是 PaperTodo 的核心业务状态协议；图片二进制不进入 JSON，而由 `NoteImageStore` / LMDB 独立保存。

恢复和图片 GC 均采用保守策略：无法证明恢复源或图片引用扫描可信时，宁可保留旧数据/禁用 GC，也不猜测删除。

后来加入的插件 settings/per-paper state 没有反向塞回这两个域，而是形成独立插件持久化域，见 D-020。

### Why

JSON 适合可迁移、可恢复的结构化 paper 状态；图片二进制会放大写入、备份和恢复成本。将二者分开后，可以对 JSON 做版本化 snapshot 保存，对图片做引用 reachability 和独立容量管理。

### Rejected / Do not reintroduce

- 不把图片 blob/base64 重新塞回 `data.json`。
- 不在失败启动后用默认空状态覆盖无法解析的主文件。
- 不在保护 snapshot 无法可靠扫描时继续做破坏性 image GC/id reuse。
- 不绕过 `NoteImageStore` 建立另一套 LMDB transaction authority。

### Evidence

- `src/StateStore.cs`。
- `src/NoteImageStore.cs` / `src/LmdbImageDatabase.cs`。

---

## D-003 — Crash boundary 不做“最后一次强行保存”

**Status:** Accepted

### Decision

未处理异常的全局边界负责记录 crash log 和结束当前错误路径，不尝试把此刻内存中的整个 `AppState` 强制持久化。

### Why

抛出未处理异常时，内存对象可能已经处于只完成一半的业务事务中。此时“尽量保存”可能比丢失最后几秒编辑更危险，因为它可能覆盖此前健康的 `data.json` / backup。

正常 durability 由自动保存、force-save 上限、同步退出保存和 `data.backup.json` 提供。

### Rejected / Do not reintroduce

不要在 Dispatcher/AppDomain crash handler 中直接调用普通保存流程，除非未来先建立可证明一致的 crash-safe snapshot 机制。

### Evidence

- `App.xaml.cs` 当前异常处理路径。
- `src/StateStore.cs` 的正常恢复/backup 机制。

---

## D-004 — Paper body 插件通过 session 边界接入，而不是直接接管 `PaperWindow`

**Status:** Accepted

### Decision

Provider 发现与单纸片 provider session 分开：

- `PaperBodyPluginRegistry` 发现/校验 builtin、Native、Web provider。
- `PaperBodyHost` 管理一张纸当前 `IPaperBodySession` 的 attach、invoke、commit/cancel/dispose。
- `PaperWindow` 仍拥有 WPF placement、paper chrome 和 provider 选择。

Native plugin 是 fully trusted / unsandboxed，并且已载入版本不能在同一进程中安全热替换；Web provider 可以重新扫描/加载。

### Why

插件只替换“纸片 body”，不会把窗口 placement、保存、edge capsule、主题和宿主生命周期一起变成插件 ABI。

### Rejected / Do not reintroduce

- 不让插件直接成为 `PaperWindow` 生命周期 authority。
- 不假设 Native WPF assembly 可以像 Web 内容一样无代价热替换。

### Evidence

- `src/PaperBodyHost.cs`。
- `src/PaperBodyPluginRegistry.cs`。
- `527f2a63c841cb95a29fbff4d197d3877e14f6a7` — `feat: implement paper body plugin system v2`。

---

## D-005 — 单纸片 Edge 状态必须走 typed Intent → Reducer → Presenter

**Status:** Accepted

### Decision

会改变一张纸 Slot / Visual / Gesture / Preview / Placement 的产品级输入使用 `EdgeCapsuleIntent`；`EdgeCapsuleReducer` 原子地产生完整 `EdgeCapsuleModel`；`EdgeCapsulePresenter` 是该纸 desired model、target plan、transition、applied frame 和 deferred work 的 authority。

队列级 preview owner、transfer corridor、arrange、visual transaction 和 proxy 生命周期由 `AppController` 协调。它可以向多张纸 dispatch intent、捕获起终帧和组织事务，但不持有第二份单纸片 desired model。

### Why

Edge capsule 同时存在单纸片状态与跨纸片会话。若 `PaperWindow`、controller、host 各自直接修改一组 bool/enum，局部修复很容易制造无法枚举的非法组合；反过来，如果把整个队列会话强塞进每张纸 reducer，又会复制 owner/corridor/transaction 状态。

### Rejected / Do not reintroduce

- 不增加通用 `SetEdgeState(...)` / 一组公开 field setter。
- 不在 `PaperWindow` 再维护第二套单纸片 edge FSM。
- 不让 controller 的 preview/transaction 状态反写成另一份 per-paper model。
- 不为每个新 race 单独增加一对 `pendingX/scheduledX` 绕开现有 reconcile。

### Evidence

- `src/EdgeCapsuleModel.cs`。
- `src/EdgeCapsuleReducer.cs`。
- `src/EdgeCapsulePresenter.cs`。
- `src/AppController.EdgeCapsulePreview*.cs` 与 `src/AppController.EdgeCapsuleVisualTransaction.cs`。

---

## D-006 — Queue placement 与物理几何各只有一个 authority

**Status:** Accepted

### Decision

- 队列 index / master offset / slot count 只由 `EdgeCapsuleQueueCoordinator` 计算。
- monitor/edge/DIP 到物理 `DeviceScreenRect` 的 docked geometry 只由 `EdgeCapsuleGeometry` 计算。
- 队列不分页；超出工作区的成员仍保持完整顺序，可以直接延伸出屏幕。

### Why

最危险的一类 edge bug 来自“每个窗口都能从邻居/当前 HWND 猜一次队列位置”和“多个路径复制像素取整公式”。PerMonitorV2、多 DPI、左右墙和跨屏环境会把这类复制放大成 1px/一帧分歧。

分页还会把纯 placement 升级成可变 visibility/state ownership，为 reorder、preview corridor、drag 和 master offset 增加另一套隐藏状态。

### Rejected / Do not reintroduce

- 不按工作区高度推导“安全容量”。
- 不加入 overflow page/header/page number/自动翻页。
- 不在动画、measure、host apply 或 controller 中复制 docked physical-pixel 公式。

### Evidence

- `src/EdgeCapsuleQueueCoordinator.cs`。
- `src/EdgeCapsuleGeometry.cs`。

---

## D-007 — V3 Lite 采用 per-paper bounded live host

**Status:** Accepted

### Decision

每张 docked capsule 的真实 HWND 由 `EdgeCapsuleHost` 长期拥有。`HostBounds` 是当前 host generation 的稳定 bounded capacity；`Bounds` 是当前可见 WPF shape。

容量只覆盖该纸在当前 monitor/DPI/edge 上真实可能需要的最大 Preview，不扩成整个工作区或整条队列。Late-bound plugin preview 可以让 capacity 增长，但正常 Resting/Hover/Preview 不靠反复 resize native host 做形变。

### Why

endpoint-sized HWND 会让 Hover/Preview 每轮改变 native surface identity，使 compositor translation 和 resize ownership 缠在一起；过大的长期透明 host 又扩大 hit-test、z-order、资源和透明区域 ownership 问题。bounded live host 把 native capacity 与可见 shape 分开。

`agent/edge-animation-v2` / PR #84 先证明了另一个更基础的事实：Edge 热帧的主要成本曾经不是插值或 C# 属性赋值，而是同步 native geometry boundary。对应 trace 中第一次真实 HWND move 常见约 10–15ms，逐帧把位置交给 `EndDeferWindowPos` 在部分机器上也会出现 10–20ms+ 阻塞；固定 HWND、只更新内部 WPF surface 后，活跃视觉更新可以降到亚毫秒级。因此这里保留“正常动画不逐帧提交 native geometry”的方向，而不是回头优化同一条昂贵系统调用。

但 V2 又把这个结论推得过远：为了完全避免 HWND movement，每张纸预留 work-area / queue 级永久透明 motion envelope。PR #85 随后必须专门测量透明 host 数量、像素面积、内存和 handle 成本。V3 Lite 因此选择中间边界：**native capacity 可以稳定，但必须按真实单纸片需求有界；不能用永久巨型透明 surface 交换掉所有 resize/move。**

### Rejected / Do not reintroduce

- 不恢复逐帧 resize 真实 HWND 的 endpoint-sized 架构。
- 不把每张纸扩成 work-area-sized / queue-sized 透明合成面。
- 透明 capacity 不能被当成交互区域。

### Evidence

- `6fb9c33d1827963df1fb84b1c7eb837eeb54cc77` — ordinary edge frames 从 HDWP batch 退回直接 HWND move，暴露 native boundary 本身的高成本。
- `26afaed1b5b65b4a9771916d4edaf91c5abbb028` / PR #84 — fixed-host V2，把 per-frame HWND geometry 移出动画热路径。
- `b02add61e9d61c427af31e3a3859e7dcb499ce1d` / PR #85 — 专门测量永久透明 host 的资源成本。
- `32866e9085c2002c3411d4a2c93a96903fe6c9ee` — `refactor(edge): establish V3 Lite bounded live hosts`。
- `ca70631d2c3b77a883a5c78f5a912cfe2ccc9294` — late-bound plugin preview capacity。
- 当前 `src/EdgeCapsuleTargetPlanner.cs` / `src/EdgeCapsuleHost.cs`。

---

## D-008 — WPF 拥有 shape；DirectComposition 只允许 live-surface translation

**Status:** Accepted

### Decision

V3 Lite 的最终职责切分：

**WPF / bounded host**：width/height morph、Resting/Hover/Active/Preview shape、rounded geometry、content/opacity、presentation/interactive bounds。

**DirectComposition queue proxy**：获取真实 live HWND surface、保持 surface identity/尺寸不变、只改变 X/Y offset、在 cover 下让 real HWND 一次 settle 到 endpoint，并用同一 logical frame 路由 proxy 输入。

### Why

如果 compositor 同时拥有 translation、clip、scale、resize、snapshot，而 WPF 也在改变真实 HWND/visual size，就会出现两套 presentation model。successor、pointer hit test、DPI handoff 和 rollback 都必须额外判断“此刻谁才是真的”。translation-only 把 compositor 限制成位置加速层，而不是第二套 UI renderer。

`codex/edge-queue-composition-proxy-v2` / PR #86 正确地纠正了 V2 的永久大 host：真实 HWND 恢复 compact，只在当前 queue 的浏览事务中临时建立 DComp proxy。但当 proxy 后续开始拥有 snapshot、clip、rounded morph、opacity、pointer geometry、endpoint replacement 和 cloak/uncloak 时，它事实上已经从“临时位置加速器”扩成第二套 presentation engine。此后每个 DPI、rollback、successor、drag、hide/close 边界都必须额外回答 WPF 与 proxy 谁拥有 shape、输入和当前可见帧。

`agent/v25-edge-smoothness-final` / PR #90 把这个信号暴露得最清楚：为了继续维护 proxy-owned resize/shape，需要逐渐增加 snapshot host/pool、warm lease、successor/late latch、admission、reveal/conceal、freeze、deferred endpoint handoff、secondary owner routing、output-growth fallback 和资源退休时序。单个机制各自都有局部理由，但如果 correctness 持续依赖越来越多 shadow ownership / deferred recovery 状态，应该把它视为 **authority 划分失衡的架构报警器**，优先收窄 owner，而不是继续扩 handoff 状态机。V3 Lite 最终通过“WPF owns shape；DComp translation-only”消掉这一类补偿需求。

### Rejected / Do not reintroduce

V3 Lite production translation backend 明确不包含：

- bitmap snapshot / frozen frame
- clip resize
- scale resize
- effect-based resize
- Reveal / Conceal resize handoff
- deferred resize state machine
- 用 compositor opacity/bitmap trick 模拟 WPF Preview shape

如果为了让 compositor-owned shape/resize 继续成立，又开始需要 snapshot pool、warm lease、freeze/conceal、deferred handoff、secondary owner index 等成套补偿状态，不把它当作“再补一个边界 case”；先重新审视 presentation authority 是否已经分叉。

### Evidence

- `c181f63dc02dde6c101439f8ee0d3737f49c4b45` / PR #86 — compact real HWND + transient queue DComp proxy 的 V2 探索。
- PR #90 (`agent/v25-edge-smoothness-final`) — 在 proxy 路线上集中出现 snapshot/warm lease/successor/late latch/freeze/deferred handoff 等补偿机制。
- `49cf645796730cdc8d2a93338a135b75aa0c44bf` / `63df9f049077ece9050f12943b4b768453a64998` / `a402a80c656f0e6aa9769225035aa6aa53267857` — conceal/freeze/owner recovery 链路的典型后期修补。
- `32866e9085c2002c3411d4a2c93a96903fe6c9ee`。
- `d4af6affc0d5b704e20e020ae9e9621170613c8c` — 删除 snapshot/pointer-proxy 路径并收紧 backend 能力。
- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb` — closeout 验证 live-surface bridge 且拒绝 clip/scale/effect/snapshot。
- 当前 `src/EdgeCapsuleQueueCompositionProxy.Visuals.cs` / `Routing.cs`。

---

## D-009 — Visual authority 必须显式交接；失败路径不能出现 all-hidden gap

**Status:** Accepted

### Decision

Queue compositor、真实 docked HWND 和 floating drag HWND 是显式 visual authority。publication / successor / handoff / rollback 任一边界都必须保证至少一个可见 authority 存在。

DComp root replacement 与 DWM cloak/uncloak 通过可验证 transaction boundary 协调。cover 丢失时先立即尝试恢复真实 HWND；只有即时恢复本身失败时才进入有界 completion retry。

一次 visual transaction 的原子单位对应**用户看到的一次 authority swap**，而不是一个 HWND。涉及同一队列的 endpoint settle / reveal / cloak / root detach 时，优先先完成所有成员需要的 apply/layout，再跨一个共享的 render / desktop-composition boundary，最后统一验证和交接；不要让每个成员各自完成一套完整 flush/handoff。

### Why

真实 HWND 已经到 endpoint 并不代表用户一定能看到它。如果 source 仍 cloaked 而 proxy root 已撤，用户会看到空白；如果 proxy 和 real source 无约束同时可见，又会出现 duplicate/flash。真正需要原子化的是谁拥有可见像素。

V2.5 的日志还证明了 transaction 粒度本身会成为性能和正确性问题：逐成员执行 endpoint / cloak / flush / verify 时，close 后仍存在约 58–85ms 的 proxy handoff 尾段，中位约 76.6ms，并随参与 member 数增长。PR #90 因此把 endpoint apply/layout 和 cloak/uncloak 收敛成整队列批处理，并减少重复 Render/DwmFlush 边界。这里保留的经验不是“永远只有一次某个 Win32 调用”，而是**共享一个可见交接的成员应该共享 publication/verification 边界**。

### Rejected / Do not reintroduce

- 不允许“先全部 cloak，稍后再发布 cover”。
- 不允许 cover 丢失后什么都不做、先空等 timer 才首次恢复 real source。
- 不把资源 Dispose 当作 authority transfer。
- 不为同一 visual transaction 按 HWND 重复执行 `apply → render/flush → verify → next member` 的完整交接；成员级准备可以独立，但 authority swap 应在共享边界统一完成和验证。

### Evidence

- `9c5c2679194edf2e3d84261f1b9a58faf7b16a5b` / PR #90 — 批量 endpoint、cloak/reveal 与 UI-critical handoff 收敛。
- `59920e0bd8b50cfc476c090b9fcfa38f427e9862` — shared compositor failure 时按 queue 安全 reveal/drain，而不是破坏其他 authority。
- `f444f2897d1a741d2478a5d9af15744ed6a99716`。
- `bb45739d49b16b4e609333476888f65f402fb17b`。
- 当前 `src/EdgeCapsuleQueueCompositionProxy.Handoff.cs`。

---

## D-010 — Successor 继承 predecessor 的 live authority

**Status:** Accepted

### Decision

同一 monitor/edge queue 上已有 active proxy 时，新事务作为 successor generation：复用同一 output HWND / DComp target，从 predecessor 当前呈现 sample 重新基线化，carry forward predecessor 仍拥有的成员和 cloaked real source；引入新 source 时可以用 predecessor live surfaces + 新 live sources 组成短暂 admission cover。只有现有 output envelope 已覆盖 successor 需要区域时才允许直接 admission。

### Why

把 successor 当成一次新的冷 proxy，会产生两套 cloak/source 集合和两个 output HWND 的 z-order，并可能让 predecessor 的 stationary peer 在 root replacement 时消失。

### Rejected / Do not reintroduce

- 不先 dispose predecessor 再冷启动 successor。
- root replacement 不只带本次 changed member 而遗漏 predecessor stationary peers。
- 不为扩大 successor envelope 移动仍承载 predecessor root 的 output HWND。

### Evidence

- `be94659d555b79759853fb392b1af5a4577d19fa`。
- `bb45739d49b16b4e609333476888f65f402fb17b`。
- 当前 successor admission / carry-forward 代码。

---

## D-011 — Floating drag 是独立且持久复用的真实 HWND

**Status:** Accepted

### Decision

脱离队列/跨边拖拽使用 `EdgeCapsuleDragWindow`，不复用 docked 单边 host。controller 序列化 capsule reorder，因此进程级只维护一个 pooled drag HWND；其 HWND 和 WPF tree 长期存在，lease 时只重新绑定 paper-specific presentation。

### Why

Docked capsule 有 wall-side straight edge、close segment、bounded capacity 和 queue placement；FloatingFree 是对称自由胶囊。复用同一 host/visual tree 会让 edge column、corner、DPI 和 width 状态相互污染；每次重新 Create/Show/Close 又会把 WPF Window 冷启动放回输入热路径。

### Rejected / Do not reintroduce

- 不让 `EdgeCapsuleHost` 临时变成 floating pill。
- 不为每次拖拽重建 WPF visual tree / HWND。
- 在 controller 仍保证单 drag session 时，不建立多个备用 drag host 池。

### Evidence

- `cc9906ab940bc0e11905401fb079fdedc1f05427` — `fix(edge): keep one persistent drag host`。
- 当前 `src/EdgeCapsuleDragWindow.cs`。

---

## D-012 — Presenter transition 使用 Rendering cadence；watchdog 只救活

**Status:** Accepted

### Decision

正常 edge transition 由 presenter 持有，并由同 Dispatcher 的 shared `EdgeCapsuleFrameScheduler` 在 `CompositionTarget.Rendering` 上推进。liveness watchdog 只在 active transition 未及时得到 Rendering 推进时补一次 frame；具体阈值属于实现参数，不是架构决策。

### Why

长期 `DispatcherTimer`/高频 timer 会与 WPF compositor cadence 漂移；纯 Rendering 又可能在某些调度边界失去活性。最终选择是一套正常 frame clock + 一个按需 rescue，而不是两套持续竞速的 frame producer。

### Rejected / Do not reintroduce

- 不恢复长期固定间隔 `DispatcherTimer` 作为第二动画引擎。
- watchdog 不在无 active transition 时持续运行。
- pending reconcile / external native batch 未释放时，watchdog 不穿透 ownership 强行推进。

### Evidence

- `303c9ebd22fa69d75a32bb7cb923c42cfb512fb5`。
- `708dcd267827cee9f9174d9e9c49303ae3b760e8`。
- `e5e07526da0d9b6178975e5c7e90debf4d4a6241`。
- `ce406c10507418c67b32bd17b9c7b99819201145`。
- `a3c8b62962178ca5d6a63f5c555c7c0a847eee56`。
- 当前 `src/EdgeCapsuleFrameScheduler.cs`。

---

## D-013 — Proxy handoff 等待真实 WPF terminal presentation，而不是靠额外 delay

**Status:** Accepted

### Decision

proxy animation 到逻辑终点后，最终 real/WPF presentation 必须先完成 endpoint flush/apply、必要的 WPF render turn、真实 bounds verify 和 authority swap 条件，再允许撤 compositor cover。

completion timer 只能发起完成尝试，本身不是 WPF terminal frame 已就绪的证明；尝试失败时 cover 继续持有 authority，并在后续重试中重新走 endpoint 准备与验证。

### Why

DComp 动画结束和 WPF 最后一帧真正进入 DWM 并非天然同一个调用点。固定 completion guard 只能降低 race 概率，不能构成 correctness proof。

### Rejected / Do not reintroduce

- 不使用“再延迟几毫秒”作为 terminal-frame 正确性的证明。
- 不把 completion timer 到期等同于 WPF 已完成。
- endpoint apply/layout/verify 失败时不先撤 cover。

### Evidence

- `c9aa1910d6533e95947567e4b057e87b0e93f7ae`。
- `bcc6740e992af048cc28f8b810168301434f9555`。
- `4200162d363dc4f22bacc198e599ba917da3f36f`。
- `9f7a04ba4c1d01103fb53679f3b939b9e16083d0`。
- 当前 `FinishEdgeCapsuleQueueCompositionProxy` 路径。

---

## D-014 — Pointer truth 来自 presented `InteractiveBounds`

**Status:** Accepted

### Decision

Hover、Preview 和 preview corridor 的物理命中，以当前用户实际看见/已应用 presentation 的 `InteractiveBounds` 为准。WPF/native enter/leave 只是触发重新采样的 signal。proxy 拥有可见像素时，也消费同一 sampled logical frame 的 `InteractiveBounds` 做输入路由。

### Why

透明 chrome、bounded host capacity、DComp translation 和 WPF transition 会让 HWND rectangle 与真正可交互 capsule rectangle 不一致。native leave 不是最终 truth，整个 host/proxy envelope 也不能变成 hit area。

### Rejected / Do not reintroduce

- 不直接在 `MouseEnter/MouseLeave` handler 写 Hover business state。
- 不把透明 host capacity 或 compositor envelope 当 capsule/corridor bounds。
- 预测算法不能否决“已经物理离开整个合法区域”的事实。

### Evidence

- 当前 `EdgeCapsulePresenter.Reconcile`。
- 当前 `src/EdgeCapsuleQueueCompositionProxy.Routing.cs`。
- `dcf2033d41b3b52c3036eb6a3d4204b2b3441cd9` / `e15796d57f6126e242445c54a8813fd022c35978`。

---

## D-015 — 四类知识分工：AGENTS 路由与执行，Architecture 当前方向，Decisions 历史取舍

**Status:** Accepted

### Context

同一个架构事实如果同时被复制进 AGENTS、专题文档、Architecture 和手工验收矩阵，会快速漂移；但把 AGENTS 过度瘦成几行目录，又会丢掉已经验证有价值的详细 Agent 执行规则。

### Decision

PaperTodo 长期维护四类互补知识：

- `AGENTS.md`：**任务路由 + Agent 执行规则**。告诉 Agent 什么任务先读哪里，同时保留项目专用、可执行、容易踩错的详细规则。
- `ARCHITECTURE.md`：**当前技术选型、架构结构和已确立技术方向**。回答“现在应该按什么原则设计”。
- `DECISIONS.md`：**历史取舍、失败路线、踩坑和 why**。回答“为什么会走到这里”。
- 关键代码注释：局部 why、不变量和危险边界。

不建立需要人工长期同步的第二套完整架构说明或长期场景验收矩阵。可执行正确性优先进入编译、行为测试、probe、诊断日志或任务当次验证记录。

### Why

这四类信息的读取时机不同：AGENTS 是常驻执行上下文，Architecture 用于建立当前技术 mental model，Decisions 用于防止重复走历史弯路，代码注释服务局部修改。让每种知识有自然 owner，才能减少漂移而不牺牲 Agent 执行细节。

### Consequences

- 把知识迁入 Architecture/Decisions **不等于机械删除 AGENTS 的详细规则**；凡是 Agent 执行任务时仍需直接遵守的规则可以继续留在 AGENTS。
- AGENTS 可以用一句硬规则重复 Decisions 的结论，但历史原因、失败过程和证据放 Decisions。
- Architecture 不记录未确认未来方案，也不复述 PR 历史。
- `docs/edge-presentation-v3-lite.md` 不再作为并行当前架构文档保留；历史演进由 Decisions + git/PR 保存。

---

## D-016 — V3 Lite 完成后删除一次性验证脚手架

**Status:** Accepted

### Decision

PR #94 为完成 V3 Lite 曾引入 source export、finalizer、clean-state verifier 等一次性 workflow/script。最终实现验证完成后，这些迁移脚手架被删除，主线只保留生产代码、通用 CI 和仍有长期价值的 diagnostics。

### Why

一次性迁移脚本适合受控大重构，但完成后继续留在仓库会制造“它是不是仍是生产流程的一部分”的歧义，并增加根目录/Actions 噪音。通用 diagnostics 与一次性 orchestrator 是不同类别。

### Evidence

- `849c9bb044550a7c267078e0a6bfe1f8af56b1bb`。
- `899f3cd284eaa19b45cc8ae6a953f5500ca2a57b` — PR #94 merge。

---

## D-017 — 托盘沿用 Hardcodet WPF `IconSource` + 本地 popup lifecycle

**Status:** Accepted

### Context

托盘菜单曾集中暴露首次右键定位、跨 DPI 坐标、Popup HWND 建立时机和 focus 归还问题。单独在 PaperTodo 外层加 popup/预热/轮询补丁会绕开 Hardcodet 自己的生命周期，修一个时序又制造另一个时序。

### Decision

托盘继续使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`；外部 `PaperTodo.ico` 保持用户覆盖入口。跨 DPI popup activation/focus 由仓库固定的 `vendor/wpf-notifyicon` 路线承担，菜单仍在打开时按当前状态重建。

### Why

当前 `IconSource + 本地 wpf-notifyicon fork + 真实 Popup HWND` 是已经共同解决 DPI/focus 问题的一整套路径。这里保留的是“不要绕开这套控件生命周期”的结论，而不是声称某一个 API 单独解释所有托盘 bug。

### Rejected / Do not reintroduce

- 不把默认托盘路径换回 `System.Drawing.Icon`。
- 不用手动 popup、菜单预热或全局鼠标轮询修首次菜单问题。
- edge context-menu focus 清理不无条件提前到 WPF menu mode 尚未退出时。

### Evidence

- `src/AppController.Tray.cs`。
- `vendor/wpf-notifyicon`。
- `200b23e0826632dae630bc565b41328421381b63` — 接入本地 fork 处理 DPI/focus。
- `5da90e5428e8f68a29b777227454556f862b8e5c` — 托盘打开前清理遗留激活状态。

---

## D-018 — Edge plugin mini 由宿主持有 window/queue/input authority；不同能力走各自安全路径

**Status:** Accepted

### Context

插件协议从结构化 capsule 发展到 1.8 mini 后，Native mini、Web `miniEntry`、曾试验的真实 WPF 正文迁移和旧 capsule fallback 的生命周期并不相同。把它们强行做成一条“先显示旧 fallback、稍后替换”的统一流水线，会重新制造 surface ownership、冷启动和 publication 时序问题。

### Decision

插件可以提供 Native 专属 mini、Web `miniEntry`、自定义/标准 capsule 或 plain text，但 Edge preview 的窗口、queue placement、外层尺寸会话和输入 authority 始终属于宿主。

当前能力路径分别处理：

- Native 专属 mini：只接纳 fresh、unparented、pure-WPF tree；创建失败可以降级到 capsule fallback。
- Web `miniEntry`：使用专属 Web mini host；当前实现不先画旧 1.6/1.7 capsule，准备期间使用透明占位，只有当前文档完成、`mini.ready()` challenge 通过并跨过真实 Rendering publication boundary 后才显示 Web surface。
- Native 正文迁移：该便利路线已退役；需要丰富 Native Edge Preview 时提供专属 `IPaperMiniViewProvider`，否则进入 capsule fallback。
- 没有专属 preview 能力时：进入 custom/standard capsule/plain-text fallback。

### Why

如果插件自己拥有 preview HWND、queue placement 或第二份 authoritative state，Edge 会重新出现多套 geometry/visibility/input ownership。直接搬运 `HwndHost`、WebView2、已挂载 tree 等 foreign/native 生命周期也会破坏 bounded-host 边界。

### Rejected / Do not reintroduce

- 不让插件拥有 edge queue HWND 或 placement authority。
- 不把 `Window`、`HwndHost`、WindowsFormsHost、WebView2 或已挂载控件当作可迁移 Native mini tree。
- 不让旧 same-origin Web document 仅凭 queued `miniReady` 获得新 generation publication authority。
- 不让插件复制宿主的自动宽度/queue placement 算法。

- 不重新引入把 authoritative body View 搬进 Edge Preview 的 reparent/snapshot/warmup/retry 路线；需要 richer Native preview 时由插件提供 fresh dedicated mini tree。

### Evidence

- `51ca6393e96c8c40a73332aea50aac5440f28907` — protocol 1.8 edge mini views。
- `0d972264c581970e9a9a762ce07f7131653e5f2b` — harden protocol 1.8 mini previews。
- `18c65047f7651d1d697b1fad21611ee65e99b940` — defer cold Web mini initialization。
- `src/PaperWindow.PluginMiniView.cs`。
- `src/WebPaperBodySession.Mini.cs`。
- PR #137 — 退役 Native body migration，删除 reparent/snapshot/warmup/retry 路线。

---

## D-019 — 内置 Note 编辑与浏览共享同一个 `MarkdownTextBox`

**Status:** Accepted

### Decision

内置 Note 的编辑态和浏览态复用同一个 `MarkdownTextBox`，通过 presentation/interaction 状态切换，而不是建立两套文本控件并同步内容、滚动和选区。

### Why

两套文本 surface 会产生滚动位置、换行测量、selection、caret、图片布局和编辑提交时序的双向同步问题。单控件切状态保持一份真实文本 surface，避免浏览态和编辑态短暂分叉。

### Rejected / Do not reintroduce

不为了浏览态视觉方便复制第二个 Markdown 编辑/显示控件并做双向同步。

### Evidence

- `src/PaperWindow.Note.cs`。
- `src/MarkdownTextBox.cs`。

---

## D-020 — 插件状态与核心 `data.json` 分域持久化

**Status:** Accepted

### Context

Paper body plugin 引入后，provider settings、stateVersion 和 per-paper plugin state 具有独立版本迁移、独立失败和独立删除清理语义。如果把 opaque plugin state 塞进核心 `AppState`，插件损坏/迁移会直接扩大核心数据恢复面的风险。

### Decision

宿主管理的插件 settings 与 per-paper state 由 `PaperBodyPluginDataStore` 独立保存在 `plugins/data/*.json`；核心 `data.json` 只保留 PaperTodo 自己需要理解的 paper/provider 关系和轻量 presentation cache。

插件状态读失败时保留问题源，并使用独立 recovery 路径继续；插件状态故障不能阻断核心 `data.json` 的正常读取。删除 paper 后的插件状态清理也独立重试，不把附属清理失败升级成核心 save failure。

### Why

插件数据和核心业务状态的 ownership、版本节奏和可靠性边界不同。分域后，宿主仍控制插件数据协议，但单个插件的数据问题不会污染整个 PaperTodo 恢复路径。

### Rejected / Do not reintroduce

- 不把任意插件 JSON/blob 重新并入 `PaperData` / `data.json` 主协议。
- 不让插件绕过宿主 DataStore 自己建立一份会与宿主状态竞争的 authoritative per-paper state。
- 不因为插件附属清理失败而回滚已经成功提交的核心 paper 删除。

### Evidence

- `src/PaperBodyPluginDataStore.cs`。
- `src/PaperBodyPluginRegistry.Settings.cs`。
- `src/AppController.PluginApi.cs` 的 deferred plugin-state cleanup。
- `527f2a63c841cb95a29fbff4d197d3877e14f6a7` — plugin system v2 建立独立 plugin runtime/state 边界。
- `a7dc481f2a5c6dfe95de51a5cfc2eb01f97cb69d` — plugin/MCP hardening，强化失败/恢复边界。

---

## D-021 — 插件与 MCP 共用 `PaperCommandService` 作为外部业务命令边界

**Status:** Accepted

### Context

MCP 和 paper-body plugin 是两种不同的外部入口，但都需要读取和修改同一份 Paper/Todo/Note 业务状态。早期 MCP 路径曾拥有自己的 commit、rollback 和 UI reconcile 辅助逻辑；插件 workspace API 加入后，如果继续让每种 transport 各复制一份业务 mutation，会产生参数约束、保存失败回滚、待提交 UI flush 和事件发布语义逐渐分叉的问题。

### Decision

所有供插件 Host API 与 GUI 侧 MCP 共用的 Paper/Todo/Note 读取和业务 mutation 统一进入 `PaperCommandService`。该 service 拥有跨 transport 一致的业务边界，包括：

- 在外部操作前提交仍停留在 UI/provider session 的待提交内容；
- 统一参数、类型和业务约束；
- 对一次 mutation 做同步持久化提交；
- 保存失败时恢复内存 snapshot / 新建对象等可回滚状态；
- 持久化成功后再做必要的 UI reconcile；
- 以 `PaperOperationContext` 区分来源并在成功后发布外部事件。

MCP transport 继续拥有 JSON/MCP 参数映射和 MCP 授权；plugin host 继续拥有 manifest permission、session validity 与事件裁剪。Transport、权限和 surface 生命周期不下沉进 `PaperCommandService`。

### Why

同一项业务操作不应因为来自 MCP 还是插件就拥有两套 transaction 语义。把可复用业务命令集中后，新增外部入口只需要做 transport/permission 适配，保存、rollback、UI/event 顺序仍由一个 authority 保证；同时避免把 `PaperCommandService` 扩成了解 WebView、MCP protocol 或插件生命周期的万能层。

### Rejected / Do not reintroduce

- 不让 MCP、Web bridge 或 Native plugin adapter 直接修改 `AppState` 后自行保存、回滚和刷新 UI。
- 不为不同外部 transport 各维护一套近似的 Paper/Todo/Note command service。
- 不把 transport authorization、plugin permission 或 surface lifecycle 吸收到共享业务 service。
- 不因为 post-commit UI 刷新失败而把已经成功持久化的 mutation 伪装成“业务未提交”，从而诱发调用方重放。

### Consequences

- 新增外部 Paper/Todo/Note 能力时，先判断能否扩展 `PaperCommandService` 的共享业务合同，再由各 transport 做适配。
- GUI 内部直接交互不因此强制全部改走这层；这条 Decision 约束的是**外部命令边界的共享业务语义**。

### Evidence

- `src/PaperCommandService.cs`。
- `src/McpCommandService.cs`：只做 MCP transport/authorization，并把业务读写委托给 `PaperCommandService`。
- `src/PaperBodyPluginHostApi.cs`：只做 plugin permission/session 边界，并把业务读写委托给同一 service。
- `16cfdb76672390df28a8445937f994af0a0cdc2f` — `feat: add reviewed MCP architecture`，形成最初外部命令事务边界。
- `a7dc481f2a5c6dfe95de51a5cfc2eb01f97cb69d` — plugin v2 / MCP hardening，收敛外部写入失败与恢复语义。

---

## D-022 — Plugin Top Bar 使用宿主绘制 descriptor + Paper/app-runtime 分域

**Status:** Accepted

### Context

Protocol 2.0 要允许插件在自己的纸片顶栏贡献动作，也要允许插件在拥有真实纸片实例时向所有纸片贡献少量 Global 动作。两类动作出现在同一条顶栏上，但真实生命周期不同：Paper action 跟随某张 paper body session；Global action 跟随 provider 级 app runtime，而该 runtime 是否存在由这个 provider 当前是否至少拥有一张实体插件 paper 决定。

如果直接让插件塞 `FrameworkElement` / Button / WebView，插件会同时获得尺寸、主题、Hover、DPI、focus、responsive layout 和 popup 等宿主 chrome ownership；如果把 Top Bar 方法塞进 `Workspace`，又会污染 D-021 已经收敛的 Paper/Todo/Note 数据命令边界。反过来，如果 Global 直接跟随某张 body session，就会因为纸片折叠、隐藏或正文未启动而误丢 Global UI；如果只凭插件安装状态又会让没有任何实体插件实例的 provider 永久占据全局 UI。

### Decision

Protocol 2.0 将 Top Bar 定义为**宿主绘制、并按真实 owner 生命周期分域的 presentation capability**：

- Paper scope 使用 `PaperBodyContext.TopBar` / `IPaperTopBarApi`，只属于当前 paper body session。
- Global scope 使用 `PaperAppRuntimeContext.GlobalTopBar` / `IPaperGlobalTopBarApi`，属于 provider 级 app runtime，但 app runtime 本身以**实体插件 paper 的存在性**为 owner：至少一张 `Note` paper 的 `BodyProviderId` 指向该 provider 时存在，0 张时不存在。
- 启动时先处理已启用的 `startupPaper`，使其有机会创建/恢复真实插件 paper；startupPaper 阶段完成后再按最终 `State.Papers` reconcile app runtime。运行中 0→1 启动、1→0 Dispose；隐藏、折叠、没有展开正文或没有 live body session 都不影响 runtime。
- 未声明 `appRuntime` 的 Native plugin 继续保持 manifest-only discovery 与按 paper 使用时懒加载；声明后也只有满足实体 paper 条件时才因此加载 Native DLL，并要求实现 `IPaperAppRuntimeProvider`。Web app runtime 使用同一插件 origin 下独立的 `runtime.html`。
- PaperTodo 始终拥有顶栏 WPF tree、按钮尺寸/位置、主题、Hover、DPI、字体缩放和 responsive layout。插件只提交 action descriptor，不提交真实控件。
- 图标只接受短字符或受限 SVG/WPF Path Data。Path 可以使用宿主当前前景色 Fill 或 Stroke；不接受完整 SVG document、WebView 或任意 WPF tree。
- Paper scope 只能 suppression 宿主明确白名单中的 `NewTodoPaper` / `NewNotePaper`，不能删除关闭、置顶、标题拖动或窗口生命周期入口。
- 每个 provider 至多有一个运行中的 Global Top Bar app-runtime owner。删除/改造非最后一张实体插件 paper 不影响 Global action；最后一张消失时 app runtime 和 Global contribution 一起结束。
- Global 点击提供目标 `PaperId` / `Type` / `BodyProviderId`。插件若要读取或修改目标内容，仍通过 app-runtime Workspace → `PaperCommandService`，不在 Top Bar 再复制一套业务 mutation。
- Web body 只拥有 Paper Top Bar，Web Mini 不拥有 Top Bar；Web `runtime.html` 只拥有 app-scope Workspace + GlobalTopBar。对应 document 导航、renderer failure、最后一张实体插件 paper 消失或 runtime/session Dispose 都撤销自己 scope 的 contribution。
- app-runtime Workspace / GlobalTopBar facade 把 Native 后台线程调用 marshal 回宿主 UI Dispatcher；paper-session presentation 继续遵守自己的 WPF Dispatcher 生命周期。
- PaperTodo 不提供插件热重载入口。插件 manifest、DLL、Web body/mini/runtime 等文件的安装、删除或修改统一在下一次启动时重新发现并生效。

### Why

这套边界让插件获得“功能入口”，但不获得宿主 chrome 的结构 authority。主题、DPI、布局和交互一致性继续只维护一套；未来 PaperTodo 修改顶栏实现时，不需要把任意插件 WPF tree 当成 ABI。

Global 的关键不是“某张纸片 session 是否正活着”，也不是“这个插件是否安装”，而是**这个 provider 当前是否真的有实体 paper 实例**。因此 startupPaper 必须先执行，再决定 runtime；已有实体 paper 即使隐藏、折叠或从未展开正文，Global 仍可在软件启动后正常注册；删除/改造最后一张时又能自然回收。这样既不会用隐藏 session 冒充全局生命周期，也不会让无实例插件静态占据全局按钮。

把 Top Bar 与 Workspace 分开也保持了 D-021 的可理解性：Workspace/MCP 共享的是业务数据语义，Top Bar 是 presentation；app-runtime Workspace 仍复用相同 `PaperCommandService`，只是拥有 provider 级生命周期和线程适配。

### Rejected / Do not reintroduce

- 不开放 `AddGlobalTopBar(FrameworkElement)`、任意 Button/WebView 或完整 SVG DOM。
- 不把 Global Top Bar 做成仅凭 manifest/安装状态就永久存在、没有实体 plugin paper owner 的静态 UI。
- 不把 Global action 绑回某张 paper session、Web body 或 Web Mini。
- 不为了维持 Global action 创建隐藏 paper/session；需要启动时产生实体插件实例时使用真实 `startupPaper`。
- 不把 paper 可见性、折叠/展开状态或 body session 是否启动误当成 app-runtime existence truth。
- 不把 Top Bar 方法塞回 `IPaperTodoHostApi` / Workspace 数据合同。
- 不在 Top Bar callback 内复制 Note/Todo 读写、保存、rollback 或 UI reconcile；业务 mutation 继续走 `PaperCommandService`。

### Consequences

- Top Bar action descriptor 是公开协议，需要保持小而稳定；新增复杂控件能力前先判断是否会重新转移宿主 chrome ownership。
- Global action 数量、字符/SVG 输入范围和可隐藏宿主 action 属于宿主保护边界；具体视觉尺寸仍由 PaperTodo 当前主题/布局实现决定。
- `appRuntime` 是显式 opt-in 的 provider 生命周期，但其存在性由实体插件 paper 集合派生；普通插件没有实体 paper 时不会仅因安装而运行。
- 插件没有 Reload/hot-replace UI；修改插件文件后统一重启 PaperTodo，避免同时维护 Web 热重载与 Native CLR 已加载版本两套语义。
- Web body/Mini/app runtime 可以复用底层 request/response transport，但各 surface 的 API scope 必须由宿主来源决定，不能靠页面自己声明身份。
- 旧 1.8 插件继续兼容加载；2.0 Paper/Global Top Bar 和 app runtime 不下放给 1.8。

### Evidence

- `PaperTodo.Plugin.Abstractions/PaperBodyPluginContracts.cs`：Paper action/icon/invocation contract 与 `IPaperTopBarApi`。
- `PaperTodo.Plugin.Abstractions/PaperAppRuntimeContracts.cs`：`IPaperGlobalTopBarApi`、`PaperAppRuntimeContext`、`IPaperAppRuntimeProvider`。
- `src/AppController.PluginStartup.cs`：startupPaper 先于 app-runtime ownership reconcile。
- `src/AppController.PluginAppRuntime.cs`：实体 paper 0↔1 reconcile、provider runtime lifetime 与失败隔离。
- `src/PaperAppRuntimeHostApi.cs`：app-runtime Workspace / GlobalTopBar facade 与 UI Dispatcher 边界。
- `src/AppController.PluginTopBar.cs`：Paper session 与 Global app-runtime 注册分域、输入校验与统一渲染状态。
- `src/PaperWindow.PluginTopBar.cs`：宿主绘制、主题/字体/响应式与 suppression reconcile。
- `src/WebPaperBodySession.TopBar.cs`：Web body 的 Paper document-generation ownership。
- `src/WebPluginAppRuntime.cs`：独立 Web app surface、Global action 与失效清理。
- `plugin-samples/PaperTodo.Plugin.TopBarWeb/`：body Paper action + `runtime.html` Global action、字符/Stroke SVG、点击后复用 Workspace 的当前示例。

---

## D-023 — Lightweight Prewarm 保留一次性首用预热

**Status:** Accepted

### Context

V3 Lite 收敛后，`EdgeCapsuleQueueCompositionProxy.PrewarmLightweight` 仍会在启动 idle 阶段创建并释放一组临时 WPF HWND / DComp surface / visual，用于提前支付首次真实 Edge hover / queue composition 的一部分系统首用成本。因为这段代码本身看起来不“轻”，后续性能审查容易反复把它当成尚未验证的删除候选。

### Decision

保留当前 **Lightweight、一次性、启动 idle 后执行** 的预热。此前已经做过实际 A/B，对首次交互有可观察收益；没有新的可复现实测反证时，不因为它会临时创建 HWND/DComp 资源而重复要求同一轮“是否需要 A/B / 是否直接删除”的验证。

这次整理没有在仓库中找回当时的原始数字，因此这里只记录已经确认的结论与边界，不补造延迟、CPU 或 DWM 数据。若以后找回原始记录，可以补充 Evidence。

### Consequences / Boundaries

- 预热不取得 presentation ownership；WPF/bounded host 继续拥有 shape/size/presentation，DComp 仍只承担 live HWND surface translation/handoff。
- 不因为“预热有效”重新引入 bitmap snapshot、clip/scale/effect resize、Reveal/Conceal、第二套 presentation state 或长期 warm pool。
- 如果未来预热实现明显扩张、Windows/DWM 行为改变，或出现新的可复现实测回归，可以重新 benchmark。

### Evidence

- `src/App.EdgeCapsuleComposition.cs`：启动 idle 条件与预热调度。
- `src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs`：Lightweight Prewarm 本体。
- D-007～D-010：bounded live host、WPF owns shape、DComp translation-only 与 handoff authority 的长期架构边界。
- PR #137：4.0 slim-down review 再次暴露了该实测结论此前没有进入长期知识 owner。


---

## D-024 — Web `backgroundUpdates` 使用 per-Paper Runtime，不借 Body WebView 保活

**Status:** Accepted

### Decision

Web provider 声明 `backgroundUpdates` 时必须同时声明 `paperRuntime` 入口。宿主由 `AppController` 按真实 `PaperData.Id` 持有一份独立后台 WebView；它从创建起固定挂在后台 runtime host，不进入 `PaperWindow`，也不因 Paper 隐藏、折叠、Body reload/失败/重建或当前没有 Window 而结束。

`WebPaperBodySession` 只负责完整正文 UI；provider 级 `appRuntime` 继续保持每 provider 0/1 的全局生命周期。Native `backgroundUpdates` 保持现有 body-session 语义，因为它没有 WebView 跨 HWND 的 controller 搬运问题。

### Why

旧实现让同一个 WebView 同时承担前台 UI 和后台 JS runtime。未展示的 WebView 先挂在隐藏 HWND，第一次进入真实 PaperWindow 时又必须 Dispose/Recreate，导致 timer、Promise、WebSocket、closure 和内存状态被 UI 宿主切换误杀。把 runtime 仅移到 `PaperWindow` 仍不完整，因为启动时本来就隐藏的 Paper 可以没有 Window。

### Rejected / Do not reintroduce

- 不把已初始化的 Body WebView 在隐藏 HWND 与 PaperWindow HWND 之间搬运。
- 不用 provider 级 `appRuntime` 模拟多 Paper 实例；每张 Paper 的后台实例必须独立。
- 不让 PaperRuntime lifetime 依赖 `PaperWindow` 是否已经构造。
- 不用保存 JSON 假装能恢复 WebSocket、Promise、timer 或 JS 闭包的连续 runtime。


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
