# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 续接规则

- 实验 PR：[#269](https://github.com/snownico0722/PaperTodo/pull/269)。唯一可写分支：`experiment/pr260-continuation-20260915`。
- 初始来源为 #260 `953ae6266af2bc8c5d1696be7660e5006525202b`，当时 main 为 `2cc019d08143ed07e8774782c0ecf733e7eb7892`。#269 以 #260 为 base 只为了清楚展示实验增量，不授权写入或合并 #260。
- 每轮必须先重新读取 #260、#269 的最新描述/评论/commits/diff/CI 和本文，再续接。不要依赖聊天上下文，不重复已经否定的实验。
- 只允许 fast-forward 写实验分支；禁止写 main、写 #260 原分支、merge、force-push、删除远程分支。PR 始终保持 Draft。
- 用户希望优先 GPT-6 Pro；只有环境真的允许选择时才能声称使用。每轮记录实际可确认模型，不能冒充。
- 北京时间 2026-09-15 09:00—16:00 每小时一轮，共 8 轮；R0 为立即执行，不占这 8 轮。
- 编译/断言通过不等于用户桌面、混合 DPI、跨进程输入或物理显示器实测。没有测量的延迟绝不补数字。

## 多模型审查假设与当前裁决

用户提供：`5.6sol模型审查4.md`、`astra模型审查1.md`、`astra模型审查2.md`、`astra模型审查3.md`、`astra模型审查4.md`。Astra 可给予更高初始置信度，但最终只按代码和可复现测试更新。

| 编号 | 假设 | 当前状态 |
| --- | --- | --- |
| H1 | 全局 `Version` 被无关队列 Request/Wake/Cancel 改写，使准备中 A 完成结果失效并留在 Sleeping | **R0 已复现并修复。** Windows WPF Dispatcher 对照：六种跨队列场景 baseline 0/6、candidate 6/6；每个受影响场景 Pending/Sleeping 由 1/1 变 0/0。 |
| H2 | retained proxy 每个 changed pointer tick 先制造 Pointer dirty，再要求所有成员 settled；持续移动可饿死逐卡真实输入交还 | **R1 已复现并修复 focused path。** pinned #260 三个 changed-coordinate sample 均未交还，随后 1 个 stationary sample 才交还；candidate 第 2 个 changed-coordinate sample 已交还，0 stationary sample，过滤 3 个 reducer-no-op member sample。完整 `--proxy-native-input` 与即时 DOWN/UP 尚未跑。 |
| H3 | settled-input 启动验证闭包长期持有 predecessor | 静态路径成立，未做 WeakReference/GC 实测；只可称托管对象非必要存活风险，不称 GPU/COM 泄漏。 |
| H4 | `State.Papers.Count == 0` 的 `StartAsync` 早退跳过 edge-prewarm startup-ready 收尾 | R1 再次静态确认早退路径存在；尚未做两种空启动 baseline/candidate 运行。 |
| H5 | 无关桌面鼠标移动也暂停全局预热并触发逐成员工作 | 尚未修改；H1/H2 不等于解决 H5。 |
| H6 | `PaperWindow` 旧 `PrewarmLightweight` 直接入口绕过 coordinator | 待验证和收拢，倾向删除重复入口而不是补第二套条件。 |
| H7 | 容量恢复后当前受限 preview request 的 Size 没恢复 | 待做同请求/内容代次恢复测试；不能只看底层 HWND 容量。 |
| H8 | DComp 动画像素与 UI timer 更新的 input HRGN 可能错位 | 高优先级原生时序风险，尚未真实移动+跨进程手势复现；禁止扩大整个 envelope 来“修”。 |
| H9 | completion retry 会丢弃第一次点击 | 当前是防止迟到重放的既有契约；不能简单重放旧点击。先保证正常稳定卡片在按下前交还，再测即时手势。 |
| H10 | 常驻 16ms 采样和逐卡交还全队列流程有额外空闲/规模成本 | 先测量；已有 SetWindowRgn 未变 fast path、surface AddRef、单 spare host，不能重复实现。 |
| H11 | DPI/display change 时立刻清空 input region | **不照抄。** 可见 cover 仍在时清空 HRGN 可能把点击泄漏给后方应用，必须和视觉/真实源恢复共同设计。 |

## 已否定或暂不重开

- E-017 dormant surface/visual cache：命中高但没有减少 cloak/flush 主成本；额外 `WaitForCommitCompletion` 无采用收益。
- E-018 原样 async successor：`transaction.commit` 变短但 prepare→animation-clock 变慢，不以内部计时改善冒充用户延迟改善。
- 路线 3 atlas/native shape 当前实现不重开；暂不做大型 WPF 队列宿主改写。
- 不删除 cover-before-cloak fence，不绕过 WPF 原生同步，不把整个透明 output envelope 变成输入阻挡区。
- Desktop Duplication/Rendering/RenderComplete/DComp commit completion 都不是逐卡物理 panel present acknowledgement。

## R0：H1 跨队列预热失效

### 模型与代码

R0 记录的实际会话模型为 **GPT-6 Astra Pro**；调度工具无模型参数，后续轮次不继承这一事实。

产品提交：

- `97bfca826020657ab6073a91316c0a0077d7359e`：`EdgePrewarmCoordinator.cs` 分离 preparation epoch 与 queue Candidate identity；无关队列变化不再作废正在准备的 A；Cancel missing queue 为严格 no-op。
- `a04a801dacb7dfda0b39b9ec20dc85593872a527`：`AppController.EdgePrewarm.cs` 在可能重入的容量/布局工作前捕获 preparation ticket，并在 native staging / publication 前复核 coordinator、epoch、同请求身份，同时保留队列成员/HWND/lifecycle/endpoint 检查。
- 后续测试/工作流提交建立冻结 baseline/candidate Windows A/B，最终产品行为没有在验证后变化。

全局输入、CancelAll、disable/enable、Dispose 和同队列 request replacement 仍会让旧 ticket 失效；这不是异步 publication 许可证。

### 可复现结果

最终 Windows 对照：[run 34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)。环境 Windows Server 2025 `10.0.26100`，SDK `10.0.401`，runtime `10.0.12`。

| 度量 | pinned #260 baseline | candidate |
| --- | ---: | ---: |
| 六种跨队列场景满足正确行为 | 0/6 | 6/6 |
| 每个受影响场景结束 Pending/Sleeping | 1/1 | 0/0 |
| 八个控制场景 | 8/8 | 8/8 |
| 总场景 | 8/14（六个预期失败） | 14/14 |

六种触发覆盖 prepare callback 内 Request(B)、Cancel(missing B)、Cancel(existing B)、Wake(B)，以及 `canPrepare`/`prepareGraphics` 回调内 Request(B)。逐场景 CSV：`doc/experiments/pr260-r0-coordination-samples.csv`。

既有整合验证：Release [34910152555](https://github.com/snownico0722/PaperTodo/actions/runs/34910152555) 与 Debug diagnostics/edge [34910152540](https://github.com/snownico0722/PaperTodo/actions/runs/34910152540) 通过。独立 Persistence Checks、完整 `--proxy-native-input`、用户回放、混合 DPI、长期空闲、物理显示/闪烁均未在 R0 执行。

保留失败记录：`34909929195` 是新测试宿主缺 `System.IO` 的编译失败；`34910152997` 是 baseline 参数没有正确传进测试进程导致模式报告错误。最终 `34910341153` 已修正并复跑，不能把前两次工具问题当成额外产品缺陷。

## R1：H2 changed-pointer selective handoff

### 实际模型、起止状态

本轮实际系统模型可确认是 **GPT-5.6 Sol**。环境没有 GPT-6 Pro 选择参数，因此没有声称已切换。

- 本轮开始重新确认 #260 仍为 `953ae6266af2bc8c5d1696be7660e5006525202b`、Draft；未修改其分支。
- #269 本轮最终代码/测试 HEAD 在验证时为 `849d99db59f2dc2ba34a1736c2a08e97a0349256`。本节文档提交会成为其后续 HEAD，但不改变产品/测试行为。
- 本轮没有写 main、没有 merge、没有 force-push。

### 产品修正

关键产品提交链最终收口在 `7af97e03b3543189349e424447ca20477f3578b8`：

`src/PaperWindow.EdgeCapsuleQueueProxy.cs` 新增 `ShouldInvalidateEdgeCapsuleQueueProxyPointer(pointer, presentedFrame)`。它非破坏性地镜像当前 `EdgeCapsuleReducer.SamplePointer` 可观察结果：只有 pointer-over 或视觉状态实际会改变时，才给 Presenter 产生本地 Pointer dirty/reconcile；**Controller 仍然收到每一个物理 pointer sample**，所以 queue owner/corridor/target 仲裁没有被位置过滤掉。

这解决的不是“减少鼠标消息”本身，而是一个顺序冲突：旧 #260 在同一 proxy tick 中先为每个 changed coordinate 排入 Presenter Pointer 工作，然后立即用 `IsEdgeCapsuleQueueProxyInputSettled` 要求所有成员无 dirty/no scheduled reconcile 才允许 selective handoff。持续移动因此可以自己持续制造“不 settled”的条件。

当前 helper 与 `EdgeCapsuleReducer.SamplePointer` 静态核对一致，但存在维护风险：两处手写同一状态转换。后续若 reducer 改动，应改成共享一个 non-mutating state-preview helper 或增加一致性测试，不应长期靠人工同步。

### focused Windows A/B 测试

新增 `tests/PaperTodo.EdgeTitleChecks/Pr260MovingHandoffRegression.cs` 与实验 workflow 的 `moving-handoff` baseline/candidate 矩阵。baseline worktree 固定 `953ae626`，复制同一 focused harness；candidate 使用当前实验代码。测试使用真实 WPF source、真实 retained DComp proxy、真实 Presenter dirty/reconcile、真实 selective successor/reveal/cloak 路径，不预填 `_hasRetainedPointerSample` / `_lastRetainedPointer` / `_lastRetainedPointerFrames`。

第一轮 [34917315231](https://github.com/snownico0722/PaperTodo/actions/runs/34917315231) **无产品结论**：baseline/candidate 都在 focused fixture 的未初始化 `AppController` 上进入 preview activation 并 NRE。该 fixture 原本只提供 presentation/native adapter，失败发生在测试外围 controller 通知，不是 H2 被否定。

`849d99db59f2dc2ba34a1736c2a08e97a0349256` 修正 harness：在主动制造 Presenter Pointer dirty 时临时使用生产已有的 `_edgeCapsuleVisualTransactionNotificationDeferred` 边界，保留真实 `PaperWindow -> Presenter` reconcile 和 native/selective handoff，只隔离未初始化 controller adapter 的通知批次。随后最终运行：

**[run 34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884)**，Windows Server 2025 `10.0.26100`，runner image `windows-2025-vs2026 / 20260907.229.1`，SDK `10.0.401`，runtime `10.0.12`。两臂 focused Release build 均 0 warning / 0 error，baseline/candidate job 都成功。

原始结果：

```text
baseline 953ae626:
RESULT pr260-moving-handoff expectDefect=True releasedDuringMovement=False movingSamples=3 stationarySamples=1 filteredNoOpSamples=0

candidate 849d99db:
RESULT pr260-moving-handoff expectDefect=False releasedDuringMovement=True movingSamples=2 stationarySamples=0 filteredNoOpSamples=3
```

因此 H2 从“高置信静态推演”升级为**冻结 #260 可复现缺陷 + focused candidate 修复通过**：

- baseline 在 3 个连续 changed-coordinate in-card sample 期间 0 次 selective handoff；只有随后 1 个 stationary sample 才释放真实 WPF target。
- candidate 在第 2 个 changed-coordinate sample 已完成 selective handoff；无需 stationary sample；同一轮共过滤 3 个对 Presenter reducer 无状态变化的 member sample。
- 这不是毫秒性能 A/B：没有报告 input→first frame 或 physical display latency。build 用时与 hosted runner 区域差异也不是性能指标。

### 当前边界与新风险

- focused test 证明真实 WPF/DComp retained source 的 changed-pointer ordering 与 selective handoff，不等于完整 `--proxy-native-input` 全部场景已通过。
- 还没有补“从外持续移动进入后立即 DOWN/UP、按下拖出再抬起”的完整 OS 手势；现有修复目标是让正常卡片在按下前恢复真实 WPF 输入，而不是重放迟到点击。
- H8 动画像素 vs HRGN 时序完全没有因此解决，也没有物理 display/present 证明。
- filtered no-op sample 仍然通知 Controller；所以 H5 的“无关桌面 pointer 导致全局预热暂停/队列级仲裁工作”仍存在，不能把 H2 修复宣传成空闲成本优化完成。

### 下一轮最明确的一步

1. **先在 candidate 上运行完整 `--proxy-native-input`**，确认 H2 产品修正没有破坏跨线程/跨进程空洞穿透、Button/CheckBox hover/down/up/capture/cancel、retry shield 等既有原生输入契约。
2. 在同一原生 fixture 中增加**不预填 retained sample 的 move-enter -> immediate DOWN/UP** 完整手势，确认用户第一下不需要等待 stationary tick，也不会漏给后方进程。
3. 上述通过后，再决定是否顺手把 `ShouldInvalidate...` 的 reducer 镜像收敛到共享 non-mutating helper；不要为了代码整洁先扩大产品变化。
4. 若原生输入任务被桌面环境阻断，则优先推进 H4 空状态启动：`AppController.StartAsync` 当前 `State.Papers.Count == 0` 分支在可选默认纸张和插件调度后直接 return，静态上仍跳过正常 restore -> startup shell prewarm -> `CompleteStartupEdgePrewarm` 收尾。必须 baseline/candidate 验证 createDefaultPaper=true/false，并保持非空启动顺序不变。

## 后续每轮记录要求

每轮结束都要把实际模型、起始/结束 HEAD、产品/测试修改、原始测试命令/CI URL、可量化结果、被证实/推翻/放弃的假设、风险和下一步写回本文与 #269 阶段评论。后续证据推翻本文时，明确写“替代 Rn 结论”，不要静默改写历史。
