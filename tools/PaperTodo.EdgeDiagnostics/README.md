# Edge 诊断工具源码

本目录保存真实 PaperTodo 进程使用的可选诊断采集器，不是另一套渲染器或正式产品功能。当前运行时边界以 [ARCHITECTURE](../../doc/ARCHITECTURE.md) 为准；实验记录归属 `doc/EXPERIMENTS.md`。

## 编译边界

- 主项目排除 `tools/**` 的默认源码扫描，再显式接入此目录。
- 根目录的 Journal、native/Dispatcher/message 观察器及输入观察器只在 Debug 主程序中编译。
- `EntryPoints/` 集中保存业务调用所需的入口、格式化辅助方法及 Release 空入口。性能采集和冷启动诊断仍使用原有 `#if DEBUG`，不改变日志、计时、故障注入或 Release 行为。
- `EntryPoints/EdgeDiagnosticJournal.Release.cs` 仅在非 Debug 配置提供空入口，不分配缓冲区、观察器、计时器或输出文件。
- 原 `src` 中独立的 Edge 诊断文件已整体迁到这里；业务调用处的埋点保留，不借目录整理改变交互流程。主程序不引用测试程序集。
- 两个诊断检查项目直接引用所需的工具源码。它们的 Release 测试配置不代表产品 Release 包含采集器。

## Windows 实机采集

在已安装仓库所需 .NET SDK 的 PowerShell 中，从仓库根目录执行。先正常退出已运行的 PaperTodo，避免单实例转发到旧进程。

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

正常退出后再读取日志。内存模式接管 edge performance、interaction 和 `PaperWindow.TraceNoteRender` 的 Markdown 日志；未启用 memory 时保留原有行为。内存采集有界保存，正常退出落盘，不通过诊断回调推进动画。更深的 native/Dispatcher 观察仍使用已有独立开关，默认不额外打开。上述说明仅针对 Journal 接管的诊断通道，不表示用户数据保存或其他日志被禁用。

## 行为检查

```powershell
dotnet run --project tests/PaperTodo.EdgeDiagnosticJournalChecks -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Journal 检查失败' }
dotnet run --project tests/PaperTodo.EdgeDiagnosticJournalChecks -c Release
if ($LASTEXITCODE -ne 0) { throw 'Journal Release 检查失败' }
dotnet run --project tests/PaperTodo.EdgeLatencyObservationChecks -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Latency observer 检查失败' }
```

Journal 的缓冲、边界和并发检查运行两个配置；子进程检查在 Debug 验证采集与退出，在 Release 验证即使设置 memory 也不启用采集或创建目录。Latency 检查需要 Windows/WPF 桌面。执行入口不等于实机验收结果。
