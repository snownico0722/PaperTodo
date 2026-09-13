from pathlib import Path
import csv
import statistics
import sys
root=Path(sys.argv[1]);evidence=Path(sys.argv[2]);doc=root/'doc/EXPERIMENTS.md'
s=doc.read_text(encoding='utf-8-sig')
if '## E-003 ' in s: raise SystemExit('E003 already exists; review rather than overwrite')
anchor='| E-002 |'
lines=s.splitlines();indices=[i for i,l in enumerate(lines) if l.startswith(anchor)]
if len(indices)!=1: raise SystemExit('E002 index missing')
lines.insert(indices[0]+1,'| E-003 | 2026-09-13 | 预览优先、折叠 Shell 延后与正常 WPF 退出 | Completed | — |')
s='\n'.join(lines)
rows=list(csv.DictReader((evidence/'real-app-summary.csv').open(encoding='utf-8-sig')))
app=[]
for variant in ['baseline','target']:
 samples=[r for r in rows if r['variant']==variant and int(r['round'])>0]
 if len(samples)!=3: raise SystemExit('actual App requires three measured samples per variant')
 app.append((variant,statistics.median(float(r['startupToReadyMs']) for r in samples),statistics.median(float(r['primaryExitMs']) for r in samples),'/'.join(r['onExitCompleted'] for r in samples)))
body='''

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
'''
for variant,start,exit_ms,onexit in app:
 body+=f'| {"基线" if variant=="baseline" else "最终组合"} | {start:.2f} ms | {exit_ms:.2f} ms | {onexit} |\n'
body+='''
实际 App 此处仍是普通 Release 多文件构建，不是 E-001 的压缩自包含发布形态；不能混用绝对毫秒数。`Ready` 是主实例接受启动命令的边界，不是物理首帧，也不代表全部后台预热完成。真实多屏/DPI、实际 WebView/第三方插件以及用户机器上的稳定内存和输入长尾尚未测量。

### 最终保留与不采用

保留现有 renderer/cache/STA 和 6 ms Shell 软预算；只改变首轮顺序，把缓存队列本轮完成 Task 暴露给 Shell 启动调用方。DComp/拖拽预热改在更低优先级执行，不取消功能；真实展开继续沿用现有同步 Shell 入口。只抑制“首次 UI 构造、内容未变”的无意义失效。正常退出让 WPF 走完自身生命周期。

不采用：永久不建折叠 Shell（会把每张首次展开成本长期留给用户）、取消 DComp 预热（收益属于成本迁移，影响 hover 首用）、多 UI 线程/共用大 HWND（改动面远大于已证实收益）、删 DPI/布局校验（本轮布局总成本不足 1 ms）、强杀自身或丢最后一次保存。E-002 的批量 Stage/Reveal 仍维持拒绝，不重新引入。

持续集成补充可执行用例：预览先就绪而 Shell 尚未构造、Shell 构造不改变源版本或重复写缓存、真实编辑继续失效、提前展开、隐藏取消预热、预热中退出、带脚本真实退出及最后一次编辑保存。完整产品源码不含临时探针、计时开关或试验 workflow。

下一步应针对真实用户的首帧与首个编辑器约束继续定位；不能把调度后移后的低 StartAsync 数字当作所有可见启动成本已经消除。
'''
doc.write_text(s.rstrip()+body+'\n',encoding='utf-8',newline='\n')
print('E003 retained, with actual App measurements:',app)
