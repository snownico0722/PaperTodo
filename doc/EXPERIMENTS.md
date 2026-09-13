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
| E-003 | 2026-09-13 | 预览优先、折叠 Shell 延后与正常 WPF 退出 | Completed | — |

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

## E-003 — 预览优先、折叠 Shell 延后与正常 WPF 退出

**日期：** 2026-09-13
**状态：** Completed
**基线：** #254 `416a6fdbf931612ffdb7f066149001c4100a9a4c`（E-002 menu-only）。

### 方法与边界

Windows Server 2025 / .NET SDK 10.0.401 / runtime 10.0.12，同一 job 内以新进程交错运行对照，正反顺序轮换。调度比较每模式 6 次，round 0 保留在原始证据但不进入下表，余下 5 次取中位数；早展开额外每模式 3 次。不是清空 Windows 文件缓存后的 SSD 冷启动。

生命周期 fixture 不含 EXE/CLR 入口；下表启动时间从 controller 构造结束计算。`Rendering observed` 是全部 Host 满足可见条件后观察到的 WPF Rendering 回调，不证明物理像素已上屏。`cache.initialReady` 在第 10 份 artifact 写入时直接打点，`shell.allBuilt` 在最后一个 Shell 完成时打点；它们相互独立，不再用“先等 Shell，再轮询缓存”的旧 `preloadReadyMs` 冒充预览最早可用时间。

每个 scope 记录墙钟、起点、线程，异步 scope 包含等待；嵌套 scope 包含子调用。不能把父子时间相加，也不能把不同运行/不同指标的中位数相减当作精确 CPU 分账。没有用户插件、真实多屏或物理显示器扫描测量。

### 先定位，再选方案

- 10 个 Host 的 `RefreshNativeMetricsLayout` 累计约 0.6 ms；不是先前猜测的数百毫秒。不删 DPI/layout/placement 校验。
- `CreateTrayIcon` 首用约 159 ms，混合 WPF 菜单壳、Hardcodet、图标与属性初始化；移动这一工作也可能只迁移 WPF 首用成本，本轮不改托盘 ownership。
- 一次性 DComp lightweight prewarm 典型约 159～179 ms，另有约 12 ms 拖拽预热。它们排在原来的 ApplicationIdle 恢复续体前，解释了 Rendering 已观察到之后仍有约 250～300 ms 的恢复尾部。
- 1 张 Shell 的 `EnsureShellBuilt` 约 190 ms，10 张累计约 244 ms；首个编辑器初始化占大头，不是每张固定消耗几十毫秒。
- 普通退出的保存和 owned resource 清理之后，`Environment.Exit` 至外部观察到进程结束仍约 330 ms；不能靠省略保存来解决这个尾部。

分段证据为 run `34733914974`（4 轮、1/10 张、脚本与退出）和 `34734228116`（3 轮、细分 DComp/拖拽/托盘/退出事件）。scope 是带探针结果，只用于定位；下面的同机对照才用于判断取舍。

### 调度隔离实验与最终结果

| 模式 | Rendering observed | StartAsync 返回 | 第 10 份缓存完成 | 第 10 个 Shell 完成 | 初始化缓存写入次数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 基线 | 482.64 ms | 778.71 ms | 1201.09 ms | 1080.48 ms | 10 |
| 预览先于 Shell | 467.95 ms | 755.16 ms | 974.91 ms | 1138.79 ms | 10 |
| **预览优先 + 可选预热后移** | **470.20 ms** | **501.00 ms** | **939.23 ms** | **1100.89 ms** | **10** |

最终组合把缓存就绪提前约 **261.86 ms / 21.8%**，完整 Shell 就绪推后约 **20.41 ms**。StartAsync 返回提前约 277.71 ms，意味着启动命令转发等后续工作能更早继续；**Rendering observed 只差约 12 ms，不能宣称胶囊首帧因此快了 278 ms**。3 组 cache 初次完成原始样本（round 1～5）为：

- baseline：1182.07 / 1206.00 / 1197.74 / 1205.98 / 1201.09 ms；
- preview：974.91 / 973.99 / 1012.43 / 903.70 / 989.02 ms；
- combined：1001.95 / 939.23 / 920.54 / 889.89 / 1031.62 ms。

更早的 run `34734324679` 同时比较了 idle-only。只把可选预热降到 SystemIdle 能让 StartAsync 更早返回，但没有提前 Rendering 或缓存就绪，故不把这种移位独立宣传成首帧优化。该轮最初的 preview-first 还暴露重复缓存：第 10 份缓存先生成，随后 Shell 初始化标题再次作废，最终写入 20 份并多等一次 500 ms。最终修正首次 `BuildTopBar -> RefreshPaperTitle` 和初次胶囊标签构建的失效语义；实际内容编辑、文本规范化和资源变化仍失效，不全面关闭缓存验证。

**代价：**提前展开尚未建 Shell 的纸片，现有 `EnsureShellBuilt` 当场接管。早展开专项中 baseline 已建 Shell，调用中位约 124.92 ms；组合方案明确尚未建 Shell，调用中位约 217.49 ms，约多 92.57 ms。这个测试覆盖的是展开调用，不是动画结束或点击到物理显示。选择延后预建而非永不预建，保留最终全部 Shell；不为这段短暂首用窗口再增加双编辑器、预估器或并行 UI 线程。

修正版调度 run：`34734678622`，tools commit `c268051a0217b7279094ecb891684ddfdac4acbb`，artifact `e003-scheduling-refined`。检查覆盖全部 10 份缓存、Shell 后不重复预热、编辑后再生成与实际早展开。

### 退出对照

同机正反交错，每场景每模式 6 次，round 0 不计入中位数。计时从主实例 `Exit` 请求到外部父进程观察到主进程真正结束，均保留最后一次编辑同步保存、界面撤下、插件/图片清理及脚本关闭，不使用 Kill 自身或跳过持久化。

| 模式 | 5 张纸片正常退出 | 5 张纸片 + 3 个脚本子进程 |
| --- | ---: | ---: |
| Shutdown 后立即 Environment.Exit | 401.17 ms | 638.39 ms |
| **正常 WPF Shutdown/Dispatcher 退出** | **104.64 ms** | **336.40 ms** |

脚本 fixture 故意不响应 stdin EOF，仍执行原有 250 ms graceful stop 上限；不为漂亮数字删掉正常结束机会。普通退出少约 296.53 ms，带脚本少约 301.98 ms。强制 Exit 版本未触发 WPF Exit 事件；正常版本可以执行 WPF Exit、Dispatcher shutdown 并返回 Application.Run。本轮只改正常主实例退出；崩溃退出和次实例命令转发退出不改。

第一次 natural-exit 探针已正常结束，但 test Main 依赖一个被 Dispatcher shutdown 取消的 await 续体来把返回码从 1 改成 0，造成假失败。修正为失败在 catch 显式置 1，成功不依赖该续体，并保留子进程返回码、最后编辑保存和可见状态断言。正式对照 run `34734637521` / tools commit `fe205dda9f9d10ae021dbbc2a3bfdd352f5fad13`，artifact `e003-exit-comparison`，24 个进程样本。

### 真实 App 补充验证

另外运行真实 `PaperTodo.exe`（不是只有 controller 的 fixture），比较基线与最终组合。每种 4 次，首轮不计，后 3 次中位；每次保持运行 4 秒以经过 telemetry bootstrap，再由第二实例 `--exit` 触发退出。主实例 Exit 入口独立打点，故下表 Exit 不包含第二个 EXE 的 CLR 启动和转发延迟。每次复用同一临时数据目录重新启动，验证 Mutex/pipe 已释放；验证 10 张纸内容与 IsVisible 保持，并要求最终版本的 `App.OnExit`（包括 base.Exit 回调）确实完成。

| 模式 | 外部启动到命令 Ready | 主实例 Exit 到进程结束 | App.OnExit 完成 |
| --- | ---: | ---: | --- |
| 基线 | 1148.06 ms | 415.27 ms | False/False/False |
| 最终组合 | 908.68 ms | 125.36 ms | True/True/True |

实际 App 此处仍是普通 Release 多文件构建，不是 E-001 的压缩自包含发布形态；不能混用绝对毫秒数。`Ready` 是主实例接受启动命令的边界，不是物理首帧，也不代表全部后台预热完成。真实多屏/DPI、实际 WebView/第三方插件以及用户机器上的稳定内存和输入长尾尚未测量。

### 最终保留与不采用

保留现有 renderer/cache/STA 和 6 ms Shell 软预算；只改变首轮顺序，把缓存队列本轮完成 Task 暴露给 Shell 启动调用方。DComp/拖拽预热改在更低优先级执行，不取消功能；真实展开继续沿用现有同步 Shell 入口。只抑制“首次 UI 构造、内容未变”的无意义失效。正常退出让 WPF 走完自身生命周期。

不采用：永久不建折叠 Shell（会把每张首次展开成本长期留给用户）、取消 DComp 预热（收益属于成本迁移，影响 hover 首用）、多 UI 线程/共用大 HWND（改动面远大于已证实收益）、删 DPI/布局校验（本轮布局总成本不足 1 ms）、强杀自身或丢最后一次保存。E-002 的批量 Stage/Reveal 仍维持拒绝，不重新引入。

持续集成补充可执行用例：预览先就绪而 Shell 尚未构造、Shell 构造不改变源版本或重复写缓存、真实编辑继续失效、提前展开、隐藏取消预热、预热中退出、带脚本真实退出及最后一次编辑保存。完整产品源码不含临时探针、计时开关或试验 workflow。

下一步应针对真实用户的首帧与首个编辑器约束继续定位；不能把调度后移后的低 StartAsync 数字当作所有可见启动成本已经消除。


### 复核资料与落盘验证

- Microsoft [Application.Shutdown](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.shutdown?view=windowsdesktop-10.0)：正常应用退出及 Exit 生命周期。
- Microsoft [Environment.Exit](https://learn.microsoft.com/en-us/dotnet/api/system.environment.exit?view=net-10.0)：与正常返回不同的强制进程退出语义；不是所有程序都会有本实验相同的时间差。
- Microsoft [DispatcherPriority](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority?view=windowsdesktop-10.0)：ApplicationIdle/SystemIdle 是相对队列优先级，不表示 CPU 空闲，也不使单次 UI 构建可抢占。

最终 Windows 验证 run `34735095310` 的 Release 构建（0 警告/0 错误）、8 组 Release 回归、Debug EdgePreview 和真实 App A/B 均通过；之后仅实验文档两处 Markdown 行尾空格触发 `git diff --check` 失败，未执行推送。落盘流程复用其 artifact `10310961672` 中的原始受测源代码补丁并验证 SHA-256，仅修正文档格式，不替换受测代码。真实 App 对照表来自同一 artifact 的 `real-app-summary.csv`。

普通退出不再强制终止潜在的第三方前台线程；本轮确认了 PaperTodo 自身线程、脚本子进程、正常 OnExit 及重复启动，未覆盖任意第三方插件自建的前台线程。真实 WebView/第三方插件组合仍需针对性验证，不把隔离用例的通过扩大成所有插件均已实测。
