# PaperTodo 全量审核进度

目标：把当前版本审核到“能解释任何行为、定位任何状态来源、判断任何改动代价”的程度，并尽最大工程可能降低 bug、结构债、性能浪费、交互断点和动画缺口。

本文件是执行清单，不是总结稿。每完成一个阶段，就在这里打勾，并补充证据、结论和遗留风险。没有证据的项目不能打勾。

## 基线

- 审核起点日期：2026-06-19
- 分支：`feature/multi-master-capsule`
- 起点提交：`8e4fab8`
- 当前变更范围相对 `main...HEAD`：21 个文件，2789 insertions / 689 deletions
- 纳入范围：全部 `.cs`、`.xaml`、`.resx`、`.csproj`、`.md`，以及发布相关目录和配置
- 排除范围：`输出/`、`obj/`、缓存、截图、历史临时文件；除非它们影响发布结果

## 打勾规则

- `[ ]` 未开始或证据不足
- `[-]` 正在进行
- `[x]` 已完成，且有当前状态证据
- `[!]` 发现问题，需要修复或明确接受风险

任何 “完成” 都必须包含至少一种证据：文件行号、命令输出、测试结果、手测路径、差异核对、资源核对、构建结果或明确的代码推演。

## 总进度

- [x] 创建全量审核进度文档
  - 证据：本文件已加入仓库根目录
- [x] 阶段 0：冻结基线与审核边界
- [x] 阶段 1：建立系统地图
- [x] 阶段 2：逐文件深读
- [x] 阶段 3：跨模块不变量审查
- [ ] 阶段 4：高风险专项攻击
- [ ] 阶段 5：性能审查
- [ ] 阶段 6：交互、视觉、动画审查
- [ ] 阶段 7：修复循环
- [ ] 阶段 8：回归矩阵
- [ ] 阶段 9：加载用户蒸馏层做最终产品复核
- [ ] 阶段 10：发布判断和最终报告

## 阶段 0：冻结基线与审核边界

- [x] 记录当前分支和起点提交
  - 证据：`git rev-parse --abbrev-ref HEAD` -> `feature/multi-master-capsule`；`git rev-parse --short HEAD` -> `8e4fab8`
- [x] 记录相对 main 的变更规模
  - 证据：`git diff --stat main...HEAD` -> 17 files changed, 1965 insertions(+), 665 deletions(-)
- [x] 记录当前审核文件集合规模
  - 证据：`rg --files -g "*.cs" -g "*.xaml" -g "*.resx" -g "*.csproj" -g "*.md"` -> 40 个文件，其中 `.cs` 29 个
- [x] 保存完整审核文件清单
  - 证据：见下方“审核文件清单”
- [x] 完成职责草图
  - 证据：见“阶段 1：系统地图”的第一版职责图；后续逐文件深读会修正它
- [x] 运行并记录基线构建
  - 证据：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error，输出 `输出\PaperTodo-v2.0\PaperTodo.dll`
- [x] 运行并记录空白 / 格式检查
  - 证据：`git diff --check` -> 无输出
- [x] 运行并记录资源 key parity
  - 证据：`ko missing=0 extra=0`，`en missing=0 extra=0`，`ja missing=0 extra=0`
- [x] 明确发布产物与版本号状态
  - 证据：`PaperTodo.csproj` -> `TargetFramework=net10.0-windows`，`Version=2.0`，`AssemblyVersion=2.0.0.0`，`FileVersion=2.0.0.0`，`InformationalVersion=2.0`，`OutputPath=输出\PaperTodo-v$(Version)\`

### 审核文件清单

- `AGENTS.md`
- `AnimationHelper.cs`
- `App.xaml`
- `App.xaml.cs`
- `AppController.cs`
- `AppController.Settings.cs`
- `AppController.Tray.cs`
- `CHANGELOG.md`
- `ClipboardHelper.cs`
- `DeepCapsuleLayout.cs`
- `FullscreenForegroundWindowDetector.cs`
- `MarkdownTextBox.cs`
- `MasterCapsuleWindow.cs`
- `Models.cs`
- `NoteTypography.cs`
- `PaperTitles.cs`
- `PaperTodo.csproj`
- `PaperWindow.Capsule.cs`
- `PaperWindow.cs`
- `PaperWindow.DeepCapsule.cs`
- `PaperWindow.Native.cs`
- `PaperWindow.Note.cs`
- `PaperWindow.Todo.cs`
- `README.en.md`
- `README.md`
- `Resources/Strings.en.resx`
- `Resources/Strings.ja.resx`
- `Resources/Strings.ko.resx`
- `Resources/Strings.resx`
- `SingleInstanceHelper.cs`
- `StartupCommand.cs`
- `StateStore.cs`
- `Strings.cs`
- `SystemSettingsHelper.cs`
- `Theme.cs`
- `TodoTextBox.cs`
- `ToolTipPreferences.cs`
- `WindowNative.cs`
- `WindowWorkAreaHelper.cs`
- `md-sample.md`

## 阶段 1：系统地图

目标：先理解系统，不急着修 bug。每个模块要能回答“谁拥有状态、谁能改变它、什么时候落盘、谁会恢复它”。

- [x] 启动、单实例、启动命令
  - 文件：`App.xaml.cs`、`SingleInstanceHelper.cs`、`StartupCommand.cs`
- [x] 数据模型、保存、加载、崩溃恢复
  - 文件：`Models.cs`、`StateStore.cs`、`App.xaml.cs`
- [x] AppController 总调度
  - 文件：`AppController.cs`
- [x] 设置、主题、资源刷新
  - 文件：`AppController.Settings.cs`、`Theme.cs`、`ToolTipPreferences.cs`、`Strings.cs`
- [x] 托盘和菜单
  - 文件：`AppController.Tray.cs`
- [x] 普通纸片窗口生命周期
  - 文件：`PaperWindow.cs`
- [x] 普通胶囊
  - 文件：`PaperWindow.Capsule.cs`
- [x] 贴边胶囊、多队列、多屏、DPI
  - 文件：`PaperWindow.DeepCapsule.cs`、`DeepCapsuleLayout.cs`、`MasterCapsuleWindow.cs`、`WindowWorkAreaHelper.cs`、`WindowNative.cs`、`PaperWindow.Native.cs`
- [x] 待办编辑、拖拽、撤销、关联笔记
  - 文件：`PaperWindow.Todo.cs`、`TodoTextBox.cs`
- [x] 笔记、Markdown、外部打开
  - 文件：`PaperWindow.Note.cs`、`MarkdownTextBox.cs`、`NoteTypography.cs`
- [x] 全屏避让和 topmost
  - 文件：`FullscreenForegroundWindowDetector.cs`、`WindowNative.cs`
- [x] 发布、更新日志、项目配置
  - 文件：`PaperTodo.csproj`、`CHANGELOG.md`、`README.md`、`README.en.md`、`AGENTS.md`

### 系统地图第一版

这张图是第一版职责草图，目标是建立“状态归属和调用方向”。它不是逐文件深读结论，不能替代阶段 2。

| 模块 | 主要文件 | 当前理解 | 状态归属 / 写入点 | 审核重点 |
| --- | --- | --- | --- | --- |
| 启动与单实例 | `App.xaml.cs`、`StartupCommand.cs`、`SingleInstanceHelper.cs` | 解析启动参数；主实例持有 Mutex；后续实例通过 named pipe 转发参数后退出。 | `_singleInstance` 属于 `App`；命令最终进入 `AppController.ExecuteStartupCommand()`。 | `exit/quit`、无参数二次启动、Mutex 释放、异常启动不覆盖数据。 |
| 数据协议与保存 | `Models.cs`、`StateStore.cs` | `AppState` / `PaperData` / `PaperItem` 是 `data.json` 协议；`StateStore` 负责加载、规范化、同步/异步写入、backup。 | `StateStore.Normalize()` 会修正 live state；`AppController.SaveNow()` 生成版本化 JSON。 | 坏数据不覆盖、未知字段兼容、旧异步保存不能覆盖新状态、字段迁移不能破坏旧数据。 |
| 控制器总调度 | `AppController.cs` | 应用状态、所有纸片窗口、托盘、保存 timer、topmost timer、多主胶囊窗口都由控制器协调。 | `State`、`_windows`、`_masterCapsules`、`_saveTimer`、`_visibilityAnimationVersions`。 | 状态变更后是否刷新 UI / 保存 / 重排；隐藏、折叠、删除语义是否清楚。 |
| 设置与主题 | `AppController.Settings.cs`、`Theme.cs`、`ToolTipPreferences.cs` | 设置页直接修改 `State`，再通知窗口、托盘、主题资源或胶囊重排。 | `State.Theme`、`ColorScheme`、功能开关、胶囊模式开关。 | 关闭模式清理状态、资源动态刷新、说明 tooltip 不被普通 tooltip 开关误关。 |
| 托盘 | `AppController.Tray.cs` | Hardcodet `TaskbarIcon` + 自绘 WPF `ContextMenu`；菜单打开时重建；纸片行支持显隐和删除确认。 | `_trayIcon`、`_trayMenu`、行内局部 confirm/suppress 状态。 | 首次点击、菜单焦点、行内按钮事件顺序、菜单重建时状态丢失。 |
| 纸片主窗口 | `PaperWindow.cs` | 单个纸片的 WPF Window，承载标题栏、主体、胶囊 shell、拖拽层、topmost、主题和几何保存。 | `_paper` 是持久数据；大量 `_deepCapsule*` / `_todo*` 是窗口瞬态状态。 | `SuppressGeometrySave`、关闭即隐藏、动画完成回调、窗口激活/topmost。 |
| 普通胶囊 | `PaperWindow.Capsule.cs` | 普通折叠胶囊 UI 和折叠/展开动画；不一定贴边。 | `_paper.IsCollapsed`、窗口 `Width/Height`、transition progress。 | 折叠时保存几何、展开恢复尺寸、不可胶囊时自动展开。 |
| 贴边胶囊与多队列 | `PaperWindow.DeepCapsule.cs`、`DeepCapsuleLayout.cs`、`MasterCapsuleWindow.cs`、`WindowWorkAreaHelper.cs`、`WindowNative.cs` | 贴边胶囊使用独立 slot-host window；队列按 `(monitor, edge)` 分组；每队列一个 master；拖单个胶囊跨边/跨屏。 | `PaperData.CapsuleSide`、`CapsuleMonitorDeviceName`、`State.CapsuleCollapseAllActiveQueues`、`State.DeepCapsuleQueueStartTopMargins`；窗口瞬态 `SlotState/VisualState/GestureState/OpenOrigin`。 | 最高风险：几何、动画、隐藏状态、持久化状态混用；多屏 DPI；slot 清理；collapse-all per-queue。 |
| 待办 | `PaperWindow.Todo.cs`、`TodoTextBox.cs` | 待办行 UI、输入、粘贴、拖拽排序、撤销/重做、关联笔记入口。 | `_paper.Items` 持久；`_undoStack` / `_redoStack` / `_todoDrag` 瞬态。 | 多行粘贴单次撤销、拖拽结束清理、关联笔记影响胶囊资格。 |
| 笔记与 Markdown | `PaperWindow.Note.cs`、`MarkdownTextBox.cs`、`NoteTypography.cs` | 笔记共用一个 `MarkdownTextBox`，在编辑/预览间切换；支持轻量 Markdown 和部分 inline HTML；外部打开写临时文件。 | `_paper.Content`、`_paper.TextZoom` 持久；`_noteBox`、`_showNotePreview` 瞬态。 | 大文本保护、滚动/选区保持、外部后缀合法性、预览点击链接。 |
| 全屏避让 / topmost | `FullscreenForegroundWindowDetector.cs`、`WindowNative.cs`、`AppController.cs` | 定时检查外部全屏窗口，必要时让纸片和胶囊退出 topmost 或插到避让窗口后。 | `_suppressTopmostForFullscreenForeground`、`_fullscreenAvoidanceWindow`。 | 200ms timer 成本、全屏误判、恢复 topmost、slot-host 和 master 一致刷新。 |
| 资源 / 发布 | `Resources/*.resx`、`PaperTodo.csproj`、`CHANGELOG.md`、`README*.md`、`AGENTS.md` | 资源四语言同步；版本号显式维护；changelog 只写用户可见行为。 | `.resx` keys、项目版本、发布输出目录。 | key parity、版本和 changelog 是否一致、发布形态是否符合 no-runtime 单文件要求。 |

### 状态所有权第一版

- `AppState`：持久化应用协议，唯一长期来源是 `data.json`；由 `AppController.State` 持有。
- `PaperData`：单纸片持久状态，包括普通窗口几何、可见性、折叠、文本、待办、胶囊队列归属。
- `PaperItem`：待办项协议，包括文本、完成状态、顺序、关联笔记 id。
- `PaperWindow` 瞬态状态：动画、slot host、拖拽、标题编辑、撤销栈、note preview，原则上不能直接成为 `data.json` 协议。
- `MasterCapsuleWindow` 瞬态状态：每队列主胶囊 UI、hover、拖动中状态；持久结果只应通过 `State.DeepCapsuleQueueStartTopMargins` 和 `State.CapsuleCollapseAllActiveQueues` 表达。
- `WindowWorkAreaHelper` / `DeepCapsuleLayout`：几何计算工具；不能拥有业务状态，除兼容旧静态 anchor 外应尽量用显式 `(monitor, edge)` 输入。

## 阶段 2：逐文件深读

每个文件记录：职责、写入状态、读取状态、外部依赖、不变量、异常路径、性能热点、动画/视觉责任、发现问题、排除问题。

- [x] `Models.cs`
- [x] `StateStore.cs`
- [x] `App.xaml.cs`
- [x] `SingleInstanceHelper.cs`
- [x] `StartupCommand.cs`
- [x] `AppController.cs`
- [x] `AppController.Settings.cs`
- [x] `AppController.Tray.cs`
- [x] `PaperWindow.cs`
- [x] `PaperWindow.Capsule.cs`
- [x] `PaperWindow.DeepCapsule.cs`
- [x] `PaperWindow.Native.cs`
- [x] `MasterCapsuleWindow.cs`
- [x] `DeepCapsuleLayout.cs`
- [x] `WindowWorkAreaHelper.cs`
- [x] `WindowNative.cs`
- [x] `PaperWindow.Todo.cs`
- [x] `TodoTextBox.cs`
- [x] `PaperWindow.Note.cs`
- [x] `MarkdownTextBox.cs`
- [x] `NoteTypography.cs`
- [x] `PaperTitles.cs`
- [x] `FullscreenForegroundWindowDetector.cs`
- [x] `Theme.cs`
- [x] `ToolTipPreferences.cs`
- [x] `SystemSettingsHelper.cs`
- [x] `Strings.cs`
- [x] `ClipboardHelper.cs`
- [x] `AnimationHelper.cs`
- [x] `App.xaml`
- [x] `Resources/*.resx`
- [x] `PaperTodo.csproj`
- [x] `CHANGELOG.md`
- [x] `README*.md`
- [x] `AGENTS.md`

### 逐文件深读记录

#### `Models.cs`

- 职责：定义 `data.json` 协议层状态、纸片、待办项，以及模式 / 尺寸 / 后缀等轻量枚举规范化。
- 持久状态证据：`AppState` 在 `Models.cs:130` 开始；多队列收起状态 `CapsuleCollapseAllActiveQueues` 在 `Models.cs:150`；per-queue 起始高度 `DeepCapsuleQueueStartTopMargins` 在 `Models.cs:157`；单纸片队列归属在 `PaperData.CapsuleSide` / `CapsuleMonitorDeviceName`，见 `Models.cs:193`。
- 协议兼容证据：旧的 `ShowTopBarNewPaperButtons` 通过 nullable + `JsonIgnore` 保留迁移入口，见 `Models.cs:170`；外部 Markdown 后缀只做文件名合法性校验，不限制业务含义，见 `Models.cs:40`。
- 结论：本轮未发现模型层字段默认值和 AGENTS 约束直接冲突；后续跨模块要继续验证普通几何与胶囊几何是否混写。

#### `StartupCommand.cs`

- 职责：把启动参数规整为 `show/hide/toggle/new-todo/new-note/exit` 等命令。
- 证据：空参数默认值由调用方传入，见 `StartupCommand.cs:25`；二次实例可把空参数解释为 `Show`，对应 `App.xaml.cs:69`；`CreatesPaper` 只覆盖 `NewTodo/NewNote`，见 `StartupCommand.cs:23`。
- 结论：命令解析本身边界清楚；`exit/quit` 是否不创建默认纸片由 `App.xaml.cs` 和 `AppController.Exit()` 继续承担。

#### `SingleInstanceHelper.cs`

- 职责：主实例 Mutex、后续实例 named pipe 转发、主实例监听。
- Mutex 证据：`TryAcquire()` 只在 `createdNew` 时设置 `_ownsMutex`，见 `SingleInstanceHelper.cs:36`；`Dispose()` 只在 `_ownsMutex` 为真时 `ReleaseMutex()`，见 `SingleInstanceHelper.cs:176`。
- 转发证据：后续实例用 Base64 JSON 写 pipe，见 `SingleInstanceHelper.cs:53`、`SingleInstanceHelper.cs:135`；主实例监听 pipe 后回调命令，见 `SingleInstanceHelper.cs:90`。
- 结论：符合“只有主实例释放 Mutex；后续实例转发后退出”的项目约束。未发现二次实例释放主锁的路径。

#### `App.xaml.cs`

- 职责：WPF 启动入口、单实例分流、启动失败处理、全局异常恢复。
- 单实例证据：拿不到 Mutex 时只 `SignalPrimaryInstance(e.Args)` 后退出，见 `App.xaml.cs:22`；主实例 listener 对空参数使用 `StartupCommandKind.Show`，见 `App.xaml.cs:65`。
- 启动命令证据：首实例 `exit` 在 `Start()` 之前执行，见 `App.xaml.cs:55`；普通启动只有非建纸命令才 `createDefaultPaper=true`，见 `App.xaml.cs:62`。
- 崩溃恢复证据：全局异常写 `PaperTodo.crash.log` 并尝试保存 `data.crash_recovery.json`，见 `App.xaml.cs:89`。
- 结论：启动入口符合单实例和 `exit` 不创建默认纸片的方向；数据恢复文件是否会被后续保存破坏，见 A001。

#### `StateStore.cs`

- 职责：加载、规范化、序列化、同步 / 异步写入、backup。
- 加载证据：主文件存在时先读 `data.json`，失败后尝试 `data.backup.json`，见 `StateStore.cs:25`；主 / 备都失败才抛出，见 `StateStore.cs:73`。
- 保存证据：`_writeLock` + `_latestWrittenVersion` 防止旧异步保存覆盖新保存，见 `StateStore.cs:79`、`StateStore.cs:88`、`StateStore.cs:106`；写入前会把当前 `data.json` 复制到 `data.backup.json`，见 `StateStore.cs:137`。
- 规范化证据：关闭胶囊 / 贴边 / 收起全部时清理 per-queue 起始高度，见 `StateStore.cs:225`；关联笔记失效时清空 `LinkedNoteId`，见 `StateStore.cs:321`；隐藏已关联笔记时强制取消其折叠，见 `StateStore.cs:330`。
- 发现并修复：A001。若主 `data.json` 解析失败但 backup 能加载，旧逻辑后续第一次保存会先把解析失败的主文件复制覆盖 `data.backup.json`，再用从 backup 规范化后的状态覆盖 `data.json`。已改为记录 backup 恢复态，首次保存前复制保留失败主文件和本次使用的 backup，并跳过这一次 backup 轮换；证据见 `StateStore.cs:24`、`StateStore.cs:72`、`StateStore.cs:146`、`StateStore.cs:170`。
- 发现并修复：A004。旧全局贴边起始高度现在按 `state.DeepCapsuleMonitorDeviceName` 对应的 work area 规范化，避免多显示器旧配置在加载时被默认主屏提前夹值；证据见 `StateStore.cs:300`、`StateStore.cs:446`。

#### `App.xaml`

- 职责：应用级滚动条 / ScrollViewer 样式。
- 证据：只定义资源和控件模板，不持有业务状态；`PaperScrollThumbStyle` 在 `App.xaml:10`，全局 `ScrollBar` 样式在 `App.xaml:34`，全局 `ScrollViewer` 模板在 `App.xaml:84`。
- 结论：本轮未发现它参与数据、胶囊、托盘或启动状态；视觉细节留到阶段 6。

#### `AppController.cs`

- 已读范围：启动命令分发、纸片创建、显示 / 隐藏 / 删除、关联笔记资格刷新、普通几何保存、贴边队列重排、收起全部每队列状态、全屏置顶避让、保存入口 / 失败 UI、离屏救援、队列高度持久化、退出释放。
- 启动命令证据：`ExecuteStartupCommand()` 在 `AppController.cs:596`，`NewTodo/NewNote` 只创建纸片，`Exit` 直接走 `Exit()`；普通启动默认纸片创建在 `AppController.cs:93`。
- 纸片创建证据：`CreatePaper()` 限制最多 100 张，初始化标题、几何、可见性和队列归属，见 `AppController.cs:132`；新纸片会继承来源纸片队列或全局队列，见 `AppController.cs:208`；显示前会避开贴边胶囊栏，见 `AppController.cs:218`。
- 显示 / 隐藏证据：`ShowPaper()` 会在不可胶囊时取消折叠、设置 `IsVisible=true`、取消旧动画、按是否贴边折叠选择主窗口或 slot，见 `AppController.cs:641`；`HidePaper()` 会先 `IsVisible=false`，从贴边栈分离，隐藏后把折叠态清掉，见 `AppController.cs:882`。
- 隐藏全部证据：`HideAllPapers()` 先清所有 `IsVisible`，逐窗口 `DetachFromDeepCapsuleStack()` 并 `SetCollapsedState(false)`，最后清所有 `IsCollapsed`，见 `AppController.cs:961`。这符合“隐藏、折叠语义分开；隐藏全部要清理 slot”的约束。
- 删除证据：`DeletePaper()` 关闭窗口、从 `State.Papers` 移除，删除笔记时清理待办链接，最后重排胶囊并保存，见 `AppController.cs:988`；`ClearTodoLinksToNote()` 后会刷新待办行和胶囊资格，见 `AppController.cs:1023`。
- 普通几何证据：`UpdateGeometry()` 遇到 `PaperWindow.SuppressGeometrySave` 直接返回，折叠时不写 `Width/Height`，见 `AppController.cs:1270`。此片段未发现把贴边半隐藏尺寸写回普通几何的直接路径，仍需继续交叉审 `PaperWindow.*`。
- 收起全部证据：队列 key 由 `(monitorDeviceName, side)` 组成，见 `AppController.cs:1295`；`ToggleCapsuleCollapseAllActive()` 只切换当前 queue key 的 `CapsuleCollapseAllActiveQueues`，见 `AppController.cs:1500`；`MigrateLegacyCollapseAllActiveQueues()` 只在旧全局 active 且没有任一 live queue entry 时迁移，见 `AppController.cs:1305`。本片段未发现“点击一个主胶囊收起所有队列”的当前直接路径。
- 关联笔记资格证据：`RefreshCapsuleEligibilityForLinkedNotes()` 自己只刷新 UI / 布局，不保存；设置调用方随后 `SaveNow()`，待办链接 / 解绑调用方先 `MarkDirty()`，见 `AppController.Settings.cs:1118`、`PaperWindow.Todo.cs:1017`。
- 全屏 / 置顶证据：`RefreshTopmostForForegroundWindow()` 每 200ms 运行但全局扫描限频到 1 秒，并把 `_suppressTopmostForFullscreenForeground` / `_fullscreenAvoidanceWindow` 同步到所有纸片窗口和主胶囊，见 `AppController.cs:775`、`AppController.cs:785`、`AppController.cs:811`；普通浮动层级恢复走 `RefreshFloatingSurfaceZOrder()`，见 `AppController.cs:868`。
- 贴边队列布局证据：`ArrangeDeepCapsules()` 按 `(monitor, edge)` 分组，slot index 在每个队列内独立递增，主胶囊占 slot 0 时用 `visualOffset` 推后真实纸片，见 `AppController.cs:1341`、`AppController.cs:1360`、`AppController.cs:1382`；主胶囊创建 / 更新 / 清理由 `SyncMasterCapsules()` 和 `DestroyAllMasterCapsules()` 收口，见 `AppController.cs:1425`、`AppController.cs:1478`。
- 拖拽队列证据：队内排序只重排当前 queue 的成员，不动其他纸片位置，见 `AppController.cs:1136`；跨队列移动只改当前纸片的 `CapsuleSide` / `CapsuleMonitorDeviceName`，并按 drop 高度插入目标队列，见 `AppController.cs:1191`、`AppController.cs:1223`。
- 保存证据：`MarkDirty()` 只重启 450ms timer，退出和 `_suppressDirty` 期间不排队保存，见 `AppController.cs:1598`；`SaveNow()` 先序列化当前状态，再用 `_saveVersion` 交给 `StateStore` 异步 / 同步保存，失败统一进 `HandleSaveFailure()`，见 `AppController.cs:1609`、`AppController.cs:1637`、`AppController.cs:1686`。
- 离屏救援证据：启动 / 显示 / 创建都会走 `EnsurePapersOnScreen()` 或 `RescuePaperIfOffScreen()`，先按目标 work area 夹尺寸，再夹坐标，仍不可用时按偏移重新放入工作区，见 `AppController.cs:109`、`AppController.cs:171`、`AppController.cs:650`、`AppController.cs:1830`、`AppController.cs:1841`、`AppController.cs:1890`。
- 队列高度证据：`SetDeepCapsuleStartTopMargin()` 对单个 `(monitor, edge)` key 写 `DeepCapsuleQueueStartTopMargins`，拖动中只重排并 `MarkDirty()`，释放时 `SaveNow()`；未再写全局高度污染其他队列，见 `AppController.cs:2004`、`AppController.cs:2021`、`AppController.cs:2031`。
- 退出 / 释放证据：`Exit()` 先置 `_isExiting`、停止保存 timer、关闭设置 / 菜单、同步保存，再释放托盘和关闭所有窗口，见 `AppController.cs:2043`；`Dispose()` 解绑系统主题事件、停止 timer、清 slot reservation、关闭 master 和托盘，见 `AppController.cs:2068`。
- 代码质量处理：删除 `SetDeepCapsuleStartTopMargin()` 前残留的旧全局队列注释，避免把 per-queue 行为误读为全局 stack 行为。纯注释清理，不写 `CHANGELOG.md`。
- 结论：本文件逐段深读完成。未发现新的保存覆盖、普通几何 / 胶囊几何混写、单个主胶囊收起所有队列、全屏避让 200ms 全量扫描、退出不同步保存或 slot / master 释放遗漏的直接问题；跨模块不变量仍进入阶段 3 做组合验证。

#### `PaperWindow.DeepCapsule.cs`

- 已读范围：slot host 创建、拖拽入口、跨队列拖拽视觉、混合 DPI 坐标、滑出 / 滑回横向动画、drop 结束路径。
- slot host 证据：贴边胶囊不再由单独 `DeepCapsuleSlotWindow.cs` 维护，而是在 `PaperWindow` 内部 `EnsureDeepCapsuleSlotHost()` 创建透明 host，见 `PaperWindow.DeepCapsule.cs:38`；拖拽缩放层 `_deepCapsuleSlotDragScale` 在 host root 上，见 `PaperWindow.DeepCapsule.cs:45`。
- 拖拽入口证据：左侧可点击区域按下后进入 `PendingClick` 并捕获鼠标，见 `PaperWindow.DeepCapsule.cs:168`；移动时若可排序，会先进入 reorder drag，见 `PaperWindow.DeepCapsule.cs:175`；开始拖动阈值使用系统最小拖动距离 + 额外 4 DIP，常量见 `PaperWindow.cs:191`。
- 磁吸 / 跨队列证据：`StartDeepCapsuleReorderDrag()` 会先把 host 锁回当前边缘 `_deepCapsuleDragLeft`，见 `PaperWindow.DeepCapsule.cs:1799`；`UpdateDeepCapsuleReorderDrag()` 在未解锁前只改 Top，不改 Left，见 `PaperWindow.DeepCapsule.cs:1831`；`ShouldUnlockDeepCapsuleCrossQueueDrag()` 只有向外拖超过 `DeepCapsuleCrossQueueDragUnlockDistance=56` 才解锁跨队列，见 `PaperWindow.DeepCapsule.cs:1953`、`PaperWindow.cs:192`。
- 拖拽视觉证据：跨队列拖拽宽度由“左 padding + 图标 + 标题 + 右 padding + chrome”计算，并取不小于普通胶囊宽度，见 `PaperWindow.DeepCapsule.cs:1013`；进入跨队列视觉时关闭区透明且不可命中，见 `PaperWindow.DeepCapsule.cs:1039`、`PaperWindow.DeepCapsule.cs:1870`。这符合“拖拽态是去掉关闭区的正常胶囊，不是纯球”的目标。
- 混合 DPI 证据：拖拽中的起点、当前位置和 drop 点通过 `WindowWorkAreaHelper.DeviceScreenPointToDip()` 从设备像素转 DIP，见 `PaperWindow.DeepCapsule.cs:1794`；drop 监视器用 `MonitorAtDeviceScreenPoint()`，见 `PaperWindow.DeepCapsule.cs:1990`。这与多屏混合 DPI 修复方向一致。
- drop 结束证据：未解锁跨队列时只调用 `ReorderDeepCapsule()`，解锁后根据 drop 点所在监视器和左右半区决定目标队列，见 `PaperWindow.DeepCapsule.cs:1961`；若目标队列未变则仍回退为队内排序。
- 滑出 / 滑回证据：`MoveExpandedDeepCapsuleSlotHost()` 不再独立动画 `Left + Width`，而是每帧固定墙边，按 rounded left/right 差值推导宽度，见 `PaperWindow.DeepCapsule.cs:499`；`ApplyDeepCapsuleSlotHorizontalProgress()` 同样按左右边界舍入后设置 host bounds，见 `PaperWindow.DeepCapsule.cs:1234`。这针对右侧贴边漏白和滑回先变窄问题。
- slot 释放证据：`ClearDeepCapsulePlacement()` 会先关闭跨队列拖拽视觉，再按是否需要动画走 `RetractAndHideDeepCapsuleSlotHost()` 或立即清状态，见 `PaperWindow.DeepCapsule.cs:1683`；释放动画用 `_deepCapsuleSlotMoveGeneration` 拦截过期 Completed 回调，见 `PaperWindow.DeepCapsule.cs:1568`、`PaperWindow.DeepCapsule.cs:1617`；隐藏完成会恢复 host root opacity / hit test，避免残留不可点状态，见 `PaperWindow.DeepCapsule.cs:1641`。
- 右键菜单 guard 证据：贴边 slot 菜单打开时注册 foreground 和低级鼠标 hook，见 `PaperWindow.DeepCapsule.cs:772`；菜单关闭、slot host 真关闭时都会停止 guard，见 `PaperWindow.DeepCapsule.cs:760`、`PaperWindow.DeepCapsule.cs:703`。本片段未发现 hook 永久残留路径。
- expanded reservation 证据：`UpdateDeepCapsuleExpandedSlotMode()` 在关闭贴边 / 隐藏 / 关闭保留槽位时会把 `ExpandedReserved` 拉回 `None` 或触发 `ClearDeepCapsulePlacement()`，见 `PaperWindow.DeepCapsule.cs:1759`；设置调用方随后 `ArrangeDeepCapsules()` 和 `SaveNow()`，见 `AppController.Settings.cs:1185`。
- 左 / 右镜像证据：slot host 位置按纸片自己的 `CapsuleSide` 解析，不依赖全局 anchor，见 `PaperWindow.DeepCapsule.cs:916`；固定布局会按边缘镜像 chrome / shell / outline 的 margin 和 alignment，见 `PaperWindow.DeepCapsule.cs:1100`；内部 close 区和标题区左右交换，见 `PaperWindow.DeepCapsule.cs:1153`。
- 右侧缩回保护证据：`SetDeepCapsuleSlotHostHorizontalBounds()` 对右侧队列按“缩小时先移动再改宽、外扩时先改宽再移动”的顺序操作顶层窗口，降低 Win32 窗口 Left / Width 分离更新导致的漏边风险，见 `PaperWindow.DeepCapsule.cs:952`。
- 关闭区证据：slot close area 点击只调用 `HidePaper(_paper)`，语义是隐藏不是删除，见 `PaperWindow.DeepCapsule.cs:248`；`CloseForReal()` 会先 `CloseExpandedDeepCapsuleSlotHostForReal()`，见 `PaperWindow.cs:608`。
- 结论：本文件逐段深读完成。未在本文件内发现新的持久化状态混写、hook 残留、过期动画回调误清新状态或跨队列拖拽绕过磁吸阈值的问题。仍需在跨模块阶段继续验证它和 `PaperWindow.Capsule.cs` 的普通折叠 / 展开状态机边界。

#### `DeepCapsuleLayout.cs`

- 职责：贴边胶囊 / 主胶囊共享几何常量、标题显示宽度估算、work area 解析、队列显式几何计算、旧全局 anchor 兼容入口。
- 纯几何证据：显式队列方法都接收 `Rect area` / `DeepCapsuleEdge edge`，包括 `DockedLeft(area, visibleWidth, edge)`、`TopForIndex(index, startTopMargin, area, slotCount)`、`MaxStartTopMarginForCount(slotCount, area)` 和 `NormalizeStartTopMargin(value, area, slotCount)`，见 `DeepCapsuleLayout.cs:137`、`DeepCapsuleLayout.cs:144`、`DeepCapsuleLayout.cs:151`、`DeepCapsuleLayout.cs:159`。这些方法不写 `AppState` 或窗口状态。
- 多队列证据：`WorkAreaForQueue(monitorDeviceName)` 用 monitor device name 查工作区，缺失 / 拔屏时回退 `SystemParameters.WorkArea`，见 `DeepCapsuleLayout.cs:117`；`MasterCapsuleWindow`、`PaperWindow.DeepCapsule` 和 `AppController` 的高风险布局路径均调用显式队列方法，见 `MasterCapsuleWindow.cs:468`、`PaperWindow.DeepCapsule.cs:924`、`AppController.cs:1390`。
- 左 / 右镜像证据：`DockedLeft(area, visibleWidth, edge)` 对左侧返回 `area.Left`，右侧返回 `area.Right - visibleWidth`，见 `DeepCapsuleLayout.cs:137`。主胶囊和纸片 slot 都复用该方法，避免左右边缘位置算法分叉。
- 垂直边界证据：`NormalizeStartTopMargin()` 会把 NaN / Infinity 还原为默认值，并 clamp 到 `TopMargin..MaxStartTopMarginForCount()`；`TopForIndex()` 对负 index 归零并用底部最大 top 限制越界，见 `DeepCapsuleLayout.cs:144`、`DeepCapsuleLayout.cs:159`。
- 标题宽度证据：`DisplayWidth()` 按 text element 枚举，CJK / 全角 / 韩文 / 日文等宽字符按 2 计，其他按 1 计，见 `DeepCapsuleLayout.cs:57`、`DeepCapsuleLayout.cs:72`。这只影响胶囊宽度估算，不改变标题文本。
- 兼容观察：旧静态 `Edge` / `MonitorDeviceName` 和无 edge 参数的 `DockedLeft()` / `NormalizeStartTopMargin()` 仍保留；当前搜索结果显示生产布局路径基本已转向显式队列几何，剩余显式使用是 `StateStore.NormalizeDeepCapsuleStartTopMargin()` 对旧全局标量做兼容规范化，见 `DeepCapsuleLayout.cs:95`、`DeepCapsuleLayout.cs:104`、`StateStore.cs:446`。
- 发现并修复：A004。旧全局高度在非主屏旧配置下可能被按主屏提前 clamp，已改为按保存的 `DeepCapsuleMonitorDeviceName` 解析 work area 后规范化，见 `StateStore.cs:448`。
- 结论：本文件逐段深读完成。未发现它持有用户数据协议、写窗口几何、或让多队列高风险路径继续共享单一全局 anchor 的当前问题；A004 修复后，旧全局高度兼容路径也显式使用保存的显示器。

#### `PaperWindow.Capsule.cs`

- 职责：普通胶囊 UI、折叠 / 展开动画、普通胶囊关闭区、胶囊资格变化后的自动展开。
- 胶囊资格证据：`UpdateCapsuleMode()` 和 `RefreshCapsuleEligibility()` 都会在当前纸片已经折叠但不能再显示胶囊时调用恢复逻辑，见 `PaperWindow.Capsule.cs:64`、`PaperWindow.Capsule.cs:80`。
- 发现并修复：A003。旧逻辑在贴边折叠纸片失去胶囊资格时先 `ClearDeepCapsulePlacement()`，再 `SetCollapsedState(false)`；由于贴边休眠时主窗口通常是隐藏的，可能留下 `paper.IsVisible=true` 但无可见主窗口 / slot 的状态。已改为 `RestoreFromCapsuleAfterEligibilityLoss()`：先从贴边 slot 恢复主窗口，再展开，并按贴边边缘对齐；证据见 `PaperWindow.Capsule.cs:96`。
- expanded slot 资格证据：`keepDeepCapsuleSlotReservation` 已加入 `CanDisplayAsCapsule()`，所以失去资格的纸片展开时不会继续保留贴边 expanded slot，见 `PaperWindow.Capsule.cs:469`。
- 普通关闭区证据：普通胶囊关闭区点击调用 `_controller.HidePaper(_paper)`，语义为隐藏而非删除，见 `PaperWindow.Capsule.cs:399`。
- 折叠动画证据：`SetCollapsedState()` 使用 `_collapseTransitionGeneration` 防止旧动画 Completed 覆盖新状态，见 `PaperWindow.Capsule.cs:456`、`PaperWindow.Capsule.cs:633`；折叠完成后如果是贴边模式，会 `ArrangeDeepCapsules()`、重置 `OpenOrigin` 并隐藏主窗口，见 `PaperWindow.Capsule.cs:690`。
- 几何证据：折叠 / 展开期间 `_isApplyingCollapsedState=true`，`SaveGeometryIfAllowed()` 会跳过保存；动画完成后 `UpdateGeometry()` 只在非折叠状态保存宽高，跨文件证据见 `PaperWindow.cs:2129`、`AppController.cs:1270`。
- 验证：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

#### `PaperWindow.cs`

- 职责：单纸片主窗口、共享视觉资源、主窗口关闭语义、标题栏、上下文菜单、标题编辑、删除确认、笔记拖到待办入口、几何保存、topmost 刷新、折叠动画依赖属性。
- 状态边界证据：窗口持有 `_paper` 和 `_controller`，同时集中声明瞬态 UI / 动画 / 拖拽状态，见 `PaperWindow.cs:49`；深胶囊状态机文档化在 `PaperWindow.cs:228`，说明 `SlotState / VisualState / GestureState / OpenOrigin` 是窗口瞬态轴。
- 生命周期证据：构造函数注册 `Loaded / LocationChanged / SizeChanged` 到 `SaveGeometryIfAllowed()`，并注册关闭、拖拽取消、激活刷新等事件，见 `PaperWindow.cs:559`；`CloseForReal()` 先关闭贴边 slot host，再设置 `_closeForReal` 真关闭，见 `PaperWindow.cs:608`。
- 关闭语义证据：`OnClosing()` 默认 `e.Cancel=true` 并调用 `_controller.HidePaper(_paper)`，只有 `_closeForReal` 才真实关闭窗口，见 `PaperWindow.cs:1797`。这符合“关闭是隐藏，不是删除”的约束。
- 几何保存证据：`SaveGeometryIfAllowed()` 在 `_isApplyingCollapsedState` 或 `SuppressGeometrySave` 时跳过，见 `PaperWindow.cs:2129`；`MoveWindowWithoutGeometrySave()` 是窗口内部摆放 / 对齐的统一闸，见 `PaperWindow.cs:2139`。
- topmost 证据：普通窗口 topmost 由 `AlwaysOnTop` 或“胶囊折叠”决定，再叠加全屏避让状态，见 `PaperWindow.cs:1434`；贴边 slot host topmost 走同一套 `WindowNative.ApplyTopmostZOrder()`，见 `PaperWindow.cs:1447`。
- 标题 / 菜单证据：标题编辑只通过 `_controller.UpdatePaperTitle()` 提交，见 `PaperWindow.cs:1536`；菜单里的隐藏 / 删除分别调用 `_controller.HidePaper()` 和 `_controller.DeletePaper()`，见 `PaperWindow.cs:1387`、`PaperWindow.cs:1625`。
- 关联笔记拖拽证据：拖动开始 / 更新 / 结束均委托给 `AppController.BeginNoteLinkDrag()` / `UpdateNoteLinkDrag()` / `EndNoteLinkDrag()`，本文件只负责视觉 ghost 和鼠标捕获，见 `PaperWindow.cs:1119`。
- 主题证据：`UpdateTheme()` 刷新窗口动态资源、主壳画刷动画、标题 / 图标 / note box / todo rows、贴边 slot host theme，见 `PaperWindow.cs:712`。这覆盖 AGENTS 中“主题变化要刷新动态生成控件和 AvalonEdit”的窗口侧要求，托盘 / 设置侧仍在对应文件审。
- 结论：本文件逐段深读完成。未发现关闭 / 删除语义混淆、窗口内部摆放直接写坏普通几何、或绕过控制器修改持久纸片状态的新问题。

#### `PaperWindow.Note.cs`

- 职责：笔记正文 UI 构建、编辑 / 预览切换、Markdown 渲染模式重建、内容保存、链接点击、文本缩放、外部编辑器打开。
- 共用控件证据：`BuildNoteBody()` 创建单个 `MarkdownTextBox`，`ShowPreview()` / `ShowEditor()` 只通过 `SetPreviewMode(true/false)` 切换同一控件的只读、焦点和光标状态，没有拆成两套文本控件，见 `PaperWindow.Note.cs:136`、`PaperWindow.Note.cs:187`、`PaperWindow.Note.cs:199`。这符合 AGENTS 中“笔记编辑态和浏览态共用同一个 MarkdownTextBox”的约束。
- 内容保存证据：`box.TextChanged` 直接写 `_paper.Content = box.Text` 并调用 `_controller.MarkDirty()`，见 `PaperWindow.Note.cs:256`；Markdown 渲染模式重建前会从旧控件取文本、光标和滚动偏移，并写回 `_paper.Content`，见 `PaperWindow.Note.cs:42`。
- 发现并修复：A005。`BuildNoteBody()` 旧写法在对象初始化器里先设置 `Text` 再设置 `MaxLength`，导致超长旧笔记初次加载时绕过 `MarkdownTextBox.OnTextChanged()` 的长度保护。已把 `MaxLength=100000` 放到 `Text` 之前，见 `PaperWindow.Note.cs:142`。
- 预览交互证据：预览态点击链接直接 `Process.Start(... UseShellExecute=true)`，失败被吞掉以免笔记崩溃；非链接点击会按点击点切回编辑并尝试恢复 caret，见 `PaperWindow.Note.cs:213`、`PaperWindow.Note.cs:241`、`PaperWindow.Note.cs:309`。
- 失焦证据：`LostKeyboardFocus` 在菜单打开或预览点击进入编辑的过渡期会跳过，否则回到预览，见 `PaperWindow.Note.cs:288`。窗口层点击正文外退出编辑，见 `PaperWindow.cs:596`。
- 缩放证据：Ctrl+滚轮只对笔记生效，委托 `AppController.SetPaperTextZoom()` 规范化并保存；窗口侧 `UpdateTextZoom()` 保持缩放提示，并在需要时重建正文保留滚动 / 光标，见 `PaperWindow.Note.cs:382`、`PaperWindow.Note.cs:420`、`AppController.cs:338`。
- 外部打开证据：`WriteExternalMarkdownFile()` 使用 `ExternalMarkdownFileExtensions.Normalize()` 后的后缀写入 `%TEMP%\PaperTodo\paper-{id}.{ext}`，UTF-8 无 BOM，再交给系统默认程序打开；失败只弹警告，不影响主程序，见 `PaperWindow.Note.cs:437`、`PaperWindow.Note.cs:491`、`PaperWindow.Note.cs:496`。
- 结论：本文件逐段深读完成。除 A005 外，未发现编辑 / 预览双控件漂移、内容变更不保存、外部打开绕过后缀规范化、链接打开失败导致崩溃或缩放不保存的新问题。

#### `MarkdownTextBox.cs`

- 职责：AvalonEdit 包装控件、Markdown 输入辅助、预览只读模式、轻量 Markdown/inline HTML 解析和渲染、链接命中、粘贴 / 长度保护。
- AvalonEdit 初始化证据：构造函数关闭 AvalonEdit 内置 hyperlink，注册自有背景渲染器、列表 / 分隔线渲染器和行 colorizer，并应用 `NoteTypography` 的字体渲染设置，见 `MarkdownTextBox.cs:32`、`MarkdownTextBox.cs:49`、`MarkdownTextBox.cs:58`。
- 长度保护证据：`MaxLength` 由窗口设为 100000；`OnTextChanged()` 超限时用 `_isTrimmingText` 防重入并截断控件文本 / caret，见 `MarkdownTextBox.cs:69`、`MarkdownTextBox.cs:453`。A005 修复后，初次设置正文时也会经过这条保护。
- 粘贴保护证据：`OnPaste()` 只处理 Unicode 文本；`TryBuildSafePasteText()` 同时受剩余 `MaxLength`、单次 `MaxSafePasteLength=30000`、单行 `MaxSafePasteLineLength=6000` 限制，超限时改写粘贴数据或取消命令，见 `MarkdownTextBox.cs:494`、`MarkdownTextBox.cs:536`。
- 渲染模式证据：`MarkdownRenderOptions.From()` 对 off/basic/enhanced 三种模式分别控制样式、语法淡化、列表 bullet、链接高亮和 block 背景，见 `MarkdownTextBox.cs:703`；`SetPreviewMode()` 只切只读 / focus / cursor，不复制文本，见 `MarkdownTextBox.cs:125`。
- 性能证据：block/list/hr 渲染器和链接命中都只遍历 `TextArea.TextView.VisualLines`，不是每帧扫全文，见 `MarkdownTextBox.cs:407`、`MarkdownTextBox.cs:1571`、`MarkdownTextBox.cs:1656`、`MarkdownTextBox.cs:1766`；fenced code 状态有缓存并在文本变化时清空，见 `MarkdownTextBox.cs:1004`、`MarkdownTextBox.cs:453`。
- Markdown 边界证据：块级识别只覆盖标题、引用、列表、任务列表、围栏代码、分隔线；inline 只覆盖 code、链接、加粗 / 斜体 / 删除线和受支持 HTML inline 标签，见 `MarkdownTextBox.cs:795`、`MarkdownTextBox.cs:1199`、`MarkdownTextBox.cs:1253`、`MarkdownTextBox.cs:1504`。
- 链接安全证据：URL 只接受 `http`、`https`、`mailto`、`www.` 自动补 `https://`，以及 Windows 绝对本地路径 / UNC；设备路径 `\\.\` / `\\?\` 被拒绝，见 `MarkdownTextBox.cs:1062`、`MarkdownTextBox.cs:1128`、`MarkdownTextBox.cs:1183`。
- 主题证据：`RefreshVisualStyle()` 刷新前景、caret、链接颜色、renderer / colorizer 和 text view layer；窗口主题切换会调用它，见 `MarkdownTextBox.cs:150`、`PaperWindow.cs:780`。
- 结论：本文件逐段深读完成。除 A005 的初始加载保护顺序外，未发现渲染器扫全文造成明显性能风险、超出产品边界的块级 HTML / 嵌入内容支持、危险设备路径链接、或预览态复制文本导致滚动 / 选区漂移的新问题。

#### `NoteTypography.cs`

- 职责：笔记字体族、代码字体族、基础字号、语言和 WPF 文本渲染选项。
- 证据：只暴露静态只读字体 / 渲染常量和 `ApplyTextRendering()` helper，见 `NoteTypography.cs:8`、`NoteTypography.cs:27`；不读写 `AppState`、`PaperData`、窗口几何或保存状态。
- 结论：本文件逐段深读完成。它是纯排版工具，未发现持久化、Markdown 解析或交互状态风险。

#### `PaperTitles.cs`

- 职责：纸片标题默认名、用户设置的标题长度规范化、自定义标题清洗、按 text element 截断。
- 状态证据：不直接写状态；调用方在创建、标题编辑、设置长度变化和加载规范化时使用它，见 `AppController.cs:161`、`AppController.cs:323`、`AppController.Settings.cs:391`、`StateStore.cs:347`。
- 边界证据：持久标题硬上限 `MaxTitleLength=40`；用户可配范围 `2..20`，默认 6；`CleanCustomTitle()` 去掉控制字符并按 `StringInfo.ParseCombiningCharacters()` 截断，见 `PaperTitles.cs:7`、`PaperTitles.cs:15`、`PaperTitles.cs:36`。
- 结论：本文件逐段深读完成。未发现标题设置越过硬上限、控制字符写入标题、或修改持久状态绕过控制器的问题。

#### `ClipboardHelper.cs`

- 职责：从系统剪贴板安全读取 Unicode 文本。
- 证据：只在待办多行粘贴路径使用，见 `PaperWindow.Todo.cs:647`；内部通过 `Clipboard.GetDataObject()` 读取 `DataFormats.UnicodeText`，捕获所有剪贴板 / OLE 异常并返回 false，见 `ClipboardHelper.cs:8`。
- 结论：本文件逐段深读完成。未发现剪贴板被其他进程占用时异常冒泡、读取非文本数据或写状态的问题。

#### `ToolTipPreferences.cs`

- 职责：统一普通 tooltip 开关，同时允许设置说明等控件标记 AlwaysEnabled。
- 注册证据：`AppController` 启动时注册 provider，见 `AppController.cs:72`；设置说明图标通过 `SetAlwaysEnabled()` 标记，见 `AppController.Settings.cs:745`；窗口 / 主胶囊刷新 tooltip 时调用 `Apply()`，见 `PaperWindow.cs:617`、`MasterCapsuleWindow.cs:294`。
- 语义证据：`OnToolTipOpening()` 在全局禁用且当前元素不在 AlwaysEnabled 链上时吞掉 opening；`ApplyCore()` 同时遍历 logical tree 和 visual tree，并用 visited set 避免重复 / 环，见 `ToolTipPreferences.cs:48`、`ToolTipPreferences.cs:64`、`ToolTipPreferences.cs:97`。
- 结论：本文件逐段深读完成。未发现普通 tooltip 开关误关设置说明图标、或 visual-only / logical-only 控件未处理导致异常冒泡的问题。

#### `Theme.cs`

- 职责：主题明暗判断、配色族规范化、基色 palette、派生画刷、frozen brush 缓存。
- 状态证据：不直接写 `AppState`；只读取 `AppController.Current?.State?.Theme` 和 `ColorScheme` 计算当前 palette，见 `Theme.cs:62`、`Theme.cs:75`。设置层修改主题 / 配色后调用 `Theme.Invalidate()`，再刷新窗口、master 和托盘，见 `AppController.Settings.cs:22`、`AppController.Settings.cs:55`。
- 缓存证据：`_isDarkCache`、`_schemeCache`、`_paletteCache` 只缓存当前主题解析结果；`Invalidate()` 清三者但不清 `BrushCache`，因为颜色到 frozen brush 是纯映射，见 `Theme.cs:45`、`Theme.cs:52`、`Theme.cs:135`。
- 系统主题证据：`IsSystemDark()` 只读 HKCU Personalize 的 `AppsUseLightTheme`，异常回退浅色；系统主题变化由 `OnUserPreferenceChanged` 调 `Theme.Invalidate()` 并刷新窗口，跨文件证据见 `Theme.cs:92`、`AppController.Settings.cs:989`。
- 派生色证据：hover、危险色、Markdown 语法淡化、checkbox hover 等都从当前 palette 派生，见 `Theme.cs:110`、`Theme.cs:119`、`Theme.cs:122`、`Theme.cs:127`；窗口、托盘、Markdown、主胶囊均通过这些静态入口取画刷。
- 结论：本文件逐段深读完成。未发现主题切换后 palette 缓存不失效、画刷被修改后污染全局、或主题层写持久状态的问题。阶段 6 仍需从视觉角度检查四套配色和动画过渡。

#### `Strings.cs`

- 职责：统一资源字符串读取和格式化。
- 证据：`ResourceManager` 指向 `PaperTodo.Resources.Strings`；`Get()` 按 `CultureInfo.CurrentUICulture` 获取，缺 key 时回退 key 名；`Format()` 用 `CultureInfo.CurrentCulture` 格式化，见 `Strings.cs:7`、`Strings.cs:10`、`Strings.cs:15`。
- 调用证据：托盘、设置、纸片菜单、错误弹窗、Markdown 默认链接文案等所有用户可见字符串都通过 `Strings.Get()` / `Strings.Format()`，代表调用见 `AppController.Tray.cs:416`、`AppController.Settings.cs:561`、`PaperWindow.cs:1391`、`PaperWindow.Note.cs:455`。
- 结论：本文件逐段深读完成。未发现资源缺失会直接崩溃的问题；资源 key parity 和格式占位一致性留到 `Resources/*.resx` 专项继续验证。

#### `Resources/*.resx`

- 职责：中文默认资源和英文 / 日文 / 韩文本地化资源。
- 文件证据：当前资源文件为 `Strings.resx`、`Strings.en.resx`、`Strings.ja.resx`、`Strings.ko.resx`；四个文件各 150 个 key。
- key parity 证据：XML 解析比对结果：`Strings.en.resx` / `Strings.ja.resx` / `Strings.ko.resx` / `Strings.resx` 相对默认资源 `missing=0 extra=0`。
- 占位符证据：对所有资源值提取 `{0}` / `{1}` 等格式占位符后，四语言占位符集合完全一致，`placeholder_mismatches=0`。这覆盖 `Strings.Format()` 的主要运行时风险。
- 代码调用证据：扫描代码中的字面量 `Strings.Get/Format("...")` 得到 `literal_used=114`，相对默认资源 `missing=0`。未被字面量扫描命中的 36 个 key 包含通过 `tipKey` 动态读取的设置说明、`ResourceTextVersion` 人工检查标记和部分保留文案；本轮不做删除，以免制造无意义资源 churn。
- 结论：本文件组逐段审计完成。未发现四语言 key 不一致、格式占位不一致、代码字面量 key 缺失的问题。`ResourceTextVersion` 仍只作为人工标记，不参与运行时逻辑。

#### `PaperTodo.csproj`

- 职责：项目 SDK、目标框架、WPF 开关、显式版本号、输出路径、资源语言和依赖。
- 版本证据：`Version=2.0`、`AssemblyVersion/FileVersion=2.0.0.0`、`InformationalVersion=2.0` 显式维护，见 `PaperTodo.csproj:9`。这符合 AGENTS 中“不要恢复自动递增版本号”的约束。
- WPF / 发布证据：`OutputType=WinExe`、`TargetFramework=net10.0-windows`、`UseWPF=true`；项目文件未设置 `PublishTrimmed` 或 Native AOT，见 `PaperTodo.csproj:3`。no-runtime / 单文件形态由发布命令控制。
- 资源 / 输出证据：`SatelliteResourceLanguages=en;ja;ko`，默认中文资源留在主程序集；`OutputPath` 指向 `输出\PaperTodo-v$(Version)`；`DefaultItemExcludes` 排除输出目录和对比 / 调试目录，见 `PaperTodo.csproj:13`。
- 依赖证据：只引用 `AvalonEdit 6.3.0.90` 和 `Hardcodet.NotifyIcon.Wpf 2.0.1`，见 `PaperTodo.csproj:23`。与当前笔记编辑器和托盘实现一致。
- 结论：本文件逐段审计完成。未发现版本号自动化、WPF trim/AOT 风险、资源语言缺失或新增重型依赖的问题。

#### `CHANGELOG.md`

- 职责：用户态版本记录、计划 / 评估 / Unreleased。
- 结构证据：顶部按 `### 计划 / 待办`、`### 评估`、`### Unreleased` 组织，见 `CHANGELOG.md:5`；本轮 A004 / A005 已以用户可感知语言写入 Unreleased，见 `CHANGELOG.md:19`、`CHANGELOG.md:20`。
- 内容边界证据：Unreleased 不写文件名、状态机或实现细节；内部工具 / 文档审计未写入 changelog。rc2 条目仍保持重点项加粗、普通修复不强行加粗。
- 结论：本文件逐段审计完成。未发现当前 Unreleased 和 AGENTS 的 changelog 约束冲突；后续若再做用户可见修复，仍需追加到 Unreleased。

#### `README*.md`

- 职责：中文 / 英文用户说明、功能边界、操作手册、下载 / 构建说明。
- 产品边界证据：README 明确无账号、无管理器、无分类 / 标签 / 搜索 / 归档 / 同步等，见 `README.md:7`、`README.md:39`；英文版对应说明见 `README.en.md:7`、`README.en.md:38`。
- Markdown 边界证据：README 明确笔记纸不是完整 Markdown 编辑器，只支持轻量 Markdown 和少量单行 inline HTML，不支持图片 / 表格 / 附件 / 嵌入 / 块级 HTML，见 `README.md:90`、`README.md:100`；英文版对应说明见 `README.en.md:88`、`README.en.md:98`。
- 发现并修正文档漂移：README 仍把贴边胶囊描述为只靠右侧 / 右上角，已改为“屏幕边缘、多显示器和左右侧队列”；外部后缀说明也改为“由 Windows 关联程序处理临时文件”，避免暗示应用自身执行代码。
- 构建 / 发布证据：README 的构建命令仍是 `dotnet build -c Release`；下载说明描述 Windows x64 单文件产物和 no-runtime 产物，见 `README.md:180`、`README.md:194`。
- 结论：本文件组逐段审计完成。除上述文档漂移外，未发现 README 承诺账号 / 同步 / 管理器等超出产品边界的能力。

#### `AGENTS.md`

- 职责：记录项目级非显然约束，供后续 agent 工作遵守。
- 约束证据：覆盖产品边界、数据协议、保存 / 单实例、托盘、胶囊高风险、待办 / 笔记、主题 / 资源、changelog 和发布流程，见 `AGENTS.md:9`、`AGENTS.md:17`、`AGENTS.md:27`、`AGENTS.md:33`、`AGENTS.md:36`、`AGENTS.md:49`、`AGENTS.md:57`、`AGENTS.md:63`、`AGENTS.md:77`。
- 当前一致性证据：本轮修复没有改变产品边界、持久化协议字段、单实例规则、托盘图标入口或发布流程；因此无需更新 AGENTS。其新增的 changelog 约束“未提交 / 未发布且用户无感知的内部 bug 不写入更新日志”与当前 Unreleased 写法一致。
- 结论：本文件逐段审计完成。未发现 AGENTS 与当前代码/流程直接冲突；后续如果改变胶囊协议、保存策略或发布流程才需要同步更新。

#### `SystemSettingsHelper.cs`

- 职责：读写 HKCU Run 启动项。
- 证据：设置页启动开关只通过 `IsStartupEnabled()` 和 `ToggleStartup()`，见 `AppController.Settings.cs:579`、`AppController.Settings.cs:1010`；helper 只访问 `Software\Microsoft\Windows\CurrentVersion\Run` 下的 `PaperTodo` 值，见 `SystemSettingsHelper.cs:7`。
- 异常证据：读写注册表均包 try/catch；权限受限时返回 false，设置调用方显示失败提示，见 `SystemSettingsHelper.cs:11`、`SystemSettingsHelper.cs:30`、`AppController.Settings.cs:1013`。
- 结论：本文件逐段深读完成。未发现写 HKLM、写其他启动项、权限异常导致崩溃或启动项关闭误删其他值的问题。

#### `AnimationHelper.cs`

- 职责：共享 easing、RenderTransform 规范化、淡入淡出、缩放、平移、颜色过渡、停止动画和少量强调动画。
- Transform 证据：`EnsureTransform()` 保证 `TransformGroup[0]` 是 `ScaleTransform`、`[1]` 是 `TranslateTransform`，保留已有非 identity transform 追加到后面，见 `AnimationHelper.cs:15`；待办新增 / 删除动画直接依赖这个顺序，见 `PaperWindow.Todo.cs:588`、`PaperWindow.Todo.cs:805`。
- 动画证据：`FadeIn()` / `FadeOut()` / `ScaleTo()` / `TranslateTo()` 都只操作 UIElement 或 transform 动画，不写 `PaperData`；完成回调只挂在 Y 动画或 opacity 动画上，见 `AnimationHelper.cs:57`、`AnimationHelper.cs:68`、`AnimationHelper.cs:79`、`AnimationHelper.cs:91`。
- 停止证据：`StopAllAnimations()` 会取消 opacity、scale、translate 动画；当前全局动画开关的关键胶囊动画多为本地状态机自管，后续阶段 6 还要做“关闭动画后立即完成”的专项验证，见 `AnimationHelper.cs:119`。
- 结论：本文件逐段深读完成。未发现 helper 自己持有状态、保存几何、无限启动动画或覆盖业务状态的问题；动画完整性留到视觉 / 动画阶段做跨模块验证。

#### `FullscreenForegroundWindowDetector.cs`

- 职责：识别外部全屏窗口，给 AppController 的 topmost 避让策略提供目标 hwnd，并生成可选 debug snapshot。
- 快速路径证据：`TryGetFullscreenWindow()` 每次先读取当前 foreground；若不是本进程窗口则记为 `_lastExternalForegroundWindow`，再优先检查该 tracked hwnd，见 `FullscreenForegroundWindowDetector.cs:25`、`FullscreenForegroundWindowDetector.cs:79`。
- 性能证据：全局 `EnumWindows()` 只在调用方允许时发生；`AppController.RefreshTopmostForForegroundWindow()` 200ms timer 中只有距离上次全局扫描超过 1 秒才允许 `allowGlobalScan=true`，见 `FullscreenForegroundWindowDetector.cs:103`、`AppController.cs:775`。
- 候选过滤证据：候选窗口必须非零、不是 shell、不是当前进程、可见且非最小化、不是 tool window、不是 DWM cloaked、不是 shell class，见 `FullscreenForegroundWindowDetector.cs:175`。这避免把 PaperTodo 自己的纸片 / 胶囊窗口和桌面 / 任务栏误判为外部全屏。
- 全屏判定证据：先用 DWM extended frame bounds，失败再用 raw `GetWindowRect()`；矩形必须有足够尺寸并覆盖最近 monitor，容差 2 像素，见 `FullscreenForegroundWindowDetector.cs:132`、`FullscreenForegroundWindowDetector.cs:146`、`FullscreenForegroundWindowDetector.cs:170`。
- AppController 联动证据：检测到外部全屏后写 `_suppressTopmostForFullscreenForeground` 和 `_fullscreenAvoidanceWindow`，逐窗口 / master 调 `RefreshEffectiveTopmost()`；关闭避让或状态不变时仍会刷新浮动层级，见 `AppController.cs:775`、`AppController.cs:811`、`AppController.cs:868`。
- debug 证据：`BuildDebugSnapshot()` 会记录 foreground、last external、screen probe、foreground children 和 top windows；实际写日志受 `EnableFullscreenDebugLog=false` 控制，并限频 / 截断，见 `FullscreenForegroundWindowDetector.cs:41`、`AppController.cs:60`、`AppController.cs:825`。
- 结论：本文件逐段深读完成。未发现 200ms timer 每次全量枚举、当前进程窗口被当成全屏、DWM bounds 失败无 fallback、或 debug 日志影响正常行为的新问题。后续阶段 4/5 仍需用真实全屏窗口手测和性能观察验证误判率。

#### `PaperWindow.Todo.cs`

- 职责：待办 body 构建、行重建、文本输入、勾选、追加 / 删除 / 清除完成项、多行粘贴、拖拽排序 / 拖到垃圾区删除、业务撤销 / 重做、关联笔记投放目标。
- 持久状态证据：本文件只写 `_paper.Items`、`PaperItem.Text`、`PaperItem.Done`、`PaperItem.Order`、`PaperItem.LinkedNoteId`，并在文本、勾选、添加、删除、清除完成、移动、链接 / 解绑后调用 `_controller.MarkDirty()`；代表入口见 `PaperWindow.Todo.cs:333`、`PaperWindow.Todo.cs:369`、`PaperWindow.Todo.cs:756`、`PaperWindow.Todo.cs:778`、`PaperWindow.Todo.cs:846`、`PaperWindow.Todo.cs:1017`、`PaperWindow.Todo.cs:1419`。
- 行重建证据：`RebuildTodoRows()` 先 `NormalizeTodoItems()` / `NormalizeOrders()`，清 `_todoEditors`、`_todoRows`、`_linkedNoteDropRow`，再按 `OrderedItems()` 重建 UI，见 `PaperWindow.Todo.cs:59`；`PaperItem` 默认有新 GUID，加载层也会修复空 / 重复 id，跨文件证据见 `Models.cs:204`、`StateStore.cs:381`。
- 输入 / 撤销证据：文本框 `TextChanged` 直接写回当前 item 并触发防抖保存；`GotFocus` 记录原文，`LostFocus` 在文本变更时把原文快照压入业务撤销栈并清 redo，见 `PaperWindow.Todo.cs:333`、`PaperWindow.Todo.cs:343`、`PaperWindow.Todo.cs:348`。`PushUndoSnapshot()` 会先 `CommitFocusedTextIfNeeded()`，确保勾选 / 删除 / 移动前已有文本变更不会丢失撤销边界，见 `PaperWindow.Todo.cs:1631`、`PaperWindow.Todo.cs:1643`。
- 多行粘贴证据：`HandleTodoPaste()` 只在清洗后行数大于 1 时接管，最多 200 行；它只调用一次 `PushUndoSnapshot()`，第一行替换当前选择，后续行通过 `AddItemAfter(..., pushUndo: false)` 添加，最后一次 `MarkDirty()`，见 `PaperWindow.Todo.cs:645`、`PaperWindow.Todo.cs:673`、`PaperWindow.Todo.cs:681`、`PaperWindow.Todo.cs:723`。这满足“多行粘贴只能形成一次撤销快照”的项目约束。
- 删除 / 清理证据：空行 Backspace 删除会先压一次撤销快照，删除后若列表为空补一个空 item，并用 `_suppressTodoBackspaceUntilKeyUp` 防止按键重复删除，见 `PaperWindow.Todo.cs:600`；`ClearDoneItems()` 同样保证至少保留一个 item，见 `PaperWindow.Todo.cs:846`。
- 拖拽证据：行拖拽只从拖动柄启动并捕获鼠标，见 `PaperWindow.Todo.cs:570`；开始拖拽后创建 ghost、源行半透明、追加区切换为垃圾区，见 `PaperWindow.Todo.cs:1098`；拖拽结束统一释放鼠标捕获、清 ghost、恢复源行透明度 / 背景 / 光标、清 drop indicator 和垃圾区状态，见 `PaperWindow.Todo.cs:1359`。窗口失焦和丢失鼠标捕获会取消拖拽，跨文件证据见 `PaperWindow.cs:580`、`PaperWindow.cs:1812`。
- 拖拽语义证据：悬停追加区时 `DropAtEnd=true` 在结束时调用 `RemoveItem()`，语义是拖到垃圾区删除；否则按最近行边界 `MoveItem()` 排序，见 `PaperWindow.Todo.cs:1170`、`PaperWindow.Todo.cs:1388`、`PaperWindow.Todo.cs:1402`。`MoveItemToEnd()` 当前未被调用，属于死 helper，不影响行为。
- 关联笔记证据：`TryHitTodoRow()` 只在关联功能启用、当前是可见未折叠 todo 时返回命中行，见 `PaperWindow.Todo.cs:959`；`LinkNoteToTodo()` / `UnlinkNoteFromTodoItem()` 会压撤销、改 `LinkedNoteId`、保存、重建行并刷新胶囊资格，见 `PaperWindow.Todo.cs:1017`、`PaperWindow.Todo.cs:1044`；`CloneItems()` 保留 `LinkedNoteId`，见 `PaperWindow.Todo.cs:1619`。
- 动画 / 性能证据：新增、多行粘贴、删除、清除完成的动画都用 `_todoRowsGeneration` 或 `_clearDoneGeneration` 拦截过期回调，见 `PaperWindow.Todo.cs:691`、`PaperWindow.Todo.cs:803`、`PaperWindow.Todo.cs:895`；待办文本变更只触发 `MarkDirty()` 的 timer 防抖保存，跨文件证据见 `AppController.cs:1598`。
- 结论：本文件逐段深读完成。未发现多行粘贴多次业务撤销、拖拽残留 ghost / 半透明源行、关联笔记不刷新胶囊资格、撤销克隆丢失 `LinkedNoteId`、或待办操作直接混写胶囊 / 普通窗口几何的新问题。

#### `TodoTextBox.cs`

- 职责：待办文本框完成态删除线绘制。
- 状态证据：只定义 `IsDone` 依赖属性，metadata 带 `AffectsRender`，见 `TodoTextBox.cs:11`；`OnRender()` 在完成态按当前控件尺寸绘制一条删除线，不写 `_paper`、控制器、保存或撤销状态，见 `TodoTextBox.cs:27`。
- 主题证据：删除线颜色优先取 `Theme.BrightWeakTextBrush` 的实体颜色，失败时有固定 fallback，见 `TodoTextBox.cs:36`。主题切换后待办行会重建 / 刷新，窗口侧证据见 `PaperWindow.cs:712`。
- 结论：本文件逐段深读完成。它是纯显示控件，未发现数据协议、保存、拖拽或胶囊状态风险。

#### `PaperWindow.Native.cs`

- 职责：`PaperWindow` 私有的 Win32 hook / foreground / low-level mouse P/Invoke 声明，主要服务贴边 slot 右键菜单 guard。
- 证据：包含 `SetWinEventHook`、`UnhookWinEvent`、`SetWindowsHookEx`、`UnhookWindowsHookEx`、`CallNextHookEx` 和 `GetWindowThreadProcessId` 声明，见 `PaperWindow.Native.cs:35`；没有业务状态写入，实际生命周期在 `PaperWindow.DeepCapsule.cs` 中开启 / 停止。
- 结论：本文件为声明层；未发现主实例、保存、胶囊状态或用户数据协议风险。

#### `WindowNative.cs`

- 职责：共享 Win32 window style / z-order helper，供纸片窗口、贴边 slot host、主胶囊使用。
- no-activate 证据：`ApplyNoActivateStyle()` 给窗口加 `WS_EX_NOACTIVATE`，避免贴边 slot / 主胶囊点击抢焦点，见 `WindowNative.cs:23`。
- topmost 证据：`ApplyTopmostZOrder()` 使用 `SetWindowPos` 切换 topmost / no-topmost，带 `SWP_NOACTIVATE`，并在退出 topmost 时可插到全屏避让窗口后，见 `WindowNative.cs:37`。
- 结论：本文件不拥有业务状态；当前调用方均先检查窗口 handle / 可见性。未发现会主动激活窗口或绕过全屏避让的路径。

#### `WindowWorkAreaHelper.cs`

- 职责：把窗口 / DIP 矩形 / 设备点解析到显示器工作区，提供多屏和混合 DPI 坐标转换。
- 工作区证据：`WorkAreaFor(Rect)` 把 DIP rect 转设备 rect 后用 `MonitorFromRect()` 找最近显示器，失败回退 `SystemParameters.WorkArea`，见 `WindowWorkAreaHelper.cs:12`；`WorkAreaFor(Window)` 用窗口 handle 找最近显示器，见 `WindowWorkAreaHelper.cs:49`。
- 持久 monitor 证据：`WorkAreaForDevice()` 枚举 monitor device name，找不到时返回 null，让上层回退主屏，见 `WindowWorkAreaHelper.cs:89`。这符合拔掉显示器后 graceful fallback 的目标。
- 混合 DPI 证据：`MonitorAtDeviceScreenPoint()` 接收 `PointToScreen` 的设备像素点直接调用 `MonitorFromPoint()`，再把工作区转系统 DIP，见 `WindowWorkAreaHelper.cs:127`；贴边胶囊跨屏 drop 使用这条路径，见 `PaperWindow.DeepCapsule.cs:1990`。
- 坐标转换证据：`DeviceScreenPointToDip()` 和无 reference 的 `DeviceRectToDip()` 都用 `SystemDpiScale()`，见 `WindowWorkAreaHelper.cs:111`、`WindowWorkAreaHelper.cs:251`；`WorkAreaFor(Window)` 则使用窗口 visual 的 `TransformFromDevice`，见 `WindowWorkAreaHelper.cs:218`。
- 结论：本文件不写业务状态；主要风险是 Windows / WPF DPI 坐标系复杂，后续阶段 4 仍需要混合 DPI 手测验证。代码层未发现对 monitor device name、空显示器或失败路径的未处理异常。

#### `AppController.Settings.cs`

- 职责：设置窗口构建、设置项状态写入、主题 / 资源刷新、胶囊相关模式开关、启动项切换、tooltip 和系统主题变化响应。
- 主题证据：`SetTheme()` / `SetColorScheme()` 修改状态后 `Theme.Invalidate()`、保存、逐窗口 `UpdateTheme()`、逐 master `UpdateTheme()`、重建托盘菜单，见 `AppController.Settings.cs:19`、`AppController.Settings.cs:47`；系统主题变化且当前为 system 时也走同一刷新链，见 `AppController.Settings.cs:989`。
- 渲染 / 外部打开证据：Markdown 渲染模式写状态后刷新每个窗口的 Markdown 显示并重建托盘，见 `AppController.Settings.cs:81`；外部 Markdown 后缀通过 `ExternalMarkdownFileExtensions.Normalize()` 规范化后保存并通知窗口，见 `AppController.Settings.cs:218`。
- tooltip 证据：普通 tooltip 开关写 `State.EnableToolTips` 后刷新所有纸片、master 和设置窗口，见 `AppController.Settings.cs:1031`；设置说明图标用 `ToolTipPreferences.SetAlwaysEnabled(hint, true)`，不会被普通 tooltip 开关关闭，见 `AppController.Settings.cs:714`。
- 胶囊模式证据：关闭普通胶囊模式时同时关闭贴边模式、收起全部、per-queue active，并调用 `ResetDeepCapsuleStartTopMargins()`，再取消所有纸片折叠，见 `AppController.Settings.cs:1059`；关闭贴边模式时同样清收起全部和 per-queue 起始高度，见 `AppController.Settings.cs:1153`。
- 关联笔记证据：`ToggleHideLinkedNotesFromCapsules()` / `ToggleTodoNoteLinks()` 都调用 `RefreshCapsuleEligibilityForLinkedNotes()` 后保存，见 `AppController.Settings.cs:1119`、`AppController.Settings.cs:1127`。这覆盖 A003 的触发入口。
- expanded slot 证据：`ToggleDeepCapsuleExpandedSlot()` 更新每个窗口的 expanded slot 模式，然后 `ArrangeDeepCapsules()` 并保存，见 `AppController.Settings.cs:1185`。
- 可见面恢复证据：关闭胶囊 / 贴边后会调用 `RestoreMissingVisiblePaperSurfaces()`，把 `paper.IsVisible=true` 但无窗口表面的纸片恢复出来，见 `AppController.Settings.cs:1199`。
- 结论：本文件逐段深读完成。未发现新增的模式关闭漏清理、tooltip 说明误关、主题刷新缺口或设置后不保存的问题。

#### `AppController.Tray.cs`

- 职责：Hardcodet 托盘图标、托盘菜单模板 / 样式、菜单重建、纸片列表显隐、行内删除确认、自定义图标加载。
- 托盘图标证据：`CreateTrayIcon()` 使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`，见 `AppController.Tray.cs:21`；`LoadTrayIconSource()` 优先加载程序目录 `PaperTodo.ico`，再找内嵌 `.PaperTodo.ico`，最后生成 fallback bitmap，见 `AppController.Tray.cs:43`。
- 菜单重建证据：`PreviewTrayContextMenuOpen` 每次打开前 `RebuildTrayMenu()`，见 `AppController.Tray.cs:30`；`RefreshTrayMenu()` 只有菜单正打开且没有 suppression 时重建，见 `AppController.Tray.cs:443`。
- 首次菜单焦点证据：`CreateTrayMenu()` 的 `Opened` 事件调用 `ActivateTrayContextMenu()`，后者同步和 Dispatcher Input 阶段各尝试 focus，并调用 `WindowNative.TrySetForegroundWindow()`，见 `AppController.Tray.cs:345`、`AppController.Tray.cs:364`。
- 行内按钮抑制证据：`TrayPaperItem()` 内部用 `suppressRowClickToken` 和 `InputManager.Current.PostProcessInput` 在删除 / 确认按钮手势期间抑制行点击，见 `AppController.Tray.cs:543`；行点击处理在 `suppressRowClick || confirmMode` 时直接吞掉，见 `AppController.Tray.cs:721`。
- 删除确认证据：首次点删除进入 confirm mode，确认按钮才调用 `DeletePaper(paper)`，见 `AppController.Tray.cs:680`、`AppController.Tray.cs:701`。删除语义仍由 `AppController.DeletePaper()` 统一处理。
- 结论：本文件逐段深读完成。未发现托盘改回 `System.Drawing.Icon`、手动弹菜单、全局轮询修首次菜单或行内按钮明显误触发行点击的新路径。

#### `MasterCapsuleWindow.cs`

- 职责：每个贴边队列的 slot 0 主胶囊；显示收起全部入口、切换当前队列收起状态、拖动当前队列起始高度。
- 队列归属证据：窗口持有 `_queueEdge` 和 `_queueMonitorDeviceName`，见 `MasterCapsuleWindow.cs:42`；控制器为每个 queue key 创建 / 更新一个 master，见 `AppController.cs:1425`。
- 点击 / 拖动证据：按下时记录当前队列的起始高度，见 `MasterCapsuleWindow.cs:203`；拖动中只调用 `SetDeepCapsuleStartTopMargin(_queueMonitorDeviceName, _queueEdge, ...)`，见 `MasterCapsuleWindow.cs:243`；松手未拖动时只切换当前队列收起状态，见 `MasterCapsuleWindow.cs:261`。
- 拖动保存证据：拖动中不 `commit`，只实时重排；松手后 `commit: true` 保存，见 `MasterCapsuleWindow.cs:254` 和 `AppController.cs:2007`。这避免拖动中持续落盘。
- 左 / 右镜像证据：`ApplyMasterEdgeLayout()` 按 `_queueEdge` 镜像 margin、内容方向和 chevron 顺序，见 `MasterCapsuleWindow.cs:397`；`MoveToTarget()` 用 `DeepCapsuleLayout.DockedLeft(area, visibleWidth, _queueEdge)` 解析所属队列边缘，见 `MasterCapsuleWindow.cs:472`。
- 发现并修复：A002。右侧主胶囊在文字宽度变化时旧逻辑会先更新 `Width`，再动画 `Left`，宽度变小时会短暂离开屏幕边缘。已改为检测 `widthChanged`，宽度变化时同步把 `Left` 设到目标贴边位置，仅保留纵向动画；证据见 `MasterCapsuleWindow.cs:480`、`MasterCapsuleWindow.cs:486`、`MasterCapsuleWindow.cs:503`。
- 验证：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

## 阶段 3：跨模块不变量审查

- [x] `data.json` 损坏时不能被空状态覆盖
- [x] 未知字段兼容和字段迁移不能破坏旧数据
- [x] `_saveVersion`、写锁、退出同步保存必须防止旧保存覆盖新状态
- [x] 普通纸片 `X/Y/Width/Height` 不能保存胶囊半隐藏坐标
- [x] 删除、隐藏、折叠三种语义不能混
- [x] 关闭胶囊 / 贴边 / 收起全部必须清理临时 slot、激发态、动画态
- [x] `ShowDeepCapsuleWhileExpanded` 为真时展开纸片仍保留边缘胶囊槽位
- [x] `UseCapsuleCollapseAll` 使用 slot 0 主胶囊，真实纸片从后面开始
- [x] 每个 `(monitor, edge)` 队列独立排序、起始高度、收起状态
- [x] `HideLinkedNotesFromCapsules` 和待办关联状态变化后胶囊资格一致
- [x] 单实例主进程 Mutex 释放规则正确
- [x] `exit` / `quit` 在无主实例时保存并退出，不创建默认纸片
- [x] 托盘必须使用 `TaskbarIcon.IconSource = LoadTrayIconSource()`
- [x] 主题变化必须刷新动态控件、托盘菜单、AvalonEdit
- [x] 四语言资源 key 必须一致

### 阶段 3 记录

#### 数据加载 / 恢复 / 保存顺序

- `data.json` 损坏保护：主数据存在但解析失败时，`StateStore.Load()` 只会尝试 `data.backup.json`，不会返回空 `AppState`；主 / 备都失败才抛出启动失败，见 `StateStore.cs:28`、`StateStore.cs:61`、`StateStore.cs:87`。从 backup 恢复时会标记下一次保存需要保留失败主文件，见 `StateStore.cs:66`、`StateStore.cs:142`；首次保存跳过 backup 轮换并复制 `failed_load` / `used_for_recovery` 副本，见 `StateStore.cs:138`、`StateStore.cs:151`。
- 未知字段兼容：`JsonOptions` 使用 `JsonSerializerOptions.Strict` 但显式 `UnmappedMemberHandling.Skip`，保留严格解析基本类型的同时跳过旧版 / 实验版遗留字段，见 `StateStore.cs:10`、`StateStore.cs:19`。字段迁移由 `Normalize()` 集中处理，例如 `ShowTopBarNewPaperButtons` 迁移到两个独立按钮，胶囊 side / monitor 缺失时继承旧全局 anchor，见 `StateStore.cs:250`、`StateStore.cs:340`。
- 异步保存顺序：控制器每次 `SaveNow()` 先递增 `_saveVersion` 并序列化当前状态，再把版本号交给 `StateStore`，见 `AppController.cs:1609`、`AppController.cs:1614`。`StateStore.SaveJsonSync()` / `SaveJsonAsync()` 共用 `_writeLock`，且版本小于 `_latestWrittenVersion` 时直接丢弃，见 `StateStore.cs:102`、`StateStore.cs:120`。退出路径 `Exit()` 停止 timer 并调用 `SaveNow(sync: true)` 后才释放托盘和关闭窗口，见 `AppController.cs:2043`、`AppController.cs:2053`。
- 崩溃兜底：全局异常处理只写 `data.crash_recovery.json`，不会直接覆盖 `data.json`，见 `App.xaml.cs:89`、`App.xaml.cs:99`。这不替代正常保存协议，但降低异常退出时的数据丢失风险。
- 结论：当前跨模块证据支持前三个数据保存不变量；本组未发现会把损坏主文件静默替换为空状态、未知字段直接导致启动失败、或旧异步保存覆盖退出同步保存的新问题。

#### 普通纸片几何与胶囊几何隔离

- 写入入口证据：普通几何集中由 `AppController.UpdateGeometry()` 写入，遇到 `PaperWindow.SuppressGeometrySave` 直接返回；折叠态只写 `X/Y`，不写 `Width/Height`，见 `AppController.cs:1270`、`AppController.cs:1284`。窗口侧 `Loaded` / `LocationChanged` / `SizeChanged` 统一走 `SaveGeometryIfAllowed()`，该方法在折叠 / 展开转换中或 suppress 期间跳过，见 `PaperWindow.cs:568`、`PaperWindow.cs:2129`。
- 贴边 slot 证据：贴边胶囊位置由独立 slot host 承担，`ApplyDeepCapsulePlacement()` / `ApplyExpandedDeepCapsuleSlotPlacement()` 设置 slot state、index、host left/top/width，不直接写 `paper.X/Y/Width/Height`，见 `PaperWindow.DeepCapsule.cs:1392`、`PaperWindow.DeepCapsule.cs:1433`。离开贴边栈统一 `DetachFromDeepCapsuleStack()` -> `ClearDeepCapsulePlacement()`，见 `PaperWindow.DeepCapsule.cs:1732`。
- 展开对齐证据：从贴边 slot 展开为普通窗口时，最终窗口对齐边缘使用 `MoveWindowWithoutGeometrySave()` 包住 `AlignExpandedToDockedEdge()`，避免对齐过程中的中间尺寸 / 坐标写回普通几何，见 `PaperWindow.Capsule.cs:495`、`PaperWindow.DeepCapsule.cs:413`。
- 发现并修复：A006。`ShowMainWindowForDeepCapsuleActivation()` 激活主窗口时会先把主窗口临时摆到 slot host 坐标再展开；虽然正常保存防抖会被后续最终几何覆盖，但这段临时摆放仍可能触发 `Loaded` / `LocationChanged` / `SizeChanged` 的几何写入链。已用 `MoveWindowWithoutGeometrySave()` 包住临时摆放和 `Show()`，见 `PaperWindow.DeepCapsule.cs:327`。
- 结论：A006 修复后，贴边 slot 几何、跨队列拖拽几何和展开前临时几何都不再进入普通纸片几何写入链；最终展开后的普通窗口位置仍由既有 `UpdateGeometry()` 保存。

#### 删除 / 隐藏 / 折叠语义和模式清理

- 删除语义证据：`DeletePaper()` 关闭真实窗口、从 `State.Papers` 移除纸片、删除笔记时清理待办链接，之后重排胶囊并保存；当最后一张纸被删除时才创建新的默认待办纸，见 `AppController.cs:988`、`AppController.cs:997`、`AppController.cs:1000`、`AppController.cs:1005`。这是唯一会从 `Papers` 移除纸片的控制器路径。
- 隐藏语义证据：普通窗口关闭事件默认 `e.Cancel=true` 并调用 `HidePaper()`，不删除数据，见 `PaperWindow.cs:1797`；`HidePaper()` 只把 `paper.IsVisible=false`，从贴边栈分离，隐藏窗口后取消折叠态，最后 `MarkDirty()`，见 `AppController.cs:882`、`AppController.cs:890`、`AppController.cs:909`。`HideAllPapers()` 同样只清 `IsVisible` 和 `IsCollapsed`，不移除 `Papers`，见 `AppController.cs:961`、`AppController.cs:980`。
- 折叠语义证据：`SetCollapsedState()` 修改的是 `_paper.IsCollapsed`，保留 `paper.IsVisible`；贴边模式下折叠后由 `ArrangeDeepCapsules()` 分配 slot，普通窗口再休眠，见 `PaperWindow.Capsule.cs:407`、`PaperWindow.Capsule.cs:479`、`PaperWindow.Capsule.cs:690`。`PaperWindow.HasVisibleSurface` 把主窗口和贴边 slot 都算作可见表面，见 `PaperWindow.cs:211`。
- 关闭胶囊模式证据：`ToggleCapsuleMode()` 关闭时同步关闭贴边和收起全部、清 `CapsuleCollapseAllActiveQueues`、重置全局 / per-queue 起始高度、把所有纸片 `IsCollapsed=false`，然后更新窗口、`ArrangeDeepCapsules()`、恢复缺失可见面并保存，见 `AppController.Settings.cs:1059`、`AppController.Settings.cs:1065`、`AppController.Settings.cs:1068`、`AppController.Settings.cs:1072`、`AppController.Settings.cs:1078`。
- 关闭贴边模式证据：`ToggleDeepCapsuleMode()` 关闭时清收起全部和起始高度，逐窗口 `UpdateDeepCapsuleMode()`；该窗口方法在模式关闭时清 expanded reservation 并 `ClearDeepCapsulePlacement()`，见 `AppController.Settings.cs:1153`、`AppController.Settings.cs:1167`、`PaperWindow.DeepCapsule.cs:1740`。
- 关闭收起全部证据：`ToggleCapsuleCollapseAll()` 关闭时清全局 / per-queue active、重置起始高度并重排；`DestroyAllMasterCapsules()` 会清 active 状态并关闭所有 master，见 `AppController.cs:1525`、`AppController.cs:1530`、`AppController.cs:1478`。被收起的真实胶囊在重新 `ApplyDeepCapsulePlacement()` 时清 `_isCollapseAllRetracted` 并恢复 opacity / slot host，见 `PaperWindow.DeepCapsule.cs:1392`。
- slot / 激发态清理证据：`ClearDeepCapsulePlacement()` 清跨队列拖拽视觉、slot state、visual state、retracted 标记、visual offset 和 index，并隐藏 slot host；`DetachFromDeepCapsuleStack()` 是隐藏、模式关闭、表面恢复的统一清理入口，见 `PaperWindow.DeepCapsule.cs:1686`、`PaperWindow.DeepCapsule.cs:1700`、`PaperWindow.DeepCapsule.cs:1735`。
- 结论：当前证据支持删除、隐藏、折叠三种语义独立；关闭胶囊 / 贴边 / 收起全部时会清 slot、激发态、收起态、主胶囊和 per-queue 起始高度。本组未发现“隐藏等于删除”或“关闭模式后残留 slot/master”的新问题。

#### 展开保留槽位 / 收起全部 slot 0 / 多队列独立性

- 展开保留槽位证据：`ShouldPaperOccupyDeepCapsuleSlot()` 对未折叠纸片在 `UseDeepCapsuleMode && ShowDeepCapsuleWhileExpanded && window.HasVisibleSurface` 时仍返回 true，见 `AppController.cs:1581`；布局时未折叠但应占槽位的纸片走 `ApplyExpandedDeepCapsuleSlotPlacement()`，该方法设置 `ExpandedReserved` 和 `Active`，并显示 slot host，见 `AppController.cs:1405`、`PaperWindow.DeepCapsule.cs:1433`、`PaperWindow.DeepCapsule.cs:1451`。
- slot 0 主胶囊证据：`ArrangeDeepCapsules()` 对每个非空 queue 在 `UseCapsuleCollapseAll` 开启时设置 `visualOffset=1`，真实纸片的 `ApplyDeepCapsulePlacement(idx, ..., visualOffset)` / `ApplyExpandedDeepCapsuleSlotPlacement(idx, ..., visualOffset)` 会从 slot 1 开始；主胶囊位置使用 `TopForIndex(0, ...)`，见 `AppController.cs:1386`、`AppController.cs:1397`、`MasterCapsuleWindow.cs:476`。
- 收起全部独立证据：`ToggleCapsuleCollapseAllActive(monitorDeviceName, edge)` 只切换当前 queue key 的 `CapsuleCollapseAllActiveQueues`，随后重排；`SyncMasterCapsules()` 为每个 live queue 创建 / 更新一个 `MasterCapsuleWindow`，见 `AppController.cs:1500`、`AppController.cs:1512`、`AppController.cs:1425`。这覆盖此前“收起全部会收起所有队列”的高风险回归。
- 队列排序独立证据：`ReorderDeepCapsule()` 先过滤当前 queue 成员，只重填这些成员在 `State.Papers` 中原本占用的位置，其他队列和非胶囊纸片保持原位，见 `AppController.cs:1136`、`AppController.cs:1164`。`MoveCapsuleToQueue()` 只改拖动纸片的 `CapsuleSide` / `CapsuleMonitorDeviceName`，按目标队列 drop 高度插入目标 queue，见 `AppController.cs:1191`、`AppController.cs:1198`、`AppController.cs:1229`。
- 起始高度独立证据：主胶囊拖动调用 `SetDeepCapsuleStartTopMargin(_queueMonitorDeviceName, _queueEdge, ...)`，控制器用同一个 queue key 写 `DeepCapsuleQueueStartTopMargins`，拖动中只重排，释放时保存，见 `MasterCapsuleWindow.cs:203`、`MasterCapsuleWindow.cs:243`、`AppController.cs:2004`、`AppController.cs:2030`。
- 结论：当前代码把展开保留槽位、收起全部 slot 0 和多队列排序 / 起始高度 / 收起状态都绑定到 `(monitor, edge)` 队列；未发现主胶囊跨队列收起、其他队列长标题影响本队列展开 inset、或队内排序误重排全局列表的新问题。

#### 关联笔记与胶囊资格

- 资格判断证据：`CanPaperDisplayAsCapsule()` 只在胶囊模式开启、且“启用待办关联 + 隐藏已关联笔记 + 当前笔记被任一待办项引用”同时成立时排除该笔记胶囊，见 `AppController.cs:418`、`AppController.cs:423`。`IsNoteLinkedToAnyTodo()` 遍历所有 todo item 的 `LinkedNoteId`，见 `AppController.cs:390`。
- 状态变化证据：link / unlink 待办项会压撤销、改 `LinkedNoteId`、`MarkDirty()`、重建行并调用 `RefreshCapsuleEligibilityForLinkedNotes()`，见 `PaperWindow.Todo.cs:1017`、`PaperWindow.Todo.cs:1044`。删除笔记时 `ClearTodoLinksToNote()` 清掉所有指向该 note 的 `LinkedNoteId` 并刷新受影响待办行，见 `AppController.cs:1000`、`AppController.cs:1023`。
- 设置切换证据：`ToggleHideLinkedNotesFromCapsules()` 和 `ToggleTodoNoteLinks()` 都调用 `RefreshCapsuleEligibilityForLinkedNotes()` 后保存；关闭关联功能还会先清拖放目标并让窗口更新关联入口，见 `AppController.Settings.cs:1119`、`AppController.Settings.cs:1129`、`AppController.Settings.cs:1134`。
- 刷新证据：`RefreshCapsuleEligibilityForLinkedNotes()` 对所有窗口调用 `RefreshCapsuleEligibility()`，然后 `ArrangeDeepCapsules()` 和 `RefreshTrayMenu()`；不可再显示为胶囊的贴边纸片会走 `RestoreFromCapsuleAfterEligibilityLoss()` 恢复为普通可见窗口，跨文件证据见 `AppController.cs:1052`、`PaperWindow.Capsule.cs:83`、`PaperWindow.Capsule.cs:96`。
- 结论：关联状态变化、设置变化和删除笔记都会刷新胶囊资格；未发现已关联笔记继续残留在贴边胶囊队列，或取消关联后不回到胶囊资格池的新问题。

#### 单实例 / exit / 托盘 / 主题 / 资源

- 单实例证据：`SingleInstanceHelper.TryAcquire()` 只有 `createdNew` 时 `_ownsMutex=true`；拿不到主锁的后续实例只 `SignalPrimaryInstance()` 后 `Dispose()`，而 `Dispose()` 只有 `_ownsMutex` 为真才 `ReleaseMutex()`，见 `SingleInstanceHelper.cs:28`、`SingleInstanceHelper.cs:145`、`App.xaml.cs:20`。主实例 listener 对空参数用 `StartupCommandKind.Show`，见 `App.xaml.cs:64`。
- `exit` / `quit` 首实例证据：`App.xaml.cs` 在 `_controller.Start()` 前识别 `StartupCommandKind.Exit` 并直接 `ExecuteStartupCommand()`；此时不会创建默认纸片，`Exit()` 会同步保存并退出，见 `App.xaml.cs:55`、`AppController.cs:623`、`AppController.cs:2043`。这覆盖“无主实例时 exit/quit 保存并退出，不恢复窗口、不创建默认待办纸”。
- 托盘证据：`CreateTrayIcon()` 使用 Hardcodet `TaskbarIcon`，明确 `trayIcon.IconSource = LoadTrayIconSource()`；`LoadTrayIconSource()` 优先外部 `PaperTodo.ico`，失败再嵌入资源，最后 vector fallback，见 `AppController.Tray.cs:21`、`AppController.Tray.cs:28`、`AppController.Tray.cs:43`。菜单通过 `PreviewTrayContextMenuOpen` 重建，不走手动弹菜单，见 `AppController.Tray.cs:29`。
- 主题证据：设置主题 / 配色时 `Theme.Invalidate()`、保存、逐窗口 `UpdateTheme()`、逐 master `UpdateTheme()`、重建托盘菜单并刷新设置窗口，见 `AppController.Settings.cs:16`、`AppController.Settings.cs:49`。系统主题变化在 `State.Theme=="system"` 时同样刷新这些目标，见 `AppController.Settings.cs:989`。窗口侧 `UpdateTheme()` 刷新动态资源、标题 / 图标 / deep slot theme，并对 note 调 `MarkdownTextBox.RefreshVisualStyle()`；该方法刷新 AvalonEdit 前景、caret、link brush、renderer / colorizer 和 visual layers，见 `PaperWindow.cs:712`、`PaperWindow.cs:782`、`MarkdownTextBox.cs:150`。
- 资源证据：`Strings.Get()` 缺 key 时回退 key 本身，不会直接崩溃；当前重新解析四个 resx：`Strings.resx`、`Strings.en.resx`、`Strings.ja.resx`、`Strings.ko.resx` 均 `keys=150 missing=0 extra=0 placeholderMismatch=0`，见 `Strings.cs:8`。
- 结论：单实例 Mutex、首实例 exit、托盘 `IconSource`、主题动态刷新和四语言资源 parity 均满足当前不变量。本阶段未发现相关新问题。

## 阶段 4：高风险专项攻击

- [x] 单屏 100% DPI 贴边胶囊
- [ ] 单屏 125% / 150% DPI 贴边胶囊
- [ ] 双屏同 DPI 左右侧队列
- [ ] 双屏混合 DPI 跨屏拖拽
- [ ] 左侧与右侧 hover 滑出 / 滑回视觉一致性
- [-] 收起全部每队列独立收起 / 展开
- [ ] 拖单个胶囊上下排序和跨边磁吸阈值
- [ ] 拖拽中丢失捕获、Alt-Tab、释放到菜单外
- [x] 隐藏全部 / 显示全部 / 关闭模式 / 重启恢复
- [ ] 托盘菜单首次点击、行点击、删除确认、菜单重建
- [-] 新建纸片位置和来源队列继承
- [ ] 待办多行粘贴、拖拽排序、撤销重做
- [-] 笔记 Markdown 大文本、链接、外部打开
- [ ] 全屏避让和 topmost 层级

### 阶段 4 记录

#### 当前测试环境

- 屏幕环境：`System.Windows.Forms.Screen.AllScreens` -> 1 个主屏，`\\.\DISPLAY1`，Bounds `{X=0,Y=0,Width=2048,Height=1152}`，WorkingArea `{X=0,Y=0,Width=2048,Height=1104}`。
- 限制：当前机器不能证明双屏同 DPI、双屏混合 DPI、跨屏拖拽和多屏左右队列的真实运行效果；这些项目保持未完成，后续需要用户环境或可控多屏环境继续实测。
- 隔离方式：复制当前 Release 输出到 `.audit-runtime/stage4-*`，排除真实 `data.json` / `data.backup.json`；运行时只写隔离目录数据文件。

#### 启动命令 / 显隐 / 重启恢复

- `exit` 空数据：隔离目录 `stage4-exit-20260619-104307` 中运行 `PaperTodo.exe exit`，结果 `dataExists=True papers=0 crashLog=False`。结论：首实例 `exit` 会保存空状态并退出，但不会创建默认待办纸。
- `new-todo` / `new-note`：隔离目录 `stage4-cmd-20260619-104221` 中依次启动主实例并用二次实例发 `exit`，结果 `afterNewTodo count=1 types=todo visible=True`，`afterNewNote count=2 types=todo,note`。
- `hide` / `show` / `toggle`：同一隔离目录中运行，结果 `afterHide visible=False,False collapsed=False,False`，`afterShow visible=True,True`，`afterToggle visible=False,False collapsed=False,False`，`crashLog=False`。
- 损坏主数据 + backup 恢复：隔离目录 `stage4-recovery-20260619-175139` 中写入损坏 `data.json` 和可用 `data.backup.json`，运行 `PaperTodo.exe exit`，结果 `exit=0 mainCount=1 mainId=todo-recovered backupCount=1 backupId=todo-recovered failedCopies=1 recoveryCopies=1 crashLog=False`。
- 关闭模式 / 旧配置规范化：隔离目录 `stage4-normalize-20260619-175459` 中写入类型正确但值非法的配置，包含 `useCapsuleMode=false`、`useDeepCapsuleMode=true`、`useCapsuleCollapseAll=true`、非空 active queues / per-queue margins、折叠纸片、非法枚举和未知字段；运行 `PaperTodo.exe exit` 后结果 `exit=0 type=todo title='VeryLongTitleShouldB' width=280 height=340 collapsed=False zoom=1.5 textZoom=1.5 useCapsule=False useDeep=False collapseAll=False topTodo=False topNote=False theme=system markdown=enhanced todoSize=medium fullscreen=avoid ext=.md crashLog=False`。
- 严格解析失败边界：隔离目录 `stage4-normalize-20260619-175331` 中故意把字符串字段 `markdownRenderMode` 写成数字，启动失败并记录 `JsonException: The JSON value could not be converted to System.String`；原 `data.json` 未被规范化覆盖。结论：类型不匹配仍按损坏数据处理，不做危险“修复后覆盖”。
- 结论：命令类新建、隐藏、显示、切换、退出保存、backup 恢复、模式关闭后的状态清理和旧配置规范化路径在当前构建下通过。

#### 笔记大文本攻击

- 攻击输入：隔离目录写入 1 张可见 note，`content` 长度 120000，启动 `PaperTodo.exe show` 后用二次实例 `exit`。
- 首次结果：修复 A005 后仍触发新异常，`PaperTodo.crash.log` 记录 `System.InvalidOperationException: No undo group should be open at this point`，堆栈为 `MarkdownTextBox.OnTextChanged()` 中同步设置 `Text = Text[..MaxLength]`，经 `PaperWindow.BuildNoteBody()` 初始赋值触发。
- 发现并修复：A007。长度截断改为排队到 dispatcher 下一轮执行，避开 AvalonEdit 初始文档更新期间打开的 undo group。
- 复验结果：隔离目录 `stage4-fix-20260619-104133` 中同样输入 120000 字符，修复后 `MainExitCode=0`，`afterRun count=1 len=100000 visible=True`，`crashLog=False`。
- 结论：大文本启动 / 截断攻击通过；Markdown 链接点击和外部打开还未在阶段 4 实测，因此该主项保持进行中。

#### 单屏 100% DPI 贴边胶囊烟测

- DPI 证据：`GetDpiForSystem()` -> `Dpi=96 Scale=100%`；屏幕仍为单主屏 `\\.\DISPLAY1`。
- 攻击输入：隔离目录 `stage4-capsule-single-20260619-175607` 中写入 4 张已折叠可见 todo，左队列 2 张、右队列 2 张，`UseCapsuleCollapseAll=true`，仅 `|left` 队列 active，per-queue 起始高度包含 `|left` 和 `|right`。
- 运行结果：启动 `PaperTodo.exe show`，等待后用二次实例 `exit`，结果 `exit=0 count=4 sides=left,left,right,right collapsed=True,True,True,True activeKeys=|left marginKeys=|right,|left crashLog=False`。
- 结论：单屏 100% DPI 下左右贴边队列、折叠胶囊、主胶囊 active key 和 per-queue margin 能启动、布局并退出保存；hover 视觉、拖拽和真实点击收起/展开另有专项，不能由本烟测替代。

## 阶段 5：性能审查

- [ ] 200ms topmost timer 是否做重活
- [ ] 拖拽过程中是否触发保存、全量重建或昂贵测量
- [ ] 胶囊重排复杂度是否可接受
- [ ] Markdown 渲染和大文本保护是否仍有效
- [ ] 托盘菜单重建是否只在必要时发生
- [ ] 主题切换是否重复 rebuild 过多
- [ ] 透明窗口移动 / 动画是否造成可感知压力

## 阶段 6：交互、视觉、动画审查

补动画原则：状态去向不清楚、跳变让用户误解、左右侧不一致时补；如果动画拖慢操作、制造错觉或增加风险，就不补。

- [ ] 普通纸片显示 / 隐藏
- [ ] 普通胶囊折叠 / 展开
- [ ] 贴边胶囊 hover 滑出 / 滑回
- [ ] 展开后边缘胶囊激发态
- [ ] 收起全部 retract / release
- [ ] 单胶囊跨队列拖出、松手归位
- [ ] 待办新增 / 删除 / 排序
- [ ] 关联笔记入口变化
- [ ] 托盘操作反馈
- [ ] 设置切换后的即时反馈
- [ ] 主题切换过渡
- [ ] 关闭动画开关后所有动画立即完成

## 阶段 7：修复循环

每个问题必须记录：

- 问题描述
- 影响范围
- 触发路径
- 修复方案
- 代价和风险
- 是否更新 `CHANGELOG.md`
- 验证路径

问题列表：

- [x] A001：backup 恢复后首次保存可能覆盖解析失败的主数据文件
  - 问题描述：`StateStore.Load()` 在 `data.json` 解析失败但 `data.backup.json` 可加载时返回 backup 状态；随后 `WriteJsonInternal()` 会先把当前 `data.json` 复制到 `data.backup.json`，再覆盖 `data.json`。这会丢失 backup 原件，并用旧 backup 生成的新主文件覆盖失败主文件。
  - 影响范围：数据恢复、启动失败保护、`exit` 启动命令、正常启动后的首次自动保存。
  - 触发路径：`StateStore.cs:25` -> `StateStore.cs:55` -> `AppController.cs:68` -> `AppController.cs:93` / `AppController.cs:2046` -> `StateStore.cs:124`。
  - 修复方案：`StateStore` 记录本次是否从 backup 恢复且主文件解析失败；首次保存前保留失败主文件和可用 backup 的原始副本，并跳过这次把坏主文件轮换进 backup 的操作。
  - 代价和风险：需要小幅扩展保存层状态；要避免影响正常保存、异步版本锁和 backup 轮换。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为数据恢复保护修复。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示；隔离复制发布目录后构造损坏 `data.json` + 可用 `data.backup.json`，执行 `PaperTodo.exe exit` -> `ExitCode=0`、`FailedCopies=1`、`RecoveryBackupCopies=1`、`BackupStillOriginal=True`、`MainRecovered=True`。

- [x] A002：右侧贴边主胶囊宽度变化时可能短暂离开屏幕边缘
  - 问题描述：`MasterCapsuleWindow.MoveToTarget()` 旧逻辑先调用 `ApplyDockedWidth()` 改 `Width`，再动画 `Left`。右侧队列里如果主胶囊文案宽度变小，窗口右边缘在动画期间会短暂从屏幕边缘向内缩，形成和普通贴边胶囊旧问题同类的漏白 / 离边。
  - 影响范围：收起全部主胶囊，尤其是右侧队列、收起 / 展开切换、计数文本宽度变化、语言文本长度变化。
  - 触发路径：`AppController.SyncMasterCapsules()` -> `MasterCapsuleWindow.UpdateState()` -> `MasterCapsuleWindow.MoveToTarget()`。
  - 修复方案：在 `MoveToTarget()` 中检测可见宽度变化；宽度变化时同步把 `Left` 放到新的贴边目标，不再让宽度变化和水平移动分两阶段呈现；纯位置变化仍保留水平动画。
  - 代价和风险：右侧主胶囊文案宽度变化时少一个短距离水平补间，但换来边缘始终贴合；纵向移动动画保留。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为贴边主胶囊动画修正。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A003：贴边胶囊失去胶囊资格时可能恢复成不可见纸片
  - 问题描述：已折叠且停靠在贴边 slot 的纸片，如果因为关联笔记隐藏、关联功能切换等原因不能再显示为胶囊，旧逻辑会先清 slot，再展开普通窗口；但主窗口在贴边休眠状态下通常是隐藏的，展开逻辑不会自动 `Show()`，可能造成纸片仍 `IsVisible=true` 但没有可见表面。
  - 影响范围：隐藏已关联笔记、关闭 / 打开待办关联功能、其他导致 `CanPaperDisplayAsCapsule()` 从 true 变 false 的路径。
  - 触发路径：`AppController.RefreshCapsuleEligibilityForLinkedNotes()` -> `PaperWindow.RefreshCapsuleEligibility()` -> `ClearDeepCapsulePlacement()` -> `SetCollapsedState(false)`。
  - 修复方案：新增 `RestoreFromCapsuleAfterEligibilityLoss()`，在贴边 slot 仍存在时先 `ShowMainWindowForDeepCapsuleActivation()` 恢复主窗口，再调用 `SetCollapsedState(false, alignExpandedToDockedEdge: true)`；同时 expanded slot 保留条件加入 `CanDisplayAsCapsule()`。
  - 代价和风险：资格丢失时会主动把纸片展开为普通窗口，这是“不能再显示为胶囊”的正确退路；不新增持久化字段。
  - 是否更新 `CHANGELOG.md`：已合并进 `### Unreleased` 的待办关联设置修复描述。
  - 验证结果：代码路径核对 + `dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A004：旧全局贴边起始高度在非主屏旧配置下可能被提前按主屏夹值
  - 问题描述：加载旧配置时，`DeepCapsuleStartTopMargin` 是单个全局标量；旧逻辑通过 `DeepCapsuleLayout.NormalizeStartTopMargin(value)` 使用静态全局 work area 规范化。若旧配置的全局贴边队列实际停在非主屏，且非主屏高度与主屏不同，加载时可能提前把用户拖过的起始高度夹到主屏范围。
  - 影响范围：没有 per-queue 起始高度字典的旧配置、非主屏贴边队列、不同高度或不同任务栏工作区的多显示器环境。
  - 触发路径：`StateStore.Normalize()` -> `NormalizeDeepCapsuleStartTopMargin()` -> `DeepCapsuleLayout.NormalizeStartTopMargin(value)`。
  - 修复方案：`NormalizeDeepCapsuleStartTopMargin()` 增加 `monitorDeviceName` 参数，先用 `DeepCapsuleLayout.WorkAreaForQueue(monitorDeviceName)` 解析保存的全局显示器，再用显式 `NormalizeStartTopMargin(value, area)` 规范化。
  - 代价和风险：只影响旧全局标量兼容路径；per-queue 字典仍按原设计保留原值并在布局时按实时显示器 clamp。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为贴边胶囊旧配置恢复修复。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A005：超长旧笔记初次加载可能绕过 MarkdownTextBox 长度保护
  - 问题描述：`BuildNoteBody()` 对 `MarkdownTextBox` 的对象初始化顺序是先赋 `Text`，再赋 `MaxLength=100000`。`MarkdownTextBox` 的长度保护在 `OnTextChanged()` 中执行，因此超长旧笔记从 `data.json` 初次进入控件时可能没有被截断，增加 AvalonEdit 布局 / 渲染卡顿风险。
  - 影响范围：手工修改或旧版本生成的超长笔记内容；正常编辑路径已有 `MaxLength` 和粘贴限制。
  - 触发路径：`PaperWindow.BuildNoteBody()` -> `new MarkdownTextBox { Text = ..., MaxLength = 100000 }` -> `MarkdownTextBox.OnTextChanged()`。
  - 修复方案：把 `MaxLength=100000` 放在 `Text` 赋值之前，确保初始正文也经过控件级长度保护。
  - 代价和风险：只影响超长笔记的 UI 展示保护，不新增持久化字段；若用户继续编辑超长旧笔记，最终保存仍会按编辑器保护后的内容落盘。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为笔记大文本保护修复。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A006：贴边胶囊激活主窗口时临时 slot 坐标可能进入普通几何写入链
  - 问题描述：从贴边胶囊激活纸片时，`ShowMainWindowForDeepCapsuleActivation()` 会把主窗口临时放到 slot host 的 `Left/Top`，然后 `SetCollapsedState(false)` 执行展开。正常情况下保存防抖会等最终展开几何覆盖后再落盘，但临时摆放仍会触发窗口 `Loaded` / `LocationChanged` / `SizeChanged` 的普通几何写入链，和“贴边半隐藏坐标不能写回普通几何”的不变量不够严格。
  - 影响范围：贴边胶囊从边缘激活、贴边胶囊失去胶囊资格后恢复、程序化打开已折叠贴边纸片；主要风险是异常中断或额外保存时的位置恢复漂移。
  - 触发路径：`PaperWindow.ActivateFromDeepCapsuleSlot()` / `ExpandForProgrammaticOpen()` -> `ShowMainWindowForDeepCapsuleActivation()` -> `Loaded/LocationChanged/SizeChanged` -> `SaveGeometryIfAllowed()` -> `AppController.UpdateGeometry()`。
  - 修复方案：用 `MoveWindowWithoutGeometrySave()` 包住激活主窗口前的临时 `Width/Height/Left/Top` 设置和 `Show()`，让最终展开几何仍按原路径保存，但临时 slot 坐标不进入普通几何写入链。
  - 代价和风险：只影响贴边激活的瞬时几何保存抑制，不改变最终展开位置、动画或持久化字段。
  - 是否更新 `CHANGELOG.md`：已写入 `### Unreleased`，描述为贴边胶囊展开保护。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。

- [x] A007：超长旧笔记初始加载时同步截断会触发 AvalonEdit undo group 异常
  - 问题描述：A005 把 `MaxLength` 放到初始 `Text` 之前后，超长旧笔记会在 `MarkdownTextBox.OnTextChanged()` 中同步执行 `Text = Text[..MaxLength]`。AvalonEdit 初始设置 `Text` 时文档 undo group 仍处于打开状态，同步重设 `Text` 会触发 `System.InvalidOperationException: No undo group should be open at this point`，导致启动异常。
  - 影响范围：手工修改或旧版本留下的超长笔记，应用启动 / 显示该笔记时触发。
  - 触发路径：`PaperWindow.BuildNoteBody()` -> `MarkdownTextBox.Text = oldContent` -> `MarkdownTextBox.OnTextChanged()` -> `Text = Text[..MaxLength]` -> AvalonEdit `UndoStack.ClearAll()`。
  - 修复方案：超限时只排队 `TrimTextToMaxLength()` 到 dispatcher 下一轮，在文档更新事件结束后再截断文本、恢复 caret 和清 selection。
  - 代价和风险：超长文本会在当前 dispatcher tick 内短暂存在；随后立即截断并触发正常 `TextChanged` 保存。正常粘贴路径仍先被 `OnPaste()` 限制。
  - 是否更新 `CHANGELOG.md`：已合并进 `### Unreleased` 的笔记大文本保护修复描述。
  - 验证结果：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；隔离运行 120000 字符 note，修复前有 crash log，修复后 `MainExitCode=0`、保存后 `content.Length=100000`、`crashLog=False`。

## 阶段 8：回归矩阵

- [ ] `dotnet build PaperTodo.csproj -c Release`
- [ ] `git diff --check`
- [ ] 资源 key parity
- [ ] 单屏基础手测
- [ ] 多屏同 DPI 手测
- [ ] 多屏混合 DPI 手测
- [ ] 托盘菜单手测
- [ ] 待办 / 笔记核心路径手测
- [ ] 退出保存 / 重启恢复手测
- [ ] 启动命令手测：`show` / `hide` / `toggle` / `new-todo` / `new-note` / `exit`

## 阶段 9：用户蒸馏层最终复核

在代码事实审计完成后，再读取用户蒸馏文件，用它审查产品判断：

- [ ] 读取蒸馏文件
- [ ] 是否仍符合“桌面上的几张纸”
- [ ] 是否有功能膨胀
- [ ] 是否把实现复杂度转嫁给用户
- [ ] 哪些应该砍、留、延后

## 阶段 10：最终报告

- [ ] 高 / 中 / 低风险问题清单
- [ ] 已修复问题清单
- [ ] 已排除风险清单
- [ ] 结构债清单
- [ ] 性能判断
- [ ] 视觉和动画判断
- [ ] 发布前必修清单
- [ ] 发布后可缓修清单
- [ ] 最终建议：能否发 rc，能否发正式版，剩余风险是什么

## 审核日志

### 2026-06-19

- 创建本文件，建立全量审核执行框架。
- 已记录起点分支、提交、变更规模和文件集合规模。
- 完成基线构建、空白检查、资源 key parity 和版本号状态记录。
- 完成系统地图第一版，记录模块职责和状态所有权；后续逐文件深读会校正这张图。
- 完成 `Models.cs`、`StartupCommand.cs`、`SingleInstanceHelper.cs`、`App.xaml.cs`、`App.xaml` 的逐文件深读记录。
- 深读 `StateStore.cs` 时发现 A001：backup 恢复后的首次保存可能破坏失败主文件和 backup 原件，已加入修复循环。
- 修复 A001：backup 恢复后的首次保存会先保留失败主文件和恢复用 backup，并跳过一次 backup 轮换；`CHANGELOG.md` 已记录用户可感知的数据恢复保护改进。
- 验证 A001：Release 构建通过；隔离运行 `PaperTodo.exe exit` 覆盖损坏主文件 + 可用 backup 场景，确认失败主文件副本、恢复用 backup 副本、原 backup 和恢复后的主文件均符合预期。
- 开始 `AppController.cs` 分段深读：已覆盖启动命令、纸片创建、显隐、删除、关联笔记资格、普通几何、队列重排和每队列收起状态；文件仍标记为进行中。
- 开始 `PaperWindow.DeepCapsule.cs` 分段深读：已覆盖拖拽磁吸、跨队列解锁、拖拽视觉宽度、混合 DPI drop、贴边滑出 / 滑回横向动画；文件仍标记为进行中。
- 补读 `PaperWindow.DeepCapsule.cs` 的 slot 隐藏 / 释放、context menu guard、expanded reservation 清理路径，未发现 hook 永久残留或过期动画回调误清新状态的直接路径。
- 完成 `MasterCapsuleWindow.cs` 深读，发现并修复 A002：右侧主胶囊文字宽度变化时保持贴边；Release 构建和空白检查通过。
- 完成 `PaperWindow.DeepCapsule.cs` 逐文件深读：补齐左 / 右镜像、右侧缩回顺序保护、关闭区语义、CloseForReal 清理路径；文件标记为已完成。
- 完成 `PaperWindow.Capsule.cs` 深读，发现并修复 A003：贴边胶囊失去胶囊资格时先恢复主窗口再展开，避免纸片可见状态与实际表面不一致；Release 构建和空白检查通过。
- 完成 `PaperWindow.cs`、`PaperWindow.Native.cs`、`WindowNative.cs`、`WindowWorkAreaHelper.cs` 深读记录；覆盖关闭语义、几何保存、topmost/no-activate、多屏工作区和混合 DPI 坐标转换。
- 本轮复验：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。
- 启动阶段 3 跨模块不变量审查；完成数据加载 / 恢复 / 保存顺序组，覆盖损坏主数据、未知字段迁移、异步保存版本门闩和退出同步保存。
- 阶段 3 普通纸片几何与胶囊几何隔离审查发现并修复 A006：贴边胶囊激活主窗口时，临时 slot 坐标不再进入普通几何写入链。
- 验证 A006：Release 构建通过；空白检查无错误，仅 CRLF 提示。
- 完成阶段 3 剩余跨模块不变量记录：删除 / 隐藏 / 折叠语义、模式关闭清理、展开保留槽位、收起全部 slot 0、多队列独立性、关联笔记胶囊资格、单实例 / exit / 托盘 / 主题 / 资源。
- 启动阶段 4 高风险专项攻击；记录当前环境为单主屏，双屏 / 混合 DPI 项暂不能证明完成。
- 阶段 4 命令类运行时攻击通过：`exit` 空状态保存但不创建默认纸片，`new-todo` / `new-note` 创建正确类型，`hide` / `show` / `toggle` 持久状态符合预期。
- 阶段 4 大文本攻击发现并修复 A007：超长旧笔记初始加载的截断改为 dispatcher 延后执行，避免 AvalonEdit undo group 异常；120000 字符 note 复验保存为 100000 且无 crash log。
- 完成 `AppController.Settings.cs` 深读记录；覆盖设置窗口、主题刷新、tooltip 说明、胶囊模式关闭清理、关联笔记资格刷新和可见面恢复。
- 完成 `AppController.Tray.cs` 深读记录；覆盖 Hardcodet `IconSource`、外部图标优先、菜单打开重建、首次菜单焦点、纸片行内删除确认和行点击抑制。
- 完成 `PaperWindow.Todo.cs`、`TodoTextBox.cs` 深读记录；覆盖多行粘贴单次撤销、文本编辑撤销边界、拖拽排序 / 删除清理、关联笔记链接后胶囊资格刷新。
- 完成 `DeepCapsuleLayout.cs` 深读记录；发现并修复 A004：旧全局贴边起始高度按保存的显示器 work area 规范化，减少多显示器旧配置重启后的高度漂移。
- 验证 A004：Release 构建通过；空白检查无错误，仅 CRLF 提示。
- 完成 `PaperWindow.Note.cs`、`MarkdownTextBox.cs`、`NoteTypography.cs` 深读记录；覆盖编辑 / 预览共用控件、Markdown 轻量边界、inline HTML 白名单、链接规范化、大文本 / 粘贴保护、外部打开临时文件。
- 修复 A005：笔记正文初始加载时先设置 `MaxLength` 再设置 `Text`，避免超长旧笔记绕过编辑器长度保护；`CHANGELOG.md` 已记录用户可感知的大文本保护改进。
- 验证 A005：Release 构建通过；空白检查无错误，仅 CRLF 提示。
- 完成 `PaperTitles.cs`、`ClipboardHelper.cs`、`ToolTipPreferences.cs`、`SystemSettingsHelper.cs`、`AnimationHelper.cs` 深读记录；未发现需要代码修复的问题。
- 完成 `Theme.cs`、`Strings.cs`、`FullscreenForegroundWindowDetector.cs` 深读记录；覆盖主题缓存 / 失效、资源缺 key fallback、全屏检测候选过滤和 200ms timer 的全局扫描节流。
- 完成 `Resources/*.resx` 审计；四语言 key 数均为 150，key parity 缺失 / 多余为 0，格式占位符不一致为 0，代码字面量资源 key 缺失为 0。
- 完成 `PaperTodo.csproj`、`CHANGELOG.md`、`README*.md`、`AGENTS.md` 深读记录；修正 README 中贴边胶囊仍写右侧单队列的过时描述，并把外部后缀说明改为系统关联程序处理。
- 完成 `AppController.cs` 剩余深读记录；补齐全屏置顶避让、master 胶囊同步、离屏救援、保存失败 UI、队列高度持久化和退出释放证据。阶段 2 逐文件深读已全部打勾。
- 清理 `AppController.cs` 中一段过时的全局队列高度注释；纯维护性注释变更，不写入用户态更新日志。
- 本轮复验：`dotnet build PaperTodo.csproj -c Release` -> 0 warning / 0 error；`git diff --check` -> 无空白错误，仅 CRLF 提示。
