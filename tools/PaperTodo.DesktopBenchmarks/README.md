# 桌面性能与导出工具

本项目直接引用当前 `PaperTodo.csproj`，不引用测试程序集，也不复制产品 renderer。需要 Windows、.NET 10 SDK 和可用的 WPF 桌面。所有性能命令只手动运行，不进入默认回归。

```powershell
# 验证工具入口，不据此评价性能。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --smoke
if ($LASTEXITCODE -ne 0) { throw 'Desktop measurement smoke failed' }

# 实际 host/presenter 的首次准备、动画与开放输入。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preview

# 冷/热准备的正反顺序样本。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preload
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --preload --reverse

# 托管内存、行内分配，以及隔离进程的启动/退出阶段。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --memory
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --inline
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --lifecycle

# 将原始 RGBA 像素写入指定的实验目录；会覆盖同名导出文件。
dotnet run --project tools/PaperTodo.DesktopBenchmarks -c Release -- --export output/edge-pixels
```

`--preview` 每组 24 次，前三次预热不进入汇总；`--preload` 每种顺序 9 次，输出原始样本，比较时须按一致口径剔除预热。`--lifecycle` 对 1/5/10/25 张纸片各运行三个独立进程，交替数量顺序；只复制构建产物到新临时目录，不读取或修改日常 PaperTodo 数据，结束后删除本次目录。不保留旧 `--baseline` 跳过行为保障的开关。

布局就绪、开放输入、动画结束与物理屏幕显示不是同一个时刻；这里没有 GPU 呈现或硬件输入延迟测量。UI 当前线程分配不含 worker；强制 GC 后的托管堆增量不是工作集或原生/GPU 内存。数字只适合同机、同构建、同场景比较，不作为 CI 的毫秒或零分配门槛。历史结果见 [PRELOAD-HISTORY](PRELOAD-HISTORY.md)。
