# Edge 诊断工具源码

此目录保存真实 PaperTodo 进程使用的可选诊断采集器；不是另一套渲染器，也不是正式产品功能。
当前运行时边界以 [ARCHITECTURE](../../doc/ARCHITECTURE.md) 为准。实验数据和历史取舍仍分别归属 `doc/EXPERIMENTS.md`、`doc/DECISIONS.md`。

## 编译边界

- `PaperTodo.csproj` 从默认源码扫描中排除 `tools/**`，仅在 Debug 配置显式编译本目录的五个 `.cs` 文件。普通 Release 不包含这些采集实现。
- `src/EdgeDiagnosticJournal.cs` 只提供 Release 空入口，没有缓冲区、观察器、计时器或文件输出。真实运行链中的 Debug 埋点保留在业务调用位置。
- 两个诊断检查项目直接引用这里的原始源码，不引用 Release 空入口。主程序不引用测试程序集。
- 这里的采集实现按 #256 的 `bd921038d73f3b0d5775a72342811393861a8c42` 原始 Git blob 搬迁，不改事件结构或采集逻辑。
- 内存模式同时接管 edge performance、interaction 和 `PaperWindow.TraceNoteRender` 的 Markdown 日志；未启用 memory 时保留原来的日志行为。

## Windows 实机采集

使用已安装仓库所需 .NET SDK 的 PowerShell，从仓库根目录执行；先正常退出已运行的 PaperTodo，避免单实例转发到旧进程：

```powershell
$diagnosticOutput = Join-Path $env:TEMP 'PaperTodo-edge-diagnostics-build'
dotnet build PaperTodo.csproj -c Debug -o $diagnosticOutput -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw '诊断构建失败' }
$previousMode = $env:PAPERTODO_EDGE_DIAGNOSTICS
$env:PAPERTODO_EDGE_DIAGNOSTICS = 'memory'
try {
    Start-Process -FilePath (Join-Path $diagnosticOutput 'PaperTodo.exe') -Wait
} finally {
    $env:PAPERTODO_EDGE_DIAGNOSTICS = $previousMode
}
```

正常退出 PaperTodo 后再读取日志。内存采集按已有机制有界保存，正常退出落盘；不通过诊断回调推进动画。更深的 native/Dispatcher 观察仍使用源码中已有的独立开关，默认不额外打开。这里的无提前写盘仅针对被 Journal 接管的诊断通道，不表示用户数据保存或其他日志也被禁用。

## 行为检查

```powershell
dotnet run --project tests/PaperTodo.EdgeDiagnosticJournalChecks -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Journal 检查失败' }
dotnet run --project tests/PaperTodo.EdgeDiagnosticJournalChecks -c Release
if ($LASTEXITCODE -ne 0) { throw 'Journal Release 检查失败' }
dotnet run --project tests/PaperTodo.EdgeLatencyObservationChecks -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Latency observer 检查失败' }
```

Journal 的缓冲、边界和并发检查在两个配置运行；子进程检查在 Debug 验证采集/正常退出/ProcessExit 回退，在 Release 验证即使设置 memory 也不启用采集或创建输出目录。Release 测试项目直接编译工具源码供纯缓冲检查，不等于产品 Release 包含采集器。

Latency 检查需要 Windows/WPF 桌面。上述命令是执行入口，不代表实机验收已经通过。
