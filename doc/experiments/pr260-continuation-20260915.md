# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 续接规则

- 实验 PR：[#269](https://github.com/snownico0722/PaperTodo/pull/269)。唯一可写分支：`experiment/pr260-continuation-20260915`。
- 初始来源为 #260 `953ae6266af2bc8c5d1696be7660e5006525202b`，当时 main 为 `2cc019d08143ed07e8774782c0ecf733e7eb7892`。#269 以 #260 为 base 只为了清楚展示实验增量，不授权写入或合并 #260。
- 每轮必须先重新读取 #260、#269 最新描述/评论/commits/diff/CI 和本文，再续接。不要依赖聊天上下文，不重复已经否定的实验。
- 只允许 fast-forward 写实验分支；禁止写 main、写 #260 原分支、merge、force-push、删除远程分支。PR 始终保持 Draft。
- 用户希望优先 GPT-6 Pro；只有环境真的允许选择时才能声称使用。每轮记录实际可确认模型，不能冒充。
- 北京时间 2026-09-15 09:00—16:00 每小时一轮，共 8 轮；R0 为立即执行，不占这 8 轮。
- 编译/断言通过不等于用户桌面、混合 DPI、跨进程输入或物理显示器实测。没有测量的延迟绝不补数字。

## 多模型审查假设与当前裁决

用户提供：`5.6sol模型审查4.md`、`astra模型审查1.md`、`astra模型审查2.md`、`astra模型审查3.md`、`astra模型审查4.md`。Astra 可给予更高初始置信度，但最终只按代码和可复现测试更新。

| 编号 | 假设 | 当前状态 |
| --- | --- | --- |
| H1 | 全局 `Version` 被无关队列 Request/Wake/Cancel 改写，使准备中 A 完成结果失效并留在 Sleeping | **R0 已复现并修复。** Windows WPF Dispatcher 对照：六种跨队列场景 baseline 0/6、candidate 6/6；每个受影响场景 Pending/Sleeping 由 1/1 变 0/0。 |
| H2 | retained proxy 每个 changed pointer tick 先制造 Pointer dirty，再要求所有成员 settled；持续移动可饿死逐卡真实输入交还 | **R1 已复现并修复。** pinned #260 三个 changed-coordinate sample 都未交还，随后 1 个 stationary sample 才交还；candidate 第 2 个 changed-coordinate sample 已交还、0 stationary sample、过滤 3 个 reducer-no-op member sample。候选随后完整 `--proxy-native-input` 574 assertions 通过。即时 move-enter→DOWN/UP 仍待新增端到端覆盖。 |
| H3 | settled-input 启动验证闭包长期持有 predecessor | 静态路径成立，未做 WeakReference/GC 实测；只可称托管对象非必要存活风险，不称 GPU/COM 泄漏。 |
| H4 | `State.Papers.Count == 0` 的 `StartAsync` 早退跳过 edge-prewarm startup-ready 收尾 | R1 静态再次确认早退路径；尚未做两种空启动 baseline/candidate 运行。 |
| H5 | 无关桌面鼠标移动也暂停全局预热并触发逐成员工作 | 尚未修改；H1/H2 不等于解决 H5。 |
| H6 | `PaperWindow` 旧 `PrewarmLightweight` 直接入口绕过 coordinator | 待验证和收拢，倾向删除重复入口而不是补第二套条件。 |
| H7 | 容量恢复后当前受限 preview request 的 Size 没恢复 | 待做同请求/内容代次恢复测试；不能只看底层 HWND 容量。 |
| H8 | DComp 动画像素与 UI timer 更新的 input HRGN 可能错位 | 高优先级原生时序风险，尚未真实移动+跨进程手势复现；禁止扩大整个 envelope 来“修”。 |
| H9 | completion retry 会丢弃第一次点击 | 当前是防止迟到重放的既有契约；不能简单重放旧点击。先保证正常稳定卡片在按下前交还，再测即时手势。 |
| H10 | 常驻 16ms 采样和逐卡交还全队列流程有额外空闲/规模成本 | 先测量；已有 SetWindowRgn 未变 fast path、surface AddRef、单 spare host，不能重复实现。 |
| H11 | DPI/display change 时立刻清空 input region | **不照抄。** 可见 cover 仍在时清空 HRGN 可能把点击泄漏给后方应用，必须和视觉/真实源恢复共同设计。 |

## 已否定或暂不重开

- E-017 dormant surface/visual cache：命中高但没有减少 cloak/flush 主成本；额外 `WaitForCommitCompletion` 无采用收益。
- E-018 原样 async successor：`transaction.commit` 变短但 prepare→animation-clock 变慢，不以内计时改善冒充用户延迟改善。
- 路线 3 atlas/native shape 当前实现不重开；暂不做大型 WPF 队列宿主改写。
- 不删除 cover-before-cloak fence，不绕过 WPF 原生同步，不把整个透明 output envelope 变成输入阻挡区。
- Desktop Duplication/Rendering/RenderComplete/DComp commit completion 都不是逐卡物理 panel present acknowledgement。

## R0：H1 跨队列预热失效

R0 实际会话模型记录为 **GPT-6 Astra Pro**；调度工具无模型参数，后续轮次不继承这一事实。

产品提交：

- `97bfca826020657ab6073a91316c0a0077d7359e`：`EdgePrewarmCoordinator.cs` 分离 preparation epoch 与 queue Candidate identity；无关队列变化不再作废正在准备的 A；Cancel missing queue 为严格 no-op。
- `a04a801dacb7dfda0b39b9ec20dc85593872a527`：`AppController.EdgePrewarm.cs` 在可能重入的容量/布局工作前捕获 preparation ticket，并在 native staging/publication 前复核 coordinator、epoch、同请求身份，同时保留队列成员/HWND/lifecycle/endpoint 检查。

全局输入、CancelAll、disable/enable、Dispose 和同队列 request replacement 仍会让旧 ticket 失效；这不是异步 publication 许可证。

最终 Windows 对照：[run 34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)，Windows Server 2025 `10.0.26100`、SDK `10.0.401`、runtime `10.0.12`。

| 度量 | pinned #260 baseline | candidate |
| --- | ---: | ---: |
| 六种跨队列场景满足正确行为 | 0/6 | 6/6 |
| 每个受影响场景结束 Pending/Sleeping | 1/1 | 0/0 |
| 八个控制场景 | 8/8 | 8/8 |
| 总场景 | 8/14（六个预期失败） | 14/14 |

六种触发覆盖 prepare callback 内 Request(B)、Cancel(missing B)、Cancel(existing B)、Wake(B)，以及 `canPrepare`/`prepareGraphics` 回调内 Request(B)。逐场景 CSV：`doc/experiments/pr260-r0-coordination-samples.csv`。

既有整合验证：Release [34910152555](https://github.com/snownico0722/PaperTodo/actions/runs/34910152555) 与 Debug diagnostics/edge [34910152540](https://github.com/snownico0722/PaperTodo/actions/runs/34910152540) 通过。R0 没跑独立 Persistence、完整 native input、混合 DPI、用户回放、长期空闲或 physical display。

保留失败：`34909929195` 是新宿主漏 `System.IO` 的构建失败；`34910152997` 是 baseline 参数未传入测试进程。最终 `34910341153` 已修正复跑，不能把两次工具问题当产品缺陷。

## R1：H2 changed-pointer selective handoff

### 模型、分支和产品修正

本轮实际系统模型可确认是 **GPT-5.6 Sol**；当前环境没有 GPT-6 Pro 选择参数，因此没有声称已切换。

本轮开始重新确认 #260 仍为 `953ae6266af2bc8c5d1696be7660e5006525202b`、Draft；没有写其分支/main。H2 最新已验证产品行为在 `849d99db59f2dc2ba34a1736c2a08e97a0349256`，后续 `5a576a3ac7e8c8192d04e6126fa755bd0243ae5a` 只增加完整 native-input 实验 workflow，本节文档提交只记录结果。

关键产品提交链收口在 `7af97e03b3543189349e424447ca20477f3578b8`：`src/PaperWindow.EdgeCapsuleQueueProxy.cs` 新增 `ShouldInvalidateEdgeCapsuleQueueProxyPointer(pointer, presentedFrame)`。它非破坏性地镜像当前 `EdgeCapsuleReducer.SamplePointer` 可观察结果：只有 pointer-over 或 visual state 实际改变时，才给 Presenter 产生本地 Pointer dirty/reconcile；**Controller 仍接收每一个物理 pointer sample**，所以 owner/corridor/target 仲裁没有被坐标过滤。

旧 #260 的时序冲突是：同一 retained proxy tick 先为 changed coordinate 排 Presenter Pointer work，随后立即通过 `IsEdgeCapsuleQueueProxyInputSettled` 要求所有成员没有 dirty/no scheduled reconcile 才允许 selective handoff。持续移动因此可以自己持续制造“不 settled”。

当前 helper 与 `EdgeCapsuleReducer.SamplePointer` 静态核对一致，但这是维护风险：两处手写同一状态转换。后续最好共享 non-mutating state-preview helper 或加一致性测试；不在 H2 尚未完整验收时为了整洁扩大代码变更。

### focused Windows A/B

新增 `tests/PaperTodo.EdgeTitleChecks/Pr260MovingHandoffRegression.cs` 与实验 workflow `moving-handoff` baseline/candidate 矩阵。baseline 固定 `953ae626`，candidate 用实验分支。测试使用真实 WPF source、retained DComp proxy、Presenter dirty/reconcile、selective successor/reveal/cloak；不预填旧测试曾使用的 retained pointer sample 私有字段。

第一轮 [34917315231](https://github.com/snownico0722/PaperTodo/actions/runs/34917315231) **无产品结论**：baseline/candidate 都因 focused fixture 的未初始化 `AppController` 进入 preview activation 后 NRE。失败在测试外围 controller adapter，不是 H2 被推翻。

`849d99db59f2dc2ba34a1736c2a08e97a0349256` 用生产已有 `_edgeCapsuleVisualTransactionNotificationDeferred` 边界隔离该 fixture 不具备的 controller 通知批次，同时保留真实 `PaperWindow -> Presenter` reconcile 和 selective/native handoff。最终 [run 34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884) 两臂 Release build 0 warning/0 error、job 均成功：

```text
baseline 953ae626:
RESULT pr260-moving-handoff expectDefect=True releasedDuringMovement=False movingSamples=3 stationarySamples=1 filteredNoOpSamples=0

candidate 849d99db:
RESULT pr260-moving-handoff expectDefect=False releasedDuringMovement=True movingSamples=2 stationarySamples=0 filteredNoOpSamples=3
```

结论：baseline 三个连续 changed-coordinate in-card sample 期间没有 selective handoff，随后一个 stationary sample 才释放；candidate 第 2 个 changed-coordinate sample 已交还，无 stationary sample，共过滤 3 个对 Presenter reducer 无状态变化的 member sample。**这是行为 A/B，不是毫秒性能 A/B。** Hosted runner build 时长不作为性能指标。

### 完整原生输入回归

为避免 focused test 只证明 H2 局部时序，本轮继续新增仅实验分支使用的 `native-input-full` workflow job，运行产品已有 `PaperTodo.EdgeTitleChecks.exe --proxy-native-input`。

最终 [run 34918285745](https://github.com/snownico0722/PaperTodo/actions/runs/34918285745)，验证提交 `5a576a3ac7e8c8192d04e6126fa755bd0243ae5a`，Windows Server 2025 `10.0.26100`、runner image `windows-2025-vs2026 / 20260907.229.1`、SDK `10.0.401`、runtime `10.0.12`：

- Release build：0 warning / 0 error。
- **Native input checks: 574 assertions passed.**
- 覆盖 hidden DComp WM_PAINT/pool reuse、settled-input publication failure/rollback、Button 与 CheckBox hover/DOWN/UP/click/capture/cancel、selective peer retention/final release、跨线程与跨进程透明空洞路由、successor hold/pending completion/completion retry shield、damaged pool。
- artifact `10376819739` 只保留两天；关键结论已写入本文，不依赖 artifact。

因此当前 H2 candidate 不仅 focused A/B 通过，也没有破坏现有 574 项完整 native input 契约。

### R1 仍未证明的内容

- 现有完整 native suite 的 `ObserveSettledPointerEntry` 仍会在普通控件组预填 processed retained sample；focused H2 测试补了 changed-coordinate ordering，但**尚无 move-enter 后不等待 handoff 就立即完整 DOWN/UP 的真实用户手势用例**。
- H8 动画像素与 HRGN 在同一物理帧的一致性仍未测试；574 assertions 不能替代 moving DComp + UI stall + lower-process click 的专门场景。
- filtered no-op sample 仍通知 Controller，因此 H5 的无关桌面 pointer 全局预热暂停/队列仲裁成本仍存在。
- 本轮没有新的 input→first-correct-frame、input→real-control-interactive、prepare→animation-clock 或 physical present 毫秒数据；不能写成“延迟降低 X ms”。

### 下一轮最明确的一步

1. 在现有 native fixture 中新增**不预填 retained sample 的 move-enter → immediate DOWN/UP** 完整手势：第一下必须恰好一次发生，不要求 stationary tick，不重复、不迟到，也不能漏给后方进程；selective peer 仍应保留。
2. 若该完整手势稳定通过，再把 `ShouldInvalidate...` 对 reducer 的手工镜像收敛成共享 non-mutating helper或加入一致性测试，避免未来状态机漂移。
3. 然后推进 H4 空状态启动 baseline/candidate：`AppController.StartAsync` 的 `State.Papers.Count == 0` 分支在可选默认纸张/插件调度后直接 return，静态上仍跳过正常 restore -> shell prewarm -> `CompleteStartupEdgePrewarm` 收尾。覆盖 `createDefaultPaper=true/false`，且非空启动顺序不得提前。
4. H8 单独排队，不能因为 574 项 native input 通过就降级；需要真实移动 proxy、受控 UI 延迟和跨进程后方窗口。

## 后续每轮记录要求

每轮结束都要把实际模型、起始/结束 HEAD、产品/测试修改、原始测试命令/CI URL、可量化结果、被证实/推翻/放弃的假设、风险和下一步写回本文与 #269 阶段评论。后续证据推翻本文时，明确写“替代 Rn 结论”，不要静默改写历史。
