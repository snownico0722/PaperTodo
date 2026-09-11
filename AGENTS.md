# PaperTodo Agent 备忘

本文件是 **Agent 的项目入口、任务路由和执行规则**。PaperTodo 已验证有价值的详细操作规则、隐藏硬约束和容易误改的禁区应继续保留；当前架构的完整解释和历史取舍不在这里重复，除非某个结论本身是 Agent 修改时必须直接遵守的 Do / Don't。

当前代码描述“现在实际怎么跑”，但不天然代表正确设计；代码、文档、决策或注释冲突时，先结合当前实现、提交历史和可观察行为核对，再统一修正。

## 项目知识入口

- [`ARCHITECTURE.md`](doc/ARCHITECTURE.md)：**当前有效的技术选型、架构结构、ownership 和已确立技术方向**。回答“现在应该按什么原则设计”。
- [`DECISIONS.md`](doc/DECISIONS.md)：**历史取舍、失败路线、踩坑、trade-off 和 why**。回答“为什么会走到今天这条路”。
- `AGENTS.md`：**任务路由 + Agent 执行规则**。规定项目专用工作方式、禁区、提交/CI/发布等执行要求。
- 当前代码与关键注释：**具体实现事实和局部 why**。真正修改前仍必须读代码，不能把任何文档当源码替代品。

常见任务先按下面的路由进入，再读相关当前代码：

| 任务 | 优先读取 |
| --- | --- |
| Edge Capsule / 胶囊流畅性 | 本文件「Edge Capsule 硬约束」→ 按下述影响范围读取 Architecture / Decisions → 相关当前代码 |
| 持久化 / 恢复 / 图片 | Architecture「状态与持久化架构」→ D-002、D-003；插件状态再读 D-020 |
| paper-body 插件 | `plugin-samples/README.md`（当前插件 API / 示例）→ Architecture「Paper 与 paper-body 插件」→ D-004；Edge mini 看 D-018，插件数据看 D-020 |
| MCP / 插件外部写入 | Architecture「进程与运行时边界」「外部读写」→ D-021 → `PaperCommandService` / transport adapter 当前代码 |
| 托盘 / Hardcodet | Architecture「OS 与全局集成」→ D-017 → 当前 tray / vendored fork 代码 |
| Note / Markdown | Architecture 的 Paper/Note 边界；涉及单正文 surface 看 D-019，再读当前 Markdown 代码 |
| 架构重构 / 恢复旧方案 | Architecture + 相关 Decisions + 相关 git/PR 历史，全部核对后再改 |
| CI / 发布 / CHANGELOG | 本文件对应章节 + `.github/workflows/` / 当前脚本 |

**按需读取：**默认只加载当前任务相关的 Architecture 章节和 Decisions 条目；除非任务本身是架构重构、全局审查或恢复旧路线，不默认全文加载所有历史。

Edge 任务先阅读本文件的硬约束，再按实际影响范围选择资料：

- 文案、颜色或不改变布局/交互边界的普通参数调整：读取相关当前代码及局部规则，不默认加载完整 Edge 历史。
- 状态、布局、命中、拖拽、窗口交接或动画调整：读取 Architecture「Edge Capsule V3 Lite」的对应小节和相关 Decisions。状态看 D-005，队列/几何看 D-006，命中看 D-014，拖拽看 D-011，surface/composition/交接看 D-007～D-010、D-013，帧节拍看 D-012；涉及插件 mini 再读 D-018。跨多个边界时合并读取。
- 整体呈现路线重构、Edge 全局审查或恢复旧方案：读取完整 Edge 架构、D-005～D-014，以及涉及插件 mini 时的 D-018，并核对相关 git/PR 历史。

阅读中发现影响超出初始判断时，先补读受影响边界再修改；按需读取不豁免任何既有硬约束。

不要只依赖当前对话、PR 描述或旧 Agent 记忆。判断**当前技术方向**先看 Architecture；判断**旧方案为什么被否决、能否恢复**先看 Decisions；决定**这次具体怎么改**必须回到当前代码。

## 文档与代码同步

每次代码变更在提交前做一次**知识影响判断**。按下面的 owner 更新；没有影响时可以明确不改对应文档，不为了“同步过”制造痕迹。

| 变化 | 知识 owner |
| --- | --- |
| 当前技术选型、ownership、数据域、关键结构或已确立方向变化 | `ARCHITECTURE.md` |
| 形成/推翻历史取舍、确认失败路线或可复用踩坑 | `DECISIONS.md` |
| Agent 工作方式、执行规则、禁区、CI/发布规则变化 | `AGENTS.md` |
| 局部隐藏不变量或危险边界变化 | 附近代码注释 |

涉及架构、ownership、历史方案或文档整理时，先检查现有文档，再读相关代码和 git/PR 历史；事实核对完成后再统一修订，不要边发现边写出随后又被推翻的说明。

不要新增并行描述“当前完整架构”的专题文档。专题材料只能补充根文档没有承载的局部信息，并明确指回 Source of Truth。一次性验证、PR 过程和临时手工场景不升级成长期验收矩阵；长期可证明的正确性优先进入编译、行为测试、诊断日志和可执行检查。

### `ARCHITECTURE.md` 写入规则

- 只记录**当前有效**的技术选型、结构、ownership 和已经确立的技术方向；不是历史日志，也不是未来 roadmap。
- ownership、主要数据流/数据域、持久化协议、paper/window/plugin 生命周期边界、关键 runtime 职责、重要 OS/进程集成和仓库主结构变化通常需要更新；颜色、文案、普通常量、普通算法细节和不改变职责边界的局部实现通常不写。
- 写入前重新核对当前代码入口、owner 和调用链；无法从当前代码和已确认选择中证明的猜测、候选方案不写。
- 可以简短说明当前方向的核心理由；历史试错、完整 trade-off、失败路线和“为什么不能回去”放到 Decisions。普通毫秒数、重试次数、尺寸、诊断阈值等易变参数留在代码，除非数值本身就是协议/兼容边界。
- 被替代的当前机制直接从 Architecture 正文移除；历史由 Decisions + git/PR 保留。若只是纠正文档与既有代码的偏差，按事实校准处理，不伪装成架构变更。

### `DECISIONS.md` 写入规则

- Decisions 是**历史技术记忆**，记录以后仍需要知道的 context / why / trade-off / rejected route / pitfall。普通 bugfix、参数微调、UI 调整、测试结果和临时诊断不自动新增 decision；只有最终形成可复用选择或教训时才提炼。
- 写新条目前先搜索现有 D-xxx。既有 Accepted 条目可以修正事实错误、补证据或澄清原意；如果技术选择本身已经改变，优先新增下一条 D-xxx，并把旧条目标为 `Superseded by D-xxx`，不要把历史改写成“从来没走过旧路”。
- 新条目优先包含 `Status`、`Context`、`Decision`、`Why`、`Evidence`；确有危险旧路线时再写 `Rejected / Do not reintroduce`，需要时加 `Consequences`。
- `Rejected` 只记录已有证据证明危险、复杂或不符合当前路线的方案，不把“没选中”自动升级成永久禁令。
- `Evidence` 优先指向当前代码中的文件/类型/关键入口；历史因果重要时补关键 commit/PR。聊天记录不作为长期证据。
- Decisions 不是 changelog。把大量试错压缩成背景、选择、关键失败原因和以后不能忘的边界；完整过程留在 git/PR。检查后确认没有新的历史取舍或必要补充时，不修改 Decisions。

## 工作方式

不要把临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现作为最终交付。快速诊断、probe、日志、实验和可回退验证可以使用，但结论确认后要么进入正式结构，要么删除/明确隔离；不要让诊断结构演化成永久第二套机制。既定路线内、用户已授权任务范围内的实现细节由 Agent 自行决定并推进；需要重新选择或推翻已确定的产品/技术路线时，先与用户确认，再实施路线变更。确认时说明现有路线为何不足、推荐方案和主要代价，不只询问“是否继续”。模型换代本身不构成重开技术选型或放宽已验证约束的理由。

避免两个极端：不要为缺乏证据的少数极端场景把系统膨胀成过重框架，也不要用一次性补丁不断叠加并行状态。优先修清 ownership、数据流和真实高风险边界。

小改默认直接在目标分支完成；只有改动较大、风险较高或需要独立验证时再开分支。

- 在 Web Chat 中处理本仓库时，先发现并优先使用当前可用的 GitHub 连接器操作；不得仅因本地缺少 `git` / `gh`、凭据或网络，或依据旧会话、通用产品说明，就断言无法提交、推送或创建 PR。
- 对用户已授权的 GitHub 写入，优先使用当前实际可用的连接器操作执行，并在写入后回读核对结果；受阻时应区分“工具未提供”“连接器授权 / 仓库策略限制”和“调用故障”，报告具体证据，不得未经核验笼统声称“只有读取权限”，也不要为了探测权限创建无意义提交或 PR。

验证规模按行为风险和影响范围决定，不按修改行数决定。目标与改动点明确、低风险的局部修改，只核对相关上下文、最终 diff 和必要的直接验证，不重复读全仓、整文件或走完整审查流程。涉及持久化、兼容性、权限或窗口/状态交接等高风险边界，即使只改一行也要完成对应验证。既有明确的构建、CI 和发布验收要求仍须遵守；验证充分后停止，只有新的失败证据或尚未覆盖的具体风险才扩大或重复检查。

需要提交时，如果改动能按功能边界无损拆分，并且每个提交都保持**可构建、可理解、可独立回滚**，应拆成独立提交；否则保持原子提交。不要混入无关文档、备份文件或用户的其他改动。

## 产品边界

PaperTodo 当前交互中心仍是“桌面上的几张纸”。没有明确产品决策时，不要把局部需求自行扩张成中心式任务管理器、中心式知识库编辑器、主管理页或整套账号/云同步/分类/标签/搜索/归档系统。

这只是默认防扩张规则，不是永久否决清单。已经存在的能力或后续明确的新方向以当前代码和最新 decision 为准；产品边界发生变化时更新 D-001 和本节。

Markdown 当前保持轻量。若要扩展到网络图片、表格、附件、块级 HTML 或完整块编辑器，先按产品/架构变更处理，不要在局部渲染代码里偷偷扩协议。

## 数据与持久化硬约束

当前数据结构和技术方向见 Architecture「状态与持久化架构」；历史安全取舍见 D-002、D-003、D-020。

- `data.json` 是核心用户数据协议，不是缓存。字段删除/改名必须考虑旧数据兼容。
- 不绕过 `StateStore` 建立第二套主状态写入；保留版本化写入和退出同步保存语义。
- 不绕过 `NoteImageStore` 直接开启 LMDB transaction；图片 GC / id reuse 不能在保护引用扫描不可信时继续执行。
- provider settings / per-paper plugin state 由 `PaperBodyPluginDataStore` 管理；不要塞回 `data.json`，也不要让插件自行建立另一套会与宿主竞争的 authoritative state。
- 启动解析失败时不能用默认空状态覆盖旧数据；crash handler 不走普通“最后强存一次”流程。
- 普通纸片几何与 edge slot/expanded 恢复几何不能互相覆盖。
- 外部打开笔记的临时文件后缀只做文件名合法性校验；不要擅自收窄成固定白名单。

## 外部 Paper/Todo/Note 写入

- 插件 Host API 与 GUI 侧 MCP 对 Paper/Todo/Note 的共享业务 mutation 必须经过 `PaperCommandService` 及现有 commit/rollback/event 边界；不要各自在 transport 或 surface 层直接修改 `AppState` 后自行保存、回滚或刷新 UI。
- 权限判断、transport 和 surface 生命周期仍属于各自上层；不要反过来把 MCP/WebView/Native plugin 的传输或 UI ownership 吸进 `PaperCommandService`。

## 单实例与托盘

- **正常 GUI 模式**下只有主 GUI 实例拥有并释放 single-instance Mutex；后续 GUI 启动只转发命令并退出。`--mcp` bridge 在 GUI 单实例协议之前分流，不受该 Mutex 规则约束。
- `exit` / `quit` 在没有现成 GUI 主实例时也不能为了执行命令恢复窗口或创建默认纸片。
- 托盘当前技术路线见 Architecture「OS 与全局集成」，历史原因见 D-017。不要把 Hardcodet `TaskbarIcon.IconSource` 改回 `System.Drawing.Icon`，也不要用手动 popup、预热菜单或全局鼠标轮询重新修同一首次菜单问题。

## Edge Capsule 硬约束

Architecture / Decisions 按「项目知识入口」中的 Edge 影响范围路由读取。以下硬约束仍须直接遵守，不因减少历史阅读而放宽：

- 单纸片 desired model / target / transition / applied frame 只有一个 `EdgeCapsulePresenter` authority；队列级 preview/transaction 由 controller 协调，但不能形成第二份 per-paper model。
- 队列 index/master offset/slot count 只由 `EdgeCapsuleQueueCoordinator` 计算；docked 物理像素几何只由 `EdgeCapsuleGeometry` 计算。**队列不分页。**
- `EdgeCapsuleHost` 只拥有 docked bounded host；`EdgeCapsuleDragWindow` 只拥有 floating surface。不要把同一 HWND/visual tree 在两种外形之间复用。
- WPF/bounded host 拥有 shape；DComp queue proxy 只做同尺寸 live-surface translation。不要重新引入 snapshot、clip/scale/effect resize、Reveal/Conceal 或 deferred-resize backend。
- proxy、real HWND、floating cover 的 visual authority 必须显式交接；任何失败路径不能出现 all-hidden gap，也不能用固定 delay 当作 terminal-frame 正确性的证明。
- pointer/preview 命中以当前 presented/applied `InteractiveBounds` 为 truth；透明 `HostBounds`、proxy envelope 和 WPF enter/leave 本身不能扩大或替代真实 hit geometry。
- preview session 建立后，owner 是 queue-wide 的 owner/target/corridor/outside arbiter；transfer corridor 只用于跨空白连续移动，不是新的 hit area。不要在 host/WPF 输入层复制第二套 preview 状态机，也不要让预测逻辑否决已经真实离开合法 transfer region 的事实；pointer capture 期间应让当前交互继续持有退出判断。
- `MasterCapsuleWindow` 只拥有 slot 0、自身 pill/手势和队列纵向锚点，不持有真实纸片的第二套 presenter 状态。
- 拖拽期间收到的全局 arrange 不能静默丢弃；display/DPI/z-order/drag 等环境边界必须先安全结束或恢复当前 visual authority，再进入下一状态。
- 插件 edge mini 由宿主拥有窗口/队列/输入 authority；当前技术边界见 Architecture「Edge mini」，历史演进见 D-018。不要把任意 child HWND/WebView2/已挂载控件直接塞进可迁移 WPF mini，也不要在插件侧复制宿主的队列/尺寸 authority。

## 待办、笔记、主题与资源

- 多行粘贴待办形成一次用户操作时，只形成一次撤销快照。
- `PaperItem.LinkedPaperId` 是跨纸片关系，不要只在单个 UI 路径里清理。
- 内置 Note 编辑/浏览共享一个 `MarkdownTextBox`；不要拆成两套独立文本 surface（见 D-019）。`MarkdownTextBox` 长度上限属于 WPF 布局/渲染保护，不要无依据删除。
- 用户可见文本同步中文、英文、日文、韩文资源；`ResourceTextVersion` 只是人工检查标记，不参与运行时逻辑。
- 主题变化要主动刷新动态生成控件、托盘菜单、AvalonEdit 背景/文本/光标/覆盖层；不要假设所有动态 UI 都会自动响应资源变化。
- `EnableToolTips` 只控制普通操作提示，不关闭设置页说明图标和扩展说明。

## 用户态更新日志

`CHANGELOG.md` 面向用户，只记录从上一个正式版到当前最终状态的**用户可感知差异**；实现过程、开发期回归、协议阶段和内部重构留在 git/PR。

- `### 计划 / 待办` 写尚未完成的产品计划；`### 评估` 写取舍/暂缓原因；`### Unreleased` 只写已经完成、最终会进入下一版本的用户变化。
- **只要本次任务产生用户可感知差异，任务结束前都必须对照 `Unreleased`，在需要时更新。**
- 正式版已存在的问题被修复时写入；只在尚未发布开发过程中引入又修掉的回归，不单独作为用户 Bug 条目。
- 同一未发布功能后续增强直接合并进原条目，描述最终能力；不要留下 1.1 → 1.2 → 1.3 式开发演进。**开发期修复如果改变了该功能最终对外行为，也必须同步改原条目。**
- 纯内部文档、测试、CI、文件整理和无用户行为变化的重构不写 Unreleased。
- 发布前从“上一个正式版用户”的视角重读整个 Unreleased，删除阶段性和被替代描述。
- 版本小节保持既有顺序；只给真正重点内容加粗，不为格式统一滥用粗体。

## 构建与发布

- 版本号显式维护在 `PaperTodo.csproj`；不要恢复自动递增。
- `plugin-samples/` 保存插件源码/说明，`plugins/` 保存可直接加载的最终产物；主程序 publish/Release 不捆绑插件。最终插件目录不保留无必要的 PDB/XML/重复 native/shared assemblies。
- PR 分支 Windows CI 由 HEAD commit marker 控制：`[debug]` → Debug 测试包，`[ci]` → Release build，`[debug-ci]` → 两者。标记必须在本次 push 的最后一个 HEAD；不要为了触发制造空提交。
- 不重新引入已删除的 `scripts/edge-refinement-tests/` 或依赖源码字符串/文件路径/方法排列的 source-shape test；若新增 Edge 自动化，应验证可执行 reducer/geometry/policy/transaction 行为，而不是源码排布。真实集成回归仍依赖编译、诊断日志和真机验证。
- 普通编译：`dotnet build PaperTodo.csproj -c Release`。
- `vendor/wpf-notifyicon` 使用父仓库记录的固定 submodule commit；更新 fork 时显式更新 gitlink，并完成构建和真实托盘手测。构建过程不自动拉取最新分支。
- 云端 Release 发布 Windows x64 self-contained 与 no-runtime 两个单文件；本地打包只生成 no-runtime。WPF 版本不启用 `PublishTrimmed` 或 Native AOT。
- 普通 build/publish 使用仓库内默认 `papertodo_lmdb.dll`；GitHub Release 必须先从仓库内 LMDB 源码 `-ForceRebuild`，不能把默认 DLL 冒充云端编译产物。
- 稳定正式版只通过完成真实多屏/混合 DPI 等发布前手测后的 `workflow_dispatch` 发布；稳定 tag push 不是发布步骤。`rc` / `alpha` / `beta` / `preview` tag 可以发布预发行版。

## 更新本文

只有 Agent 路由、执行方式、产品默认边界、数据安全禁令、关键不可破坏 invariant、CHANGELOG/CI/发布规则等发生变化时才修改 `AGENTS.md`。已经验证有价值的详细执行规则可以长期保留；当前技术选型/方向更新 Architecture，历史取舍/踩坑更新 Decisions，普通 UI/参数变化不为了制造同步痕迹修改本文件。
