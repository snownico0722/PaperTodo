# 输出目录经验归档核对（2026-09-20）

本次为清理前的历史资料核对，没有重新运行性能实验、构建或删除文件。结论是：**绝大多数核心经验已经进入云端；本地大目录主要承担原始证据、旧包和复现副本的保存。** 两处尚未找到对应详细记录的历史补充收录于下文。云端已有记录不意味着候选已经合并，也不意味着原始日志已完整上传。

当前技术方向仍见 [ARCHITECTURE.md](../ARCHITECTURE.md)，历史取舍见 [DECISIONS.md](../DECISIONS.md)，既有实验见 [EXPERIMENTS.md](../EXPERIMENTS.md)。本记录不增加技术决策，不恢复失败实现，也不把旧实验写成当前版本的验收结论。

## 核对范围与版本

- 云端主线固定为 [`c10837bb9efb0b306b993805663b15ea78b1b714`](https://github.com/snownico0722/PaperTodo/commit/c10837bb9efb0b306b993805663b15ea78b1b714)。通过 GitHub API 核对分支和文件 blob SHA，再与本地相同 Git 对象比对，未将本地较旧 main 当作云端现状。
- 主线 `doc/EXPERIMENTS.md` blob：`d5cafba323286e3dff1715507d7b09b34b82064f`；`doc/DECISIONS.md` blob：`3066f17b062a2bf48f1d1231f5b25e45c73ec554`。
- PR #260 检查 HEAD：`953ae6266af2bc8c5d1696be7660e5006525202b`，仍为 Draft、未合并。PR #290 的 SoftwareOnly 候选已完成取证并按失败实验收口：专用实现不进入主线，只归档实验记录与逐轮数据。
- 路线 3 的原候选 #265 已关闭、未合并；知识整理 #267 已合并。#238 的实际 API 状态为已合并，其旧描述中的 Draft 字样不能代替当前状态。
- 本地检查 `正式版/输出` 的完整文件体积清单、主要最终报告/索引、相关 JSON，以及下文导航检查两次失败日志；没有逐一重新解释全部原始 journal、图像或 45 万个文件的内容。

目录共 **455,490 个文件，82,833,585,655 字节，约 82.83 GB / 77.14 GiB**，无扫描读取错误。该数字是文件逻辑长度之和，不是释放磁盘空间的精确承诺。28 个顶层项目的计数及体积见 [目录清单](output-knowledge-audit-20260920/output-inventory.csv)。

## 已进入云端的知识

以下“主线”均指上述固定提交；PR 分支记录单独列出，不与已采用实现混同。

| 本地材料 | 云端保存位置 | 已保存的主要经验 |
| --- | --- | --- |
| `edge-replay-20260913`、两个中文诊断包 | 主线 E-005、D-037 的历史来源 | 严格共同动作窗、历史版本顺序、代理复用收益与代价；旧 watchdog 更新不能换算成物理 FPS |
| `edge-journal-20260913` | 主线 E-006 | 内存采集、退出封存、日志扰动；单 owner 形状变化与整队列回调分开 |
| `edge-cadence-20260913` | 主线 E-007、D-032 | 相同 RenderingTime 不代表同一次合法通知；按 QPC 推进，同时保留同步重入及事务保护 |
| `edge-deep-latency-20260913` | 主线 E-008、D-032 | WPF/Dispatcher 与原生等待的分段证据；无记录空档不能直接归因 GPU、GC 或一次绘制 |
| `edge-pointer-filter-20260913` | 主线 E-009、D-032 | Pointer 排队或订阅次数减少没有自动带来节拍改善；已测候选撤回 |
| `edge-render-chain-20260913` | 主线 E-010、D-032 | 请求、执行、提交及呈现反馈不是同一事件；需同时看提交前状态的新鲜度 |
| `edge-fixed-state-20260914` | 主线 E-011 | 固定机器状态后分布仍变化；CPU、日志模式和更新间隔各自比较，不能只凭历史绝对数字归因 |
| `edge-pr238-cause-20260914` | 主线 E-012、D-032 | 在直接父版本上做单变量对照；旧 watchdog 确实贡献更新，去重修正不是所有长尾的解释 |
| `edge-render-cause-20260914` | 主线 E-013、D-032 | MIL 通知下游也可能等待；粗时钟的零排队值不是亚毫秒保证；keep/resume 与计时策略未获稳定收益 |
| `edge-net11-20260914` | 主线 E-014 | 同包核验实际运行时模块；.NET 11 RC1 没有显示稳定解决卡顿的收益，零动作沙箱轮排除 |
| `edge-three-routes-20260914` | 主线 E-015、D-032 | 请求正常 Rendering 与直接补状态分开；HWND 少移动不等于完整交接正确；组合收益不自动相加 |
| `edge-request-handoff-20260914`、`edge-pr-integration-20260914` | 主线 E-016、D-038 | 采用活动 render demand 和 Detached Pointer 准入修正；源位置保留、提前归位和原生最终请求候选未作为性能优化采用 |
| `edge-route3-minimal-20260914` | [主线路线 3 复盘](edge-route3-20260915.md)，由 [#267](https://github.com/snownico0722/PaperTodo/pull/267) 合入 | 独立动画能力不等于产品收益；同包 OFF/ON 的准备、CPU、内存回退；透明度、真实输入与端点内容须各自验收 |
| `edge-input-fix-20260915` | [#260 的 E-017/E-018](https://github.com/snownico0722/PaperTodo/blob/953ae6266af2bc8c5d1696be7660e5006525202b/doc/EXPERIMENTS.md)、该分支 D-037 及两份样本 CSV | 输出/输入 HWND 分工、逐卡真实输入交还、BeginPaint/EndPaint 消费隐藏绘制；缓存、额外等待、方法 1/2 的失败与最终取舍 |
| `pr238-windows-validation` | [#238](https://github.com/snownico0722/PaperTodo/pull/238)、主线 D-032～D-035 | 调度与输入边界、共享 STA、不可变 artifact 到单 renderer 的后续替代关系；旧测试包不代表当前实现 |
| `pr260-user-test-20260915`、其构建脚本 | [#260](https://github.com/snownico0722/PaperTodo/pull/260) | 是旧测试交付材料；核心产品修复与取舍已经记录在该 PR，未发现需新立决策的独立结论 |

SoftwareOnly 的新资料实际在 `输出` 之外的 `diagnostics` 目录；已整理为 [SoftwareOnly 渲染实验记录及逐轮 CSV](edge-software-rendering-20260920.md) 进入主线归档。它不应被算作此次 `输出` 目录内唯一尚存的结论。

本地封存报告的旧编号并非云端当前编号：旧 E-003～E-014 在主线整合后对应 E-005～E-016，例如本地 `.NET 11` README 的 E-012 实为当前 E-014。#265 历史上的 E-017/D-039 也不能与 #260 的 E-017/E-018 混用。核对必须同时看提交、目录和标题。

## 补充一：E-007 后剩余空档的逐事件复核

本地 `edge-delay-audit-20260913/剩余延时核对.md` 是对 E-007 已封存最终两轮的只读复核。此次在已检查的云端主线、#260 实验/决策以及路线 3 专题中，没有找到这三个具体样本及其对应数值。核心因果边界在 E-008～E-013 已有延续，补记的作用是保存该轮独立证据，不重开已做过的实验。

原报告沿用 unique presenter/transition、owner episode 和实际宽高/透明度变化规则，复算为 393 / 383 个间隔。三个样本摘录于 [remaining-rendering-gaps.csv](output-knowledge-audit-20260920/remaining-rendering-gaps.csv)，保留 seq、QPC 和来源指标；CSV 的下一 callback 时间直接从原 JSON 中的 `render.callback` 事件提取。

| 运行及 seq | 形状更新间隙 | 下一 Rendering 相对前次更新 | 回调到本次 shape.applied |
| --- | ---: | ---: | ---: |
| final-full-r1，111989 → 112188 | 38.1658 ms | 37.9989 ms | 0.1669 ms |
| final-full-r1，43242 → 43316 | 35.1723 ms | 35.0479 ms | 0.1244 ms |
| final-full-r2，63629 → 63721 | 37.6728 ms | 37.6161 ms | 0.0567 ms |

空档中存在应用线程处理的鼠标消息；下一合法 callback 到达后，实际 shape 很快更新。重叠已打点 scope 的最大值仅为 0.2438 / 0.1939 / 0.0952 ms，第一、三例末尾的退订窗口约 0.237 / 0.240 ms，第二例没有订阅变化。因此，**不能把整段 35～38 ms 说成已观测到的 UI 连续阻塞、同一次绘制或退订耗时**。打点覆盖有限，同样不能据此排除未观察的 WPF/GC 工作或认定唯一底层原因。

另一组准备成本必须分开：两轮 `proxy.prepare` P95 为 14.522 / 13.670 ms，最慢为 20.022 / 21.705 ms；两次均 `reused=10, created=0`，对应 `EndDeferWindowPos` 计时为 19.258 / 20.550 ms，prepare 内 GC 计数为 0。缓存命中并没有消除真实窗口同步提交。最慢事务 22.036 / 23.843 ms 来自另外的样本，不能拿它们与上述 prepare 或更新空档拼成一条时间链。

本次只从保存的 JSON 复核三个样本的 QPC 差值与 callback 时间，没有重新回放或全量重算当时的 393 / 383 个间隔。它们都是应用侧历史事件，不是 present、物理 FPS 或当前版本的性能承诺。

## 补充二：路线 3 接入前的历史基线没有全绿

`edge-route3-product-20260914/CLOSEOUT.md` 记录了停止继续实现时的检查点，早于后续路线 3 失败归档。基线为 `26142c1d1d055c35e2b1c8843cee6030203a31cc`，合并了当时 #260 候选和 main，冻结 669 个文件。

- 标准 Release 构建成功，0 警告、0 错误。
- 12 个配置检查组中 **11 个通过、1 个失败**，不是“全部测试通过”。各组原退出码见 [route3-baseline-checks.csv](output-knowledge-audit-20260920/route3-baseline-checks.csv)。
- TodoNavigation Release 的 `Native End then Up crosses from first line with trailing X` 连续两次失败，退出码均为 `-532462766`。此次同时核对原始两份检查日志，未只依赖汇总文字。
- 失败发生在冻结基线，尚未混入路线 3 候选；当时原因未定位，也没有运行该任务的性能回放。不能将它归因为后续代理 shape 候选，或把该基线当作全绿对照。
- 当时三个候选 worktree 只是 WIP：source `39f10c1`、input `e56623d`、native shape `fe2bdec`，尚未构建/实机验证。它们目前位于 `输出` 之外；目录清理与是否继续这些分支是不同事项。

该记录只补齐 **2026-09-14 的历史基线边界**。后来云端 E-016、#260 或其他版本的通过结果使用不同提交/检查点，不能相互覆盖；本次没有重测当前 main，不能据此宣称当前仍存在同一导航失败。无需为这个旧检查点新增架构或技术决策条目。

## 来源完整性

下列 SHA-256 对应本次读取的原始本地文件，不是对全部 45 万个文件重新做哈希后的声明。

| 来源，均相对 `输出/` | SHA-256 |
| --- | --- |
| `edge-delay-audit-20260913/剩余延时核对.md` | `dc66dd17d1e8b88343d890204ac59e607029a66717527d664669d79d2468db30` |
| `edge-delay-audit-20260913/remaining-long-gaps.json` | `5f74ef1937ec1b65981d64b25961b1da0750706fb2cf8ef0788b0e320936bafd` |
| `edge-route3-product-20260914/CLOSEOUT.md` | `cf4996ee9b3ee6d17a85c0b082e5c5c5ac33972f78a334a5e8b5f3cdc9464864` |
| `edge-route3-product-20260914/closeout-validation-summary.json` | `d9b880cb4b554fcc2127a93b68aee21acc8ff31356c3f51e84b8727bccb24362` |

## 对本地保留的判断

`输出/` 被 Git 忽略，项目也将其排除出源码输入；普通构建通过 `OutputPath` 再次生成版本输出。因此旧 EXE/DLL、bin/obj、重复源码与输入副本、解包运行时和应用缓存不必为了保存经验而整目录保留。未采用候选的独有源码也不应一概声称能从 main 重建，应按上述 PR 历史或少量最终补丁另行追溯。

若仍要使用同一录制做后续对照，最有用的操作材料是 `数据.exe` 与一份 `实机数据`，合计 **131,480,623 B，约 125.39 MiB**；保留理由是复测价值，不是个人资料。回放和分析脚本按实际复测需求选择一套可用版本，没必要连同每次运行的程序、字体和插件副本全部保存。

若目标只是保留已确认经验，云端结论、失败取舍和小样本表格已能承担主要用途。原始 journal、像素、dump 仍有追溯价值：删掉后不能仅凭摘要重新计算所有旧分位数或重做像素分析。它们不是工程日常构建的必需文件，也不应被称作“完全没用”。本次仅完成归档核对和补充，**没有执行任何删除、移动或压缩**。
