# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 边界与续接入口

- 实验 PR：[#269](https://github.com/snownico0722/PaperTodo/pull/269)。唯一可写分支：`experiment/pr260-continuation-20260915`。
- 初始来源为 #260 的 `953ae6266af2bc8c5d1696be7660e5006525202b`，其 main 基线为 `2cc019d08143ed07e8774782c0ecf733e7eb7892`。#269 的 base 选 #260 只为展示增量，不授权向它合并或写入。
- 本文、PR 描述、阶段评论、commit/diff 和 CI 是持久化上下文。每轮首先重新读取 #260、#269 及最新阶段结果，然后续接，不依赖聊天或其他轮次本地文件。
- 只修改/提交/推送本实验分支。禁止写 main、写 #260 原分支、merge、force-push、删除远程分支。保持 Draft。
- 北京时间 2026-09-15 09:00、10:00、11:00、12:00、13:00、14:00、15:00、16:00 共八轮；立即执行的 R0 不占这八轮。定时任务已成功创建并绑定 #269。
- 用户优先 GPT-6 Pro。调度工具未提供模型参数，不能承诺强制选择。每轮如实记录可确认模型；不能核验路由时明确说明，不冒充 GPT-6 Pro。
- 每轮结束记录模型、基线/HEAD、具体修改和测试、量化数据或未测原因、证实/推翻/放弃方向、风险和下一步。后续证据推翻旧结论时明确标记。
- 上轮 CI 尚未完成时先检查结果；写入前读取最新 HEAD；只 fast-forward，发生并发变化时衔接新提交，不覆盖。不要为重复运行检查制造产品改动。

## 五份审查的独立归纳

来源：用户提供的 `5.6sol模型审查4.md`、`astra模型审查1.md`、`astra模型审查2.md`、`astra模型审查3.md`、`astra模型审查4.md`。Astra 初始置信度较高，但不同报告的重复意见不是独立复现；以下按代码及测试更新。

| 编号 | 来源与假设 | 当前判定 |
| --- | --- | --- |
| H1 | Sol4：全局 Version 被无关队列 Request/Wake/Cancel 改写，使准备中 A 的完成不被消费、留在 Sleeping | **R0 已在真实生产 coordinator + Windows WPF Dispatcher 下复现并修复**；六种触发是同一缺陷，不是六个独立 bug。实际原生 admission/用户延迟尚未专项测量。 |
| H2 | Astra3/4：先逐卡 Pointer invalidation，随后要求全员 settled，持续移动可阻止自动逐卡交还 | 下一轮第一优先级。不能预填 retained sample 绕过真实时序，不能直接删除稳定性检查。 |
| H3 | Astra2/4：启动验证闭包继续持有 predecessor | 静态路径成立，未做 GC 实测；不是已证明的 GPU 泄漏。核查回调最后使用点，WeakReference 验证退休后收集。 |
| H4 | Astra2：无纸张 StartAsync 跳过预热就绪收尾 | 待专门复现两种空启动分支；保留非空 preview→Shell→native 顺序。 |
| H5 | Astra1/2/3/4 + Sol4：无关桌面鼠标使全部预热暂停、逐卡刷新 | 尚未修改；H1 不等于解决鼠标暂停范围。保留真实应用交互优先，不顺便缩短 quiet delay。 |
| H6 | Astra3/4：PaperWindow 的旧 PrewarmLightweight 入口绕过 coordinator | 待验证并收拢，优先删重复入口，不增加第二套许可判断。 |
| H7 | Astra3：容量恢复未更新当前 preview request 的受限 Size | 待验证同请求/内容代次恢复，覆盖插件提供器及内容闭包，不能只测 HWND 容量。 |
| H8 | Astra2/3：DComp 动画与 UI timer 输入 region 可失配 | 高优先级原生时序风险；需要跨进程完整手势与受控 UI 延迟。尚未实机复现，禁止用整个 envelope 挡住空洞。 |
| H9 | Astra3：重试丢弃第一次点击 | 现有防迟到误触契约，不能简单补发过期点击；先改善按下前交还，再测即时完整手势。 |
| H10 | 多份：常驻重复采样/逐卡交还整队成本 | 先归因。已有 SetWindowRgn 未变缓存、surface AddRef、单 spare host，不重复实现。 |
| H11 | Sol4：DPI 先清空 input region | **不照抄**：画面仍可见时清空可能泄漏点击；必须和 cover/真实窗口恢复一起设计并验收。 |

## 已否定或暂不重开

- E-017 dormant surface/visual cache 命中不等于减少 cloak/flush；额外 WaitForCommitCompletion 未显示采用收益。
- E-018 async successor 的 transaction P95 虽短，prepare→animation-clock 更晚，不能原样恢复。
- 路线3 atlas/native shape 当前接入未获采用；不重开，暂不采用之前聊天提出的大型队列宿主重写。
- 不删除 cover-before-cloak fence，不绕过 WPF 窗口同步，不放大透明空洞输入区域。
- Desktop Duplication 是桌面合成捕获，不是物理面板扫描验收；Rendering、RenderComplete、DComp Commit 完成不是逐卡物理 present ack。

## 验证规则

冻结 baseline、candidate、运行时、输入和诊断配置。新缺陷优先 baseline 红/candidate 绿；报告实际命令、场景和 CI URL。编译、源码推演、原作者历史数据均不代替本轮实测。输入→首次正确桌面变化、输入→真实控件可交互、prepare→animation-clock、transaction 和空闲成本分开统计。

## R0 完成：H1 跨队列预热失效修复

本节替代初始“尚无产品修改/Windows 测试”和 PR 早期“验证进行中”检查点。完成日期北京时间 2026-09-15，首个定时轮次尚未开始。

### 模型、环境与提交

模型：本会话标识 **GPT-6 Astra Pro**；无独立运行时路由查询，未切换模型，不承诺后续轮次同模型。

本地 Linux 无 dotnet，GitHub DNS 不通；通过实际可写 GitHub 连接器提交，并在 GitHub Actions Windows Server 2025 `10.0.26100` 上运行。测试 SDK `10.0.401`，实际 runtime `10.0.12`。

| 提交 | 实际变化 |
| --- | --- |
| `97bfca826020657ab6073a91316c0a0077d7359e` | `EdgePrewarmCoordinator.cs` 独立 preparation epoch；现有 Candidate 实例识别队列请求，Version 仅作诊断/FIFO及唯一请求序号。无关队列变动不再使 A 失效，Cancel 不存在队列为 no-op。 |
| `a04a801dacb7dfda0b39b9ec20dc85593872a527` | `AppController.EdgePrewarm.cs` 在容量/布局回调前捕获准备 ticket；原生 staging 前及原有发布回调检查原 coordinator、epoch、同请求身份，并保留队列/窗口句柄/生命周期/端点校验。 |
| `3e1793b`、`2e512a2`、`a1d2c6792874561442e5077728a9b7faf47977ac` | 新独立回归宿主链接真实生产 scheduler，真实 WPF Dispatcher/Rendering/timer。只替换可选诊断日志；补齐 WPF 项目的 System.IO 导入。 |
| `2a16df7`、`2ba3416ec7490efd1be57daae4a094065fa098f4` | 仅本实验分支的 baseline/candidate workflow；修正显式 baseline 参数传递并验证结果中的模式。 |
| `f7b556a4035d96c2853e5240b753af0af4801c98` | 保存最终对照逐场景 CSV；本节所在提交只更新记录，不改已测产品或测试代码。 |

产品改动只有两个文件，不改 DComp/input region/动画/fence。仍然同步准备；ticket 只在当前有效准备期间可用，不是异步 publication 许可。全局输入、CancelAll、禁用/启用、Dispose 和本队列替换仍使旧请求失效。

知识影响：局部有效性规则已写附近注释。本实验不改变既定 WPF/DComp 职责，不把尚未采用的候选写成 Architecture/Decisions，也不新增正式产品 changelog。

### 测试命令与结果

独立宿主：`tests/PaperTodo.Pr260RegressionChecks`。workflow 用 `git show <source-ref>:src/EdgePrewarmCoordinator.cs` 取得冻结源码，同一测试代码分别执行：

```powershell
dotnet build tests/PaperTodo.Pr260RegressionChecks -c Release "-p:CoordinatorSource=<frozen-source-path>"
# baseline 可执行文件：必须恰好重现指定六个失败且八个控制通过
./tests/PaperTodo.Pr260RegressionChecks/bin/Release/net10.0-windows/PaperTodo.Pr260RegressionChecks.exe --expect-baseline-defects
# candidate 可执行文件：必须十四个场景全通过
./tests/PaperTodo.Pr260RegressionChecks/bin/Release/net10.0-windows/PaperTodo.Pr260RegressionChecks.exe
```

这两个命令针对各自先行编译的 baseline/candidate，不是对同一 candidate 二进制切开关。baseline 源码 `953ae626`，最终候选/宿主提交 `2ba3416e`。

**最终确认运行：[34910341153](https://github.com/snownico0722/PaperTodo/actions/runs/34910341153)。** baseline job `104196193795`、candidate job `104196193630` 均成功；两臂构建各 0 warning、0 error。

| 度量 | baseline | candidate |
| --- | --- | --- |
| 六种跨队列回归满足正确行为 | 0/6 | 6/6 |
| 每个受影响场景结束后的 Pending / Sleeping | 1 / 1 | 0 / 0 |
| 同队列替换、取消、全局取消、禁用重启、输入后嵌套静默结束、Dispose、Deferred/Wake、FIFO 八个控制 | 8/8 | 8/8 |
| 总场景通过 | 8/14（六个预期失败） | 14/14 |
| 完整动画/输入到物理显示延迟 | 未测 | 未测 |

六种触发：prepare A 回调内 Request(B)、Cancel(不存在 B)、Cancel(已有 B)、Wake(Sleeping B)，以及 canPrepare/prepareGraphics 回调内 Request(B)。前四个场景还验证 admission guard：baseline 被错误作废，candidate 保持有效。baseline guard 适配使用原 AppController 的 Version 比较；candidate 通过反射调用真实 ticket API。**这不是实际 DComp publication 的端到端测试。**

[逐场景 CSV](pr260-r0-coordination-samples.csv) 来自最终实际 JSON，不包含用户数据。原始结果 JSON SHA-256：baseline `37c631d420f074dc45e769dae96ba5e7edf6433c0a6615491fcd6b67b667f159`；candidate `84b78fdd2dde28e284272709269070ddb78d79c31cf99d642631e837d817209b`。Artifacts `10374351805` / `10373658664` 受仓库策略仅保留两天，因此关键结果已入库，不能只靠临时下载链接续接。

既有整合验证在 `a1d2c679` 上完成，其产品/测试代码与 `2ba3416e` 相同，后者只修新 workflow：

- [Pull request build 34910152555](https://github.com/snownico0722/PaperTodo/actions/runs/34910152555)：Release 产品构建、Markdown semantic/editing、Todo navigation、EdgeTitle、EdgePreview rendering/lifecycle、Threading、Startup/shutdown/small-workset 的所有实际步骤通过。job `104195628426`。
- [Edge diagnostics 34910152540](https://github.com/snownico0722/PaperTodo/actions/runs/34910152540)：Journal Debug、Journal Release disabled collection、Native/Dispatcher observer、EdgeTitle/prewarm Debug 通过。job `104195627745`。
- PR Test Debug 因未请求 debug package 按规则 skipped；**独立 Persistence Checks 本轮未运行**。更正早期检查点未经核实的“持久化工作流也被触发”说法。
- 未执行 `--proxy-native-input` 完整专项、用户实机回放、混合 DPI、长时间空闲或物理显示/闪烁验收。普通 observer/EdgeTitle 通过不覆盖这些项目。

### 保留失败与解释修正

1. `34909929195`：新增宿主遗漏 System.IO，Path/File 编译失败；产品缺陷场景未执行。`a1d2c679` 已修复，不把编译红当成 baseline 复现。
2. `34910152997`：实际六个 baseline 失败形态与 candidate 14/14 均已出现，但 baseline 程序报告 `Baseline=False`，缺陷期望参数未正确传入，工作流因此红。其日志有 `baselineExactSignature=True`，不是发现第七个产品缺陷。`2ba3416e` 改为显式参数并检查输出模式；最终 `34910341153` 两臂正确通过。
3. 不把 JSON 的 HarnessMilliseconds 当作 UI/动画延迟，它包含测试泵消息和受控等待；不比较两个 hosted runner 的性能高低。
4. H1 从“待验证静态假设”更新为“生产调度器缺陷已复现，局部修复已通过对应对照和既有整合验证”。H2—H11 不因 H1 结果自动成立或解决。

### 下一轮最明确的一步

先重新读取 #260/#269 的真实最新状态与本节后的评论/提交。R0 结束复核 #260 原分支仍是 `953ae626`，未写入 main 或 #260。不要重新跑原样 H1 调研，也不要回到 atlas/cache/async-successor 失败路线。

**优先推进 H2：从真实连续鼠标移动开始复现自动逐卡交还被自己新建的 Pointer dirty 阻止。** 入口是 `EdgeCapsuleQueueCompositionProxy.Routing.cs` 的 OnSampleTimerTick/ShouldReleaseForPointerInput、`PaperWindow.EdgeCapsuleQueueProxy.cs`、Presenter reconciliation，以及 `ProxyNativeInputChecks.cs` 的 ObserveSettledPointerEntry。新增用例不能预设 `_hasRetainedPointerSample`、`_lastRetainedPointer`、`_lastRetainedPointerFrames`，也不能先等待交还成功才假装测了“进入即点”。

先取得 baseline 失败，再考虑将交还判断衔接到现有 reconcile 完成边界；保留来源/端点稳定性，避免新增轮询或强制 Flush。测实际进入→真实控件可交互与第一次完整 DOWN/UP，记录当前队列其他成员是否仍复用。同步关注 H8，但不要用放大 region 或删除 fence 解决它。

若该轮不能运行原生输入，用同一 PR 如实记录阻塞；优先实际推进可验证的 H3（启动回调托管存活）、H4（空启动就绪）或 H6（旧并行预热入口），不能伪造 H2/H8 原生测试结果。后续新增 product 改动提交带 `[ci]`，仅修该独立宿主/workflow 可用 `[coordination-ci]`；文档/CSV提交无需重新跑未变代码。
