# Edge SoftwareOnly 渲染实验（2026-09-20）

本文整理自 [PR #290](https://github.com/snownico0722/PaperTodo/pull/290)，只保存一次性实验条件、原始结论和证据边界，不改变 PaperTodo 当前架构。当前技术方向仍以 [ARCHITECTURE.md](../ARCHITECTURE.md) 为准，历史取舍仍以 [DECISIONS.md](../DECISIONS.md) 为准。失败候选的专用 workflow、probe、诊断入口和本地 harness 不进入主线。

当前结论：**云端、本机探针和真实用户回放均未支持把 SoftwareOnly 作为稳定的卡顿修复或生产默认。** RTX 2080 实机回放中，活跃纸片更新基本持平，CPU 没有稳定收益，进程私有内存和工作集下降。云端 CPU 约减半的现象没有在真实回放中复现。

## 目的

验证 Edge Capsule 当前残余的 WPF/Rendering 长尾，是否能通过把 WPF 进程渲染模式从默认路径切到 `RenderMode.SoftwareOnly` 获得稳定改善。

本实验不修改生产 `App.xaml.cs` 或默认渲染模式。SoftwareOnly 通过独立探针或隔离的本地 Debug 诊断包选择。

## 云端与本机探针方法

基线为 main `c10837bb9efb0b306b993805663b15ea78b1b714`。

测试入口复用 `PaperTodo.EdgeTitleChecks` 和真实生产 Edge 类型：

- 每个样本都是独立进程，并在创建 WPF `Application` / HWND 前设置进程渲染模式；
- Default 臂：`RenderMode.Default`（记录值 0）；
- SoftwareOnly 臂：`RenderMode.SoftwareOnly`（记录值 1）；
- 每个进程创建 10 个真实 `EdgeCapsuleHost`；
- 使用当前 `EdgeCapsuleTargetPlanner` 与 `EdgeCapsuleTransitionPolicy`；
- Resting ↔ Hovered 连续往返 24 段，每段 140 ms；
- 每段使用固定 QPC 起点和固定起始 frame，不按上一回调重新基线；
- 记录 `CompositionTarget.Rendering` callback interval、10 个 Host 合计的 `Apply` 耗时、进程 CPU、Working Set 与总 wall time；
- 两臂先各跑一个 warm-up 进程，随后 6 组 AB/BA 交错独立进程，共 12 个持久样本。

这些 callback interval 是 WPF 应用侧回调节拍，不是物理面板 FPS，也不是 input→pixel 延迟。

## 云端 Windows 结果

最终有效 run：[`35475347644`](https://github.com/snownico0722/PaperTodo/actions/runs/35475347644)

- Windows：`10.0.26100.0`
- SDK：setup 使用 `10.0.401`
- workflow setup 安装 runtime `10.0.12`；受测进程摘要实际报告 `.NET 10.0.11`，这里保留观测值，不据此混做 runtime 对照
- 两臂 `RenderCapability.Tier` 均报告 **2**
- Default 的 `RequestedRenderMode=0`，SoftwareOnly 的 `RequestedRenderMode=1`
- Tier 表示硬件能力，不表示 SoftwareOnly 没有生效；它也不能把 hosted runner 等同于真实独显/物理显示器

### 六轮中位数

| 指标 | Default | SoftwareOnly |
| --- | ---: | ---: |
| Rendering callback P95 | **33.238 ms** | **32.300 ms** |
| Rendering callback P99 | **37.031 ms** | **42.063 ms** |
| callback max 中位 | 42.651 ms | 42.982 ms |
| 10 Host Apply P95 | 2.278 ms | 1.884 ms |
| 进程 CPU | 6093.8 ms | 3085.9 ms |
| 总 wall time | 4373.2 ms | 4019.3 ms |
| Working Set | 138,889,216 B | 118,425,600 B |
| >20 ms callback 次数 | 110 | 56 |
| >33 ms callback 次数 | 10 | 5.5 |

聚合中位数直接比较只能描述这台 runner 上的样本形状；更重要的是同轮配对差值。

### 同轮配对：SoftwareOnly - Default

| Round | callback P95 | callback P99 | Apply P95 | CPU |
| --- | ---: | ---: | ---: | ---: |
| 1 | -0.383 ms | +5.632 ms | -0.270 ms | -2687.5 ms |
| 2 | -0.329 ms | +2.333 ms | -0.145 ms | -2687.5 ms |
| 3 | -9.141 ms | -18.569 ms | -0.272 ms | -2859.4 ms |
| 4 | -0.742 ms | +3.371 ms | -0.306 ms | -3125.0 ms |
| 5 | -1.140 ms | +5.107 ms | -0.942 ms | -3703.1 ms |
| 6 | -1.306 ms | -3.765 ms | -0.931 ms | -3375.0 ms |
| **paired median** | **-0.941 ms** | **+2.852 ms** | **-0.289 ms** | **-2992.2 ms** |

P95 六轮都偏向 SoftwareOnly，但除 Round 3 外幅度大多只有约 0.3～1.3 ms；P99 则 4/6 轮 SoftwareOnly 更差，paired median **+2.852 ms**。因此不能把 SoftwareOnly 描述成稳定改善长尾。

## 本机探针复测

### 版本、环境与有效样本

- 受测 PR HEAD：`6f250cb2765886418034dcd7de2e211c61cfd807`；固定子模块：`59b35037d4fcb65728eb630b71648d7f5da6e2de`。
- RTX 2080（驱动 `32.0.16.1088`），Ryzen 7 5700X，8 核 16 线程；Windows build 26200，SDK `10.0.401`。
- 唯一活动输出 2560×1440，系统报告 **59 Hz**，窗口 DPI 120（125%）；实际执行桌面和输入桌面均为 `Default`。未改变刷新率。
- 系统安装了 MuMu 虚拟显示适配器；采样时只有 NVIDIA 的显示输出活动，不能据此推断虚拟驱动没有任何影响。
- 两臂请求模式分别为 0/1，Tier 均为 2；未另测 GPU 引擎利用率来验证每一步渲染路径。
- 先完整运行 PR 原版，再运行可见对照；每个批次各有两臂预热和六组 AB/BA，共 **24 个正式进程样本**，预热不计入统计。
- Release 原版与可见对照均构建成功，0 警告、0 错误；原版常规 EdgeTitleChecks 通过 **2574 条断言**。

### 六轮中位数

| 批次 | 指标 | Default | SoftwareOnly |
| --- | --- | ---: | ---: |
| PR 原版 | Rendering callback P95 | 33.435 ms | 35.614 ms |
| PR 原版 | Rendering callback P99 | 43.883 ms | 46.230 ms |
| PR 原版 | 10 Host Apply P95 | 4.992 ms | 4.959 ms |
| PR 原版 | 进程 CPU | 4226.6 ms | 3898.4 ms |
| PR 原版 | Working Set | 165,894,144 B | 141,522,944 B |
| 可见对照 | Rendering callback P95 | 33.920 ms | 35.122 ms |
| 可见对照 | Rendering callback P99 | 43.316 ms | 47.778 ms |
| 可见对照 | callback max 中位 | 45.709 ms | 55.236 ms |
| 可见对照 | 10 Host Apply P95 | 0.169 ms | 0.163 ms |
| 可见对照 | 进程 CPU | 3531.3 ms | 3187.5 ms |
| 可见对照 | Working Set | 165,484,544 B | 143,460,352 B |
| 可见对照 | wall time | 4003.914 ms | 4003.787 ms |

同轮 SoftwareOnly - Default 的 P95 / P99 配对中位数：原版 **+2.179 / +1.593 ms**，可见对照 **+1.350 / +2.931 ms**。原版两个分位均在 5/6 组更慢；可见对照 P95 在 5/6 组、P99 在 6/6 组更慢。可见对照的 CPU 聚合中位数约下降 9.7%，Working Set 约下降 13.3%，没有复现云端 CPU 约减半的幅度。

### 可见性控制及其限制

PR 原版调用 `SetExperimentalPassive(true)`，Host 每次 `Apply` 都会走 `ApplyBottomZOrder`。原版预热存在上层窗口矩形重叠，Visible 标志本身不能证明像素无遮挡，Apply 也不是纯绘制耗时。

本地变体增加 `--visible`：Host 置顶、关闭 passive 置底、保留点击穿透，起始纵坐标从 28 改为 160 DIP，避开观测到的浮动输入法工具条。初始两次可见性检查因重叠中止，未进入正式统计。最终两臂预热均确认 10 个可见、非 cloaked 的 HWND，没有其他进程的可见上层窗口矩形与其重叠。这只是采样时的窗口几何证据，不是连续像素呈现证明。

Apply P95 从原版约 5 ms 降至可见对照约 0.17 ms，与额外的置底路径相符；但两批同时改变了 Z order、passive 和位置，批次之间也未交错，**不能把跨批差值视为置底操作的单变量因果证明**。每批内部两臂的窗口配置相同。

两批生产 `PaperTodo.dll` SHA-256 均为 `3474E298DFB2C8015B0D927AD4AF8C4E47D77C4819229C3EEB1EA9D0BA81DD05`。原版探针 DLL 为 `51E08E617316C18B27C0E3E6917F7240DE2A45DE1A4D9DCECBEEB72184E14E7D`，可见对照探针 DLL 为 `39F6031069558826C65072A595E3308C101FE5CDCAE51E2EAC427F42C85D9B0E`。

## 真实用户录制与实机数据回放

### 方法与诊断条件

使用用户提供的 TinyTask 1.77 录制 `数据.exe` 和 `实机数据` 中的 10 张 Note/Todo；每轮重新复制同一初始数据、LMDB、字体和插件，不复用上一轮退出状态。录制 SHA-256：`96A13963CC3D070050E18039E938FF5A673DE0DA947A6412ACDBF0BAA2C50CB6`。

本机硬件、显示模式及受测 HEAD 与上节相同。实际模块路径与哈希确认受测进程加载 `.NET 10.0.12`。八次正式回放使用同一优化 Debug、Windows x64、无运行时单文件包，SHA-256：`D1AE45CE1C2E0C2B93363C628734B7D629631187665DF261390244AC1FD4F67B`。该包与上节 Release 探针不是同一构建，指标分别统计。

本地诊断源码仅增加以下观察与控制，准确补丁保存在下方资料中，未作为活动源文件提交：

1. Debug 模块初始化读取 `PAPERTODO_EXPERIMENT_RENDER_MODE=default/software`，在 WPF Application 创建前设置进程渲染模式。每轮均验证请求值 0/1 及 `beforeApplication=true`。
2. 原观察器已有 `MapPresenter` 但未接入；在既有 `Applied` 观察点按现有 DiagnosticId 补一次性映射，使活跃 owner 可解析。首轮预热发现此问题后重建，正式八轮均使用同一个最终包。

诊断参数：`PAPERTODO_EDGE_DIAGNOSTICS=memory`，记录容量 `1048576`，文本容量 `128 MiB`；`PAPERTODO_EDGE_OBSERVATIONS=1`，`DEEP_OBSERVATIONS` / `MESSAGE_OBSERVATIONS` / `DWM_OBSERVATIONS` 对应完整 `PAPERTODO_EDGE_` 变量均为 0。两臂采集开销一致，内存日志在正常退出时封存。

两臂先做完整预热，再进行 **AB、BA、AB、BA 四组配对，共八次正式回放**。每次启动等待 7 秒、核对运行时与窗口，再等待 1 秒，执行约 27 秒录制，结束后等待 2 秒并正常退出。CPU 是回放及约 2 秒收尾区间的进程所有线程累计耗时；内存取退出前采样，不包含退出封存阶段。

### 指标定义

按严格的 `(open/close, owner)` 动作序列对齐共同窗口；八轮均完整匹配 **36 个动作**。活跃纸片指标按实际宽高/透明度变化的 owner 分组，不跨独立活跃片段拼接间隔；队列指标再按 scheduler frame pair 去除同一帧多个纸片的重复计数。平移、闲置期和重复事件不用于冒充形状更新。`transaction.commit` 是同步事务耗时。

回放分析器在每轮排序样本中取 `ceil((n-1)*0.95)` 位置的 P95，不插值；下表再取同模式四轮指标的中位数，偶数项中位数取中间两项平均。**每臂中位数之差与同轮差值的中位数不是同一个统计量。** 本节不是前两节的全局 `CompositionTarget.Rendering` callback 指标，不能把两者毫秒数直接混比。

### 四轮中位数

| 指标 | Default | SoftwareOnly |
| --- | ---: | ---: |
| 活跃纸片宽高/透明度更新间隔 P95 | **13.324 ms** | **13.344 ms** |
| 去重后的队列形状更新间隔 P95 | 15.515 ms | 16.179 ms |
| transaction.commit P95 | 45.904 ms | 38.549 ms |
| CPU 累计耗时 | **8.875 s** | **9.242 s** |
| 进程私有字节 | **309.32 MiB** | **206.73 MiB** |
| Working Set | 309.75 MiB | 290.05 MiB |

MiB = 1,048,576 B。活跃纸片 P95 聚合差约 0.02 ms；CPU 聚合中位数约增加 4.1%。私有字节约减少 102.59 MiB（33.2%），Working Set 约减少 19.71 MiB（6.4%），内存下降方向在四组中一致。这两个内存指标不能互相替代，也不表示 GPU 或整机资源同比下降。

### 同轮 SoftwareOnly - Default

| 组 | 顺序 | 活跃纸片 P95 | transaction.commit P95 | CPU |
| --- | --- | ---: | ---: | ---: |
| 1 | Default → SoftwareOnly | -0.105 ms | -8.446 ms | -0.016 s |
| 2 | SoftwareOnly → Default | +0.091 ms | -10.528 ms | -0.047 s |
| 3 | Default → SoftwareOnly | +0.183 ms | +0.550 ms | +0.719 s |
| 4 | SoftwareOnly → Default | +0.001 ms | +1.660 ms | +0.641 s |
| **配对中位数** | — | **+0.046 ms** | **-3.948 ms** | **+0.313 s** |

活跃纸片更新基本持平；CPU 两组小幅下降、两组明显增加，没有稳定节省。事务 P95 虽然聚合值降低，但只有两组改善、两组变慢，不能认定为稳定交接收益。

### 有效性与排除记录

- 八次正式回放全部正常退出，零日志容量/文本丢失，全部 owner 可解析，没有记录到崩溃；退出前没有提前写出的 edge 诊断日志。
- 现有分析器九项回归检查通过。最终诊断包构建成功，保留一条原有 `PaperBodyPluginRegistry.cs` 的单文件 `IL3000` 警告。
- 首次打包因 SDK 不支持无运行时单文件的内置 bundle 压缩而失败；之后使用非压缩诊断包，两臂一致。失败构建不属于运行样本。
- 初始预热的 owner 映射不足，预热包未混入八个正式样本；预热只用于跑通输入和定位诊断问题。
- 第三组准备复制数据时磁盘空间耗尽，程序尚未启动；半成品及先前重复输入副本转存后，从原始输入重新开始第三组。半成品未进入统计；四组之间并非不间断采样。
- 结束时原始录制、原包和实机数据共 **947 个文件**逐一 SHA-256 校验：修改、缺失、新增均为 0。正式版工作区仍为 main `778dd667ddbf9aa015946145eb826a840c7ea448`，无改动。

## 综合结论与剩余边界

1. **不支持把 SoftwareOnly 作为已验证卡顿修复或生产默认。** 云端 P99 反而变差；本机两个探针批次的高分位尾部也偏差；实机回放动画更新基本持平。
2. **资源收益依赖工作负载。** 云端 CPU 约下降 49%，本机可见探针约下降 9.7%，真实回放却约增加 4.1%；不能把 hosted runner 的资源信号推广到真实使用。真实回放的私有字节及 Working Set 下降是这批样本中一致的现象。
3. 所有节拍都是应用侧事件，`transaction.commit` 也不是显示器 present 或 input→pixel。当前实测为系统报告 59 Hz；240 Hz、120 Hz、其他 GPU、多屏/混合 DPI、长期功耗及其他 Markdown/WebView2 负载尚未验证。
4. 真实回放覆盖了完整 controller、队列、预览和 DComp 交接路径，但没有深层 MIL/DWM 呈现证据，不能据此定位 GPU raster 或下游的唯一原因。Debug 内存含诊断缓冲区，不代表正式 Release 绝对占用。
5. 这些是单机、小样本、顺序配对实验，没有统计显著性或跨机器推广结论。本路线停止作为卡顿修复候选；主线仅保留实验记录与逐轮数据，不据此接入产品设置或专用探针。

## 随主线保存的本机记录

| 文件 | 内容 |
| --- | --- |
| [local-probe-samples.csv](edge-software-rendering-20260920/local-probe-samples.csv) | 原版与可见对照共 24 个正式样本；`cohort` 分批，`mode=default` 对应原始探针的 `hardware` 参数，并非 GPU 路径证明 |
| [local-probe-paired.csv](edge-software-rendering-20260920/local-probe-paired.csv) | 两批共 12 组 SoftwareOnly - Default 差值 |
| [real-replay-samples.csv](edge-software-rendering-20260920/real-replay-samples.csv) | 八次实机回放的逐轮指标、包哈希、动作匹配与丢失计数 |
| [real-replay-paired.csv](edge-software-rendering-20260920/real-replay-paired.csv) | 四组 SoftwareOnly - Default 差值 |

CSV 保留原始指标精度，配对差值从逐轮值复算。字段 `Milliseconds` / `_ms` 为毫秒，`Bytes` / `_bytes` 为字节；`owner_over33_count` 实际阈值为 33.333 ms。表格可复算本报告的聚合与配对结论，但不含用于重新计算每轮分位数的全部事件。原始 journal、含纸片内容或标识的数据、LMDB、私有路径和可执行文件留在本地证据目录，未上传到 PR。

## 失败记录与产物

首次 run `35475232044` 在 focused build 阶段失败，原因是新 probe 漏了 `System.IO`，导致 `Path/Directory/File` 未解析；产品 `PaperTodo.dll` 已先正常构建。该 run 不属于渲染结果。

有效 run `35475347644` 全部步骤成功。artifact：

- 名称：`edge-software-rendering-experiment`
- Artifact ID：`10594044556`
- 上传内容 SHA-256：`f583065d3524d0f272a3895465945760508905dd4ea66295a2a9e983437ec844`
- 内容：12 个原始样本 JSON + `summary.json`

用于该次取证的专用 workflow 和 probe 仅属于实验实现，归档时不进入主线。
