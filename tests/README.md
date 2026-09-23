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
| regression | MarkdownSemanticChecks、MarkdownEditingChecks | 解析结果、编辑、撤销、排版与可见文本 |
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

## 测试保留与退出

测试是否保留取决于现在能否保护重要行为，不取决于它是否曾属于某个 bug 修复。重复的弱断言、已由集成场景覆盖的私有辅助步骤、固定优化档位和低回归风险的参数排列可以删除，不另搬到“可选测试”中存活。

仍保留保存失败和最后编辑不丢失、公开插件兼容、跨线程调用撤销、真实输入命中、窗口交接、取消与过期回调等高影响边界。计时器用于防止测试挂住、控制模拟时钟或验证不能阻塞的退出行为时，不属于性能跑分。

生命周期默认检查不采样、不比较旧实现，只保留八种启动/退出行为。窗口层级覆盖所有操作在普通窗口与隐藏 owner 两种关键形态，任务栏隐藏采用两种代表操作，避免完整交叉排列。背景图片保留加载与损坏输入回退，不再枚举每种对齐和固定解码尺寸。

桌面性能、预览内存与像素导出统一在 `tools/PaperTodo.DesktopBenchmarks`；Markdown 解析采样在 `tools/PaperTodo.MarkdownBenchmarks`。`-Group all` 不构建或执行这两个工具。
