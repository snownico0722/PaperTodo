# PaperTodo 实验记录

本文保存 **以后仍可能影响技术选择的可复现实测、A/B 方法、环境和数据**。它不是当前架构说明，也不是最终技术决策。

- 当前技术方向见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。
- 已经形成长期取舍的结论见 [`DECISIONS.md`](DECISIONS.md)。
- 本文负责回答“当时到底怎么测、测到了什么”；候选路线即使表现很好，也不能仅凭这里的数据自动升级成当前产品路线。
- CI runner 上的毫秒数只用于同机同轮相对比较。真实用户机器、物理显示器扫描、杀毒/磁盘状态等仍可能改变绝对值。

## 实验索引

| ID | 日期 | 主题 | 状态 | 关联决策 |
| --- | --- | --- | --- | --- |
| E-001 | 2026-09-13 | Windows 发布形态：Single-file / Compression / ReadyToRun / Multi-file | Completed | D-036 |
| E-002 | 2026-09-13 | Edge Host 首次呈现：菜单延后与批量首帧 | Completed | — |
| E-003 | 2026-09-13 | 实机录制：代理常驻复用及封版后的历史版本对照 | Completed | D-037 |
| E-004 | 2026-09-13 | 统一内存日志、扰动检查与 16 版历史对照 | Completed | — |
| E-005 | 2026-09-13 | Rendering 预计呈现时间误去重与同机单变量回放 | Completed | D-032 |
| E-006 | 2026-09-13 | 原生消息、WPF 呈现等待和 Dispatcher promotion 定位 | Completed | D-032 |
| E-007 | 2026-09-13 | Pointer 无效更新过滤与活跃 Rendering 保留交叉对照 | Completed; candidates rejected | D-032 |
| E-008 | 2026-09-13 | 渲染请求、遍历、提交时钟与反馈的关联定位 | Completed; diagnostic fix only | D-032 |
| E-009 | 2026-09-14 | 固定电脑状态后的44轮原包复测 | Completed; no runtime changes | D-032 |

---

## E-001 — Windows 发布形态与端到端冷启动

**日期：** 2026-09-13  
**状态：** Completed  
**目的：** 确认 PaperTodo 当前 3～5 秒级主观启动/退出延迟中，进程/CLR/单文件打包成本占多少；重新验证历史上关闭 ReadyToRun 的原因，并比较压缩单文件、多文件和 framework-dependent 发布形态。

### 基线与环境

- 产品代码基线：#254 生命周期优化提交 `cf8b6cdc7be7bfb0ea0b51714a1ab1360740848b`。
- 基准提交：`524b3fd9ce1eb6bb388a1c6ffab81caef32cfd88`，只在上述基线上增加临时 benchmark/instrumentation。
- GitHub Actions run：`34723619518`，最终 `benchmark` job 成功。
- Runner：GitHub hosted Windows Server 2025，`10.0.26100`，image `windows-2025-vs2026 / 20260907.229.1`。
- .NET SDK：`10.0.401`；runtime：`10.0.12`。
- 工作集：10 个可见、已折叠、位于 Edge queue 的短 Note；关闭持久 PowerShell 与 MCP，开启 Edge capsule / hover preview。
- 每个发布变体执行 3 组 pair；每组先 `fresh` 再 `warm`，因此每个变体共有 6 个启动样本，8 个变体共 48 个样本。

基准分支直接继承 #254 的产品提交；`cf8b6cdc… -> 524b3fd9…` 仅增加临时 benchmark workflow/script 与构建参数辅助，不改变生产逻辑。

### fresh / warm 的含义

这里的 `fresh` **不是“整个 Windows 文件缓存完全冷”**。

- 每个 pair 开始前删除专用 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`。
- `fresh` 在空 bundle extraction 目录上运行。
- 随后的 `warm` 复用同一个 extraction 目录。
- 每次运行都复制同一发布产物到新的临时 run directory，并使用同一份 10 Note fixture。

因此 fresh/warm 主要用于观察 single-file extraction/cache 边界；不能把它解释成严格的物理 SSD cold/warm benchmark。

### 测量边界

外部父进程在 `CreateProcess` 前取 T0；被测进程记录：

1. `managed_module`：最早 `ModuleInitializer`；
2. `App.OnStartup`；
3. `AppController` ctor begin/end；
4. `StartAsync` enter；
5. `restore_surfaces_end`；
6. 首个/全部可见 surface 进入 `CompositionTarget.Rendering`；
7. `DwmFlush` 完成；
8. 收到 `--exit` 后到主进程真正结束。

`DwmFlush` 只表示调用前提交的桌面合成工作已经经过 DWM 同步边界，**不等于物理显示器已经扫描出像素，也不等于用户主观“完全可交互”时间**。

`workingSet` 在全部 surface Rendering 且 `DwmFlush` 完成时由 `Environment.WorkingSet` 采样；它是启动完成附近的工作集快照，不是长时间稳态峰值/最低值。

### 发布矩阵

缩写：

- `SC`：self-contained；
- `FD`：framework-dependent；
- `SF`：single-file；
- `R2R`：ReadyToRun。

所有发布均为 Windows x64 Release、`PublishTrimmed=false`、`DebugType=none`；single-file 继续使用 `IncludeNativeLibrariesForSelfExtract=true`。

### 产物与端到端结果

以下时间均为 3 次样本的中位数；大小统一换算为 MiB（1 MiB = 1,048,576 bytes）。

| 变体 | 发布目录 MiB | EXE MiB | Fresh managed entry | Fresh DWM | Warm DWM | Fresh Exit | Fresh Working Set MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SC + SF + compression | 77.4 | 76.5 | 301.77 ms | 1452.10 ms | 1414.87 ms | 897.71 ms | 224.0 |
| SC + SF + no compression | 184.0 | 183.1 | 295.41 ms | 1416.15 ms | 1378.47 ms | 680.14 ms | 119.8 |
| SC + SF + R2R + compression | 102.0 | 101.1 | 658.24 ms | 1470.08 ms | 1493.49 ms | 1125.52 ms | 265.8 |
| SC + SF + R2R + no compression | 260.6 | 259.7 | 512.93 ms | 1356.16 ms | 1300.70 ms | 661.05 ms | 134.4 |
| SC + multi-file | 190.4 | 0.27 | 130.06 ms | 1501.27 ms | 1241.49 ms | 716.03 ms | 122.7 |
| SC + multi-file + R2R | 229.3 | 0.27 | 121.51 ms | **1100.33 ms** | **1078.64 ms** | 671.67 ms | **115.4** |
| FD + SF | 17.2 | 16.3 | 106.55 ms | 1193.38 ms | 1162.40 ms | 656.72 ms | 117.2 |
| FD + SF + R2R | 50.1 | 49.2 | 113.62 ms | **1041.10 ms** | **1087.06 ms** | 657.09 ms | **115.8** |

> multi-file 行的 `EXE MiB` 只表示很小的 apphost；真正需要比较的是整个发布目录大小。

### 启动阶段明细

| 变体 | Fresh App.OnStartup | Fresh Controller ctor end | Fresh surfaces restored | Fresh DWM |
| --- | ---: | ---: | ---: | ---: |
| SC + SF + compression | 455.40 ms | 583.38 ms | 1426.22 ms | 1452.10 ms |
| SC + SF + no compression | 411.27 ms | 538.98 ms | 1389.93 ms | 1416.15 ms |
| SC + SF + R2R + compression | 762.88 ms | 833.34 ms | 1444.11 ms | 1470.08 ms |
| SC + SF + R2R + no compression | 616.00 ms | 688.28 ms | 1343.63 ms | 1356.16 ms |
| SC + multi-file | 263.67 ms | 398.20 ms | 1471.38 ms | 1501.27 ms |
| SC + multi-file + R2R | 249.33 ms | 352.87 ms | 1067.58 ms | 1100.33 ms |
| FD + SF | 225.92 ms | 349.15 ms | 1174.76 ms | 1193.38 ms |
| FD + SF + R2R | 225.07 ms | 326.24 ms | 1013.83 ms | 1041.10 ms |

### Warm 工作集与退出

| 变体 | Warm DWM | Warm Exit | Warm Working Set MiB |
| --- | ---: | ---: | ---: |
| SC + SF + compression | 1414.87 ms | 920.98 ms | 223.8 |
| SC + SF + no compression | 1378.47 ms | 668.35 ms | 119.9 |
| SC + SF + R2R + compression | 1493.49 ms | 1144.67 ms | 266.1 |
| SC + SF + R2R + no compression | 1300.70 ms | 640.74 ms | 134.1 |
| SC + multi-file | 1241.49 ms | 678.42 ms | 116.9 |
| SC + multi-file + R2R | 1078.64 ms | 664.48 ms | 115.3 |
| FD + SF | 1162.40 ms | 672.94 ms | 117.2 |
| FD + SF + R2R | 1087.06 ms | 690.93 ms | 115.8 |

### Bundle extraction 观察

- SC single-file 四组：fresh/warm extraction 目录约 `8,708,176` bytes，8 个文件。
- FD single-file 两组：约 `492,736` bytes，3 个文件。
- SC multi-file：0，发布时已经是展开目录。

这只能说明实际 extraction footprint；不能单凭该数字把启动差异全部归因到“解压”。host、bundle 映射、PE/R2R image loader、文件映射和 OS cache 仍混在 `CreateProcess -> managed_module` 前置阶段里。

### 关键对照

#### A. 当前正式完整包：R2R 是负优化

当前正式形态是 `SC + SF + compression + no-R2R`。

加入 R2R 后：

- 发布目录约 77.4 -> 102.0 MiB（约 +32%）；
- EXE 约 76.5 -> 101.1 MiB；
- Fresh managed entry 301.77 -> 658.24 ms（约 +118%）；
- Fresh DWM 1452.10 -> 1470.08 ms，没有端到端收益；
- Warm DWM 1414.87 -> 1493.49 ms，反而更慢；
- Fresh working set 224.0 -> 265.8 MiB（约 +19%）。

因此 D-036 保持正式 compressed single-file 的 `PublishReadyToRun=false`。

这轮也单独证明了 **当前真实、未插桩源码能够成功 R2R publish**；没有复现“R2R 构建/兼容性坏掉”。问题是当前打包组合的成本收益，而不是 R2R 功能不可用。

#### B. 关闭 single-file compression 不值得

相对当前正式包：

- 发布目录约 77.4 -> 184.0 MiB（约 +138%）；
- Fresh DWM 1452.10 -> 1416.15 ms，只快约 36 ms（约 2.5%）；
- Warm DWM 1414.87 -> 1378.47 ms，也只快约 36 ms。

因此没有理由仅为这几十毫秒把完整包扩大到两倍以上。

#### C. Multi-file + R2R 是有效候选，不是当前决策

相对 SC multi-file no-R2R：

- 发布目录 190.4 -> 229.3 MiB（约 +20%）；
- Fresh DWM 1501.27 -> 1100.33 ms（约 **-26.7%**）；
- Warm DWM 1241.49 -> 1078.64 ms（约 **-13.1%**）；
- Fresh working set 122.7 -> 115.4 MiB；
- Warm working set 116.9 -> 115.3 MiB。

本轮**没有观察到 multi-file R2R 带来内存上升**，反而中位数略低。原始 Fresh working-set 三次样本为：

- SC multi-file：128,696,320 / 128,847,872 / 122,478,592 bytes；
- SC multi-file + R2R：120,840,192 / 128,147,456 / 120,967,168 bytes。

这证明 multi-file + R2R 值得作为未来发布形态候选继续评估，但它会失去“单 EXE”便携性，所以本实验**不把它自动升级为当前正式发布方案**。

#### D. Framework-dependent 仍是最快、最小的轻量路线之一

FD single-file no-R2R 已把 Fresh DWM 降到 1193.38 ms；R2R 后进一步到 1041.10 ms，但发布目录约 17.2 -> 50.1 MiB，约 3 倍。

因此“是否给 no-runtime 包单独启用 R2R”是独立产品/发布取舍，不能因为速度更快就直接采用。

### 当前可得的启动预算

以当前正式 `SC + SF + compression + no-R2R` Fresh 中位数为例：

```text
CreateProcess
  ~302 ms  → 最早托管 ModuleInitializer
  ~153 ms  → App.OnStartup（累计 ~455 ms）
  ~128 ms  → AppController ctor end（累计 ~583 ms）
  ~843 ms  → 10 个 surface restore 完成（累计 ~1426 ms）
   ~26 ms  → DwmFlush 完成（累计 ~1452 ms）
```

因此发布/CLR 前置成本确实存在，但当前最大的 PaperTodo 自身可控区间仍是：

`AppController ctor end -> restore_surfaces_end`，约 **843 ms**。

后续若继续优化启动，优先拆 `PaperWindow / EdgeCapsuleHost / HWND / Loaded / Arrange / first presentation`，而不是继续猜 ReadyToRun 或 compression。

### 限制

- GitHub hosted runner 不是用户桌面，绝对时间不可直接外推。
- 样本只有每模式 3 次，适合强对比，不适合解释很小的个位数百分比差异。
- `fresh` 只严格清理 bundle extraction 目录，不是 OS page cache 的完全冷启动。
- `DwmFlush` 不是物理显示器扫描边界。
- working set 是 ready 附近快照，不代表长期 steady-state、峰值 private bytes 或 commit charge。
- benchmark 注入了少量时间戳/JSON probe；所有变体使用同一插桩，所以用于相对 A/B。另有未插桩真实源码 R2R publish probe 用于单独验证兼容性。

### 原始证据

- Workflow run：`34723619518`。
- Benchmark commit：`524b3fd9ce1eb6bb388a1c6ffab81caef32cfd88`。
- 产品基线：`cf8b6cdc7be7bfb0ea0b51714a1ab1360740848b`。
- Artifact：`cold-start-packaging-benchmark`，包含：
  - `summary.csv`：各变体中位数；
  - `startup-samples.csv`：48 个 fresh/warm 启动样本；
  - `publish-results.csv`：发布耗时与产物大小；
  - `raw.json`；
  - 各变体 publish logs；
  - 未插桩真实源码 R2R publish log。

GitHub artifact 有保留期限，因此长期判断应以本文保留的实验条件和关键数值为准；需要重新做发布选择时，优先在当时的 runtime / Windows / PaperTodo 版本上复跑，而不是机械沿用 2026-09 的绝对毫秒数。

---

## E-002 — Edge Host 首次呈现与非首帧初始化

**日期：** 2026-09-13  
**状态：** Completed  
**目的：** 继续拆解 10 个 Edge capsule 启动恢复成本，验证两条候选：把非视觉右键菜单移出首帧关键路径，以及把首次呈现从逐 Host flush 改成全部 Stage 后统一跨 Render/Reveal 边界提交。

### 基线、候选与环境

- 原始 #254 产品基线：`8ca1276efb760314394ba5763ad768e61bfb99bd`。
- menu + batch 候选提交：`20b007d716b6abf9af72a26919dcb0baf754ba00`。
- 分段 probe / 3 轮 profile run：`34727969772`，Windows Server 2025 / .NET SDK 10.0.401 / runtime 10.0.12，完整成功。
- menu-only / menu+batch 隔离 run：`34728469483`，同一 Windows runner 内依次测 3 个变体，每个变体 5 个独立新进程、固定 10 个可见已折叠短 Note。
- 隔离测试只比较 controller/restore 之后的相对启动路径；不是完整 EXE/CLR 冷启动，也不是物理显示器扫描时间。

### 隔离 A/B

5 次样本取中位数：

| 变体 | Restore 返回 | Shell 全部就绪 | Preload 全部就绪 |
| --- | ---: | ---: | ---: |
| 原始 #254 | 824.91 ms | 1114.25 ms | 1197.83 ms |
| **只延后右键菜单** | **733.99 ms** | **1018.08 ms** | **1130.12 ms** |
| 右键菜单延后 + 批量首帧 | 732.55 ms | 1019.01 ms | 1146.38 ms |

原始 #254 的 5 个 restore 样本为：`774.33 / 787.25 / 824.91 / 860.39 / 1715.04 ms`；menu-only 为 `721.80 / 732.66 / 733.99 / 739.82 / 867.40 ms`；menu+batch 为 `706.32 / 731.12 / 732.55 / 736.92 / 784.63 ms`。

结论：

- menu-only 相对原始 #254：restore 中位约 **-90.92 ms / -11.0%**，Shell ready 约 **-96.17 ms / -8.6%**，preload ready 约 **-67.71 ms / -5.7%**。
- 在 menu-only 基础上加入“全部 Stage -> hidden Render -> Reveal -> visible Render”的批量首帧路径，restore 只再改善 **1.44 ms / 0.2%**；Shell ready 反而慢约 0.94 ms，preload ready 慢约 16.26 ms。
- 因此批量首帧收益落在噪声量级，不足以支付额外启动状态和 3 个专用文件的长期复杂度；最终产品只保留菜单延后。

### Host 分段 probe

在 menu+batch 候选上额外跑 3 次 instrumented 10-capsule 新进程。以下为每次 10 个 Host 的累计时间中位数：

| 阶段 | 10 个 Host 总计中位 |
| --- | ---: |
| `PaperWindow` ctor | 20.41 ms |
| `EdgeCapsuleHost.Create` | 9.32 ms |
| 图标测量 | 0.96 ms |
| 输入事件绑定 | 13.73 ms |
| Native hooks | 0.52 ms |
| 初始 Theme | 0.20 ms |
| **`Window.Show()`** | **41.47 ms** |
| **整个 `Host.Apply`** | **134.51 ms** |
| 批量 hidden Render | 4.67 ms |
| Reveal loop | 2.54 ms |
| 可见 Render | 0.38 ms |
| **10 套右键菜单 Build** | **41.72 ms** |

这里 `Host.Apply` 包含 `Window.Show()`，不能把两行相加当成独立总成本。

### 采用 / 拒绝

**采用：右键菜单延后。**

- Edge Host 首帧不需要 ContextMenu，因此不再同步 `BuildDeepCapsuleSlotContextMenu()`。
- Host 建立后只把菜单初始化排到 UI Dispatcher `SystemIdle`；仍在 UI 线程创建 WPF 菜单。
- 不为“启动后极短时间内第一次右键”增加 placeholder/fallback；如果这一次恰好早于 SystemIdle，允许它没有菜单，下一次正常。该极端边界不足以换取永久复杂度。

**拒绝：启动专用批量首帧 Stage/Reveal。**

- 运行时已有共享 frame scheduler / native transaction 机制；E-002 不证明还需要一套启动专用呈现状态。
- menu-only 已拿到几乎全部改善；批量首帧额外 restore 收益只有约 1.4 ms，中位 Shell / preload 没有改善。
- 因此最终代码恢复既有逐 Host presentation 语义，只把非首帧菜单工作移出关键路径。

### 下一步

E-002 说明真正值得继续拆的是 `Host.Apply`，而不是 `EdgeCapsuleHost.Create`、Theme、hook 或图标测量：

- 10 个 Host 的 `Host.Apply` 中位约 134.5 ms，其中 `Window.Show()` 约 41.5 ms；
- 剩余约 90 ms 混合了 native bounds 查询/提交、WPF 属性与布局、首次 HWND/WPF source 生命周期、post-Show placement 和 verify；
- 下一轮应直接给 `Host.Apply` 内部再分段，重点测 `EnsureHandle/SetWindowPos/Show/post-Show SetWindowPos/layout/verify`，不要继续为几毫秒的小初始化增加框架。

### Evidence

- Profile + Host probe：Actions run `34727969772`，artifact `e002-startup-batch-profiled-evidence`。
- Menu vs batch isolation：Actions run `34728469483`，artifact `e002-menu-vs-batch-isolation`。


---

## E-003 — 实机录制、代理常驻复用与历史版本对照

**日期：** 2026-09-13
**状态：** Completed
**目的：** 使用用户 PR254 正式包的数据与已录制动作，区分代理接管成本和动画更新间隔，并比较代理路线封版后的关键版本。实际保留的预接管/复用机制见 D-037；本节只记录这次实测。

### 方法与可比范围

- 输入来自 `输出/实机数据`；原始正式 EXE、`data.json`、`note-assets.lmdb` 和 `输出/数据.exe` 均保留且 SHA-256 未变。每次回放使用独立数据副本；直接运行用户的录制 EXE，约 27 秒，没有使用截图判断卡顿。
- 7 个历史版本均从本地精确 commit 与固定子模块归档重建；不修改历史源码，不 fetch、不推送。每版两个独立新进程，第一轮正序、第二轮逆序；每次启动后等待 6 秒，再运行同一录制。
- 所有比较包使用优化 Debug、win-x64、framework-dependent、single-file、R2R=false、trim=false、Fody/SDK 压缩关闭。当前 SDK 拒绝 framework-dependent 与 bundle compression 的组合，因此统一使用未压缩包。诊断包形态与用户原正式包不同，不能将绝对耗时直接等同于正式包。
- Windows NT 10.0.26200、16 个逻辑处理器、实测 DPI 1.25。详细构建参数、SDK、源码哈希与环境保存在下述本地产物。
- 各版均完成 24 次展开；最早两版的最后一次命中对象与后续版本不同。主表只比较 **16 轮日志中逐项核对相同的前 23 次展开**；全程统计也独立保留。
- 本地实现含 `f64d63f`（正文 artifact 缓存）及 `1d55939`（常驻预接管和容量管理）。当前包两轮在同一进程运行，中间静置 60 秒，以验证复用持续性；其进程条件与历史两次冷进程不同，已保留完整运行身份，不混称严格冷启动 A/B。

### 相同前 23 次展开的结果

各列为两轮实测范围，单位 ms。事务中位数是 `transaction.commit totalMs`；Rendering 列是连续活动区段内 accepted Rendering 处理完成附近的 QPC 间隔 P95。

| 版本 | commit | 事务中位数 | Rendering 处理间隔 P95 |
| --- | --- | ---: | ---: |
| PR94，V3 Lite 合入主干 | `899f3cd` | 32.969–34.310 | 25.493–25.495 |
| V3 Lite + 首用轻量预热 | `440941d` | 33.976–35.634 | 25.450–25.760 |
| PR242，bounded / 无滚动预览 | `dbf1f87` | 34.800–39.166 | 25.388–25.620 |
| PR245，重正文合并预热 | `07eeb01` | 32.694–37.635 | 25.433–25.656 |
| PR238，Rendering 调度与共同起始时钟 | `a563a25` | 36.242–36.486 | 31.782–32.811 |
| PR251，统一 artifact renderer | `5bcf564` | 39.887–42.009 | 32.893–33.608 |
| PR254，用户数据对应基线 | `416a6fd` | 34.990–37.617 | 32.757–34.282 |
| 本地预接管与复用 | `1d55939` | 2.076–2.350 | 30.343–30.440 |

按本地实际合入顺序，PR245 在 PR238 前。PR238 后处理间隔长尾有所增加，数据没有证明“以前完全不卡”，也不能把不同版本的全部差异归因于某一个调度函数。

### 当前包的持续复用与代价

两轮完整录制各完成 36 次事务，全部使用 successor 复用；交互过程中冷创建 0 次，fallback/retry 0 次。事务中位数分别 2.350 / 2.379 ms、P95 19.833 / 15.587 ms。每轮正文 artifact 命中 10 次、miss 0 次。启动阶段仍需要一次真实准备，10 个 source 的 prepare 为 50.725 ms，controller 总计 64.062 ms；这是把成本移到可取消的后台准备，并非消除成本或保证任意新对象 100% 命中。

静置 60.022 秒，整个进程 CPU 增加 390.625 ms，约单个逻辑核心的 0.65%；private bytes 145,461,248 → 136,605,696，handles 867 → 856，第二轮结束 859。该短期记录不证明长期 GPU/内存无泄漏。插件按最大值准备会增加真实 WPF backing surface；未声明最大值的 Native 首次报告更大尺寸仍需要安全交接和扩容。真实多屏多缓存未验收，不承诺跨屏命中率。

### 统计陷阱与验证边界

旧版含 12ms watchdog。PR94 第一轮的 changed 样本中 447/920（48.6%）来自 watchdog；因此混合软件更新的 14–16ms 间隔不能与后来的 Rendering-only 约 31–34ms 直接换算为显示帧率减半。

原 v1 分析还会误取紧邻、未改变的 frame 为间隔起点。原脚本及 JSON 原样保留，主表使用追加的 v2：按 QPC 重新计算、仅在连续 active fingerprint 区段内连接、真正 changed 才更新 changed 基准；完成 endpoint 先计入再清空。fingerprint 不是每次动画唯一编号。分别保留 ShapeChangedGap、RenderingOpportunityGap、RenderingShapeChangedGap 和 raw accepted/duplicate/suppression 计数。

这些日志描述软件状态和 Rendering 处理节拍，**不测量 DWM 物理呈现或显示器 FPS**。行为验证包括 Release build 0 警告/错误、EdgeTitleChecks 6/6 组 3145 断言，以及 EdgePreviewChecks 全套通过（96 组 WPF 宽度/DPI/舍入、80 组冷热像素完全一致等）。多屏与长期 GPU 成本仍未实测。

### 本地保留的证据

所有原始及中间试验均位于 `输出/edge-replay-20260913/`，用户要求只在本地保留；这些大体积产物不纳入 git，也未上传：

- `历史版本对照-结果.md`、`历史版本对照-候选.md`、`历史回放-帧间隔统计口径.md`：完整结果、历史选择与口径核查。
- `history/packages-uniform-20260913-02.json`、`history/README.md`：7 个精确源码归档、固定依赖、构建参数/日志/包/哈希及失败提取尝试。
- `history-<commit>-r1/`、`history-<commit>-r2/`：14 次实际运行包、数据副本、原始日志、时间边界、v1/v2/common23 分析。
- `current-final-idle-1/`：最终包两轮及中间静置的原始记录；`history/replay-results-v2.json`：历史与当前共 32 份全程/同前缀结果。
- `baseline-*`、`resident*`、`final-idle-*`、`prewarm-*`、`defererase-*` 等此前原始记录、失败/撤回试验与报告全部保留。
- `Run-Replay.ps1`、`Run-IdleReplay.ps1`、`Run-History.ps1`、`Analyze-Replay-v2.ps1`、`original-hashes.json`：可复跑入口和原始文件身份。
- `输出/边缘浏览预热完成-诊断包-20260913/`：含原始数据副本及最终诊断 EXE。EXE SHA-256 `5867E1E145E5319A47F3E948CF0A9B179D164679784F74EE0C74D391EC22D652`；发布早于本地 commit，源码内容对应 `1d55939`，运行身份以包哈希为准。

### 追加的有界订阅合并 A/B

尝试将短暂全队列阻塞的 Rendering 退订合并到单次 Loaded 操作，并保留逐队列屏障、无动画 retry 和 shutdown 清理。检查曾通过 3167 断言；最后两项夹具稳健性调整尚未重跑即随实验撤回。

一轮实验后紧接新进程基线，前 23 次展开一致。基线/实验的事务中位数为 2.430/2.612ms，Rendering 间隔 P95 为 32.376/36.665ms，changed 间隔 P95 为 32.670/37.290ms。样本没有支持稳定收益，因此撤回额外调度逻辑，最终仍交付 1d55939。没有根据一个最大值改善就保留实验；原始 patch、包、检查日志与 subscription-baseline-1、subscription-coalesce-1 数据均保留。

### 追加取样纠正与 16 个历史版本的最终对照

最初把 PR94 当作代理路线封版起点，漏掉了用户记忆中的 V2.5。按本地代码核对：PR88 的 1a239c3 是 V2.5 初成，PR90 分支 a402a80 是 V2.5 后期修正版；它们仍使用 snapshot、native clip、reveal/conceal。d4af6af 开始切到 V3 Lite，PR94 的 899f3cd 是 WPF shape + 同尺寸 live DComp translation 的合入点，后者才是当前代码沿用的路线。

追加 9 版全部以未改动的历史源码重新构建，含切换路线的两个中间点、hover intent、锚点布局、回墙 endpoint、Markdown 扩预算与裁剪 viewport。用户数据开启 hover intent（low），故 PR112 也纳入。每版两轮新进程，追加批次第一轮按历史正序、第二轮逆序；期间没有构建或其他测试竞争 CPU。

最终 32 轮历史 + 当前 2 轮全部完成。PR214 第一轮后段命中不同，只展开 23 次；其第二轮和其他版本均为 24 次。逐项核对 **34 轮的前 22 次 owner 完全一致**，最终表仅比较此范围。首批的前 23 次统计和所有全程数据原样保留，不能把表内不同裁切范围的数字交叉相减。
| 版本 | commit | 事务中位数 ms | 事务 P95 ms | Rendering 间隔 P95 ms |
| --- | --- | ---: | ---: | ---: |
| PR88，V2.5 初成 | 1a239c3 | 58.115–62.640 | 151.170–153.234 | N/A |
| PR90 分支，V2.5 后期修正 | a402a80 | 59.929–60.565 | 141.990–142.231 | N/A |
| V3 Lite 首次完整切换 | d4af6af | 27.430–27.566 | 32.334–43.385 | N/A |
| V3 Lite Render 优先级与 watchdog | 849c9bb | 33.697–36.954 | 51.393–51.611 | N/A |
| PR94，V3 Lite 合入 | 899f3cd | 32.969–34.310 | 44.719–45.422 | 25.490–25.526 |
| 首用轻量预热 | 440941d | 34.463–35.634 | 67.021–72.457 | 25.450–25.763 |
| PR112，hover intent 与 capture | 254158c | 33.231–35.334 | 46.301–54.823 | 24.869–25.057 |
| PR199，预览锚点与排布 | 8a2c87b | 33.873–38.630 | 46.856–57.130 | 25.204–25.299 |
| PR214，回墙 endpoint 几何 | 3f7e19e | 37.174–37.863 | 62.072–62.083 | 25.261–25.736 |
| PR234，Markdown 模式与扩预算 | f481eb6 | 35.000–39.952 | 46.611–48.869 | 25.560–25.749 |
| PR236，裁剪 viewport | a550e14 | 37.013–43.881 | 49.208–66.189 | 25.481–26.019 |
| PR242，bounded / 无滚动预览 | dbf1f87 | 34.800–39.166 | 59.297–61.694 | 25.345–25.620 |
| PR245，重正文合并预热 | 07eeb01 | 32.694–37.901 | 51.318–62.677 | 25.200–25.450 |
| PR238，Rendering-only 调度 | a563a25 | 36.242–36.515 | 60.446–60.715 | 31.547–32.811 |
| PR251，统一 artifact | 5bcf564 | 39.887–42.009 | 57.783–58.934 | 32.891–33.581 |
| PR254，实机数据基线 | 416a6fd | 35.192–38.253 | 49.545–61.058 | 32.415–34.151 |
| 本地预接管与持续复用 | 1d55939 | 2.350–2.379 | 15.587–19.833 | 29.957–30.269 |

N/A 表示最早四个版本没有 wpfChanged / wpfTransitionId 等同口径字段，不能从日志计算后版的连续活动 Rendering 间隔，也不能填 0。V2.5 的形状主要由 DComp 更新，即使另取 WPF callback 数也不等同于其动画出屏。transaction.commit totalMs 是同步提交调用成本，不是动画完成时间；proxy prepareMs 也不是包含所有更早资源创建/快照准备的完整首用成本。历史 renderer、正文预算及功能工作量不同，固定输入和打包参数并未抹平这些产品差异。

结果解释：

- V2.5 的事务中位数约 58–63ms，后期版约 60ms；切换到 V3 Lite 后约 27.5ms。它们说明同步接管成本变化，**没有证明用户记忆中的 V2.5 动画更顺或更卡**。
- 在可用的同口径 Rendering 指标中，PR94 至 PR245 多数为约 25–26ms，PR238 后约 32–34ms；这段长尾差异值得继续定位。旧 watchdog 改变软件采样来源，不能由混合 gap 宣称 FPS 减半。
- 当前预接管和复用将 PR254 约 35–38ms 的同步事务中位数降到约 2.35–2.38ms，Rendering 间隔 P95 仍约 30ms。交互接管优化成立，所有动画卡顿已解决的结论不成立。
- 34 轮在相应回放时间内没有记录到明确 fallback、retry、verify/endpoints failure 或 WPF apply failure。V2.5 初版分别有 11/8 次 requestedSuccess=False 完成，但 endpointsReady=True；该字段可能对应取消/替换，单凭它不判作交接失败。
- PR214 针对拖拽回墙 endpoint；普通浏览录制不能证明完整覆盖该新增分支，列入该版用于核对产品整体回归。两轮样本也不足以证明所有设备或长期资源行为。

新增机器可读产物：history/replay-results-all-v2.json（68 份全程/共同前缀记录）、history/comparison-all-common22.csv、history/sequence-equivalence-all.json、history/run-outcomes-all.json；原首批 replay-results-v2.json 不覆盖。追加包的身份与构建日志见 history/packages-supplement-20260913-03.json。所有快照、包、输入副本、原始日志、失败试验、脚本及旧统计均保留。

## E-004 — 统一内存日志与历史全程对照

**日期：** 2026-09-13
**状态：** Completed
**目的：** 补齐 E-003 最早四版缺少统一帧字段的问题，把代理接管、展开形状更新和日志自身扰动分开测量。

### 本轮结论

- 当前同步事务显著快于 PR254：相同动作窗内中位数从 37.828～39.973ms 降到 2.122～2.303ms，P95 从 54.277～64.620ms 降到 18.330～18.801ms。
- 当前展开对象的宽高、透明度变化，单看 Rendering 来源，间隔 P95 仍为 31.496～32.563ms。PR94 为 30.832～31.395ms，PR245 为 30.374～31.253ms；这些两轮样本没有显示 13ms 对 32ms 那样的差距。旧版混合 watchdog 的 13～15ms 软件更新不能直接与当前 Rendering-only 比较成显示帧率翻倍。E-003 的整队列 accepted Rendering 指标与本轮单个展开对象形状指标也不是同一个量。
- PR254 这两轮形状间隔 P95 为 33.585～45.491ms，当前有所改善，但不能宣布卡顿已经消失。最早 V3 切换版 d4af6af 同样出现约 33～34ms 的形状长尾。
- V2.5 仍由 DComp 做原生形状动画，WPF 记录不描述其每个原生中间帧。本轮不能判定用户记忆中的“以前更顺”是错觉，也不能证明 V2.5 的实际显示帧率更高。

### 采集与比较方法

16 个历史版本均从 E-003 保存的精确 archive 和固定子模块重新提取，先逐文件核对 SHA-256，再只加观察点。统一使用同一份 Journal、Observation 和文本缓冲源码；`SourceModified=true` 明确表示诊断副本，原始历史包及上一轮数据不覆盖。动画、渲染、输入策略及功能预算仍保留各版原有实现，不能把版本间所有差异归因于某一个调度函数。

包参数继续统一为优化 Debug / win-x64 / framework-dependent / single-file / R2R=false / 不压缩 / 不裁剪 / Fody 关闭。每轮新进程，启动后等 6 秒，直接执行原 `数据.exe` 约 27 秒，再等 2 秒并通过正常命令退出。历史第一轮正序、第二轮倒序；采集期间不运行构建、其他应用测试或全量分析。当前包也改成两次新进程，与 E-003 同进程双轮的条件分开记录。

共完成 32 轮历史＋2 轮当前完整观察＋2 轮当前关闭详细观察的对照；另保留一个独立 pilot。36 轮正式采集全部正常退出、无容量/文本预算丢弃、无 span 配对异常，退出前检查均无匹配诊断文件。15/32 历史轮的后段动作与当前不同：PR88 在第 31 个动作多出一次收起，因此主比较限定为严格相同的前 30 个 open/close 动作。每次完整回放和动作差异均保留，不能跨 E-003 与 E-004 的不同窗口直接相减。

日志只观察既有回调，不额外订阅 Rendering、补帧或强制布局。每个 presenter/transition 使用独立观察编号；同一帧多次 apply 只留最后状态，分开统计队列平移与实际宽高/透明度变化，并提供去除多纸片重复权重的统计。圆角原始事件保留，本次形状间隔汇总未纳入圆角。清除 transition 不代表成功完成；scope 退出不代表操作成功；嵌套 span 不直接相加。分位数沿用 E-003 的非插值定义：median 下中位，P95 取排序后 ceil((n−1)×0.95) 项。

### 相同动作窗结果

本轮主表比较严格相同的前 30 个动作（20 次展开、10 次收起）。每格是两轮结果的范围，单位 ms。

| 版本 | commit | 事务数 r1/r2 | 事务中位数 | 事务 P95 | 展开对象形状变化间隔 P95，仅 Rendering |
| --- | --- | ---: | ---: | ---: | ---: |
| PR88 · V2.5 初成 | 1a239c3 | 31/30 | 101.764–107.154 | 125.557–127.867 | N/A |
| PR90 分支 · V2.5 修正 | a402a80 | 30/30 | 54.903–55.571 | 141.452–160.796 | N/A |
| V3 Lite 首次切换 | d4af6af | 30/30 | 25.343–26.071 | 44.699–46.997 | 32.836–34.159 |
| V3 Lite · Render 优先级 / watchdog | 849c9bb | 30/30 | 33.414–37.468 | 47.828–70.173 | 29.087–29.278 |
| PR94 · V3 Lite 合入 | 899f3cd | 30/30 | 35.555–36.954 | 53.877–60.349 | 30.832–31.395 |
| 首次轻量预热 | 440941d | 30/30 | 37.722–43.654 | 60.408–62.262 | 30.126–32.313 |
| PR112 · hover intent | 254158c | 30/30 | 35.110–40.457 | 51.553–61.545 | 29.710–30.967 |
| PR199 · 锚点排布 | 8a2c87b | 30/30 | 36.190–40.291 | 53.368–59.866 | 26.624–30.776 |
| PR214 · 回墙端点 | 3f7e19e | 30/30 | 34.484–38.551 | 60.602–62.662 | 26.727–29.846 |
| PR234 · Markdown 预算 | f481eb6 | 30/30 | 33.998–36.762 | 49.743–59.412 | 30.215–30.340 |
| PR236 · 裁剪 viewport | a550e14 | 30/30 | 37.750–39.533 | 50.495–63.574 | 26.349–30.959 |
| PR242 · bounded 预览 | dbf1f87 | 30/30 | 34.943–38.779 | 49.719–59.912 | 26.872–29.636 |
| PR245 · 正文预热 | 07eeb01 | 30/30 | 33.387–37.209 | 58.983–59.177 | 30.374–31.253 |
| PR238 · Rendering-only | a563a25 | 30/30 | 38.955–44.322 | 54.413–69.914 | 33.494–34.608 |
| PR251 · artifact | 5bcf564 | 30/30 | 38.751–40.060 | 49.423–51.988 | 31.890–32.326 |
| PR254 · 实机基线 | 416a6fd | 30/30 | 37.828–39.973 | 54.277–64.620 | 33.585–45.490 |
| 本地预接管 / 复用 | current | 30/30 | 2.122–2.303 | 18.330–18.801 | 31.496–32.563 |

形状列按同一 presenter、唯一 transition、同一展开 owner 分段，只保留宽高或透明度实际变化并去掉纯平移；仅取 Rendering 来源，未计圆角。它描述应用更新，不是物理显示帧。旧版仍有 watchdog 在两次 Rendering 之间更新状态，因此也不能据此把所有差异归因于一个调度改动。V2.5 原生形状动画不参加 WPF 节拍排名。

数据来源：`analysis-matrix-final/analysis-20260913T054615-847860Z/summary.json`。完整软件来源混合统计及 native 阶段见 CSV 和该目录 JSON。


PR88 第一轮在第 30 个共同动作区间内实际记录两条事务（0.021、119.619ms），因此该窗为 31 条事务，其余轮为 30；保留真实额外调用，不按序号硬删。

### 同包 ABBA：日志扰动与代价

四轮完整动作序列均为同样的 24 次展开、12 次收起。关闭详细观察时只保留原有文本的内存记录，新增结构化事件数确为 0；这不是无日志 Release 对照。

| 顺序 | 详细观察 | 全回放 CPU 增量 ms | 事务 P95 ms | 原 v2 changed Rendering 间隔 P95 ms | 退出写出阶段 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| A1 | 开 | 6218.750 | 18.801 | 32.422 | 514.369 |
| B1 | 关 | 7250.000 | 20.032 | 31.831 | 47.154 |
| B2 | 关 | 6703.125 | 15.994 | 33.716 | 47.815 |
| A2 | 开 | 6703.125 | 18.330 | 32.022 | 476.098 |

两对样本没有检测到新增详细观察导致 P95 系统性上升，不等于证明零扰动，也未逐版测量历史版本的观察开销。B2 有一次 379.132ms 长间隔，原样保留。旧版未改动的 Analyze-Replay-v2 在四份复制日志上独立复算，12 项 count/median/P95 与新分析一致（误差不超过 0.001ms）。

默认记录数组为 131072 条、文本预算 32MiB；正式矩阵设为 262144 条、64MiB。x64 每条 96 bytes，所以本轮数组预留 24MiB；文本预算是保守 UTF-16 载荷计量，不是实际驻留字符串大小。完整观察每轮约 22.5～22.6MB JSONL，对照约 2.5～2.6MB。采集过程总分配约 129.930/130.06MiB（完整）与 126.495/127.614MiB（对照），GC 均为 8/4/2 次；累计分配不是稳态内存占用。

结构化记录核心入队累计约 6.683/6.588ms，不包括对象编号、GC 查询、调用层和旧文本格式化全部成本。表中退出写出时间只到 footer 开始，最终 footer、文件刷盘与 rename 另计。实测封存原因是 `process-exit`，存在封存后拒收 0～1 条的关闭期记录，与采集期间丢弃分开统计。预算耗尽明确计数，保留已收集数据；强杀/断电不保证保存。

### 准备阶段的具体等待

按 span 的 parent 链核对慢样本：PR254 第一轮一个 53.607ms prepare 内，两次串行 DwmFlush 合计 49.318ms；第二轮最慢的 64.175ms 内合计 59.912ms。PR238 的一个 69.259ms prepare 内对应 65.146ms。旧路径的这部分等待有直接证据。

当前最慢 prepare 为 23.129/17.972ms，内部没有 DwmFlush 子 span，两次 DComp commit 合计仅 0.0403/0.0485ms，期间 GC 为 0。因此当前剩余 prepare 时间不能继续归因于这两种已测原生等待；还未细分的步骤与展开更新节拍需要另行定位。本轮没有为让曲线好看而改变调度路线。

### 验证与证据

最终 Release 构建 0 警告/错误；EdgeTitleChecks 6/6 组、3145 断言通过；新增内存日志检查 6/6 组、12047 断言通过（采集期不写文件、容量计数、零分配 typed 记录、并发顺序、退出、重试、helper 隔离）；分析合成检查 9/9 通过。历史 16 包构建全部成功，已知单文件 IL3000 警告保留。

PresentMon 官方独立采集工具的 pilot 因本机 ETW 会话权限不足返回 6，CSV 为空，stderr 原样保留。没有从空文件推导 FPS，也没有以截图判断卡顿。因此这里仍是应用状态、回调与 native 等待证据，不是物理显示帧率。

本地证据根目录为 `输出/edge-journal-20260913/`：

- `README.md`、`完整日志对照报告.md`、`comparison-common30.csv`：操作方法、结论、17 版比较表。
- `observer-source/`、`builds/`、`packages-uniform-01.json`：诊断源码、原始哈希验证、每版改动清单、构建参数/日志/包哈希。
- `history-<commit>-full-r1/r2`、`current-full-r1/r2`、`current-control-r1/r2`、`current-full-pilot-1`：实际包、数据副本、原始 JSONL/文本、退出前文件检查及采集边界。
- `analysis-matrix-final/analysis-20260913T054615-847860Z`、`analysis-abba-final/analysis-20260913T054606-111927Z`、`analysis-pilot-final/analysis-20260913T054605-938649Z`：最终全程/共同前缀统计、逐动作差异、实际执行分析脚本/test/SHA/命令；先前派生结果也保留。
- 矩阵目录内 `prepare-attribution.json` 和 `analyze_prepare_examples.py`：原始 QPC/id/parent 与可重跑的慢准备归因。
- `legacy-crosscheck/verified.json`：旧分析器独立复算的 12 项比对。
- `delivery/启动内存日志.cmd`：当前诊断包的内存日志启动入口，附原实机数据独立副本。EXE SHA-256 为 `E2EE2AEB93B61762DB0BDBD0D97880D2491D94B45434B7988A6BDCB1E185953F`；其后仅整理了四个源码文件的空白，非空白字符序列保持一致，源码 patch 与说明一并保留。
- `preservation-summary.json`、`preserved-files-sha256.csv`、`original-input-hashes.json`：最终目录清单、逐文件哈希及四项原输入复核。

本轮只增加 opt-in Debug 诊断与实验记录，没有正式版用户行为变化，未改 Unreleased，也未形成新的产品路线 decision。所有提交和大体积证据只保留在本地，没有推送。

## E-005 — Rendering 预计呈现时间误去重与同机单变量回放

**日期：** 2026-09-13

**状态：** Completed

**基线：** `b40c6eb`，已包含预接管/复用和 E-004 内存诊断。

**证据根：** `输出/edge-cadence-20260913/`；此前 E-003/E-004 的包和数据保留。

### 定位与最终修正

E-004 的长间隙并非单一耗时：原始记录分别出现约 36ms 没有新 Rendering、约 47ms 中途仅有相同 RenderingTime 通知被过滤、约 60ms 内有 45.496ms 的 pending 退订窗口。最后一例包含代理指针采样排出的 10 个 Pointer-only reconcile，最终没有 shape.applied；其中发生 GC 的 scope 不等同于 GC 暂停时间，也不能解释整个无记录空档。逐 seq 审计及脚本保存在 `audit/`。

WPF 的 `RenderingTime` 是预计呈现时间，不是唯一通知编号。[官方 MediaContext 源码](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/MediaContext.cs) 允许复用估计值，并在每个 render handler 的首个 tick 发出通知；layout/tick 内环不会反复发同一个通知。项目的 transition 使用 QPC，按预测时间值去重会丢掉后续合法更新。最终删除该过滤条件和对应缓存；保留单一订阅、同步重入保护、外部 native apply 保护、队列屏障与终点退订，不增加 timer、轮询或主动补帧。

### 单变量与撤回实验

使用原始 `数据.exe`、独立数据副本、每轮新进程、6 秒启动等待及 2 秒收尾。沿用 E-004 的优化 Debug/单文件/无 R2R/无 Fody 参数和内存日志。各组前后动作完全相同，主比较为全部 36 动作（24 展开、12 收起），不与 E-004 的 common30 直接相减。每格是两轮实测范围，单位 ms；形状仅统计当前展开 owner 的实际宽高/透明度变化，不含纯平移和圆角，仍是应用更新而非物理显示帧。

| 对照组 | 外形更新间隔中位数 | 外形更新间隔 P95 | 结论 |
| --- | ---: | ---: | --- |
| 原行为，第一组 ABBA | 16.641–16.718 | 26.197–31.538 | 基线 |
| 仅取消纯 Pointer 屏障 | 17.361–18.906 | 32.963–35.426 | 屏障从约 800 次降到 36 次，但节拍未改善，撤回 |
| 同包保持 RenderingTime 去重 | 16.687–16.873 | 32.563–32.830 | 第二组 ABBA 控制 |
| 同包接受相同预计时间的新通知 | 10.243–10.408 | 23.593–26.123 | 采用；无新增帧源 |
| 接受通知，同时取消 Pointer 屏障 | 16.325–16.637 | 32.428–33.226 | 组合也未改善，撤回 Pointer 改动 |
| 组合前后复测，仅接受新通知 | 10.225–10.408 | 21.577–23.593 | 支持保留单一改动 |

第二组单变量使用同一个 EXE，只在 Debug 诊断包中切换过滤开关；最终包已删除实验开关，Release 与 Debug 都采用同一通知处理。第二组控制 CPU 为 7843.750/6781.250ms，候选为 7500.000/6890.625ms，未见明显总 CPU 增长；候选累计分配约 141MiB，控制约 131MiB，候选多一次 Gen0 GC，不能宣称零成本。候选两轮最大形状间隔为 37.409/50.534ms，后续复测仍有约 49ms，不能宣布长停顿或物理帧率问题全部解决。

### 验证与复现

- `RepeatedRenderingNotificationChecks` 在保留旧去重条件时准确失败于第二个同预计时间通知；修正后 EdgeTitleChecks 6/6 组、3168 断言通过。覆盖 QPC 推进、终点、取消、同步重入、native apply 和显式事务恢复，不依赖 Sleep 或机器帧率。
- 最终同包日志开关 ABBA 四轮均完成相同 36 动作、采集期零丢弃、正常退出封存；完整观察两轮 active-owner 宽高/透明度间隔 P95 为 27.771/25.152ms。关闭详细观察仍保留旧文本内存日志，该组不能直接计算 active-owner 指标；旧 changed-Rendering P95 为 24.365/27.280ms，完整观察为 26.365/26.304ms，不能宣称详细日志零扰动。关闭观察第二轮保留一条 421.848ms 的旧指纹分段间隔，逐记录核实它跨了前段收尾、静置和下一次收起，不能解读为连续动画停顿；精确证据见 `audit/final-control-outlier.md`，旧指纹指标不能与唯一 transition 的形状指标混用。
- 最终标准 Release 构建成功，0 错误；4 条 NU1900 为 NuGet 漏洞数据源连接失败，漏洞检查未完成。诊断 publish 成功，保留既有单文件 IL3000 警告。本轮共 15 次回放，原输入、各次数据副本、原始日志和未采用候选均保留。
- 运行标记审计保留了未采用 `pointer-v1-r1` 的一次启动期 `startup-failed` fallback，发生在录制开始前约 4.1 秒，不能将该候选描述为全生命周期无失败。其余 14 轮未见同类 fallback 标记，15 轮均未见正数 `wpfApplyFailed`；这只是已记录标记检查，不替代视觉或物理呈现验证。详见 `runtime-failure-scan.json` 和 `audit/pointer-v1-startup-fallback.md`。
- `packages/` 保存各候选源码差异、输入源码哈希、完整 publish 参数、构建日志和 EXE 哈希；`pointer-v1-source/` 保留被撤回的生产与测试代码；`audit/` 保留独立审查和 WPF 机制核对。
- `abba-analysis/`、`render-v2-analysis/`、`combined-v3-analysis/` 保存完整 CSV/JSON、每次实际执行的分析器源码与哈希。最终包及其日志开关复测另见根目录报告，不覆盖实验组。
- 旧 `duplicateCallbacks` 字段保留为 0 以兼容日志 schema，表示没有按预计时间过滤；不表示原始 RenderingTime 没有重复。旧版 watchdog 的真实更新与本次合法 Rendering 通知仍需按来源和实际形状变化区分，不能直接换算成显示 FPS。

本轮保持 WPF shape / DComp translation-only 分工，同步更新 D-032、Architecture 和 Unreleased；仅本地提交，不推送。最终包 SHA-256：`DA8A31304C371C1C36EBBD813F9BEE0E8144203FA6435468C423B1DE4BD3E56A`。

## E-006 — 原生消息、WPF 呈现等待和 Dispatcher promotion 定位

**Status:** Completed（诊断完成，未新增生产调度修复）

**基线：** `62f8dcc8`，已包含 E-005 的 Rendering 通知修正。

**证据根：** `输出/edge-deep-latency-20260913/`。此前三轮实验目录保留原状。

### 方法与范围

同一优化 Debug 单文件包，原始实机数据的独立副本和原录制 `数据.exe`，每轮新进程，6秒启动等待与2秒收尾。已有详细内存日志保持开启；新增 deep 开关只观察 Dispatcher 生命周期、现有 WPF MediaContext 和几何批次内下游 HWND 消息。采集不新增 Rendering 订阅、调度操作或补帧；仅 Debug 编入。

完成 deep 开启两轮、关闭两轮、deep＋外部 EventPipe 一轮。实际顺序是开启1→采样1→关闭1→关闭2→开启2，中间有分析，不称连续 ABBA。关闭1尾部少一个动作，共35，其余36；全体严格共同前缀33动作。五轮正常退出、采集零丢弃，退出前检查无匹配诊断文件。关闭时 deep 事件为0；开启三轮均531对 native 消息，无缺失配对。

### 已定位的调用点

1. **真实 HWND 移动的同步刷新。** `CommitEdgeCapsuleQueueProxyLogicalEndpoints → EndDeferWindowPos → WM_WINDOWPOSCHANGING → HwndTarget.UpdateWindowSettings → Channel.SyncFlush`。不带采样的两轮，下游0x46消息最长15.8239/12.7285ms；采样轮14.8517ms，8个 UI External 样本位于 `UpdateWindowSettings` 的 IL offset616，即614的 `SyncFlush` 调用与619的下一指令之间。真实位置变化也会触发此路径，不要求 resize；复用代理不免除真实源 HWND 的位置同步。
2. **临时退订后退出 interlock 的同步收尾。** 采样轮某一真实 owner 宽高/透明度更新间隔50.5823ms：先约31ms处于 WaitingForResponse，随后临时退订 Rendering，`ScheduleNextRenderOp → LeaveInterlockedPresentation → CompleteRender` 进入同步等待。约19ms快照跨度内13个 UI External 样本的 IL offset68，对应65的 `Channel.WaitForNextMessage` 调用与70的下一条指令。它与上一类 SyncFlush 是两个接口。恢复订阅后真正 posted→started 仅0.0062ms。首段反馈、传递、接收的进一步归因仍未知；采样不等于逐纳秒归属。无采样的开启1有48.7156ms同型状态序列，但没有该轮逐指令证明。
3. **WPF 的低优先级渲染 promotion 间隔。** 另外39.6142/37.3477ms样本先排 Inactive，配置10ms的 input promotion 实际约+23.09/+21.03ms发生，再在 Input 等约16ms；handler自身0.886/0.537ms。后半窗口的8/10个样本全在 `Dispatcher.GetMessage`，不是一直计算；estimated-vsync timer未启用，不能混称NoPresent计时器。底层timer晚到及Input等待的原因尚未确定。

上述三段采样窗口未见实际GC/Start。采样器的约20069次SuspendOther不能误认成GC；全采集实际GC/Start为9次。精确seq/QPC、完整栈及复算脚本在根报告、`analysis-native/`、`analysis-wpf/` 和 `native-repro-analysis/`。

### 扰动对照

仅统计共同33动作的当前owner宽高/透明度实际变化间隔，不桥接transition，不是物理显示帧时间：

| 组 | P95 ms | 最大 ms |
| --- | ---: | ---: |
| deep关闭，两轮 | 22.6779–25.5536 | 35.9359–51.4942 |
| deep开启，两轮 | 24.9849–26.8912 | 35.8569–48.7156 |
| deep＋EventPipe，一轮 | 27.6606 | 39.6142 |

50.5823ms调用栈样本位于共同33动作之后，仅用于阻塞定位，不用于该表排名。关闭deep仍有51.4942ms，支持长间隙并非deep独有；样本数和系统波动不足以宣称零扰动或精确开销。开启deep增加约6.3万条记录、约4～7MiB capture allocation；外部采样另有成本。两轮关闭应用CPU为6593.750/7390.625ms，两轮开启7093.750/7546.875ms，采样轮7875ms，均覆盖各自完整harness窗口，且不含外部采样器CPU。

### 版本核验与验证边界

- 实际 `PresentationCore.dll` 为10.0.12，SHA256 `A0CE98A232C65ED2B9F9AF58B39359A6016066AFD62E85ECFD745DA202F0B01B`，MVID `4fccf047-3c3d-43a3-aa9e-e7c2f44ce373`。已保存实际方法IL及精确 `dotnet/dotnet` VMR revision `95017c711e6afc1085133d440e42b4bd78155701` 下WPF源码，不只依据旧版本源码猜测。
- 系统WPR CPU和WPF ETW因当前Windows令牌权限失败，没有启动采集；不是自动审批拒绝。进程内EventPipe正常、转换eventsLost=0。尚无内核调度/GPU证据，不能继续归因到某个DWM/GPU/驱动问题。
- 新 `PaperTodo.EdgeLatencyObservationChecks` 独立链接生产探针，受控隐藏HwndSource/Dispatcher行为192断言通过，0警告/错误；验证转发一次、原参数/结果、嵌套批次、Remove/reinstall、销毁及操作优先级/FIFO/取消。9项分析器回归通过。标准Release构建0错误，4条NU1900为漏洞数据服务网络失败，未完成漏洞审计。
- v1失败构建、v2成功包、来源/哈希、所有副本和日志、nettrace/etlx、解析器/IL工具及派生结果保留。v2 EXE SHA256 `F8987C5E6914B0463B79628CDB66EF423709547573C5955E173FE4E647235A84`。四个原输入哈希复核未变；只本地提交，不推送。

本轮确立“在UI侧具体等哪个接口”的证据，未证明合成端为何迟到，也未采用此前失败的Pointer屏障实验。当前运行职责未变；Architecture记录隔离诊断能力，D-032补充退订/恢复的成本，因无新增用户行为差异不追加Unreleased条目。

## E-007 — Pointer 无效更新过滤与活跃 Rendering 保留交叉对照

**Status:** Completed；三个候选均未采用，生产与测试源码恢复到 `a469cfd397dac0123f25773a46adc609c6e9d8a7`。

**证据根：** `输出/edge-pointer-filter-20260913/`。原输入与 E-003～E-006 证据不改动。

### 候选边界

- **过滤 F：** 代理的每成员采样仍先进入现有 controller 仲裁；Presenter 用最终采样共用的 PointerIntent＋纯 reducer 判断是否会改变 model，覆盖视觉态、菜单、peer reorder，而非只比坐标或 PointerOverSurface。已有非Pointer dirty、visual deferral、native apply/retry/deferred时保守放行。无变化时不排本地 reconcile/barrier，但仍按原顺序使队列命中缓存失效，确保静止鼠标下的自主代理位移被重新解析。有效更新保留旧屏障。本轮没有合并队列 controller 广播或更改其缓存生命周期。
- **保留 K：** 仅在已经订阅且仍有活跃transition时，跨越临时 owner 阻挡保留 Rendering；每个队列仍先通过 CanAdvanceQueue，native重入保护不变。一开始就受阻仍不首次订阅，取消、终点和shutdown继续释放。不新增timer、补帧或修改WPF私有状态。

### 第一阶段：仅过滤，同包ABBA

优化Debug、framework-dependent win-x64单文件，R2R/Fody/压缩关闭，与前轮参数一致。四轮新进程、原实机数据独立副本、6秒启动等待、原 `数据.exe`、2秒收尾和正常退出。原有详细内存观察开启，deep/EventPipe关闭。同一包只切 F，顺序旧1→过滤1→过滤2→旧2；全部相同36动作。

| 组 | owner WH/opacity P95 ms | 代理Pointer排队 | barrier注册 | 订阅次数 | 全harness应用CPU ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| 旧行为，两轮范围 | 20.7543–22.1402 | 7980–8340 | 8528–8872 | 350–351 | 7531.250–7921.875 |
| 过滤，两轮范围 | 24.2882–31.9450 | 51–53 | 723–725 | 51–53 | 7578.125–7593.750 |

过滤约99%的代理Pointer请求，capture allocation约215MiB降至197MiB，但CPU无稳定下降，更新间隔变差。计数下降不能作为采用依据。v1包SHA256 `99EE697B947BC1213290BE9B113F7681C77A58536A92E452AEDD6A4385253F85`；实际源码与所有新增测试已在package/source快照。

### 第二阶段：同包2×2交叉对照

新v2包分别切F/K，A=旧行为、B=只F、C=只K、D=F＋K，顺序 **A1→B1→C1→D1→D2→C2→B2→A2**；8轮全部严格相同36动作。每格是两轮范围，仍是当前owner按唯一transition及owner episode统计的应用宽高/两种透明度变化间隔，不是物理帧时间。

| 组 | 更新间隔P95 ms | 最大间隔 ms | 代理Pointer排队 | 订阅次数 | 全harness应用CPU ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| A 旧行为 | 19.8885–21.1164 | 32.8272–35.9002 | 8010–8110 | 341–349 | 7421.875–7765.625 |
| B 仅过滤 | 31.4111–32.3762 | 36.1205–37.3572 | 52–54 | 52–53 | 6875.000–7718.750 |
| C 仅保留订阅 | 31.0336–31.2836 | 34.7292–35.8954 | 7840–8150 | 37–38 | 7078.125–7406.250 |
| D 组合 | 31.2175–32.1076 | 34.9560–39.1366 | 54 | 37 | 7281.250–7500.000 |

候选median约16.15～16.40ms，对照9.75/11.07ms；候选实际owner更新数也减少。组合capture allocation约196MiB，单保留约206～207MiB，对照约214.5～214.9MiB，保留这些开销收益事实，但三组都没有达到本次流畅性目标，全部撤回。v2包SHA256 `14245EBD9375AC89F43C7324D8565EAA948AFDFDC833B440F1DAB22721D39F05`。

### 逐间隙审查与验证

- 第一阶段基线最长48.8617/34.4371ms仍有约17ms的pending退订跨度；过滤候选两轮前三大间隙内部已无退订，pending早已drained，后续Rendering晚到。
- 矩阵中仅保留订阅两轮前三大间隙全程已订阅、内部无退订，后续raw Rendering晚到约34～36ms。组合多数同型；组合1第三大中途有Rendering但owner尺寸未变，不能把所有形状间隙直接等同于回调间隔。本轮没有deep状态/采样栈，不能把这些窗口套成E-006的CompleteRender、promotion或GPU等待。
- 两阶段共12轮正常退出，容量/文本丢弃均0，退出前检查无匹配诊断文件；全部相同24open/12close，分析无unmatched transaction。完整日志未见fallback/retry-exhausted/failed/正数wpfApplyFailed标记。矩阵8轮的10个presenter最后target相同，且common36尾部最后shape.applied与各自target共80/80匹配；这不是屏幕像素或每次native呈现的独立证明。
- 过滤候选完整EdgeTitleChecks通过3614断言；第二阶段同一个Debug DLL在K=0/K=1均通过3643断言，覆盖未cloaked/cloaked的动画中途双重barrier、零提前更新、最后释放后的真实Rendering恢复与cancel退订。保留首次测试坐标类型编译失败、受限桌面原生路由失败以及修正/真实桌面成功的独立日志。9项冻结分析器回归通过。
- `packages/`含实际源码、tracked patch、所有新增文件哈希、完整参数/日志和EXE；`rejected-source/`再次保存撤回前8个实验生产/测试文件，并逐一验证与v2打包源码相同。`scripts-v1/`保留最初harness版本；各分析目录保存执行时分析器源码。`independent-abba-review/`及`independent-matrix-review/`保留逐gap脚本、JSON、60份上下文与失败/终点审计。

源码恢复后标准Release构建通过，0错误；4条NU1900为漏洞数据服务网络失败，未完成漏洞审计。最终仅提交实验结论和D-032踩坑补充，不改Architecture/AGENTS/Unreleased，不把未获收益的过滤或订阅开关留在日用程序；所有实验包与日志继续保留，只本地提交，不推送。

## E-008 — 渲染请求、遍历、提交时钟与反馈的关联定位

**Status:** Completed；本轮只保留只读诊断修正，不合入过滤、保留订阅或新的帧请求策略。长间隔仍存在，未宣称流畅性问题已解决。

**证据根：** `输出/edge-render-chain-20260913/`；工作区基线 `2899a3bf93c7679c1732d862a37cefa414996452`。原数据、录制与之前已封存目录保持不变。

### 方法与测量边界

先复用 E-007 v2 同一 EXE，F=0/1、K始终0，开启已有 deep 观察做四轮 ABBA；再分别为两组增加一轮 EventPipe 调用栈采样。最后修正静态字段读取，另打 v3，同包做第二组四轮 ABBA。共10轮，全部严格相同36动作（24open/12close）、正常退出、记录/文本零丢弃、退出前无匹配诊断文件；两份采样 eventsLost=0。各组单独冻结 common36，不跨不同探针/采样形态排名。

`CommittingBatch` 也会在同步等待路径调用，是提交/等待之前的通知，不能直接计为已完成的帧提交。`_lastCommitTime` 只覆盖 interlocked CommitChannel 路径；`_lastPresentationTime` 是被采样观察到的反馈时钟，其内嵌值可能晚于观察QPC，不能当成消息到达时刻或屏幕像素时间。各字段变化只给可观察下界。UI侧 shape.applied、Rendering 和全局 render-walk 编号均不是物理显示帧。

实际显示配置通过只读 EnumDisplayDevices/EnumDisplaySettings 核对为一个 attached DISPLAY1，2560×1440、报告59Hz、RTX2080。该整数配置不是实测物理呈现间隔。前两次空结果为PowerShell null字符串被转换为空串；改为C#内部传真实null后成功，三份输出保留，不能把空结果解释成无显示器或权限不足。

### 额外更新没有同幅增加提交/反馈

修正探针后的同包 common36：

| 组 / 轮 | owner 更新间隔P95 ms | 直接 Render handler 次数 | Animated handler 次数 | 观察到的 commit 时钟变化 | 观察到的 presentation 时钟变化 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 原行为1 | 28.6651 | 375 | 514 | 467 | 424 |
| 原行为2 | 27.5150 | 375 | 529 | 482 | 436 |
| 过滤1 | 34.0243 | 132 | 516 | 482 | 424 |
| 过滤2 | 33.4639 | 130 | 527 | 492 | 451 |

第一组原包也得到同型结果：直接 handler 原行为386/378、过滤123/127，Animated相近；commit时钟原行为486/458、过滤493/483，presentation时钟449/413对452/440。WPF的Rendering add accessor确实调用PostRender，但当前探针没有直接记录每个PostRender调用原因，不能把所有直接handler一律归到鼠标或重订阅。

v3中，24个owner-transition有效形状首末窗口内，相邻观察commit的全局renderID增量中位数原行为为2、过滤为1，支持额外请求增加了提交间的遍历；该静态编号跨MediaContext共享。与此同时，最近owner宽高/透明度变化到precommit观察的年龄中位数原行为13.9204/14.6906ms、过滤16.9799/17.2623ms，P95分别30.1304/30.0479与34.1530/33.4092ms。该年龄只描述已记录UI状态，不能证明这些状态已序列化进该批次或显示在屏幕上。窗口首末随各轮实际更新略有变化，不拿全common里的静止期状态年龄排名。

因此，E-007的应用更新间隔退化不能直接升级成“过滤降低物理FPS”；相同提交数量也不能升级成“体验一样”。原行为可能以额外遍历换来更及时的状态，最终收益还需内容与实际呈现的对应证据。此前未采用候选的决定保留，本轮不因某一个计数或年龄指标恢复它。

进一步按实际WPF源码的CountsToTicks、RefreshPeriod、TicksUntilNextVsync及CommitChannel复算请求时刻，在上述owner窗口内原行为可复算167/174次、过滤179/174次。请求相对commit时钟的提前量中位数原行为20.6797/20.4293ms、过滤20.5729/20.5456ms，P95分别23.8553/24.7593与24.4056/23.8494ms，未呈稳定过滤特异差异。计算保留C#负数余数语义；不少记录中的presentation时钟晚于commit，源码公式选择其后的周期。此为字段和固定源码重建的请求值，不是实际native参数抓取，更不是反馈到达或屏幕延时；不能把等待全算为UI计算，也不能仅凭该重建值宣称整个长间隔原因已经确定。

### 调用栈区分两类等待

- **没有待执行Render，等反馈/消息：** `chain-pipe-filter-r1` 的34.7051ms间隔，seq63545→63651、QPC4023209121939→4023209468990，全程保持订阅。开头状态为WaitingForResponse/currentOp0；UI原生线程13004的20个External样本均落在Dispatcher.GetMessage。到+34.4311ms才posted Animated op7035，+34.4483ms started，排队只有0.0172ms。此采样轮前五大间隔都属于该状态形态。
- **已有低优先级Render，仍未获执行：** `chain-pipe-control-r1` 的32.1651ms间隔，seq46234→46363、QPC4022169088599→4022169410250。op4665在+0.2876ms以Inactive排队，+16.4029ms记录旧优先级0的变更钩子，+31.8700ms才以Input开始；变更后8个External样本仍落在GetMessage。同段另有2个SyncFlush样本，不能把整段全部算成空闲。

上述两个区间都没有GC/Start；采样数量不是精确时间占比。未采样过滤轮也出现等待低优先级操作的46.1256ms样本，但不能借用另一轮栈作其直接证明。仅看GetMessage不足以区分两类；必须同时看操作是否存在、优先级与同轮QPC。当前证据尚未拆出合成端处理、通知传递及OS唤醒各自的成本，没有新增内核/GPU呈现证据。

### 修正、验证与保留

- 实际WPF `_contextRenderID` 是static int；旧读取器只查Instance导致不可读。`MakeNumericReader`增加静态字段只读支持，缺失/不支持类型仍安全降级；不新增事件、操作、订阅或计时器。v3四轮number/object availability均为1023/15，原包为511/15。
- 完整EdgeLatencyObservationChecks通过225断言（原192＋新增33），覆盖static/instance int、精确long、bool、enum、TimeSpan、实时值、对象选择及降级，原生转发与Dispatcher生命周期检查仍执行。冻结分析器9项、commit/request关联分析器5项检查通过，后者覆盖首次clock不向后借用renderID、缺失mask、C#负余数及estimated后推选择；标准Release构建0错误、4条NU1900为漏洞数据源网络失败，未完成漏洞审计。
- v3 EXE SHA256 `5186C0E2AF33C61CB14DB9ABC9D886B260C893B56DB5F278ECCA0FEE522EA33A`，复用原包仍为 `14245EBD9375AC89F43C7324D8565EAA948AFDFDC833B440F1DAB22721D39F05`。全部源码、测试、参数、失败/成功日志、nettrace/etlx、调用栈、逐间隙及commit关联均保存。打包用的3个运行时实验文件已按保存哈希恢复；四个原始输入哈希复核未变。

当前架构、调度及用户行为保持不变；本地提交探针修正、行为检查和结论，不推送，不追加Unreleased或改写架构。归档报告保留这一轮的因果边界，不能用它宣称最终显示流畅性已经验证。

## E-009 — 固定电脑状态后的原包复测

**日期：** 2026-09-14

**状态：** Completed; no runtime changes

**证据目录：** `输出/edge-fixed-state-20260914/`

用户固定电脑状态后，复用E-007的8轮同包2×2矩阵、E-008的4轮v3深度ABBA以及E-004的16个历史诊断包正序/倒序，共44轮。所有包沿用已封存EXE、原动作、独立数据副本、启动6秒/收尾2秒及各自旧日志预算；回放期间不编译、不跑完整日志分析或并行性能测试。只读前后显示配置仍为2560×1440、报告59Hz、RTX2080，电源计划均为平衡。新整机快照的whole-harness busy均值17.0%–20.4%，不是纯空载，也没有旧轮同口径负载可作因果对照。

同一冻结分析器联合处理44份新日志和44份旧日志，最近组共同36动作，历史组共同30动作；历史全段35–37动作差异保留。下表为active owner宽高/opacity/contentOpacity实际变化间隔P95，两轮范围，单位ms，只在同一行内比较：

| 组别 | 动作前缀 | 旧 | 新 |
| --- | ---: | ---: | ---: |
| 最近基线，普通详细日志 | 36 | 19.8885–21.1164 | 26.0423–27.9933 |
| Pointer筛选 | 36 | 31.4111–32.3762 | 32.8675–34.0595 |
| 保持Rendering订阅 | 36 | 31.0336–31.2836 | 33.1643–33.5584 |
| 两项组合 | 36 | 31.2175–32.1076 | 32.8970–33.0695 |
| 最近基线，深度日志 | 36 | 27.5150–28.6651 | 26.8514–29.4602 |
| Pointer筛选，深度日志 | 36 | 33.4639–34.0243 | 33.1584–33.9928 |
| PR94 / 899f3cd，历史统一日志 | 30 | 13.3961–13.9881 | 13.3503–13.5617 |
| PR254 / 416a6fd，历史统一日志 | 30 | 33.5846–45.4905 | 32.5355–32.6729 |

数据有变化，但未呈统一、稳定改善。普通详细日志基线CPU累计时间由7.422/7.766秒降为6.891/6.953秒，更新P95却更大；PR254更新P95改善，而PR251由31.8896–32.3259变为33.3822–35.6939。不能以单个旧新数字定因于后台负载。

历史版本更密的应用更新再次复现，但PR94的all-sources包含watchdog；Rendering-only旧P95为30.8322–31.3951、新为26.8241–27.4990。两种口径都不能换算成物理屏幕帧率，也不能仅凭这些数据宣称过去的主观流畅完全是错觉。PR88/PR90仍按原生shape单列，WPF排名为N/A。

新深度组仍有WaitingForResponse/currentOp0及Inactive/Input已排队两种状态形态；`walk-filter-r1`的49.8867ms和52.6344ms样本分别出现这两类观察，订阅保持。最近基线native batch每轮最大耗时仍约16.641–22.645ms。本轮未开EventPipe，不借用旧轮栈分摊新间隔。直接Render handler原行为旧375/375、新374/386，筛选旧132/130、新129/130；回调、遍历与可观察commit时钟均不证明物理呈现。

44轮正常退出、记录/文本零丢弃、退出前无匹配诊断文件，未发现所检查的失败/回退标记；原始四输入前后哈希一致。旧新别名副本日志逐字节hash相同，历史别名保留原生路线识别前缀；全部原始记录、来源清单、实际分析源码/参数、对照表、逐间隔证据、辅助分析失败与修正均保存。冻结分析器9项检查通过。只提交本实验记录，不改运行时、Architecture、Decisions或Unreleased，不重新编译或推送。
