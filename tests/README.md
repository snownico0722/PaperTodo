# 行为检查

本目录保护结果和可观察行为，不保护某一版内部写法。独立可执行检查项目继续使用 `dotnet run`，不是 `dotnet test` 项目；不切换框架或强行合并项目。

## 运行

需要 Windows、.NET 10 SDK、PowerShell 7。涉及窗口、输入、裁切和渲染的检查还需要可用的 Windows/WPF 桌面；插件组需要 Node.js。先按仓库要求初始化固定的 `vendor/wpf-notifyicon` 子模块。涉及数据的故障注入应使用测试隔离目录，不能指向日常数据目录。

```powershell
# 仅列出项目和配置，不执行，也不要求 Windows。
./tools/testing/Run-Checks.ps1 -Group all -List

# 常规行为回归。
./tools/testing/Run-Checks.ps1 -Group regression

# 插件合同、存储故障、Debug 诊断，分别按需运行。
./tools/testing/Run-Checks.ps1 -Group plugins
./tools/testing/Run-Checks.ps1 -Group persistence
./tools/testing/Run-Checks.ps1 -Group diagnostics

# 全部正确性检查；不包含性能采样。
./tools/testing/Run-Checks.ps1 -Group all
```

每组仍可直接 `dotnet run --project tests/PaperTodo.<项目名> -c Release`。诊断组必须使用下表的配置，不能统一替换成 Release。执行器按顺序运行并汇总失败，不把未执行或失败报告为通过。

## 分组

| 分组 | 项目 | 主要保障 |
| --- | --- | --- |
| regression | WindowStackChecks、WindowCloseActivationChecks | 实际窗口层级、关闭后激活，不只检查字段 |
| regression | MarkdownSemanticChecks、MarkdownEditingChecks | 解析结果、公式扫描/矢量布局、编辑、撤销、排版与可见文本 |
| regression | TodoNavigationChecks | 待办定位与导航结果 |
| regression | EdgeTitleChecks、EdgePreviewChecks | 标题、几何、交互、预览结果与生命周期 |
| regression | ThreadingChecks、LifecycleChecks | 跨线程资源可用性、启动退出、保存和释放 |
| plugins | ProtocolPolicyChecks、PluginApiChecks、SettingsApiChecks、CodexCliBridgeChecks | 公开插件合同、请求行为、设置、提示词和桥接 |
| plugins | plugin-samples/Check-Samples.ps1 | 清单有效性、脚本可解析、示例行为、源码与分发产物一致 |
| persistence | PersistenceChecks，Release / win-x64 | 保存失败、回滚与原始数据保留 |
| diagnostics | EdgeDiagnosticJournalChecks，Debug + Release | 采集与退出；Release 不启用产品采集 |
| diagnostics | EdgeLatencyObservationChecks、EdgeTitleChecks，Debug | 实际观察器与 Debug 路径 |

四个相关 CI 工作流调用同一入口，保留原有触发方式和配置差异。`fixtures/` 保存样本；性能采样独立放在 `tools/PaperTodo.MarkdownBenchmarks`，真进程诊断放在 `tools/PaperTodo.EdgeDiagnostics`。

插件组会重建仓库内的原生示例并检查 `plugins/` 分发副本差异，这是原有产物一致性检查，不是只读命令；不安装到日常使用的 PaperTodo 数据目录。

## 用例边界

保留实际输入输出、可执行 policy/reducer、协议与公开程序集兼容、持久化、线程行为、窗口和像素/排版回归。通过反射触发原有行为或布置故障，不因此视为无效测试；公开插件 API 的签名本身也是兼容合同。

撤掉以源码字符串、私有字段/类型存在性、某个字段的 CLR 类型、固定 helper 名称、旧实现必须消失、缓存对象必须同一引用等作为结论的断言。不要把这些断言换个目录继续运行。内部复用策略、增量解析窗口长度与走哪条优化路径不是产品正确性的定义；应验证最终结果，性能比较留给按需工具。

测试目录、项目数量和历史 PR 来源不是删除依据。名称里带 cache 的用例也可能实际保护排版或线程行为，按其断言内容判断。

## 保留、增补与退出

先找已有用例。同一行为在一个主用例中验证，只有不同层确实保护不同失败边界时才保留两层；不能仅因名称相近、用了反射或来自旧 PR 就删除。新增回归先放进所属现有项目，优先覆盖真实编辑结果、保存失败、公开兼容、输入命中、窗口交接、取消和过期回调。

修复一次问题不自动要求增加永久回归。保留依据是仍有重复发生的机制、失败后果及现有覆盖的缺口；普通样式调整、已移除路径和一次性排障通常在当次验证后结束。补充断言优先放进已有行为用例，避免再搭一套相同流程。

定期按整组价值退出测试：普通设置逐项映射、旧版本迁移、装饰效果、帮助文案和专项诊断数据精度，不因已有用例就默认永久保留。保留内容与保存、撤销、真实输入、重要状态切换和资源释放的代表流程；可以明确接受低后果 UI 细节不再有专门自动回归，在 PR 中说明舍弃范围，不另建可选测试保存原脚手架。

异步或子进程检查只有完成全部必要步骤后才能报告成功。观察退出、清理或资源释放时，先检查产品结果，再做测试自身的兜底清理；不要让重新加载、测试侧 Kill 或 Dispose 帮产品完成被测动作。跳过的检查明确报告 SKIP，不计为 PASS。

不以源码字符串、私有字段/类型存在、固定 helper 名、控件精确搬动次数、缓存命中/构建次数、对象必须同一引用或增量窗口长度定义正确性。焦点、选区、撤销、数据不丢和资源释放仍须验证；操作后读取当前控件检查结果，不把某种复用算法当合同。公开 API 签名本身是兼容边界，不属于可随意删除的实现断言。

减少参数全排列前，说明代表用例覆盖了哪些状态机制，以及有意退出哪些低价值组合。分支不同本身不构成保留理由；联合变化会引入独立的数据、输入或生命周期风险时才保留交叉组合。计时器用于控制模拟时间、防挂起或证明退出不阻塞时不是跑分。真实像素、完整解析对照及取消后的资源释放也应按保护的结果判断，既不一概删除，也不自动永久保留。

一次性探针验证后删除，证据留在 PR；高重复风险的最小回归融入已有项目，不保留专用脚手架。清理无效断言时连同无用辅助代码一起删除，不转移到“可选测试”中继续维护。性能和内存采样只在 `tools/` 按需运行，不加入正确性门槛；工具入口见 [工具说明](../tools/README.md)。

每次调整在 PR 里说明删掉了什么、哪些行为仍被覆盖、实际执行了哪些命令及其结果。先验证受影响项目；持久化、公开兼容、线程和窗口交接按实际风险扩展。既有 CI 验收仍须通过，不以重跑通过、延长等待或更换 CI 机器掩盖真实产品缺陷。对已证明在未修改基线中也不可靠的 DWM 最终截图，按 D-057 删除无法作为正确性门槛的整套检查及辅助代码；不将其改名为可选测试，也不把离屏渲染宣称为真实桌面通过。

当前分支已退出 DWM 最终桌面截图和 Markdown 局部/全量两次离屏重绘的逐字节比较；不再生成相应截图产物。材质检查继续覆盖原生状态、真实设置操作、窗口交接、反光移动/缩放与资源释放，Markdown 检查继续覆盖解析、编辑、撤销、公式和可观察布局。
