---

## E-004 — 首帧候选筛选与基于记录的后台 JIT

**日期：** 2026-09-13
**状态：** Completed
**基线：** #254 `92e835a5fb259519fa41403e8eb41cc4bfec969e`（E-003）。

### 问题与候选

继续降低首帧、预览和完整编辑器就绪时间，不把调度后移自动等同于消除成本。本轮先比较托盘后移和共享按钮样式，再测试 CLR `ProfileOptimization`。没有重开 E-002 否决的启动 Stage/Reveal、删 DPI 校验或增加 UI 线程。

第一轮以 10 个折叠 Note 和 1 个展开 Note 为两个场景。每变体 6 次新进程，正反顺序交错，round 0 保留但不进入中位数。分段 scope 包含子调用，不能相加；下面的时间从 controller 构造结束起算。

| 10 个折叠 Note | Rendering 回调 | StartAsync 返回 | 第 10 份缓存写入 | 第 10 个 Shell 建立 |
| --- | ---: | ---: | ---: | ---: |
| E-003 基线 | 514.73 ms | 560.44 ms | 1038.28 ms | 1276.75 ms |
| 托盘移到恢复之后 | 459.41 ms | 600.70 ms | 1032.48 ms | 1278.55 ms |
| 共享图标按钮 Style | 516.96 ms | 566.25 ms | 993.15 ms | 1225.28 ms |

**不采用托盘后移。** Rendering 约早 55 ms，但命令恢复返回约晚 40 ms，缓存和 Shell 没有对应改善；这是部分迁移首次 WPF/托盘成本，暂不值得改变入口出现次序。

**不采用新的图标按钮缓存。** 70 次 Style 构造实际累计约 1.17 ms，共享后约 0.03 ms。表面上约 45 ms 的缓存提前发生在这些按钮首次构造之前，不能归因为本改动；带 scope 的实际 Shell 构造累计也未改善。不为了这一量级加入新的线程/字体缩放缓存状态。

展开 Note 的首个 `new MarkdownTextBox`（含基类、实例初始化和对象初始化器）约 136 ms，派生构造器自身约 5 ms；重复 RefreshTextView 累计只有约 1 ms。因此本轮不改正常重绘语义，而继续检查启动阶段的编译等待。

第一轮 run `34737455686` / tools `5a26d615bc56fa43d2cc416a988cb956e9288a80` 完成全部构建与 36 个进程样本；最终打印汇总时 Python 变量 `round` 遮蔽同名函数，导致该 workflow 的汇总步骤失败。CSV 已在错误前写出并保存在 artifact `e004-first-frame-editor`，上表由 CSV 独立复算；不把这条 run 描述为全绿。该轮 working set 在 Dispose 后采集，不用于启动内存结论。

### 后台 JIT：先用同一二进制隔离

依据 Microsoft 的 `ProfileOptimization` 文档，第一次运行记录实际使用的方法；后续由 CLR 读取记录，在后台**编译而非执行**这些方法，可能让 UI 线程在首次调用时少等 JIT。它不是 ReadyToRun，也不是保存本机代码或 UI 树；不改变 WPF 对象的创建线程和方法执行顺序。

先在相同 fixture 二进制中用环境参数决定是否调用该 API，分三种情况：

- baseline：不启用；
- fresh：启用，但每次删除专用编译记录；
- recorded：启用，保留该场景上次生成的记录。

每种情况各测 10 个折叠 Note / 1 个展开 Note，7 轮交错运行，round 0 不计，余下 6 轮中位数，总共 42 个进程。只留下少量时间标记，不再给每个函数加完整 scope。外部总时间包含 fixture 自己的状态准备，**不是产品 EXE 的绝对启动时间**。

| 场景 / 模式 | 外部启动到 fixture 全部就绪 | controller 后 Rendering | controller 后第 10 份缓存 | controller 后 Shell | 就绪 CPU 累计 | 就绪 working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 折叠 / baseline | 1377.71 ms | 428.98 ms | 894.08 ms | 1042.68 ms | 1179.69 ms | 140.49 MiB |
| 10 折叠 / fresh | 1390.02 ms | 427.72 ms | 878.98 ms | 1027.18 ms | 1132.81 ms | 141.05 MiB |
| 10 折叠 / recorded | 950.61 ms | 297.40 ms | 603.64 ms | 677.80 ms | 1296.88 ms | 145.85 MiB |
| 1 展开 / baseline | 1398.28 ms | — | — | 539.03 ms | 1234.38 ms | 120.51 MiB |
| 1 展开 / fresh | 1373.75 ms | — | — | 540.52 ms | 1226.56 ms | 121.03 MiB |
| 1 展开 / recorded | 1069.69 ms | — | — | 313.64 ms | 1476.56 ms | 125.57 MiB |

10 折叠场景外部就绪约快 31%，代价为 CPU 累计约 +10%、working set 约 +5.4 MiB。1 展开场景 CPU 累计约 +20%。记录文件约 164～168 KiB；首次没有记录不宣称加速。fixture 较早启动记录，最终产品需要在 GUI 主实例拥有 Mutex 后才开始，因此仍必须用真实 EXE 复核。

该轮 run `34737789418` / tools `836913bfb3e561ff32cbf3178a19a04c6cf3b22d` 全部成功，artifact `e004-background-jit`。`summary.csv` 保留每次样本，`profile-files.csv` 和各日志 `PROFILE_BYTES` 确认有记录可读，不把未真正启用的开关当作有效对照。

### 真实 EXE 复核口径

真实 `PaperTodo.exe` 同机对照两种发布形态，每种各测 baseline / fresh / recorded；每组 5 次新进程，首轮保留不计、后 4 次中位数，总共 30 个主实例及其退出转发实例。

- 多文件：framework-dependent、no-R2R；
- 压缩单文件：self-contained、compression、no-R2R，保留当前发布方向；
- 两组均不裁剪、不启用 NativeAOT。比较不改变 renderer、预览缓存或 Shell 顺序。
- 主实例使用 10 个折叠短 Note，开启边缘预览，无第三方插件，关闭 telemetry/MCP/持久脚本。
- 启动 T0 在外部 `Start-Process` 前；Rendering 是全部窗口满足可见条件后的 WPF 回调，**不是物理首帧**；Ready 是接受启动命令；Cache 是第 10 份完整 artifact 写入；Shell 是第 10 个 `BuildShell` 返回后设置 `IsShellBuilt`，不包括随后附属属性刷新。
- 每次 Ready 后继续运行 4 秒，再由第二实例 `--exit` 退出。Exit 从主实例 Exit 入口到外部观察到其进程结束，排除第二实例自身启动与转发延迟。
- 内存和累计 CPU 在这 4 秒后的 Exit 入口、资源释放之前采集；它不是峰值，也不是数小时稳态。Profiler 输出先缓存在进程内，再在 Ready/Exit 写日志，减少打点自身 I/O。
- 每次保留同一临时纸片目录重新启动，验证内容/IsVisible、OnExit 完成、恰好 10 份缓存和主实例独占启动记录。测试覆盖目录不可写/记录损坏的正常退回，不新增用户主数据协议。
- `fresh` 只清理编译记录；未清空 Windows page cache。原生 bundle extraction 目录也未逐次清理，不能称为 SSD/重启后的冷启动实验。四个有效样本不用于承诺可靠 p95/p99。

| 发布形态 / 记录状态 | 外部到 Rendering | 命令 Ready | 10 份缓存 | 10 个 Shell | 主进程退出 |
| --- | ---: | ---: | ---: | ---: | ---: |
| FDD 多文件/no-R2R / baseline | 660.47 ms | 694.21 ms | 1099.07 ms | 1262.74 ms | 94.26 ms |
| FDD 多文件/no-R2R / fresh | 655.42 ms | 692.38 ms | 1069.55 ms | 1233.86 ms | 140.28 ms |
| FDD 多文件/no-R2R / recorded | 511.92 ms | 551.14 ms | 811.67 ms | 900.25 ms | 115.57 ms |
| SC 压缩单文件/no-R2R / baseline | 818.32 ms | 857.22 ms | 1248.60 ms | 1423.76 ms | 108.05 ms |
| SC 压缩单文件/no-R2R / fresh | 821.97 ms | 861.29 ms | 1227.78 ms | 1402.80 ms | 104.29 ms |
| SC 压缩单文件/no-R2R / recorded | 848.88 ms | 884.11 ms | 1259.53 ms | 1439.45 ms | 107.97 ms |

| 发布形态 / 记录状态 | 4 秒后 working set | 4 秒后 private bytes | 累计进程 CPU | 编译记录大小 |
| --- | ---: | ---: | ---: | ---: |
| FDD 多文件/no-R2R / baseline | 144.97 MiB | 65.97 MiB | 1335.94 ms | 0 B |
| FDD 多文件/no-R2R / fresh | 145.55 MiB | 66.59 MiB | 1335.94 ms | 144840 B |
| FDD 多文件/no-R2R / recorded | 149.86 MiB | 68.05 MiB | 1421.88 ms | 144896 B |
| SC 压缩单文件/no-R2R / baseline | 259.77 MiB | 134.94 MiB | 1531.25 ms | 0 B |
| SC 压缩单文件/no-R2R / fresh | 260.34 MiB | 134.85 MiB | 1445.31 ms | 64 B |
| SC 压缩单文件/no-R2R / recorded | 260.37 MiB | 135.14 MiB | 1476.56 ms | 64 B |

FDD 多文件 no-R2R 在已记录后，Rendering 约快 22.5%，全部 Shell 约快 28.7%；4 秒后 working set 多约 4.9 MiB、private bytes 多约 2.1 MiB、累计 CPU 多约 6.4%。退出从约 94 ms 到 116 ms；首次只有记录而无复用的退出约 140 ms。不能把它称为同时降低启动和退出耗时。

SC 压缩单文件则没有观察到收益：所谓 recorded 组文件始终只有 64 B，Rendering 818 -> 849 ms，Shell 1424 -> 1439 ms。样本里还有一个 recorded 退出 598.64 ms 的离群值，已保留，不用中位数掩盖未分析的长尾，也不直接归因给 profiling。

### 补测：精简单文件和 R2R 多文件

为避免误把“SC 压缩单文件无效”扩大成“所有单文件都无效”或“R2R 已不需要它”，再在另一同机 job 比较 FDD 无压缩单文件与 SC R2R 多文件。方法仍为各 5 次、首轮不计，30 个主实例及对应退出命令；两次 job 之间不比较绝对毫秒数。

| 发布形态 / 记录状态 | 外部到 Rendering | 命令 Ready | 10 份缓存 | 10 个 Shell | 主进程退出 |
| --- | ---: | ---: | ---: | ---: | ---: |
| FDD 单文件/no-R2R / baseline | 829.80 ms | 870.08 ms | 1310.09 ms | 1516.04 ms | 129.50 ms |
| FDD 单文件/no-R2R / fresh | 838.13 ms | 878.97 ms | 1321.17 ms | 1524.80 ms | 158.67 ms |
| FDD 单文件/no-R2R / recorded | 714.48 ms | 750.32 ms | 1139.08 ms | 1330.04 ms | 149.68 ms |
| SC 多文件/R2R / baseline | 724.22 ms | 756.17 ms | 1107.12 ms | 1254.81 ms | 109.33 ms |
| SC 多文件/R2R / fresh | 723.32 ms | 756.47 ms | 1125.51 ms | 1272.15 ms | 151.57 ms |
| SC 多文件/R2R / recorded | 615.88 ms | 643.10 ms | 955.28 ms | 1079.29 ms | 138.15 ms |

| 发布形态 / 记录状态 | 4 秒后 working set | 4 秒后 private bytes | 累计进程 CPU | 编译记录大小 |
| --- | ---: | ---: | ---: | ---: |
| FDD 单文件/no-R2R / baseline | 142.88 MiB | 65.02 MiB | 1671.88 ms | 0 B |
| FDD 单文件/no-R2R / fresh | 143.52 MiB | 65.64 MiB | 1585.94 ms | 117954 B |
| FDD 单文件/no-R2R / recorded | 147.39 MiB | 66.20 MiB | 1734.38 ms | 118136 B |
| SC 多文件/R2R / baseline | 142.41 MiB | 65.65 MiB | 1289.06 ms | 0 B |
| SC 多文件/R2R / fresh | 142.98 MiB | 66.18 MiB | 1382.81 ms | 131942 B |
| SC 多文件/R2R / recorded | 147.88 MiB | 67.02 MiB | 1468.75 ms | 132054 B |

FDD 单文件已记录后的 Rendering 约快 13.9%、Shell 约快 12.3%；SC R2R 多文件仍可让 Rendering 约快 15.0%、Shell 约快 14.0%。对应 working set 多约 4.5/5.5 MiB，累计 CPU 多约 3.7%/13.9%，4 秒后退出中位多约 20/29 ms。首轮只记录、不复用，没有稳定的启动收益，退出增加约 29/42 ms。

这里的 CPU 是多线程累计处理器时间，不是平均 CPU 利用率或能耗；内存和退出只代表指定采样窗口，未证明长时间常驻后同样增加。各变体始终保留最后一次数据保存、正常 WPF OnExit 和单实例转发，不靠退出强杀换速度。

补测 run `34738597056` / tools `27c352b09ec96f521430daf5c66f11a627a8fd9b`，artifact `e004-packaging-boundaries`，保留 `real-app.csv`、`medians.csv` 和末次 `.prof`；它与前一真实 EXE job 都成功。FDD 单文件约 118 KB、SC R2R 多文件约 132 KB 的记录与 SC 压缩单文件的 64 B 有明显区别，不仅检查“文件存在”。

### 为何限制到 file-backed runtime

已核对受测版本 [.NET 10.0.12 的 MulticoreJitManager::IsSupportedModule](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/multicorejitplayer.cpp#L429-L467)：`PEAssembly.GetPath().IsEmpty()` 时直接忽略该内存模块。SC bundle 将运行库一起放在内存中，与本轮只写 64 B、无优化收益的结果一致。FDD 单文件的应用程序集虽然在 bundle 内，外部 .NET/WPF 运行库仍有文件路径，不能和 SC bundle 一概而论。R2R 也仍有方法装载/修正及未预编译方法的成本，不把它当成自动排除 profiling 的条件。

最终仅在 `typeof(object).Assembly.Location` 非空时启用。该查询有意用空字符串识别 bundled CoreLib，不把 Location 当可写目录；IL3000 只在这一方法局部标注解释性抑制，未关闭整个项目的单文件分析。SC bundle 在创建目录之前返回，不再生成无价值的 64 B 缓存。

没有为绕过限制开启 `IncludeAllContentForSelfExtract`；它改变解包和磁盘占用，且 Microsoft 将其列为不推荐的旧兼容模式。本轮没有测这条路线，也不把未采用写成永久禁令。

### 保留范围与资料

**采用：在文件化运行库下使用 CLR 自带的启动编译记录。** 主 GUI 实例取得 Mutex、启动命令监听后再开始；MCP bridge、次实例和仅退出命令不记录。新 `StartupCompilationProfile` 仅负责开启该可选 runtime 能力，缓存放在 `%LOCALAPPDATA%/PaperTodo/Cache/StartupCompilation/startup.prof`，独立于便携纸片/插件数据。目录不可写或记录不可用时继续普通启动；没有新的业务状态、UI 线程、任务管理器或模式开关。SC bundle 不启用；FDD 单文件、多文件和受测 R2R 多文件可以使用。未改变发布参数。

新记录没有前次数据可用，首次启动不宣称加速；版本/方法变化由 CLR 的记录匹配和重写处理，不额外建立版本清理框架。编译不执行用户代码，也不以 profiler 缓存替代完整 Markdown artifact/Shell 预热。多用短暂编译 CPU、约几个 MiB 驻留内存和有限记录写入成本，换后续启动更早呈现/就绪；不是省电模式。

最终新增可执行检查覆盖记录目录被普通文件占用、损坏记录与正常 StateStore 保存/读取；完整范围验证为 Release 主程序构建、Lifecycle/Persistence/Threading/EdgePreview 四组 Release 回归。最终实际发布烟测再次检查精简单文件生成/复用有效记录，SC 压缩单文件完全不创建记录目录，重复启动、内容/可见状态、恰好 10 份缓存、次实例不污染记录、OnExit 及进程正常结束。

最终范围验证 run `34738988011` / tools `53082086958b0958feb5da3d86e2e6b034fca55a` 完整成功，artifact `e004-final-scoped`（`10312047256`）。Release 主程序构建 0 警告/0 错误；上述四组回归通过，其中 Persistence 为 12/12、Threading 为 12/12。另 8 次实际主实例及对应退出转发确认：FDD 单文件有有效记录且可复用，SC 压缩单文件连记录目录都不创建。该 8 次用于验证最终门禁，不另以一两个样本替代上面的性能 A/B。

单文件 publish 仍报告基线已有的 `PaperBodyPluginRegistry.cs` IL3000 警告；前后日志都保留，本轮未修改该不相关代码，不能把“主程序普通 Release build 零警告”扩大为“所有 publish 零警告”。最终受测源码复制及 SHA-256 位于 `candidate-files/`、`source-sha256.json`，产品代码不包含临时标记、环境开关、实验脚本或基准 workflow。

未验证范围：真实用户多屏/混合 DPI、物理首帧、输入延迟长尾、实际 WebView/第三方插件、单核/低功耗机器、不同用户工作集交替的收益及长时间稳态。功能回归通过不等于这些性能边界都已测过。

- Microsoft [ProfileOptimization](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.profileoptimization?view=net-10.0)：多核后台编译、不执行 UI、首次创建记录没有性能收益、记录无效时正常运行。
- Microsoft [StartProfile](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.profileoptimization.startprofile?view=net-10.0)：记录实际调用的方法；记录停止条件及空名称停止记录。
- 第一轮候选：run `34737455686`，artifact `e004-first-frame-editor`；汇总打印失败的限制见上文。
- 同二进制编译对照：run `34737789418`，artifact `e004-background-jit`。
- 真实发布包与行为验证：run `34738219291`，tools commit `df9a7aa0fd35ce78797d2574f13c28262fe690c2`，artifact `e004-real-app-validation`。原始文件包含 `real-app.csv`、`medians.csv`、日志、`product.diff` 与插桩前受测源码 `candidate-files/`。
- Microsoft [单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)：bundled Assembly.Location、运行库加载与全量自提取边界。
