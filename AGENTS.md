# PaperTodo Agent 备忘

本文件记录项目专用的执行规则和修改禁区。

## 项目知识入口

从下表选取任务入口，再读相关代码。当前架构见 [ARCHITECTURE.md](doc/ARCHITECTURE.md)，历史取舍见 [DECISIONS.md](doc/DECISIONS.md)。产品要求以用户明确要求和有来源的已确认决策为依据；代码、测试和可观察行为说明实现现状，Agent 维护的文档不能自行产生产品要求。资料有冲突时，分别核对需求来源与实现证据，再修正受影响内容。

| 任务 | 优先读取 |
| --- | --- |
| Edge Capsule / 胶囊流畅性 | 本文件「Edge Capsule 硬约束」→ 按下述影响范围读取 Architecture / Decisions → 相关当前代码 |
| 持久化 / 恢复 / 图片 | Architecture「状态与持久化架构」→ D-002、D-003；审查空工作区初始化和启动图片清理时，遵循 [D-070 已接受的主工作区缺失边界](doc/DECISIONS.md#主工作区缺少全部核心文件)；插件状态再读 D-020、D-040 |
| paper-body 插件 | `plugin-samples/README.md`（当前插件 API / 示例）→ Architecture「Paper 与 paper-body 插件」→ D-004；Edge mini 看 D-018，插件数据看 D-020、D-040 |
| MCP / 插件外部写入 | Architecture「进程与运行时边界」「外部读写」→ D-021、D-040 → `PaperCommandService` / transport adapter 当前代码 |
| 托盘 / Hardcodet | Architecture「OS 与全局集成」→ D-017 → 当前 tray / vendored fork 代码 |
| Note / Markdown | Architecture 的 Paper/Note 边界；涉及单正文 surface 看 D-019，再读当前 Markdown 代码 |
| 架构重构 / 恢复旧方案 | Architecture + 相关 Decisions + 相关 git/PR 历史，全部核对后再改 |
| CI / 发布 / CHANGELOG | 本文件对应章节 + `.github/workflows/` / 当前脚本 |

**按需读取：**默认只加载当前任务相关的 Architecture 章节和 Decisions 条目；除非任务本身是架构重构、全局审查或恢复旧路线，不默认全文加载所有历史。

Edge 任务先阅读本文件的硬约束，再按实际影响范围选择资料：

- 文案、颜色或不改变布局/交互边界的普通参数调整：读取相关当前代码及局部规则，不默认加载完整 Edge 历史。
- 状态、布局、命中、拖拽、窗口交接或动画调整：读取 Architecture「Edge Capsule V3 Lite」的对应小节和相关 Decisions。状态看 D-005，队列/几何看 D-006，命中看 D-014，拖拽看 D-011，surface/composition/交接看 D-007～D-010、D-013，当前帧节拍看 D-038，历史取舍看 D-012、D-032；涉及插件 mini 再读 D-018。跨多个边界时合并读取。
- 自由队列与主胶囊：[D-066](doc/DECISIONS.md#d-066---free-queues-extend-existing-direct-manipulation) 记录最初的拖动设计，主胶囊归属看 [D-067](doc/DECISIONS.md#d-067---free-queue-masters-follow-queue-identity-2026-11-04)，插入与独立落点看 [D-068](doc/DECISIONS.md#d-068---free-queue-drops-preview-independent-placement-and-prefer-downward-insertion-2026-11-05)、[D-069](doc/DECISIONS.md#d-069---independent-landing-ghosts-consume-the-adjacent-gap-before-moving-neighbours-2026-11-05)，关闭自由队列后的 Ctrl 拖动看 [D-072](doc/DECISIONS.md#d-072--ctrl-整队拖拽归属胶囊自由队列)。
- 整体呈现路线重构、Edge 全局审查或恢复旧方案：读取完整 Edge 架构、D-005～D-014，以及涉及插件 mini 时的 D-018，并核对相关 git/PR 历史。

发现改动跨越其他边界时，先补读对应资料和硬约束。

## 文档与代码同步

提交前按实际变化更新对应位置。

| 变化 | 知识 owner |
| --- | --- |
| 当前技术选型、ownership、数据域、关键结构或已确立方向变化 | `ARCHITECTURE.md` |
| 形成/推翻历史取舍、确认失败路线或可复用踩坑 | `DECISIONS.md` |
| Agent 任务路由、执行规则、产品边界、数据安全约束或 CI/发布规则变化 | `AGENTS.md` |
| 用户操作入口、选项位置或操作步骤变化 | 对照实际界面，同步[中文用户指南](doc/USER_GUIDE.md)和[英文用户指南](doc/USER_GUIDE.en.md)中的相关说明 |
| 局部隐藏不变量或危险边界变化 | 附近代码注释 |

架构或决策变更先核对现有条目、当前代码和相关 git/PR 历史。专题文档只补充局部信息，并链接回对应主文档。一次性验证留在 PR，长期需要保护的行为优先用行为测试、诊断日志或可执行检查验证。

### `ARCHITECTURE.md` 写入规则

记录已核实的职责、数据流、持久化协议、生命周期、集成边界及有依据的技术约束。机制被替代时直接更新正文，历史留在 Decisions 和 git/PR；普通参数留在代码，只有构成协议或兼容边界的数值进入架构。

- **应写：**谁拥有状态、谁负责写入与释放、操作经过哪些边界，以及已验证的兼容性／数据安全约束。
- **不应写：**未经确认的产品要求、为现有代码事后编造的设计理由、把一次实现写成永久禁令、按钮文案和布局细节清单、临时排障或待选方案。用户操作说明归用户指南，未确认建议留在 PR。
- **证据不可循环自证：**代码只能证明“现在这样做”，测试只能证明其覆盖的行为；两者都不能证明“用户要求这样做”。不得把另一模型的总结或旧架构文字当成用户授权。用户已明确纠正的要求直接落实，不为维护旧文档而新增手势、入口或兜底。
- **更新而非叠加：**修改时替换失效正文、清掉相反表述和重复说明；保留仍有效的技术边界，不借局部修正文档重开无关选型。误写的理由直接纠正并在 PR 说明，不伪装成曾经确认的历史决策。

### `DECISIONS.md` 写入规则

新增决策前先搜索现有 D-xxx。事实澄清可补入原条目。选择改变时新增决策，并在旧条目和索引中注明被替代的范围。条目格式、证据和退役路线的记录标准见 [Decisions 维护规则](doc/DECISIONS.md#维护规则)。

## 工作方式

不要把临时最简原型、止血式局部假模型或明显偏离产品形态的替代实现作为最终交付。快速诊断、probe、日志、实验和可回退验证可以使用，但结论确认后要么进入正式结构，要么删除/明确隔离；不要让诊断结构演化成永久第二套机制。既定路线内、用户已授权任务范围内的实现细节由 Agent 自行决定并推进；需要重新选择或推翻已确定的产品/技术路线时，先与用户确认，再实施路线变更。确认时说明现有路线为何不足、推荐方案和主要代价，不只询问“是否继续”。这些要求同样适用于 6sol、6ASTRA 及后续模型：接手时回读本次用户要求、相关决策来源和当前代码，不把上轮 Agent 的建议升级为要求，也不因模型切换新增规则或恢复已撤销方案。模型换代本身不构成重开技术选型或放宽已验证约束的理由。

避免两个极端：不要为缺乏证据的少数极端场景把系统膨胀成过重框架，也不要用一次性补丁不断叠加并行状态。优先修清 ownership、数据流和真实高风险边界。

小改默认直接在目标分支完成；只有改动较大、风险较高或需要独立验证时再开分支。

- 在 Web Chat 中处理本仓库时，先发现并优先使用当前可用的 GitHub 连接器操作；不得仅因本地缺少 `git` / `gh`、凭据或网络，或依据旧会话、通用产品说明，就断言无法提交、推送或创建 PR。
- 对用户已授权的 GitHub 写入，优先使用当前实际可用的连接器操作执行，并在写入后回读核对结果；受阻时应区分“工具未提供”“连接器授权 / 仓库策略限制”和“调用故障”，报告具体证据，不得未经核验笼统声称“只有读取权限”，也不要为了探测权限创建无意义提交或 PR。
- Agent 创建 PR，或更新当前用户自己创建的 PR 时，PR 标题和描述默认使用中文；维护其他贡献者已经以英文创建的 PR 时继续沿用英文，不为了统一语言强制改写。

验证规模按行为风险和影响范围决定，不按修改行数决定。目标与改动点明确、低风险的局部修改，只核对相关上下文、最终 diff 和必要的直接验证，不重复读全仓、整文件或走完整审查流程。涉及持久化、兼容性、权限或窗口/状态交接等高风险边界，即使只改一行也要完成对应验证。既有明确的构建、CI 和发布验收要求仍须遵守；验证充分后停止，只有新的失败证据或尚未覆盖的具体风险才扩大或重复检查。

需要提交时，如果改动能按功能边界无损拆分，并且每个提交都保持**可构建、可理解、可独立回滚**，应拆成独立提交；否则保持原子提交。不要混入无关文档、备份文件或用户的其他改动。

### 代码审查与复杂度控制

审查以产品要求、实际正确性和职责边界为依据，不以发现问题的数量、增加的防御代码或删除的代码量衡量质量。没有发现需要修改的问题，是有效结论。

- **审查意见必须有依据。** 对需要修改代码的问题，简要说明可达的触发条件、违反的要求或不变量，以及实际后果。区分已确认的缺陷、有明确机制的风险和待验证的猜测；不得将纯理论可能性直接列为必须修复的问题。
- **证据不限于复现。** 可以通过复现、测试或完整的代码因果链确认问题。对于数据丢失、权限越界等高风险问题，不因尚未复现而忽略；也不以测试通过代替对未覆盖路径的判断。
- **问题成立，不代表建议的修法值得采用。** 修复前评估实际收益与新增复杂度，选择满足既定需求的最简单方案。不为低价值风险引入额外状态、兜底机制、兼容分支或跨组件依赖，不借修复擅自扩大产品的功能和支持范围。
- **检查职责边界。** 关注某项行为为什么依赖另一个组件、操作或生命周期，确认这种依赖有实际必要。测试通过不能证明需求合理或职责划分正确。
- **简化也需要依据。** 优先消除导致问题的不必要状态、依赖和分支，但不得为了减少代码而删除已支持的行为、必要保护或合理的恢复能力。
- **按功能组织文件。** 新代码优先放回负责该功能的文件。同一对象的状态、更新和收尾若总要一起读改，就放在一起。独立控件、跨模块共用的规则、有自己订阅和释放时机的组件，即使很短也适合单列。文件行数只用于筛查。移动源码时核对测试或基准工程的显式文件引用。
- **合并重复规则时保留入口差异。** 例如多处删除待办可共用末行占位和排序规则，调用方仍负责各自的动画、通知和保存时机。只比较关联 ID 时只保存 ID，需要撤销或失败回滚时保留足以恢复状态的快照。
- **删除前核对全部入口。** 删除前核对 UI、插件／MCP 公共接口和旧数据迁移。确认退役后，一起清理专属设置、持久化字段、资源和文档。仍受支持的别名继续可用。只为迁移保留的旧字段只在迁移时读取，运行时刷新应读取当前设置。
- **验证充分后停止。** 完成与变更相关的验证后，除非出现新的失败证据或具体未解决风险，不反复扩大审查范围，也不为凑出新问题继续修改。

### 测试增补与退出

按失败后果和现有覆盖缺口决定是否补测试，优先扩充已有行为用例。清理测试时在 PR 说明舍弃的覆盖。具体标准见[保留、增补与退出](tests/README.md#保留增补与退出)，命令见[运行入口](tests/README.md#运行入口)。

Markdown/AvalonEdit 的局部重绘与强制全量重绘缺少稳定的完成时序，禁止恢复两次离屏位图的逐字节对照，也不要用等待、重跑或放宽容差使其通过。

## 产品边界

PaperTodo 当前交互中心仍是“桌面上的几张纸”。没有明确产品决策时，不要把局部需求自行扩张成中心式任务管理器、中心式知识库编辑器、主管理页或整套账号/云同步/分类/标签/搜索/归档系统。

这只是默认防扩张规则，不是永久否决清单。已经存在的能力或后续明确的新方向以当前代码和最新 decision 为准；产品边界发生变化时更新 D-001 和本节。

Markdown 当前保持轻量。若要扩展到网络图片、表格、附件、块级 HTML 或完整块编辑器，先按产品/架构变更处理，不要在局部渲染代码里偷偷扩协议。

## 数据与持久化硬约束

当前数据结构和技术方向见 Architecture「状态与持久化架构」；历史取舍见 D-002、D-003、D-020、D-040。

- `data.json` 是核心用户数据协议，不是缓存。字段删除/改名必须考虑旧数据兼容。
- 不绕过 `StateStore` 建立第二套主状态写入；保留版本化写入。用户主动退出须同步保存成功才关闭；系统关机、崩溃和被动 Dispose 不新增最后保存。停止计时器不等于撤销已排队写入：实际写入入口和模态/异步返回后都要核对同一终止信号。
- 不绕过 `NoteImageStore` 直接开启 LMDB transaction；图片 GC / id reuse 不能在保护引用扫描不可信时继续执行。
- provider settings / per-paper plugin state 由 `PaperBodyPluginDataStore` 管理；不要塞回 `data.json`，也不要让插件自行建立另一套会与宿主竞争的 authoritative state。宿主不提供插件业务数据恢复系统；只保留普通文件读写和一次写入完整性，不恢复 `.json.recovered` 分流。插件自己的业务存储、备份和恢复由插件负责。
- 核心保存只同步内置 Markdown 的待提交文本；不要把第三方 `IPaperBodySession.Commit()` 恢复成全局保存钩子。外部写入只有在目标就是该内置 Markdown 时才先提交目标用户文本；修改其他纸片不得因此调用无关第三方正文的 `Commit()`。
- 启动解析失败时不能用默认空状态覆盖旧数据；crash handler 在日志/提示框之前封闭持久化入口，不走普通“最后强存一次”流程。
- 普通自动保存不监听或比对外部数据文件，仍按原有版本化原子写入；显式 `reload_data` 使过期排队保存失效，`merge_data` 必须携带调用方实际读取的原始基线，并在确认与提交之间核验本次外部输入未被替换。终止阶段禁止新写入，冲突差异记录及未完成临时记录仍作为图片保护输入，扫描不完整时禁止 GC/编号复用。
- 普通纸片几何与 edge slot/expanded 恢复几何不能互相覆盖。
- 外部打开笔记的临时文件后缀只做文件名合法性校验；不要擅自收窄成固定白名单。

## 外部 Paper/Todo/Note 写入

- 插件 Host API 与 GUI 侧 MCP 对 Paper/Todo/Note 的共享业务 mutation 必须经过 `PaperCommandService` 及现有 commit/rollback/event 边界；不要各自在 transport 或 surface 层直接修改 `AppState` 后自行保存、回滚或刷新 UI。
- 权限判断、transport 和 surface 生命周期仍属于各自上层；不要反过来把 MCP/WebView/Native plugin 的传输或 UI ownership 吸进 `PaperCommandService`。

## 单实例与托盘

- **正常 GUI 模式**下只有主 GUI 实例拥有并释放 single-instance Mutex；后续 GUI 启动只转发命令并退出。`--mcp` bridge 与 `--import-note-image` 单次导入在 GUI 单实例协议之前分流，不受该 Mutex 规则约束，也不创建第二份 GUI 状态。
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

- `README.md` 为默认英文首页，`README.zh.md` 保留中文，顶部互链。
- `README.zh.md` 本身保持完整中文内容；当用户要求把中文 README 翻译成英文 README 时，不把 QQ 群号、QQ群链接或其他 QQ 群联系方式翻译进英文正文。
- `README.zh.md` 是首页中文折叠区的唯一来源；修改后运行 `python .github/scripts/sync_readme.py`，同步更新 `README.md` 底部默认收起的完整中文 `<details>`，不要手改生成区。
- Release 说明由 `.github/scripts/release_notes.py` 从 `CHANGELOG.md` 与 `CHANGELOG.zh.md` 提取同一版本：中文折叠区放在最上面，英文正文随后，双语下载链接保持展开。发布前必须补齐两种语言的对应版本小节；仅补历史说明时只更新 Release 正文，不重建资产、移动 tag 或改变最新版本。
- 版本号显式维护在 `PaperTodo.csproj`；不要恢复自动递增。
- `plugin-samples/` 保存插件源码/说明，`plugins/` 保存可直接加载的最终产物；主程序 publish/Release 不捆绑插件。最终插件目录不保留无必要的 PDB/XML/重复 native/shared assemblies。
- PR 分支 Windows CI 由 HEAD commit marker 控制：`[debug]` → Debug 测试包，`[ci]` → Release build，`[debug-ci]` → 两者。标记必须在本次 push 的最后一个 HEAD；不要为了触发制造空提交。
- 不重新引入已删除的 `scripts/edge-refinement-tests/` 或依赖源码字符串/文件路径/方法排列的 source-shape test；若新增 Edge 自动化，应验证可执行 reducer/geometry/policy/transaction 行为，而不是源码排布。真实集成回归仍依赖编译、诊断日志和真机验证。
- 普通编译：`dotnet build PaperTodo.csproj -c Release`。
- `vendor/wpf-notifyicon` 使用父仓库记录的固定 submodule commit；更新 fork 时显式更新 gitlink，并完成构建和真实托盘手测。构建过程不自动拉取最新分支。
- 用户在 chat 中要求“打包”时，默认使用 GitHub Actions `Package`（`.github/workflows/debug.yml`）打包指定分支，配置为 `Release`（正式版）、Windows x64、`single_file=true`（单文件）、`include_dotnet=false`（不内置 .NET）；明确指定的选项只覆盖对应默认值。允许 `Release`/`Debug`、单文件/多文件、内置/不内置 .NET 三项独立组合；每次只生成所选组合的一个产物，非单文件以 ZIP 提供，不默认同时打包其他组合；单文件内部压缩保持关闭。普通聊天打包不创建 GitHub Release；主线 push 的自动打包也使用上述默认组合。对仓库内 PR 的默认包，可通过移除后重新添加 `package` 标签触发 `Package`，无需制造提交；工作流固定签出的 PR head SHA，不打外部 fork，不赋予写权限或 Secrets。其他打包选项仍通过手动 `workflow_dispatch` 选择。PR `[debug]` 测试包触发规则保持不变。
- 云端 Release 发布 Windows x64 self-contained 与 no-runtime 两个单文件。WPF 版本不启用 `PublishTrimmed` 或 Native AOT。
- 普通 build/publish 使用仓库内默认 `papertodo_lmdb.dll`；GitHub Release 必须先从仓库内 LMDB 源码 `-ForceRebuild`，不能把默认 DLL 冒充云端编译产物。
- 稳定正式版只通过完成真实多屏/混合 DPI 等发布前手测后的 `workflow_dispatch` 发布；稳定 tag push 不是发布步骤。`rc` / `alpha` / `beta` / `preview` tag 可以发布预发行版。
