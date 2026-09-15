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
| H2 | retained proxy 每个 changed pointer tick 先制造 Pointer dirty，再要求全部 settled，持续移动可饿死逐卡输入交还 | **R1 已复现并修复。** baseline 需要 stationary sample；candidate changed-coordinate 期间完成交还。move-enter→immediate DOWN/UP 与完整 native-input 回归通过。 |
| H3 | settled-input 启动验证闭包长期强持有 predecessor | **R2 已复现并修复。** 真实 WPF/DComp GC A/B：baseline predecessor 在 live successor 下仍存活，candidate 可回收；随后完整 native-input 574 assertions 通过。只称托管对象非必要存活，不称 GPU/COM 泄漏。 |
| H4 | `State.Papers.Count == 0` 的 `StartAsync` 早退跳过 edge-prewarm startup-ready 收尾 | **R2 已复现并修复。** `createDefaultPaper=true/false` 两种空启动获得 ready，非空恢复保持 generation=1 的原顺序。 |
| H5 | 无关桌面鼠标移动也暂停全局预热并触发逐成员工作 | **待推进。** H1/H2 没有顺带解决。 |
| H6 | `PaperWindow` 旧 `PrewarmLightweight` 直接入口绕过 coordinator | **待推进。** 当前 branch 的 `App.EdgeCapsuleComposition.cs` 已无直接调用，但 `PaperWindow.EdgeCapsulePreview.cs` 仍在 `SystemIdle` 直接调用。 |
| H7 | 容量恢复后当前受限 preview request 的 Size 没恢复 | 待做同 request/content generation 恢复测试。 |
| H8 | DComp 动画像素与 UI timer 更新的 input HRGN 可能错位 | 高优先级原生时序风险；尚未真实移动 + UI stall + 跨进程背景窗口复现。禁止扩大整个 envelope 来“修”。 |
| H9 | completion retry 会丢弃第一次点击 | 当前是防止迟到重放的既有契约；不能简单重放旧点击。R1 已先确保正常稳定卡片快速进入时第一击走真实 WPF。 |
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

产品：
- `97bfca826020657ab6073a91316c0a0077d7359e`：`EdgePrewarmCoordinator.cs` 分离 preparation epoch 与 queue candidate identity；无关队列变化不再作废正在准备的 A；Cancel missing queue 为 no-op。
- `a04a801dacb7dfda0b39b9ec20dc85593872a527`：`AppController.EdgePrewarm.cs` 在可重入的容量/布局工作前捕获 preparation ticket，并在 native staging/publication 前复核 coordinator、epoch、同请求身份，同时保留队列成员/HWND/lifecycle/endpoint 检查。

最终 Windows A/B：[34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)，Windows Server 2025 / SDK 10.0.401 / runtime 10.0.12：

| 度量 | pinned #260 | candidate |
| --- | ---: | ---: |
| 六种跨队列场景满足正确行为 | 0/6 | 6/6 |
| 每个受影响场景结束 Pending/Sleeping | 1/1 | 0/0 |
| 八个控制场景 | 8/8 | 8/8 |
| 总场景 | 8/14（六个预期失败） | 14/14 |

工具失败保留：`34909929195` 缺 `System.IO`；`34910152997` baseline 参数未传入。最终 run 已修正，不能把工具问题当产品缺陷。

## R1 — H2 changed-pointer handoff 与第一击

实际模型：**GPT-5.6 Sol**；环境无 GPT-6 Pro 路由参数。

产品修正收口在 `src/PaperWindow.EdgeCapsuleQueueProxy.cs`：只有 pointer-over / visual reducer 可观察状态实际改变时才给 Presenter 产生本地 Pointer dirty/reconcile；**Controller 仍接收每一个物理 pointer sample**，owner/corridor/target 仲裁没有被过滤。

### changed-coordinate focused A/B

最终 run：[34917898884](https://github.com/snownico0722/PaperTodo/actions/runs/34917898884)：

```text
baseline 953ae626:
releasedDuringMovement=False movingSamples=3 stationarySamples=1 filteredNoOpSamples=0

candidate:
releasedDuringMovement=True movingSamples=2 stationarySamples=0 filteredNoOpSamples=3
```

初轮 `34917315231` 两臂因 focused fixture 未初始化 AppController 而 NRE；后续使用生产已有 notification-deferral 边界隔离 fixture 缺失部分后复跑通过。

### move-enter → immediate DOWN/UP

最终 run：[34920113728](https://github.com/snownico0722/PaperTodo/actions/runs/34920113728)。不预填 retained sample，changed-coordinate 进入后立即发送完整 DOWN/UP：

- baseline changed sample 后未 selective handoff，第一击留在代理路径，不能迟到重放给真实 WPF。
- candidate changed sample 内先 selective handoff；代理不消费第一击，真实 WPF 恰好收到一次 DOWN、一次 UP、一次 click/toggle；peer 继续 retained。
- 前一轮 `34919855512` 是 focused fixture 不稳定失败；后续修复 fixture 后得到上述最终结果。

### 完整 native-input

[34918285745](https://github.com/snownico0722/PaperTodo/actions/runs/34918285745)：Release 0 warning / 0 error，**574 assertions passed**。覆盖 hidden DComp paint/pool reuse、settled-input rollback、Button/CheckBox hover/DOWN/UP/click/capture/cancel、selective peer retention/final release、跨线程/跨进程透明空洞、successor hold/pending completion/completion retry shield、damaged pool。

R1 没有新的毫秒 latency 或 physical-present 数据；H5/H8 仍未关闭。

## R2 — H4 空启动收尾 + H3 predecessor 托管存活

实际模型：**GPT-5.6 Sol**；环境无 GPT-6 Pro 模型选择参数。

R2 起始 HEAD：`33b43aa708d2ffc215ba483c88a93f88ac744a74`。#260 重新确认仍为 `953ae6266af2bc8c5d1696be7660e5006525202b` Draft；未写 main / #260 原分支。

### H4 空状态 startup-ready

产品改动在 `src/AppController.PluginStartup.cs`：公共启动尾部仅在 `_paperSurfaceRestoreGeneration == 0 && !_edgePrewarmStartupReady` 时完成空启动 edge-prewarm 收尾；两种空启动获得 ready，而非空恢复仍由原 restore/prewarm generation 顺序完成。

最终 Windows A/B：[34921002375](https://github.com/snownico0722/PaperTodo/actions/runs/34921002375)。candidate Release 0 warning / 0 error：

```text
empty-no-default: papers=0 restoreGeneration=0 startupReady=True coordinatorCreated=True
empty-default:    papers=1 restoreGeneration=0 startupReady=True coordinatorCreated=True
nonempty:         papers=1 restoreGeneration=1 startupReady=True coordinatorCreated=True
```

baseline 同一 harness 对两种空启动明确复现旧缺陷，并保留 nonempty 控制。因此 H4 已关闭。

### H3 settled-input startup callback lifetime

#260 原路径的 `TryReleaseSettledInput()` 构造 `StillValid()`，强捕获旧 proxy 和 endpoint windows，并把 `_ => StillValid()` 存入 successor `_endpointCommitRequested`。`FinishStartup()` 虽清 `_predecessor`，却不清该回调。

候选产品提交 `fbcb187ad1c22f3c0342fda5a30a76a9d7c4c88a`：startup validator 改用 `WeakReference` predecessor/window guards。startup 期间 staged successor 的 `_predecessor` 本来就强保持 predecessor，因此同步验证语义不变；publication 后清 `_predecessor` 后，validator 不再形成历史强引用链。没有改 cloak/DComp fence、publication、rollback 或输入路由顺序。

真实 WPF/DComp GC focused harness 保持 peer-only successor 存活，移除 fixture 自己的 `_originalGeneration` 强引用后强制 GC，并继续验证 successor/peer 正常 final release。最终 run：[34922247020](https://github.com/snownico0722/PaperTodo/actions/runs/34922247020)，两臂 Release 0 warning / 0 error：

```text
baseline 953ae626:
expectDefect=True retiredAliveAfterGc=True releaseCount=2

candidate 0acb9614:
expectDefect=False retiredAliveAfterGc=False releaseCount=2
```

`releaseCount=2` 是 selective target release + 测试尾部 peer final release，不是重复 selective handoff。

两次测试工作流失败保留且**无产品结论**：
- `34921819496`：把 `StartupObject` 作为全局 MSBuild 属性传入，污染 ProjectReference，`PaperTodo.Plugin.Abstractions` 报 CS2017；后改为测试 csproj 条件 entry point。
- `34922022733`：harness 漏 `using PaperTodo;`，`DeviceScreenPoint` 编译失败；补 namespace 后最终 A/B 通过。

随后在完整产品原生输入专项复核 H3 候选：[34922484480](https://github.com/snownico0722/PaperTodo/actions/runs/34922484480)，验证树 `bc68b4ab5f853d6002613d4999f0b9403460b152`，Windows Server 2025 / SDK 10.0.401 / runtime 10.0.12：

- Release build：**0 warning / 0 error**。
- `PaperTodo.EdgeTitleChecks.exe --proxy-native-input`：**574 assertions passed**。
- 仍覆盖 settled-input publication safe rollback、真实 Button/CheckBox hover/手势/capture/cancel、peer retention/final release、跨线程/跨进程透明空洞、completion retry shield 与 damaged pool。

因此 H3 现已具备：冻结 baseline 明确复现 → candidate GC 行为反转 → 完整 native-input 契约不回归。当前不再仅称“静态风险”；但仍只证明托管对象可回收，不把它扩大成 GPU/COM/长期内存泄漏结论。

## 下一轮最明确的工作

1. **H6 统一 graphics prewarm 入口。** 当前 branch 已确认 `App.EdgeCapsuleComposition.cs` 无直接预热，但 `PaperWindow.EdgeCapsulePreview.cs::ScheduleEdgeCapsuleCompositionPrewarm()` 仍 `SystemIdle` 直接调用 `EdgeCapsuleQueueCompositionProxy.PrewarmLightweight(Dispatcher)`，绕过 `EdgePrewarmCoordinator` 的 startup/interaction/can-prepare 规则。下一轮先在当前 HEAD 找全 caller，再做 baseline/candidate 资格测试；倾向让 `PaperWindow` 报告需求，由 coordinator 统一执行，而不是复制第二套条件。
2. **H5 无关桌面鼠标活动。** 区分“阻止新的低优先级准备开始”和“使已在途结果失效”；无关纯移动不能反复取消所有可选预热，真实 click/capture/drag 仍保守让路。
3. **H7 受限 preview Size 恢复。** 补同 request/content generation 的端到端恢复测试，不能只验证 HWND capacity。
4. **H8 原生时序专项。** 真实 DComp 移动 + 受控 UI stall + 跨进程背景窗口，验证可见前沿不漏点、真实空洞仍穿透。574 assertions 不能替代该实验。
5. **H10 先量化。** 记录 idle tick / presentation read / queue arbitration / CPU/handle 规模，再决定是否改事件驱动。

本任务当前仍没有新的 request→first-correct-frame、prepare→animation-clock 或物理显示延迟数据；不得把 GC、调度正确性或手势修复写成“降低了 X ms”。
