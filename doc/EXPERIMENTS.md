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
