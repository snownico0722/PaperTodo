# Edge 预览行为检查

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release
if ($LASTEXITCODE -ne 0) { throw 'Edge preview checks failed' }
```

保留原生链接的真实命中、裁切、键盘与焦点行为；独立手写 WPF 图像参考；六组代表性的冷/热显示一致性；取消、版本失效、主题/DPI 变化和 worker 阻塞时宿主动画仍能结束。

不再验证预加载固定档位、固定读取次数、缓存命中计数或内部对象复用方式。普通显示与高风险交接由可执行行为覆盖，不把历史每个细小 bug 都永久扩成一个测试项目。

性能、内存采样及像素导出已迁到 [DesktopBenchmarks](../../tools/PaperTodo.DesktopBenchmarks/README.md)，历史采样见该工具的 `PRELOAD-HISTORY.md`。测试程序不再接受性能/导出参数，错误参数返回失败而不是偷偷运行全部检查。架构边界以 [ARCHITECTURE](../../doc/ARCHITECTURE.md) 为准。
