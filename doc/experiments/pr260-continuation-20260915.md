# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 续接规则

- 持久化实验 PR：[#269](https://github.com/snownico0722/PaperTodo/pull/269)。唯一可写分支：`experiment/pr260-continuation-20260915`。
- 初始来源固定为 #260 `953ae6266af2bc8c5d1696be7660e5006525202b`；#269 以 #260 为 base 只为展示实验增量。
- 每轮开始先重新读取 #260、#269 描述/最新阶段评论/commits/diff/CI 与本文，再从已完成点续接；不要依赖聊天上下文，不重跑已否定路线。
- 只允许 fast-forward 写实验分支；禁止写 main、#260 原分支、merge、force-push、删除远程分支。#269 保持 Draft。
- 用户希望优先 GPT-6 Pro；只有环境真正允许选择时才能声称使用。各轮按实际系统模型记录。
- 编译/断言通过不等于用户桌面、混合 DPI、跨进程输入或物理显示器实测。没有测量的延迟绝不补数字。

## 多模型审查假设与当前裁决

五份 5.6 Sol / Astra 审查只作为初始假设；最终以代码、冻结 baseline、Windows 实测及可复现测试裁决。

| 编号 | 假设 | 当前状态 |
| --- | --- | --- |
| H1 | 全局 `Version` 被无关队列 Request/Wake/Cancel 改写，使准备中 A 失效并留在 Sleeping | **R0 已复现并修复。** 六种跨队列场景 baseline 0/6、candidate 6/6；Pending/Sleeping 1/1 → 0/0。 |
| H2 | retained proxy changed-pointer tick 先制造 Pointer dirty，再要求 settled，持续移动可饿死逐卡输入交还 | **R1 已复现并修复。** baseline 需要 stationary sample；candidate changed-coordinate 期间完成交还；move-enter→immediate DOWN/UP 与完整 native-input 通过。 |
| H3 | settled-input 启动验证闭包长期强持有 predecessor | **R2 已复现并修复。** 真实 WPF/DComp GC A/B：baseline predecessor 在 live successor 下仍存活，candidate 可回收；完整 native-input 574 assertions 通过。只称托管对象非必要存活，不称 GPU/COM 泄漏。 |
| H4 | 空状态 `StartAsync` 早退跳过 edge-prewarm startup-ready 收尾 | **R2 已复现并修复。** create-default / no-default 两种空启动获得 ready；非空恢复保持 generation=1 原顺序。 |
| H5 | 无关桌面鼠标移动暂停全局预热并触发无关工作 | **R3 已收窄全局预热暂停范围。** retained proxy 的物理桌面采样仅在存在 preview session 或指针实际命中该 edge capsule 时通知 prewarm；实际 WPF 输入仍走 InputManager 并保持全局保守暂停。完整 native-input 574 assertions 通过。**尚无直接 pause-count / 完成率 A/B，且逐成员采样成本仍归 H10，不能写成 H5 全部关闭。** |
| H6 | `PaperWindow` 旧 `PrewarmLightweight` 直接入口绕过 coordinator | **待推进。** `App.EdgeCapsuleComposition.cs` 已无直接预热，但 `PaperWindow.EdgeCapsulePreview.cs::ScheduleEdgeCapsuleCompositionPrewarm()` 仍在 `SystemIdle` 直接调用。 |
| H7 | 容量恢复后当前受限 preview request 的 Size 没恢复 | 待做同 request/content generation 恢复测试。 |
| H8 | DComp 动画像素与 UI timer 更新的 input HRGN 可能错位 | 高优先级原生时序风险；尚未真实移动 + UI stall + 跨进程背景窗口复现。禁止扩大整个 envelope 来“修”。 |
| H9 | completion retry 会丢弃第一次点击 | 当前是防止迟到重放的既有契约；不能简单重放旧点击。R1 已确保正常稳定卡片快速进入时第一击走真实 WPF。 |
| H10 | 常驻 16ms 采样和逐卡交还全队列流程有空闲/规模成本 | 先测量；已有 SetWindowRgn 未变 fast path、surface AddRef、单 spare host，不重复实现。 |
| H11 | DPI/display change 时立刻清空 input region | **不照抄。** cover 仍可见时清空 HRGN 可能把点击泄漏给后方应用，需与视觉/真实源恢复共同设计。 |

## 已否定或暂不重开

- E-017 dormant surface/visual cache：命中高但没有减少 cloak/flush 主成本；额外 `WaitForCommitCompletion` 无采用收益。
- E-018 原样 async successor：内部 `transaction.commit` 变短但 prepare→animation-clock 变慢。
- 路线 3 atlas/native shape 当前实现不重开；暂不做大型 WPF 队列宿主改写。
- 不删除 cover-before-cloak fence，不绕过 WPF 原生同步，不把整个透明 output envelope 变成输入阻挡区。
- Desktop Duplication / Rendering / RenderComplete / DComp commit completion 都不是逐卡物理 panel present acknowledgement。

## R0 — H1 跨队列预热失效

实际模型：**GPT-6 Astra Pro**。

产品提交：
- `97bfca826020657ab6073a91316c0a0077d7359e`：`EdgePrewarmCoordinator.cs` 分离 preparation epoch 与 queue candidate identity；无关队列变化不再作废正在准备的 A；Cancel missing queue 为 no-op。
- `a04a801dacb7dfda0b39b9ec20dc85593872a527`：`AppController.EdgePrewarm.cs` 在可重入容量/布局工作前捕获 preparation ticket，并在 native staging/publication 前复核 coordinator、epoch、同请求身份，同时保留队列成员/HWND/lifecycle/endpoint 检查。

Windows A/B：[34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)，Windows Server 2025 / SDK 10.0.401 / runtime 10.0.12：六种跨队列场景 baseline 0/6、candidate 6/6；受影响场景 Pending/Sleeping 1/1 → 0/0；八个控制场景双方 8/8。工具失败 `34909929195`（缺 System.IO）和 `34910152997`（baseline 参数未传入）均已保留且不算产品结论。

## R1 — H2 changed-pointer handoff 与第一击

实际模型：**GPT-5.6 Sol**；环境无 GPT-6 Pro 路由参数。

产品修正收口于 `src/PaperWindow.EdgeCapsuleQueueProxy.cs`：只有 pointer-over / visual reducer 可观察状态实际改变时才给 Presenter 产生本地 Pointer dirty/reconcile；Controller 仍接收物理 pointer sample，owner/corridor/target 仲裁不被过滤。

- changed-coordinate focused A/B：[34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884)：baseline `releasedDuringMovement=False, movingSamples=3, stationarySamples=1`；candidate `releasedDuringMovement=True, movingSamples=2, stationarySamples=0, filteredNoOpSamples=3`。
- move-enter→immediate DOWN/UP：[34920113728](https://github.com/snownico0722/PaperTodo/actions/runs/34920113728)：candidate 在第一击前完成 selective handoff，真实 WPF 恰好收到一次 DOWN/UP/click(toggle)，peer 继续 retained；baseline 第一击仍落在代理路径。
- 完整 native-input：[34918285745](https://github.com/snownico0722/PaperTodo/actions/runs/34918285745)：Release 0 warning / 0 error，574 assertions passed。
- 夹具失败 `34917315231`、`34919855512` 均已保留并修正后复跑，不能当产品结果。

R1 没有新的毫秒 latency 或 physical-present 数据；H5/H8 未因 H2 自动关闭。

## R2 — H4 空启动收尾 + H3 predecessor 托管存活

实际模型：**GPT-5.6 Sol**；环境无 GPT-6 Pro 模型选择参数。R2 起始 HEAD `33b43aa708d2ffc215ba483c88a93f88ac744a74`。

### H4 空状态 startup-ready

`src/AppController.PluginStartup.cs` 让 generation=0 的空启动经过统一 edge-prewarm 收尾；非空恢复仍由原 restore/prewarm generation 顺序完成。最终 Windows A/B：[34921002375](https://github.com/snownico0722/PaperTodo/actions/runs/34921002375)：

```text
empty-no-default: papers=0 restoreGeneration=0 startupReady=True coordinatorCreated=True
empty-default:    papers=1 restoreGeneration=0 startupReady=True coordinatorCreated=True
nonempty:         papers=1 restoreGeneration=1 startupReady=True coordinatorCreated=True
```

### H3 settled-input startup callback lifetime

产品提交 `fbcb187ad1c22f3c0342fda5a30a76a9d7c4c88a` 将 startup validator 改为 weak predecessor/window guards。startup 期间 staged successor 的 `_predecessor` 本来就强保持 predecessor；publication 后清 `_predecessor` 后，validator 不再形成历史强引用链。未改 cloak/DComp fence、publication、rollback 或输入路由顺序。

真实 WPF/DComp GC A/B：[34922247020](https://github.com/snownico0722/PaperTodo/actions/runs/34922247020)：baseline `retiredAliveAfterGc=True`，candidate `False`，双方 `releaseCount=2`。随后完整 native-input：[34922484480](https://github.com/snownico0722/PaperTodo/actions/runs/34922484480)：Release 0 warning / 0 error，574 assertions passed。

测试工作流失败 `34921819496`（StartupObject 污染 ProjectReference）和 `34922022733`（harness 漏 using PaperTodo）均为测试工具问题，最终 A/B 已修正复跑。

## R3 — H5 收窄无关桌面指针对预热的干扰

实际模型：**GPT-5.6 Sol**；当前环境没有 GPT-6 Pro 路由/选择参数。

起始 HEAD：`258895e4a778b2ba95ff1942a97a215b28370c08`。本轮重新确认 #260 仍为 `953ae6266af2bc8c5d1696be7660e5006525202b` Draft，未写 main / #260 原分支。

### 代码结论

`src/AppController.EdgeCapsulePreviewPointerInput.cs` 原来在 `NotifyEdgeCapsulePreviewPhysicalPointer()` 一开始无条件调用 `ObserveEdgePrewarmPointer(pointer)`。retained proxy 会轮询全桌面物理坐标，因此即使用户只在其他应用中移动鼠标，也会把变化解释为 PaperTodo 全局 prewarm interaction。

产品提交 `ea7929a99275689d79cc64a6df2ba2e13b55b116` 改为：

- 已有 preview session：继续通知 prewarm，保持 corridor/transfer/outside motion 的保守语义；
- 无 session：只有 `pointer.HasValue && inputWindow.IsEdgeCapsuleInteractiveAt(pointer)` 时，proxy 的物理采样才通知 prewarm；
- 真正的 WPF mouse/button/wheel/key/touch 输入仍通过 `InputManager.PreProcessInput` 进入 `OnEdgePrewarmInput()`，没有放松 click/capture/drag 等实际应用交互的全局让路；
- 没有改 180ms quiet delay、coordinator ticket/H1 逻辑、DComp/HRGN 或 H2 selective handoff。

这只收窄 **proxy 轮询导致的全局 prewarm 暂停**。它不停止 retained proxy 的 16ms 采样，也不宣称逐成员空闲成本已消失；那部分仍属于 H10。

### Windows 回归

PR260 continuation run：[34923789358](https://github.com/snownico0722/PaperTodo/actions/runs/34923789358)，被测 product commit `ea7929a99275689d79cc64a6df2ba2e13b55b116`：

- Release `PaperTodo.EdgeTitleChecks` build：**0 warning / 0 error**，约 51.27s；
- `PaperTodo.EdgeTitleChecks.exe --proxy-native-input`：**574 assertions passed**；
- 覆盖 hidden DComp paint/pool reuse、settled-input publication/rollback、Button/CheckBox hover/DOWN/UP/click/capture/cancel、selective peer retention/final release、跨线程/跨进程透明空洞、successor hold/pending completion/completion retry shield、damaged pool。

本次 commit tag 只要求 full native-input；同 workflow 的 H1/H2 focused jobs 按条件正确 skipped，Edge diagnostics workflow 也按条件 skipped。这些 skipped 不是失败。

### 证据边界

本轮**没有**直接做 “无关桌面移动 N 秒 → prewarm pause 次数 / 完成率” 的 baseline/candidate A/B，因此 H5 当前只能写成“调用范围已收窄且完整原生输入契约未回归”，不能写成“预热命中率提高 X%”或“延迟降低 X ms”。active preview session 期间仍保持保守全局暂停；这一点是刻意保留，不是遗漏。

## 下一轮最明确的工作

1. **先给 H5 补 direct focused 量化或决定证据已足够。** 最有价值的是记录 unrelated proxy movement 下 `NotifyInteraction`/quiet-window reset 次数和待预热完成情况；不要为测试另造一套命中语义。如果直接量化需要过重 AppController fixture，则保留当前局部修正和 native-input 证据，不为“证明少一次调用”扩大测试架构。
2. **H6 统一 graphics prewarm 入口。** 当前 `PaperWindow.EdgeCapsulePreview.cs::ScheduleEdgeCapsuleCompositionPrewarm()` 仍 `SystemIdle` 直接调用 `PrewarmLightweight()`。先做资格回归，再让 PaperWindow 只报告需求、由 coordinator 决定何时真正执行；不要复制第二套 CanPrepare 条件。
3. **H7 受限 preview Size 恢复。** 补同 request/content generation 的端到端恢复测试，不能只验证 HWND capacity。
4. **H8 原生时序专项。** 真实 DComp 移动 + 受控 UI stall + 跨进程背景窗口，验证可见前沿不漏点、真实空洞仍穿透。现有 574 assertions 不能替代。
5. **H10 先量化。** 记录 idle tick / presentation read / queue arbitration / CPU/handle 规模，再决定是否改事件驱动。

当前仍没有新的 request→first-correct-frame、prepare→animation-clock 或物理显示延迟数据；不得把 H5 调度范围收窄、GC、调度正确性或手势修复写成“降低了 X ms”。