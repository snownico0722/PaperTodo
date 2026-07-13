# PaperTodo Agent 备忘

本文件只记录“不通读历史和全量代码很难知道”的项目约束。代码是真相；普通文件职责、字段含义、WPF/C# 常识不要写进来。

## 工作方式

不要用临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现来交付改动。除非改动巨大到需要重新定路线，必须先向用户确认，再按真实产品结构修改。

需要提交时，如果未提交改动能按功能边界无损拆分，并且每个提交都保持可构建、可理解、可独立回滚，应拆成多个独立提交方便管理；不要把无关文档、备份文件或用户的其他改动混入功能提交。

## 产品边界

PaperTodo 是“桌面上的几张纸”，不是任务管理器、知识库或文档编辑器。默认不做账号、同步、分类、标签、搜索、归档、统计、提醒、日历、主管理页和集中列表页。

Markdown 只做轻量显示和编辑辅助。可兼容少量单行内联 HTML 标签（`b/strong/i/em/s/del/u/code/a href`）；笔记图片只支持内部 `i:` 独占行图片块，不扩展网络图片、表格、附件、其他嵌入内容、块级 HTML 或完整块编辑器。

## 数据和保存

- `data.json` 是用户数据协议，不是内部缓存。新增字段要兼容旧数据；删除 / 改名字段要特别谨慎。
- 笔记图片保存在单个 `note-assets.lmdb` 中：原始字节与元数据分库、事务增量写入。为保持单文件，LMDB 使用 `MDB_NOSUBDIR | MDB_NOLOCK`，所有访问必须继续由进程内同一把锁串行化；不要绕过 `NoteImageStore` 直接开启事务。
- 启动失败时不能用空状态覆盖旧文件。严格解析失败的数据不要“修好后覆盖”，否则可能破坏可恢复数据。
- 保留 `_saveVersion`、`StateStore` 写锁和退出同步保存，避免旧异步保存覆盖新状态。
- 删除、隐藏、折叠是三种语义：删除才从 `Papers` 移除；隐藏仍保留纸片；折叠仍是可见纸片，只是胶囊形态。
- `paper.X/Y/Width/Height` 是普通纸片几何。胶囊尺寸和独立贴边 HWND 的坐标不能写回普通几何。
- 外部打开笔记的临时文件后缀只做文件名合法性校验；允许用户选择系统已关联的任意后缀。

## 单实例

只有主实例释放 Mutex。后续进程只转发启动参数并退出，不释放主实例锁。

`exit` / `quit` 在没有主实例时也应保存并退出；不要恢复窗口，也不要因为空数据目录创建默认待办纸。无参数的后续实例按 `show` 处理。

## 托盘

Hardcodet 托盘必须走 `TaskbarIcon.IconSource = LoadTrayIconSource()`。不要改回 `System.Drawing.Icon`；这个回归曾导致首次右键菜单位置错误、首次点击纸片被吞。

外部 `PaperTodo.ico` 是用户自定义入口，优先级高于内嵌图标。托盘菜单打开时重建，别用手动弹菜单、预热菜单、全局鼠标轮询等方式修首次菜单问题。

## 胶囊和贴边胶囊

这是最高风险区，问题通常来自“窗口几何、动画状态、隐藏状态、持久化状态”混在一起。

- 普通胶囊和贴边胶囊共用度量来源：`PaperLayoutDefaults` / `EdgeCapsuleLayout`。
- 应用清单固定为 `PerMonitorV2,PerMonitor`；贴边 HWND 的物理像素几何以目标显示器和已创建宿主的实际 DPI 为准，不得回退到主纸片窗口的 DPI。
- 贴边槽位不再由 `DeepCapsuleSlotWindow.cs` 或零散 `PaperWindow` 字段维护；`EdgeCapsuleHost` 独占 docked HWND 和视觉树，floating drag 继续使用独立 HWND。
- 所有贴边输入先变成带强类型参数的语义 `EdgeCapsuleIntent`，再经过 `EdgeCapsuleReducer`；不得重新引入 `SetSlot` / `SetVisual` / `SetPlacement` 这类字段 setter、通用参数袋或在 `PaperWindow` 另写布尔状态机。
- 每张纸的 desired model、target presentation、transition、applied frame 和延迟工作只能由一个 `EdgeCapsulePresenter` 持有；`PaperWindow` 只提供环境快照和一个 `EdgeCapsuleHost.Apply(frame)` 效果入口，不得再增加并行真相。
- `EdgeCapsuleTargetPlanner` 必须一次产出完整 shape plan；`Docked*` 和 `FloatingFree` 是互斥外形，悬浮拖拽窗口只能消费 planner 的 `FloatingFree`，不得由构造参数临时拼关闭区、圆角或宽度。
- 显示器、边、顶部、内容宽度和关闭宽度到 `DeviceScreenRect` 的转换只走纯 `EdgeCapsuleGeometry`；不得在窗口移动、动画或 measure 回调中复制物理像素公式。
- per-window 的显示器 settle、标题 measure、物理指针采样和 frame apply 共用一个 dirty/reconcile 调度入口；需要同步交接时调用同一管线的 `Flush`，不得直接调用 planner/apply，也不得为新条件增加独立 pending/scheduled 布尔对。跨胶囊 arrange 只由队列协调器单独合并。
- 同一 Dispatcher 上的动画 Presenter 必须共用一个帧调度器和每帧一次的物理指针采样；布局快照只在标题、显示器或队列布局失效时重算。position-only 帧只移动 `HostBounds` 对应的 HWND，visible-width-only 帧只更新 `VisualSurface` 与固定分段，二者都不得触发 WPF `UpdateLayout`。
- 指针是否位于胶囊上只根据 applied frame 的物理 `InteractiveBounds` 判断；该矩形排除透明阴影边距，WPF enter/leave 只负责唤醒采样，不能直接写 Hover。
- 每个队列的 index、master offset 和 slot count 只由 `EdgeCapsuleQueueCoordinator` 生成，`AppController` 和单个窗口不得各自重新推导。
- 贴边 slot host 使用固定的最大展开透明合成面，真正可见的 Chrome / Shell 使用当前帧真实宽度并在该合成面内钉住墙边；透明预留区不是胶囊的一部分，外形不得依赖 `ClipToBounds`、屏幕边缘或超宽子元素裁切。slot 0 主胶囊不参与水平伸缩，继续使用自身真实窗口宽度。
- 贴边胶囊的关闭区位于屏幕墙边、悬停时从 0 宽度展开并把图标/标题推向屏幕内部；靠墙侧始终为直角，内容区拥有朝屏幕内部的圆角。
- 贴边胶囊水平伸缩只插值已经取整的可见物理宽度；水平伸缩动画期间不得水平移动或改变 docked HWND 宽度，垂直重排仍可移动宿主。关闭区宽度和透明度必须从该可见宽度反推，不得建立独立的布局插值通道。
- `EdgeCapsuleHost.Apply(frame)` 是 docked HWND 的唯一呈现契约；`HostBounds` 只表示稳定的透明合成容量，`Bounds` 才是当前真实胶囊。正文段与关闭段必须使用明确固定宽度，且两段之和与当前可见宽度一致，禁止用 `Star`、隐藏列或额外动画吸收差值。
- 固定宿主超出 `InteractiveBounds` 的透明区域必须在 `WM_NCHITTEST` 返回 `HTTRANSPARENT`，不得把最大宿主矩形当成悬停或点击区域。
- 跨队列拖拽使用独立的 floating drag HWND；贴边 slot host 永远只保留贴边布局，禁止把它改造成自由胶囊或在两种外形间复用列顺序、圆角和宽度状态。
- 拖动期间收到的全局 `ArrangeDeepCapsules` 请求必须合并并在拖动结束后刷新，不能静默丢弃；显示器指标刷新可用自己的延迟刷新吞并该请求。
- 标题测量刷新只改变 target 的真实内容宽度，不得重新推导 Hover / Active、关闭区或槽位语义；它不能覆盖已经排队的动画，动画中从当前 applied frame 平滑 retarget，拖动中则延迟到会话结束。
- 折叠胶囊、贴边胶囊、展开后的边缘激发态应复用同一套胶囊 UI。激发态只是持久外移、外描边和状态变化，不应再重绘一套 UI。
- `ShowDeepCapsuleWhileExpanded = true`：从贴边胶囊展开纸片后，边缘胶囊仍显示并占槽位。
- `UseCapsuleCollapseAll` 使用 slot 0 的主胶囊；真实纸片槽位从后面开始。`CapsuleCollapseAllActive` 为真时，真实胶囊收向主胶囊并隐藏可点击面。
- `HideLinkedNotesFromCapsules` 开启时，已被待办关联的笔记不应显示为胶囊。
- 隐藏全部、关闭胶囊模式、关闭贴边模式、从边缘展开后再隐藏，都要清理临时 slot / 激发态 / 动画状态，避免下次显示错位或残留占位。

## 待办和笔记

- 多行粘贴待办只能形成一次撤销快照。
- `PaperItem.LinkedNoteId` 会影响删除笔记、关闭关联功能、显示关联笔记名称、以及“已关联笔记不显示为胶囊”。
- 笔记编辑态和浏览态共用同一个 `MarkdownTextBox`。不要拆成两套文本控件，否则滚动、换行、选区和测量容易漂。
- `MarkdownTextBox` 长度上限是 WPF 布局 / 渲染保护，不要直接删除。

## 主题、资源、提示

用户可见文本同步四个资源文件：中文、英文、日文、韩文。`ResourceTextVersion` 只是人工检查标记，不参与运行时逻辑。

主题变化要主动刷新动态生成控件、托盘菜单、AvalonEdit 背景 / 文本 / 光标 / 覆盖层；不要只依赖动态资源。

`EnableToolTips` 只控制普通操作提示，不应关闭设置页说明图标和扩展说明。

## 用户态更新日志

`CHANGELOG.md` 顶部按 `### 计划 / 待办`、`### 评估`、`### Unreleased` 组织。用户要求记录软件目标、修改计划或待办时写入计划；要求记录取舍、暂缓原因或实现评估时写入评估；二者都不等同于已完成改动。

改动完成后，如果影响用户可见行为，必须更新 `CHANGELOG.md` 的 `### Unreleased` 小节。

发布版本小节按版本号从旧到新排列；从 `### Unreleased` 挪到具体版本号时，把新版本放到已有版本列表末尾的正确位置，不要插在 `Unreleased` 和旧版本之间。

`### Unreleased` 尽量按可直接挪到正式版本号下的发布格式维护：参考 v2.0 正式版，必要时用 `**新功能**`、`**胶囊相关改动**`、`**bug修复和边界修正**` 等粗体小分组组织条目；每条都应是面向用户的完整发布说明，而不是临时流水账。

明显重磅的新功能要单独成组，例如用 `**脚本胶囊**` 这类功能名作为小标题；相关设置、优化和边界说明尽量收束在该组下，避免被普通新功能或零散修复冲淡。

只写用户能感知的修复、新增和优化，不写文件名、状态机、重构方式等实现细节。尚未提交 / 发布、且用户没有感知到的内部 bug 修复不写入更新日志。

修复 `### Unreleased` 中尚未发布功能自身的问题，不要作为独立修复条目追加；直接保留最终用户能看到的功能 / 优化描述即可。

更新日志条目里只有重点内容需要加粗；非重点条目不要为了统一格式而加粗。

纯内部整理、文档、构建流程变化可不写，并在最终回复说明 ### Unreleased

## 构建和发布

版本号显式维护在 `PaperTodo.csproj`，不要恢复自动递增版本号。

普通编译：

```powershell
dotnet build PaperTodo.csproj -c Release
```

云端 Release 发布两个 Windows x64 单文件：自包含 .NET Runtime 的压缩单文件，以及不带运行库的 no-runtime 单文件。本地打包只生成 no-runtime 单文件。WPF 版本不要开启 `PublishTrimmed` 或 Native AOT。

稳定正式版不要靠 tag push 自动发布；完成真实多屏 / 混合 DPI 等发布前手测后，用 GitHub Actions `workflow_dispatch` 并显式确认稳定版发布。`rc` / `alpha` / `beta` / `preview` 标签可以继续由 tag push 发布为预发布。

推送或移动稳定版 tag 只会把 tag/commit 送到 GitHub；Actions 是后置检查，失败不会撤回这次 push。不要把稳定版 tag push 当作发布步骤，也不要为了正式发布制造必然失败的稳定版 tag push run；正式版发布只认成功的 `workflow_dispatch` run。

## 更新本文

只有产品边界、持久化兼容、保存 / 单实例 / 托盘 / 胶囊 / 发布流程发生变化时才更新本文。普通 UI 微调、文案、颜色、间距、动画参数不需要同步。
