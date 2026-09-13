from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding='utf-8')
heading = '## E-002 — Edge Host 首次呈现与非首帧初始化'
if heading in text:
    raise SystemExit(0)

index_row = '| E-001 | 2026-09-13 | Windows 发布形态：Single-file / Compression / ReadyToRun / Multi-file | Completed | D-036 |'
new_row = '| E-002 | 2026-09-13 | Edge Host 首次呈现：菜单延后与批量首帧 | Completed | — |'
if text.count(index_row) != 1:
    raise SystemExit('E-001 index row missing or duplicated')
text = text.replace(index_row, index_row + '\n' + new_row, 1)

record = r'''

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
'''

path.write_text(text.rstrip() + record + '\n', encoding='utf-8', newline='\n')
