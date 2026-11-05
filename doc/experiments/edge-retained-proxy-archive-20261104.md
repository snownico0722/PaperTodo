# Edge 常驻代理实验归档：#260 / #269 / #271 / #291

**归档日期：2026-11-04。** 本文保存四条实验线的方法、已验证收益、失败原因、后续纠正和未完成边界。归档决定是把知识纳入主线并关闭旧 PR；不合入实验产品代码、临时探针或自动实验工作流，不删除原分支。

当前架构仍以 [ARCHITECTURE.md](../ARCHITECTURE.md) 和当前源码为准；历史取舍见 [DECISIONS.md](../DECISIONS.md) D-037 / D-065；#260 的原始方法与聚合样本已整理为 [EXPERIMENTS.md](../EXPERIMENTS.md) E-017 / E-018。本文是 E-019 的历史详细附件，不是新的架构规范、待执行计划或长期验收矩阵。

## 1. 固定版本与证据边界

| 实验 | 归档时 HEAD | 基线与关系 | 最终知识状态 |
| --- | --- | --- | --- |
| [#260](https://github.com/snownico0722/PaperTodo/pull/260) | `953ae6266af2bc8c5d1696be7660e5006525202b` | 从 #258 拆出的静态预获取、常驻代理与容量复用组；目标 main | 有输入/绘制/交还修复和性能对照，整体未完成实机验收 |
| [#269](https://github.com/snownico0722/PaperTodo/pull/269) | `252952b0d85581606ffe61e003a0e028ef7de3b6` | 固定从 #260 `953ae626` 出发 | H1～H4、H6 有修复验证；H5 局部收窄；H7 后续候选未通过最终专项 |
| [#271](https://github.com/snownico0722/PaperTodo/pull/271) | `8752755c9e8e9eeadfdc365f9c8038c8ca8538c8` | 同样从 #260 出发，与 #269 并行，不含其全部修复 | H5 独立收敛；H8 双向输入错误和普通 worker 失败得到实证；独立 owner 窄原型可行 |
| [#291](https://github.com/snownico0722/PaperTodo/pull/291) | `5aa796befa944b53bd0e1191ded2bd4fe89b0246` | 从 #271 `8752755c` 出发 | 路线 1 有动态与连续浏览证据、仍未完成；路线 2 的所测提交顺序方案被否定 |

四个 PR 不是四项可独立相加的改进：#269 与 #271 共享 #260 基线，H5 部分重复；#291 继承 #271，并不自动包含 #269 的修复。关闭 PR 是实验收束，不把它们视作已经合并，也不把尚未完成的路线改成“失败”。

归档时核对的主线是 [`f6041ca6b5ebd787a02aa8158c6e67660b421be0`](https://github.com/snownico0722/PaperTodo/tree/f6041ca6b5ebd787a02aa8158c6e67660b421be0)。其中没有实验分支的 `EdgePrewarmCoordinator`、`EdgeCapsuleQueueDedicatedInputOwner`、`SettledInput` 常驻交还实现；`EdgeCapsuleQueueProxyWindow` 仍用 `WM_NCHITTEST` 调用当前命中回调。不能把 #260 的双 HWND / HRGN 问题直接当成当前主线的同一实现缺陷。#291 人工 A/B 使用的历史 main 则是 `c10837bb9efb0b306b993805663b15ea78b1b714`，与归档时主线不同。

### 1.1 这次实际保存了什么

本次按用户要求不新增云端性能对照。云端负载、图形环境和跨版本差异会干扰结果；旧数据仅作为特定版本/环境下的历史证据。结论更新依据现有源码、提交和后续证据，不为补齐结论重跑性能实验。

- 将 #260 的 E-017 / E-018 方法与结果纳入主线，明确原文“采用”仅指实验分支。
- 原样保存三份版本库内 CSV：[E-017 样本](E-017-retained-input-samples.csv)、[E-018 样本](E-018-admission-methods-samples.csv)、[#269 R0 样本](pr260-r0-coordination-samples.csv)。不重算或扩大其统计意义。
- 将分散在 PR 正文、阶段评论、源码和工作流中的结论合并到本文，保留固定 SHA、关键实现位置和 Actions 链接。
- 保留实验源码在原分支/提交历史中；不把一次性 workflow、按源码字符串打补丁脚本和失败探针搬入主线。

本次没有重新执行 Windows 实验。数值来自历史 PR/账本与现存 CSV；对 #269 最终 H7、#291 最终两次人工 A/B 另外回读了 Actions 作业结论。三个作业的原始日志下载均返回 **HTTP 410**，H7 步骤查询为空，因此不能声称本次重新读到了原始日志、确定 H7 最后一个异常，或重算了 #291 的每轮样本。保留这层证据来源，比凭历史绿色状态补造结果更重要。

#260 记录的本地 34 个运行目录、27 个测试包、原始 journal、实机数据和哈希目录，本次没有访问或重新复制；本文仅保留其已提交的索引性说明和聚合 CSV。旧 Actions artifact 可能过期，不能把其 URL 当成原始数据已永久备份的证明。

## 2. #260：长期复用为何不能只看动画和缓存

### 2.1 当时实现了什么

目标是减少首次接管和反复浏览的资源准备：空闲时提前接管真实静态队列，动画结束甚至收回胶囊后继续保留代理，后继事务复用设备、输出窗口和 live surface。单纸片 WPF source 只预留产品最大预览容量；输出窗口另外预留有限队列运动范围，避免把每张 source bitmap 扩成整队尺寸。

`EdgePrewarmCoordinator` 统一协调静态接管与一次性 graphics prewarm。工作由 Rendering 屏障转入低优先级 Dispatcher，每次准备一个队列；交互、拖拽、隐藏、关闭或显示环境变化撤销旧任务。暂未就绪的队列等待真实事件唤醒，不靠不断重试。Markdown speculative 预热可暂停，当前正文需求仍由原缓存系统负责，协调器不拥有第二份正文或纸片状态。

这些设计把短时动画代理变成长期承担显示与输入边界的对象，新增成本是资源驻留、容量失效、输入交还、空闲采样和取消/恢复协调。历史性能收益不能单独证明这组生命周期复杂度值得进入主线。

### 2.2 输入与绘制修正

| 问题 | 实验分支的处理 | 已有证据与限制 |
| --- | --- | --- |
| 透明输出范围挡住后方应用 | 输出 HWND 让出输入；独立输入 HWND 只对真实 `InteractiveBounds` 并集建立有限 HRGN | 跨线程、跨进程空洞 DOWN/UP 验证；不能用 `HTTRANSPARENT` 代替跨应用穿透机制 |
| 常驻代理只有转发按下，真实控件收不到完整交互 | 指针进入稳定卡片后，逐卡交还真实 WPF；其他胶囊保留代理 | Button/CheckBox hover、DOWN/UP、click、capture/cancel 验证；不是所有插件内容与光标/tooltip 场景的穷尽证明 |
| 整队交还导致反复冷接管 | 静止 successor 排除当前卡片；已交还且未移动的卡片不再次接管 | 位移或容量/身份变化时仍须重新准入，不承诺所有切换都免同步等待 |
| 隐藏 layered 输出不断收 WM_PAINT | 成对 `BeginPaint/EndPaint` 消费更新区 | 原配置约 10 秒 1,503,577 次消息；旧版失败、新版通过；不泛化到所有 layered HWND |
| 静止交还出现覆盖切换风险 | 保留旧 root，先揭示并验证真实 source，再发布排除该卡的 successor；后段失败先恢复旧 cover 再反向 cloak | 故障/回滚检查通过；原生可见标志不等于物理显示器已经出帧 |

关键源码：[窗口与 HRGN](https://github.com/snownico0722/PaperTodo/blob/953ae6266af2bc8c5d1696be7660e5006525202b/src/EdgeCapsuleQueueProxyWindow.cs)、[逐卡交还](https://github.com/snownico0722/PaperTodo/blob/953ae6266af2bc8c5d1696be7660e5006525202b/src/EdgeCapsuleQueueCompositionProxy.SettledInput.cs)、[预热协调器](https://github.com/snownico0722/PaperTodo/blob/953ae6266af2bc8c5d1696be7660e5006525202b/src/EdgePrewarmCoordinator.cs)。当时常规检查 3407 断言、原生专项 574 断言通过；原生专项使用产品 PerMonitorV2 apphost。断言数说明覆盖范围，不能代替显示时序或日用体验结论。

### 2.3 没有采用的性能路线

| 候选 | 测到什么 | 没有采用的原因 |
| --- | --- | --- |
| dormant surface/visual 缓存 | 两轮均 13/13 次合格重入命中；事务 P95 59.490 / 55.906 ms，对照 53.357 / 49.905 ms | 资源准备仅约 0.15～0.61 ms，数十毫秒的交接等待没有省掉；高命中不是用户收益 |
| union Commit 后增加 `WaitForCommitCompletion` | P95 64.145 ms，对照 54.040 ms | 增加等待，没有取得可采用收益 |
| 方法 1：按真实输入需求分类，背景继续保留代理 | 正常布局未找到可保留背景；18 次分类状态变化都需要真实输入，交还 17 次，对照 16/18 次 | 没有省交还的有效覆盖，不能靠削弱 hover/tooltip/插件输入契约制造收益 |
| 方法 2：首段 DwmFlush 移到 worker，UI 校验后续接 | transaction.commit P95 52.938～61.024 → 15.717～18.983 ms；新增 source 的 prepare→animation-clock P50 41.6795～49.6350 → 57.0199～58.0882 ms | 同步调用变短，但完整准备到动画启动更慢；没有纳入 #260 |

方法 2 v1 的 35 请求中有 23 次失败，根因是把合法端点更新误判为失效；该版本不能作为优化效果样本。v3 修正身份、取消与旧请求 fallback 后，两轮 34/35 个请求分别全部成功，worker 请求 5/6 次全部成功；原生 admission 883 断言、完整输入 574 断言通过。即使功能通过，完整等待仍未改善，所以没有采用。该结论否定的是当时实现，不能写成“后台等待永远无效”。

最终基线的 transaction.commit P90 47.974 / 49.043 / 50.484 ms，P95 52.938 / 53.225 / 61.024 ms；是调用诊断时间，不是显示器 present 延迟。新增 source 是长尾重点，提前预热或静态接管的数据不能混进同一个分母。

### 2.4 归档时仍缺少的结论

长期空闲、多队列规模、真实混合 DPI、所有实际内容交互和物理闪烁仍未闭合。用户当时说“没有观察”，不能改写为“观察后没有闪烁”。WPF Rendering、RenderComplete、DComp commit completion、Desktop Duplication 观察都不能单独充当某张卡已经在物理面板显示的回执。

## 3. #269：H1～H11 的验证与未完成事实

### 3.1 状态总表

| 项目 | 归档结论 | 可复用经验 |
| --- | --- | --- |
| H1 无关队列使准备中的任务失效并滞留 Sleeping | 已复现、候选修复通过 | 全局撤销代与队列自身请求身份分开；无关请求不能取消当前任务 |
| H2 持续移动阻止逐卡交还，第一击进入代理路径 | 已复现、候选修复通过 | 物理坐标变化不等于 Presenter 状态变化；输入仲裁不能被无效 dirty 饿死 |
| H3 startup validator 强持有退休 predecessor | 真实 GC 对照确认并修复 | 一次性回调不能延长历史对象链的生命周期；不扩大成 GPU/COM 泄漏结论 |
| H4 空启动跳过 startup-ready | no-default/default/nonempty 场景通过 | 空路径也必须完成同一生命周期收尾 |
| H5 无关桌面指针反复暂停预热 | 调用范围已收窄，两条分支独立回归通过 | 保留真实应用输入与 active session 退让；没有 pause-count/完成率 A/B，不量化性能收益 |
| H6 旧 PaperWindow graphics prewarm 绕过 coordinator | 旁路删除，相关回归通过 | 执行资格由一个 owner 决定；刷新设置/容量预留不应顺便启动另一套预热 |
| H7 扩容后当前受限 request/content 不恢复目标尺寸 | 静态链已确认；候选与夹具多轮推进，但最终专项失败、未正式提交修复 | 先明确 session-size freeze 契约；容量恢复不等于业务内容恢复 |
| H8 动画画面与 UI-owned HRGN 脱节 | 后续由 #271 实证、#291 扩展 | 见第 4～5 节；不是 #269 已修复项 |
| H9 completion retry 丢弃第一击 | 当时防迟到重放的既有契约 | 不能在重试成功后补发旧点击；H2 解决的是正常稳定进入时及时交还 |
| H10 常驻 16 ms 采样与逐成员处理成本 | 未完成直接 idle/规模量化 | 不能由少做一次调用推断 CPU/耗电已解决；也不凭猜测新建事件系统 |
| H11 显示/DPI 变化立即清空 input region | 未形成完整安全交接方案 | cover 仍可见时撤去输入保护可能漏点；要与视觉恢复、真实窗口交还共同设计 |

### 3.2 H1：失效范围与重入边界

旧 `Version` 被任何队列 Request/Wake/Cancel 改写。准备 A 时发生 B 的变化，A 会被视作过期并留下 Pending/Sleeping。修复分别处理 preparation epoch 与 queue candidate identity；取消不存在的队列是 no-op，真正替换/取消 A、全局交互、禁用、重启和 Dispose 仍撤销旧任务。

`AppController.EdgePrewarm` 在可能重入的容量/布局工作之前捕获 ticket，并在 native staging/publication 前复核同一协调器、epoch、请求和源身份。保留成员/HWND/生命周期/端点校验，没有用放宽失效条件绕过真实撤销。

Windows A/B [34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)：六种跨队列场景 baseline 0/6、candidate 6/6；受影响场景 Pending/Sleeping 1/1 → 0/0；八个控制场景双方均通过。共 14 场景 baseline 8/14、candidate 14/14。实现提交 [97bfca8](https://github.com/snownico0722/PaperTodo/commit/97bfca826020657ab6073a91316c0a0077d7359e) / [a04a801](https://github.com/snownico0722/PaperTodo/commit/a04a801dacb7dfda0b39b9ec20dc85593872a527)，逐场景数据见保存的 R0 CSV。测试运行时间不作为用户延迟。

### 3.3 H2：不要让持续输入永远破坏“稳定”条件

retained proxy 每次坐标变化先产生 Pointer dirty，再检查全员 settled，形成“必须先停止移动才交还”的循环。修复只在 pointer-over / visual reducer 可观察状态实际改变时产生本地 reconcile；controller 仍收到物理采样，queue owner/corridor/outside 仲裁不能被一起过滤。

对照 [34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884)：baseline 连续三个 changed-coordinate sample 不交还，需一个 stationary sample；candidate 第二个 changed-coordinate sample 已交还，无需 stationary sample。后续 move-enter→immediate DOWN/UP [34920113728](https://github.com/snownico0722/PaperTodo/actions/runs/34920113728) 中，candidate 第一击前完成 selective handoff，真实 WPF 恰好一次 DOWN/UP/click(toggle)，peer 继续保留；完整 native input [34918285745](https://github.com/snownico0722/PaperTodo/actions/runs/34918285745) 574 断言通过。

用例不能先填充旧采样字段或等交还后才开始计“第一击”。这些行为证据不等于降低了某个已测毫秒数，也没有自动解决 H8。

### 3.4 H3 / H4：对象生命周期与空路径

H3 中，staged successor 的 `_predecessor` 在启动阶段本来就应强持有前任；publication 后清掉该字段仍不够，startup validator 闭包继续强引用前任。提交 [fbcb187](https://github.com/snownico0722/PaperTodo/commit/fbcb187ad1c22f3c0342fda5a30a76a9d7c4c88a) 用弱前任/窗口校验去掉多余存活链，未改 cloak、publication、rollback 顺序。真实 WPF/DComp GC A/B [34922247020](https://github.com/snownico0722/PaperTodo/actions/runs/34922247020)：baseline `retiredAliveAfterGc=True`，candidate `False`，双方 `releaseCount=2`。证明托管非必要存活，不证明 GPU/COM 泄漏。

H4 将 generation=0 的空启动接到统一收尾。[34921002375](https://github.com/snownico0722/PaperTodo/actions/runs/34921002375) 中，空且不建默认纸片、空但建默认纸片均 ready；非空恢复保持 generation=1 顺序。不是通过强制建纸片或改变正常恢复顺序让测试通过。

### 3.5 H5 / H6：收窄干扰与统一执行入口

H5 只收窄 retained proxy 全桌面采样对 optional prewarm 的通知：有 active preview session 时保守通知；无 session 时只有命中当前胶囊才通知。WPF mouse/button/wheel/key/touch 仍走 `InputManager.PreProcessInput` 全局退让，原 pointer 去重保留，quiet delay 未变。#269 提交 [ea7929a](https://github.com/snownico0722/PaperTodo/commit/ea7929a99275689d79cc64a6df2ba2e13b55b116)，native input [34923789358](https://github.com/snownico0722/PaperTodo/actions/runs/34923789358) 574 断言通过。

#271 的第一版同时删除全局 pointer 去重并建立新 observer，范围过大；之后恢复 canonical 去重与 WPF 路径，只留下同样的 narrow gate。独立 run [34927273152](https://github.com/snownico0722/PaperTodo/actions/runs/34927273152)：coordinator 112、standard EdgeTitle 3407、native input 574 断言通过。这是两条线语义收敛及无回归证据，不能当作两次独立性能提升；仍没有无关桌面移动的 pause-count/完成率对照，也没有消除 16 ms 轮询成本。

H6 确认两处 PaperWindow 调度：刷新 hover settings 和预留最大容量都会触发旧 `SystemIdle → PrewarmLightweight` 入口。提交 [4169d80](https://github.com/snownico0722/PaperTodo/commit/4169d80d5bd467fe5750a2848f28a60e62cfb852) 删除 scheduler 和两处调用，只由 controller/coordinator 决定是否执行。结构上 PaperWindow 直接调用 1→0，coordinator 入口保持 1；focused [34927967372](https://github.com/snownico0722/PaperTodo/actions/runs/34927967372)、标准 build [34927967368](https://github.com/snownico0722/PaperTodo/actions/runs/34927967368)、continuation [34927967382](https://github.com/snownico0722/PaperTodo/actions/runs/34927967382) 通过相应检查。结构计数只帮助定位旁路，不应复制为长期源码形状测试，也不是 latency A/B。

### 3.6 H7：正文停在 R4，分支后来继续尝试但未闭环

归档核对发现 #269 共 84 个提交。正文和账本结束于 R4 `aa7b3c59`，实际 HEAD 是 `252952b0`；其后约三十个提交主要是 H7 patch/workflow/fixture 迭代。因此不能仅从 PR 描述说“从未试过 H7”，也不能从提交标题推断“已经修复”。

静态路径是：source 仍被代理持有而不能扩容时，保存 pending capacity，用当前 available size 创建 request/content；释放代理后能够扩大 host 并清 pending，却不重建当前 request/content。反证是 descriptor 明确把 normalized size 冻结在 session 内。是否应在延迟扩容后打破冻结，是需要明确的产品契约，不是见到 request 尺寸没变就自动成立的主线 bug。

实验脚本尝试给 request 增加 `RequestedSize` 与 `CreateContentForSize`，扩容后创建新 content，再让 controller 用同 owner 的布局会话接纳目标尺寸；增长时保持 owner 顶部并推开后续成员。新对象发布前后校验 request identity、body/content generation、window lifecycle 和 owner。其初稿在替换 request 后仍用旧 request 作为 rollback 当前身份，导致回滚匹配不到；后续脚本把 expected-current 与 original-request 分开，补上 controller 拒绝后恢复旧 content/request 的用例。

这一轮还有重要的测试教训：原 fixture 用未初始化的 PaperWindow/部分 controller，调用真实布局后缺字段、反射匹配错误，甚至不能合法执行 `DependencyObject.SetValue`。后续改成恢复必要字段，并把 host/request 核心与 controller 布局回调拆开测试。这样的局部测试即使通过，也不能冒充完整打开→释放→扩容→当前预览恢复的真实用户路径。

最终 workflow [34946224252](https://github.com/snownico0722/PaperTodo/actions/runs/34946224252) 对应 HEAD `252952b0`，`h7` 作业（`104306145528`）结论为 **failure**。同 HEAD 普通 PR build、Edge diagnostics、continuation 等为 skipped；Plugin samples 的 success 与 H7 成败无关。原始日志已过期，无法确认最后失败于补丁、构建、候选、baseline 还是回归中的哪个步骤，不补猜测。

固定 [workflow](https://github.com/snownico0722/PaperTodo/blob/252952b0d85581606ffe61e003a0e028ef7de3b6/.github/workflows/pr260-h7-capacity-restore.yml) 的顺序是：在 runner 临时应用五个脚本 → candidate recovery/rollback → 冻结 baseline → full native input → 最后才提交修复。最终分支源码没有两份 `*PreviewCapacityRecovery.cs`，现有 `PaperWindow.EdgeCapsulePreview.cs` 也没有候选的 `RequestedSize` 路径；没有那个“验证通过后提交”的产品提交。因此 H7 的精确状态是 **有静态依据和可追溯候选，最终验收失败，未提升成正式修复**。

保留候选来源：[初始 patch](https://github.com/snownico0722/PaperTodo/blob/252952b0d85581606ffe61e003a0e028ef7de3b6/scripts/pr260_h7_experiment.py)、[回滚修正](https://github.com/snownico0722/PaperTodo/blob/252952b0d85581606ffe61e003a0e028ef7de3b6/scripts/pr260_h7_finalize_experiment.py)、[测试核心拆分](https://github.com/snownico0722/PaperTodo/blob/252952b0d85581606ffe61e003a0e028ef7de3b6/scripts/pr260_h7_testable_core_experiment.py)。归档不运行它们，不把脚本里尚未验证的代码写入主线。

## 4. #271：画面和输入为何会分离

### 4.1 H8 双向错误，而不只是视觉抖动

实验链路为真实 48×48 WPF source HWND → `CreateSurfaceFromHwnd` → DComp 平移 → #260 的透明 output + 有限 input HWND，下方放独立进程窗口，用 tagged OS DOWN/UP 判断实际收件人。冻结 UI/Dispatcher 360 ms，compositor 仍会前进。

[34935212192](https://github.com/snownico0722/PaperTodo/actions/runs/34935212192) 中，像素从 `[192,240)` 移到 `[312,360)`，HRGN 留在起点。可见红色前沿 `(318,216)` 不在旧区域内，真实完整 click 到达下层进程；旧 HRGN 内已无红色像素的空白点则仍由代理接收，挡住下层。两种错误同时成立：看得见但不接收输入；看不见却仍拦输入。

这是受控停顿下的真实原生输入证据，不表示普通使用中 360 ms 停顿的频率，也不证明物理面板逐帧扫描。把 DispatcherTimer 调快不能在 Dispatcher 被阻塞时恢复推进；把整个运动包围盒设成输入区会把漏点问题变成更大的空白误挡。

同轮标准 native-input 曾在 Button 重新进入悬停节点 2.5 s 超时，同 job 重跑后 574 断言通过。失败应保留，但在没有可重复反例时不能把单次 hosted hover 超时当作 H8 产品回归。历史 probe 的源码在 [Pr260H8InputTimingChecks.cs](https://github.com/snownico0722/PaperTodo/blob/8752755c9e8e9eeadfdc365f9c8038c8ca8538c8/tests/PaperTodo.EdgeTitleChecks/Pr260H8InputTimingChecks.cs)。

### 4.2 被排除的小改法：worker 更新原 UI-owned HWND

[34941737943](https://github.com/snownico0722/PaperTodo/actions/runs/34941737943) 保持 input HWND 的 UI ownership，只把同 QPC/easing 的 region 更新交给 worker。360 ms 停顿结束时 `attemptsAtWake=1`、`completedAtWake=0`、`successfulAtWake=0`；像素已从 `[232,280)` 移到 `[352,400)`，第一次 region 调用仍未返回。

`SetWindowRgn` 涉及同步窗口消息，owner 的消息泵仍然是依赖。改变调用线程没有改变窗口归属，不能解决 UI 不可用时的推进。一次性 probe/workflow 随后删除，保留 [阶段记录](https://github.com/snownico0722/PaperTodo/pull/271#issuecomment-5676471280) 和源码历史。这不是“所有多线程方案无效”的证明。

### 4.3 独立 owner 窄原型可行，但没有动态零误差证明

下一原型让 input-only HWND 由独立 native STA thread/message loop 创建、更新和销毁，output/DComp 留在原线程。两侧收到同一个绝对 QPC 开始时间、duration、device-pixel 几何和 easing；native owner 不读 WPF object。

[34946011617](https://github.com/snownico0722/PaperTodo/actions/runs/34946011617)：Release 0 warning/error，46 断言；360 ms UI stall 中完成 48 次 region publication、0 native failure。像素从 `[234,282)` 到 `[354,402)`，最终输入区域跟到位；真实跨进程点击验证可见点由新 input HWND 接收、旧空白点到达下层。

但 hosted observer 只有 **1 个有效动态 sample**；当时 `visibleMisses=0 / staleBlocks=0` 使用 `>12 px` 阈值，不能解读成整个动画 0 px/0 frame 错位。早期失败来自 observer 阻塞、逐像素 GetPixel 太慢，以及 test-only Static HWND 未设 `SS_NOTIFY`；最后一项是夹具没有收到 click，不能当作独立 owner 架构失败。取证后探针和专用 workflow 被移除，细节留在 [Round 6](https://github.com/snownico0722/PaperTodo/pull/271#issuecomment-5677045821)。

## 5. #291：路线比较、集成修正与真实浏览

### 5.1 两条路线的具体差别

路线 1：独立 native owner 持有输入 HWND，接收不可变动画票据；DComp 自主运动，owner 用同一时间基准更新有限 HRGN，UI stall 时两者都能继续。它新增线程、消息循环、票据和跨线程 handoff 的生命周期成本。

路线 2：应用取同一个时间样本，依次更新 DComp visual、Commit 和 HRGN，试图让两者同步推进。问题是同一计算样本不等于两个系统原子生效；这轮实验专门检查不同提交顺序的可见/输入先后差。

### 5.2 R0～R2：动态证据与路线 2 反例

| 阶段 | 结果 | 解释 |
| --- | --- | --- |
| R0 [35476939938](https://github.com/snownico0722/PaperTodo/actions/runs/35476939938) | 共 77 断言；360 ms UI stall 时路线 1 publication 23 次，路线 2 0 次 | 机制和端点可行性，不是动态同帧证明；更早 `EntryPointNotFoundException` 是 P/Invoke 夹具错误 |
| R1 路线 1 [35480813988](https://github.com/snownico0722/PaperTodo/actions/runs/35480813988) | 像素/输入各移动 91 px；绝对错位 P50 2、P95 3、max 3 px | 真实动态跟随，但并非零误差原子同帧 |
| R1 路线 2，同一 run | 像素移动 120、输入 115 px；错位 P50 2、P95 7、max 10 px，input-ahead 15 个观测 | `Commit → HRGN` 仍出现输入领先 |
| R2 路线 1 [35484858423](https://github.com/snownico0722/PaperTodo/actions/runs/35484858423) | 两成员保留 48 px 真空洞，真实跨进程路由通过；epoch A→B 后旧 A 不复活 | 多区域、替换和 stale epoch 的有限证据 |
| R2 路线 2，同一 run | `Commit → DwmFlush → HRGN` 错位 P50 1、P95 10、max 13 px，visual-ahead 11 个观测；最大 DwmFlush 61.37 ms | 加 fence 把错误方向翻转，没有使提交原子化 |

R2 另做 110 px 确定性跳转：先确认新位置已出现像素，暂不更新 HRGN，再发真实 OS click。新可见位置漏给下层，旧空白位置仍被挡，直接证明视觉确认与输入发布之间仍有间隙。路线 2 的所测方案据此记为 rejected；不是否定任何未来“统一 authority”架构，而是不能靠继续调 Commit/Flush/SetWindowRgn 顺序或固定 delay 宣称解决。

### 5.3 R3 / R4：生产边界接入与两个 race

新增 [EdgeCapsuleQueueDedicatedInputOwner.cs](https://github.com/snownico0722/PaperTodo/blob/5aa796befa944b53bd0e1191ded2bd4fe89b0246/src/EdgeCapsuleQueueDedicatedInputOwner.cs)，把 input-only HWND 交给独立线程，output 留在 WPF/queue runtime。`EdgeCapsuleQueueInputAnimationTicket` 跨线程携带 QPC、duration、成员几何快照，不携带可变 PaperWindow/controller/WPF 对象。旧 H8 观察器从 WPF 线程 `SetWindowSubclass` 已不能合法观察另一线程的 HWND，所以更换集成测试入口，不能为了沿用观察器而改回错误 ownership。

随后修正两处集成竞态：先成功提交 DComp transaction，再激活**同一个绝对 QPC** input ticket，避免 Commit 卡住时输入提前跑；active native animation 期间，等价的旧 UI non-empty region refresh 不抢回输入推进权，真实 empty/hide/environment invalidation 仍可撤销。没有删去原来的 cover、terminal verification 和回滚边界。

R4 `81f065f` 的普通 build / diagnostics 成功，专用 [35492965455](https://github.com/snownico0722/PaperTodo/actions/runs/35492965455) 却在“430 ms UI stall 中 region 至少继续更新 4 次”处失败。早期把剩余问题归到 timer/ticket/publication；后续证据修正了这项归因。

### 5.4 R5 与后续纠正：不能用夹具失败否定正确机制

R5 [35496280718](https://github.com/snownico0722/PaperTodo/actions/runs/35496280718) 的机制诊断记录：`baseline=2, afterStart=3, afterAutomatic=16, beforeManual=16, afterManual=28`。220 ms 内自动更新 3→16，随后手工 WM_TIMER 又到 28，推翻“原生 timer 根本不投递”的假设。专用集成 gate 仍失败，所以当时只能记为集成或夹具差异，不能标 validated。

后续人工 A/B 发现旧 gate 使用会改变 wall X 的横向运动，而真实 queue slot translation 沿边缘纵向移动、wall X 不变。纵向对照证实生产 wrapper 内 native owner 可以持续更新。**旧失败保留为历史夹具不匹配，不再作为 timer/publication 失效证据。** #271 横向原语 probe 用于证明 UI-owned HRGN 与独立 DComp 可以脱节；它与 #291 的生产 queue-plan 夹具适配是两个问题，不能把这次纠正反向抹去 H8 原始反例。

自动实验在 R1～R5 上限停止，后面的两组 A/B 来自当时用户另行要求的人工工作；归档不恢复任何定时任务。

### 5.5 纵向 UI-stall A/B

固定历史 main `c10837b` 与 route1 `6cc9e5b`；同一 Windows runner、相同测试源、900 ms DComp 动画、纵向 120 px、430 ms UI stall。采样改为 BitBlt + DIBSection，避免 GetPixel 扫描拖到停顿结束数秒后。最终 [35515667098](https://github.com/snownico0722/PaperTodo/actions/runs/35515667098) 的 `main-vs-route1` 作业成功。

| 模式/点位 | stall 内采样时刻 | 像素位移 | 输入中心相对像素 | region 更新 | 真实点击结果 |
| --- | ---: | ---: | ---: | --- | --- |
| main / leading | 308.9 ms | 75 px | -75.0 px | 非此 HRGN owner 机制 | absorbed，未完成业务 handoff |
| main / stale | 327.0 ms | 77 px | -77.0 px | 非此 HRGN owner 机制 | proxy，105.6 ms 后处理 |
| route1 / leading | 292.0 ms | 78 px | +5.0 px | 3→42 | absorbed，未漏给下层、未迟到重放 |
| route1 / stale | 312.9 ms | 75 px | +11.5 px | 3→46 | lower，18.8 ms 到达 |

历史 main 用 UI-owned output HWND + `WM_NCHITTEST` / mutable hit truth，不能冒充 #271 的 UI-owned HRGN。这里可以说输入几何不再冻结几十像素、旧空白点不再长期误挡；不能说 UI 卡死时业务点击仍正常。两边 leading 样本都没有完成业务 handoff，路线 1 按 fail-closed 丢弃，而不是恢复后重放。

route1 可见胶囊约 48 px、HRGN 32 px，是上下 `WindowChromeMargin` 内缩后的正常交互几何，不是漏了 16 px。该组是有限点位与相位样本，不能把 +5/+11.5 px 当作稳定误差上界或物理高刷体验保证。

### 5.6 真正连续 Edge Preview 浏览 A/B

用户进一步明确目标是鼠标沿 A→B→C 连续浏览，不只是单次 slot translation。最终 [35545416312](https://github.com/snownico0722/PaperTodo/actions/runs/35545416312) 使用真实 AppController、PaperWindow、胶囊 HWND 和 preview owner 状态机，通过 OS SendInput 移动指针。修复 paper identity、owner provenance 和启动插件污染后，用隔离目录仅比较 built-in browsing；历史 main `c10837b` 与候选 `5aa796b` 各 5 次，共 10 个独立运行。

每次包括首次进入 A、稳态 A→B→C→B→A、10 次快速连续浏览。快速阶段约 32 ms owner transfer 后短暂停留，不等约 200 ms 动画结束。下表时间是五次每-run 指标的中位数，成功数和 geometry miss 是五次合计；不是把全部原始事件混合后重算分位。

| 指标 | 历史 main | #291 候选整体 |
| --- | ---: | ---: |
| 首次 owner latency | 28.1 ms | 24.4 ms |
| 首次完整稳定 | 432.1 ms | 454.9 ms |
| 稳态 transfer owner P50 | 71.1 ms | 49.4 ms |
| 稳态 transfer owner P95 | 140.4 ms | 95.4 ms |
| 稳态 geometry settle P50 | 320.0 ms | 294.7 ms |
| 稳态 geometry settle P95 | 383.3 ms | 330.3 ms |
| 快速浏览成功 | 26/50 | 30/50 |
| 快速 geometry miss | 24/50 | 10/50 |
| 快速 owner latency P50 | 72.5 ms | 38.8 ms |
| 快速 owner latency P95 | 83.4 ms | 67.7 ms |
| 10 次快速扫后最终 owner 正确 | 0/5 | 5/5 |

两边正常稳态每轮都完成 4/4 transfer；main 每轮 fast success 为 5、6、5、5、5，candidate 每轮 6；main geometry misses 为 5、4、5、5、5，candidate 每轮 2。收益集中在运动中命中连续性、transfer 准入和最终停留目标；首次完整稳定没有改善，快速成功也仍只有 30/50。

**因果限制：** 两分支不只差一个 dedicated-owner commit。结果支持“#291 当时方案整体在该 fixture 下更稳定”，不能把所有延迟差异归因于独立线程。候选仍需 UI Dispatcher 执行 Prepare/Open/Close/Arrange，native owner 没有独立运行整个预览状态机，不能称作 UI 卡顿时业务仍能自主切换。

### 5.7 最终路线状态

路线 1 保留为“有生产边界、动态、多区域和连续浏览实证，但集成验收未完成”。未完成范围包括完整 capture/drag/real HWND handoff、source/environment generation、mixed DPI/负坐标副屏、hide/close/异常回滚、CPU contention 与物理多屏/高刷新率。已有少量 hide/display/epoch 用例不代表这些边界全部覆盖。

路线 2 的同样本+调整提交顺序方案保持 rejected；若将来重开，需要新的 ownership/提交协议证据。H11 尤其不能因路线 1 里有“环境变化清 region”的代码就被视作解决：视觉还在时何时解除输入保护，仍属于完整交接问题。

## 6. 长期经验与以后怎样使用这份归档

| 经验 | 本轮依据 | 使用边界 |
| --- | --- | --- |
| 优化目标应落在完整用户路径 | E-018 同步事务下降但启动等待上升；#291 真浏览指标与单次平移不同 | 明确输入、准备、时钟启动、owner transfer、内容稳定、可见帧分别测什么 |
| cache 命中率、调用次数、断言数量不是最终收益 | dormant 13/13 命中仍更慢；H6 入口合一无 latency A/B | 不按单个内部计数决定采用 |
| ownership 比“放后台”更关键 | worker 的 SetWindowRgn 卡在 UI-owned HWND；独立 owner 能继续 | 同一线程或同一 QPC 也不保证 DComp/Win32 原子生效 |
| 局部请求与全局撤销不能混成一个版本 | H1 无关 B 取消 A | 保留真正全局失效，不复制第二份业务状态 |
| 无效通知可能阻止正确交接 | H2 指针持续变化永远制造 dirty | 过滤 reducer-no-op，保留真实 owner/outside 仲裁 |
| 缓存/代理驻留要连同真实输入生命周期评估 | #260 逐卡交还、H3 闭包、H10 未测 | 不把短时动画通过直接推广到长期常驻 |
| 测试场景必须吻合所验证协议 | #291 横向 gate、H7 未初始化 WPF fixture、#271 SS_NOTIFY | 夹具失败先定性，原语可行性也不能替代完整用户路径 |
| 结论要记录证据升级与撤回 | timer 假设被诊断推翻；H7 描述落后源码；日志过期 | 保存固定 SHA、分母、方法、失败与修正，不把旧 checkpoint 当最终事实 |

这些经验应帮助缩小未来改动范围，不成为无条件增加状态、线程、缓存或禁令的理由。尤其不能把“候选未采用”扩成永久否定异步/缓存，把“零有效失败样本”写成正确性证明，或把两个分支各自通过的局部验证拼成整体产品已验收。

主线后来的 [D-057](../DECISIONS.md#d-057--dwm-桌面截图退出-ci-自动验收2026-10-12) 已明确说明 hosted DWM 桌面截图不适合作为可靠自动门槛。这里保留历史屏幕采样的研究观察，不恢复旧截图 workflow、固定等待或反复重跑脚手架，也不抹去已经记录的原生输入反例；未来产品实现仍按当前可执行行为检查和必要实机验证评估。

若以后有明确需求重新启动，应从届时主线开干净分支，先定位当前仍存在的用户问题，再挑选与之相关的历史机制；不能把四条旧分支整体合并，也不默认接着旧的“下一轮”清单执行。H7 先明确尺寸契约，路线 1 先明确要改善的交互和可接受成本，H10 先量化再决定是否改调度。历史版本与证据在这里足够定位，新的采用决定仍需当前代码依据。

## 7. 原始记录入口与保留范围

| 材料 | 固定入口 | 本次用途 |
| --- | --- | --- |
| #260 实验原文 | [953ae626 / EXPERIMENTS.md](https://github.com/snownico0722/PaperTodo/blob/953ae6266af2bc8c5d1696be7660e5006525202b/doc/EXPERIMENTS.md) | E-017/E-018 原方法、样本解释、局部采用与未采用原因 |
| #269 R0～R4 账本 | [252952b0 / continuation](https://github.com/snownico0722/PaperTodo/blob/252952b0d85581606ffe61e003a0e028ef7de3b6/doc/experiments/pr260-continuation-20260915.md) | H1～H6 的状态和验证；其“下一步 H7”被本文补充的最终未完成事实更新 |
| #269 完整提交历史 | [PR commits](https://github.com/snownico0722/PaperTodo/pull/269/commits) | 识别正文之后的 H7 脚本/夹具迭代；不能按 commit 标题认定测试通过 |
| #271 账本 | [8752755c / parallel](https://github.com/snownico0722/PaperTodo/blob/8752755c9e8e9eeadfdc365f9c8038c8ca8538c8/doc/experiments/pr260-parallel-automation.md) | H7 静态反证与普通 worker 失败；最终 owner 可行性须同时读阶段评论 |
| #271 阶段评论 | [H8 双向反例与讨论](https://github.com/snownico0722/PaperTodo/pull/271)、[Round 6](https://github.com/snownico0722/PaperTodo/pull/271#issuecomment-5677045821) | 原生实验、observer/fixture 失败和 1-sample 限制 |
| #291 最终正文 | [PR #291](https://github.com/snownico0722/PaperTodo/pull/291) | R0～R5、后续纵向与真浏览 A/B；本文已保存核心数据与纠错 |
| #291 真浏览夹具 | [5aa796b / EdgeBrowsingComparison.cs](https://github.com/snownico0722/PaperTodo/blob/5aa796befa944b53bd0e1191ded2bd4fe89b0246/tests/PaperTodo.EdgeTitleChecks/EdgeBrowsingComparison.cs) | 恢复历史测量方法时的源码入口，不作为当前长期 CI |
| 既有输出归档 | [2026-09-20 核对](output-knowledge-audit-20260920.md)、[路线 3](edge-route3-20260915.md) | 补充更早实验的边界；不把路线 3 与 #291 的路线 1/2 混为一谈 |

关闭四个 PR 后仍保留标题、原正文/评论、提交和远程实验分支；关闭说明只附加归档入口，不抹去失败、贡献或纠错历史。归档的成功条件是知识已进入主线，旧实验 PR 不再占用活动列表；不要求把未完成方案继续做完，也不将其伪装成合并完成。
