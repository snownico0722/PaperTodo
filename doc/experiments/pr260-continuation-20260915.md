# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 续接规则

- 实验 PR：[#269](https://github.com/snownico0722/PaperTodo/pull/269)。唯一可写分支：`experiment/pr260-continuation-20260915`。
- 初始来源固定为 #260 `953ae6266af2bc8c5d1696be7660e5006525202b`，当时 main 为 `2cc019d08143ed07e8774782c0ecf733e7eb7892`。#269 以 #260 为 base 只是为了显示实验增量。
- 每轮先重新读取 #260、#269 的描述/阶段评论/commits/diff/CI 和本文，再从已完成点继续；不要依赖聊天上下文，不重复已否定实验。
- 只允许 fast-forward 写实验分支；禁止写 main、写 #260 原分支、merge、force-push、删除远程分支。#269 始终保持 Draft。
- 用户希望优先 GPT-6 Pro；只有环境真的允许选择时才能声称使用。每轮记录实际可确认模型。
- 编译/断言通过不等于用户桌面、混合 DPI、跨进程输入或物理显示器实测。没有测量的延迟绝不补数字。

## 多模型审查假设与当前裁决

用户提供五份 5.6 Sol / Astra 审查。Astra 给予较高初始置信度，但所有结论以代码、冻结 baseline、实际 Windows 测试和可复现结果更新。

| 编号 | 假设 | 当前状态 |
| --- | --- | --- |
| H1 | 全局 `Version` 被无关队列 Request/Wake/Cancel 改写，使准备中 A 完成结果失效并留在 Sleeping | **R0 已复现并修复。** Windows 对照六种跨队列场景 baseline 0/6、candidate 6/6；受影响场景 Pending/Sleeping 1/1 → 0/0。 |
| H2 | retained proxy 每个 changed pointer tick 先制造 Pointer dirty，再要求所有成员 settled；持续移动可饿死逐卡真实输入交还 | **R1 已复现并修复。** baseline 需要 stationary sample；candidate 在 changed-coordinate sample 内交还。随后 move-enter→immediate DOWN/UP focused A/B 和完整 native-input 回归均通过候选。 |
| H3 | settled-input 启动验证闭包长期持有 predecessor | **R2 已从静态风险升级为 Windows GC A/B 复现并修复候选。** baseline 在 peer successor 仍存活时 predecessor 强引用存活；candidate 可回收。当前产品修法为启动闭包使用 weak predecessor/window guards；还需完整 native-input 回归。只称托管对象非必要存活，不称 GPU/COM 泄漏。 |
| H4 | `State.Papers.Count == 0` 的 `StartAsync` 早退跳过 edge-prewarm startup-ready 收尾 | **R2 已复现并修复。** `createDefaultPaper=true/false` 两种空启动都走统一空启动收尾；非空恢复保持原有 generation 顺序。 |
| H5 | 无关桌面鼠标移动也暂停全局预热并触发逐成员工作 | 未修改；H1/H2 不等于解决 H5。 |
| H6 | `PaperWindow` 旧 `PrewarmLightweight` 直接入口绕过 coordinator | 仍存在；当前实验分支的 `App.EdgeCapsuleComposition.cs` 已无直接预热，重点是 `PaperWindow.EdgeCapsulePreview.cs` 旧入口。下一轮先核对所有 current-ref caller，再决定删除或改为 coordinator request。 |
| H7 | 容量恢复后当前受限 preview request 的 Size 没恢复 | 待做同请求/内容代次恢复测试；不能只看底层 HWND 容量。 |
| H8 | DComp 动画像素与 UI timer 更新的 input HRGN 可能错位 | 高优先级原生时序风险，尚未真实移动 + UI stall + 跨进程手势复现。禁止扩大整个 envelope 来“修”。 |
| H9 | completion retry 会丢弃第一次点击 | 当前是防止迟到重放的既有契约；不能简单重放旧点击。R1 已先保证正常稳定卡片的快速进入第一击走真实 WPF。 |
| H10 | 常驻 16ms 采样和逐卡交还全队列流程有额外空闲/规模成本 | 先测量；已有 SetWindowRgn 未变 fast path、surface AddRef、单 spare host，不能重复实现。 |
| H11 | DPI/display change 时立刻清空 input region | **不照抄。** 可见 cover 仍在时清空 HRGN 可能把点击泄漏给后方应用，必须和视觉/真实源恢复共同设计。 |

## 已否定或暂不重开

- E-017 dormant surface/visual cache：命中高但没有减少 cloak/flush 主成本；额外 `WaitForCommitCompletion` 无采用收益。
- E-018 原样 async successor：`transaction.commit` 变短但 prepare→animation-clock 变慢，不以内计时改善冒充用户延迟改善。
- 路线 3 atlas/native shape 当前实现不重开；暂不做大型 WPF 队列宿主改写。
- 不删除 cover-before-cloak fence，不绕过 WPF 原生同步，不把整个透明 output envelope 变成输入阻挡区。
- Desktop Duplication/Rendering/RenderComplete/DComp commit completion 都不是逐卡物理 panel present acknowledgement。

## R0 — H1 跨队列预热失效

R0 实际会话模型：**GPT-6 Astra Pro**。

产品提交：

- `97bfca826020657ab6073a91316c0a0077d7359e`：`EdgePrewarmCoordinator.cs` 分离 preparation epoch 与 queue candidate identity；无关队列变化不再作废正在准备的 A；Cancel missing queue 为 no-op。
- `a04a801dacb7dfda0b39b9ec20dc85593872a527`：`AppController.EdgePrewarm.cs` 在可能重入的容量/布局工作前捕获 preparation ticket，并在 native staging/publication 前复核 coordinator、epoch、同请求身份，同时保留队列成员/HWND/lifecycle/endpoint 检查。

最终 Windows 对照：[34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)，Windows Server 2025 / SDK 10.0.401 / runtime 10.0.12：

| 度量 | pinned #260 | candidate |
| --- | ---: | ---: |
| 六种跨队列场景满足正确行为 | 0/6 | 6/6 |
| 每个受影响场景结束 Pending/Sleeping | 1/1 | 0/0 |
| 八个控制场景 | 8/8 | 8/8 |
| 总场景 | 8/14（六个预期失败） | 14/14 |

失败记录保留：`34909929195` 是测试宿主缺 `System.IO`；`34910152997` 是 baseline 参数未传入。最终 run 已修正，不能把工具问题当产品缺陷。

## R1 — H2 changed-pointer selective handoff 与第一击

R1 实际系统模型：**GPT-5.6 Sol**；环境无 GPT-6 Pro 路由参数。

产品修正收口在 `7af97e03b3543189349e424447ca20477f3578b8`：`PaperWindow.EdgeCapsuleQueueProxy.cs` 只在 pointer-over / visual reducer 可观察状态实际变化时给 Presenter 产生本地 Pointer dirty/reconcile；**Controller 仍接收每个物理 pointer sample**，owner/corridor/target 仲裁未被坐标过滤。

### H2 focused A/B

最终 changed-coordinate 对照：[34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884)：

```text
baseline 953ae626:
releasedDuringMovement=False movingSamples=3 stationarySamples=1 filteredNoOpSamples=0

candidate:
releasedDuringMovement=True movingSamples=2 stationarySamples=0 filteredNoOpSamples=3
```

早一轮 `34917315231` 两臂都因 focused fixture 未初始化 AppController 而 NRE；后续用生产已有 notification-deferral 边界隔离 fixture 缺失部分后复跑通过。

### move-enter → immediate DOWN/UP

最终 focused run：[34920113728](https://github.com/snownico0722/PaperTodo/actions/runs/34920113728)。测试不预填 retained sample，使用 changed-coordinate 进入后立即发送完整 DOWN/UP：

- pinned baseline：changed sample 后尚未 selective handoff，第一击留在代理路径，真实 WPF 不得到这次正常点击；随后再处理不能迟到重放。
- candidate：changed sample 内先完成 selective handoff，代理不消费第一击；真实 WPF 只收到一次 DOWN、一次 UP、一次 click/toggle；peer 继续 retained。
- 前一轮 `34919855512` 是 focused fixture 不稳定失败，`5ce0f0bc61eecb7d2871fb32a6dddf182752727f` 后复跑得到最终结果。

### 完整原生输入回归

[34918285745](https://github.com/snownico0722/PaperTodo/actions/runs/34918285745)：Release build 0 warning/0 error，**574 assertions passed**。覆盖 hidden DComp paint/pool reuse、settled-input rollback、Button/CheckBox hover/DOWN/UP/click/capture/cancel、selective peer retention/final release、跨线程/跨进程透明空洞、successor hold/pending completion/completion retry shield、damaged pool。

R1 没有新的毫秒 latency 或 physical-present 数据；H5/H8 仍独立未关闭。

## R2 — H4 空启动收尾 + H3 predecessor 托管存活

R2 实际系统模型：**GPT-5.6 Sol**；当前工具无 GPT-6 Pro 模型选择参数。

R2 开始 HEAD：`33b43aa708d2ffc215ba483c88a93f88ac744a74`。#260 重新确认仍为 `953ae6266af2bc8c5d1696be7660e5006525202b` Draft，本轮没有写 main 或 #260 原分支。

### H4 空状态 startup-ready

产品修正在 `src/AppController.PluginStartup.cs`：公共启动尾部仅在 `_paperSurfaceRestoreGeneration == 0 && !_edgePrewarmStartupReady` 时完成空启动 edge-prewarm 收尾。这样两种空启动获得 ready，而非空恢复（generation=1）仍由既有 restore/prewarm 顺序完成，没有把门禁粗暴提前到构造器。

最终 Windows 对照：[34921002375](https://github.com/snownico0722/PaperTodo/actions/runs/34921002375)。candidate Release build 0 warning/0 error，并得到：

```text
empty-no-default: papers=0 restoreGeneration=0 startupReady=True coordinatorCreated=True
empty-default:    papers=1 restoreGeneration=0 startupReady=True coordinatorCreated=True
nonempty:         papers=1 restoreGeneration=1 startupReady=True coordinatorCreated=True
```

baseline 同一 harness 明确接受两种空启动的旧缺陷并保留 nonempty 控制。H4 因此从“静态确认”升级为已复现、已修复、三分支 Windows 回归通过。

### H3 settled-input startup callback lifetime

静态路径：#260 的 `TryReleaseSettledInput()` 构造 `StillValid()`，强捕获旧 proxy 与 endpoint windows，并把 `_ => StillValid()` 存入 successor 的只读 `_endpointCommitRequested`。`FinishStartup()` 虽清 `_predecessor`，却没有清这个回调，因此当前 retained successor 仍能强持有退休 predecessor。该回调只用于 startup validation；这里讨论的是**托管对象非必要存活**，不是 GPU/COM 泄漏。

本轮候选 `fbcb187ad1c22f3c0342fda5a30a76a9d7c4c88a` 将该 startup validator 的 predecessor 和 endpoint window guards 改为 `WeakReference`：startup 期间 staged successor 本身仍强持有 predecessor，因此验证语义不变；publication 后清 `_predecessor` 时，validator 不再成为历史强引用链。没有改 cloak/DComp fence、publication、rollback 或 input route 顺序。

新增真实 WPF/DComp GC focused harness：保持 peer-only successor 存活，移除 fixture 自己的 `_originalGeneration` 强引用，强制 GC，同时继续证明 successor/peer 仍有效并正常 final release。最终对照：[34922247020](https://github.com/snownico0722/PaperTodo/actions/runs/34922247020)，baseline/candidate 均 Release build 0 warning/0 error：

```text
baseline 953ae626:
expectDefect=True retiredAliveAfterGc=True

candidate 0acb9614:
expectDefect=False retiredAliveAfterGc=False
```

两臂随后都完成 remaining peer 的正常释放；日志中的最终 `releaseCount=2` 包括 selective target release + 测试结尾 peer final release，不表示 selective handoff 重复执行。

测试工作流有两次**无产品结论**的前置失败并已保留：

- `34921819496`：把 `StartupObject` 作为全局 MSBuild 属性传入，污染 ProjectReference，`PaperTodo.Plugin.Abstractions` 报 CS2017；改为测试 csproj 内受 `Pr260CallbackLifetimeHarness` 条件控制的 entry point。
- `34922022733`：lifetime harness 漏 `using PaperTodo;`，`DeviceScreenPoint` 编译失败；补 namespace 后最终 A/B 通过。

H3 当前仍需候选完整 `--proxy-native-input` 回归；本提交用 `[native-input-ci]` 触发该验证。若通过，下一轮再决定是否值得把 weak-guard 实现进一步收敛为“startup callback 完成后显式清空”的更简单结构；不能为了代码整洁在无等价回归时改验证语义。

## 当前下一步

1. **先读取本提交触发的完整 native-input 结果。** H3 focused A/B 通过不等于所有 publication/rollback/capture 路径通过。
2. H3 完整回归若绿，优先推进 **H6 统一 graphics prewarm 入口**：先以当前实验分支为 ref 找全 `PrewarmLightweight` caller，避免依据 main 的搜索结果误删；目标是让 `PaperWindow` 只报告需求而不直接绕过 coordinator。
3. 随后推进 H5：区分“阻止新的低优先级准备开始”和“使已在途结果失效”，无关桌面纯移动不能反复取消所有可选预热；真实 click/capture/drag 仍必须保守让路。
4. H7 受限 preview Size 恢复需要同 request/content generation 测试。
5. H8 保持高优先级独立实验：真实 DComp 移动 + 受控 UI stall + 跨进程背景窗口，验证可见前沿不漏点、真实空洞仍穿透；574 assertions 不能替代它。
6. H10 先记录 idle tick / presentation read / queue arbitration / CPU/handle 规模，再决定是否事件驱动；不凭调用次数猜收益。

本任务到目前没有新的 request→first-correct-frame、prepare→animation-clock 或物理显示延迟数据；不得把 GC、调度正确性或手势修复写成“降低了 X ms”。
