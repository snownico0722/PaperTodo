# 开发与测试工具

`tests/` 负责验证行为；本目录负责运行、采集和测量。不建立第二套产品实现，也不让主程序依赖测试程序集。

| 入口 | 用途 |
| --- | --- |
| `testing/Run-Checks.ps1` | 本地与 CI 共用的行为检查入口；分组、打印结果、失败返回非零 |
| `PaperTodo.MarkdownBenchmarks` | 手动运行 Markdown 解析与编辑采样，不进入默认回归 |
| `PaperTodo.EdgeDiagnostics` | Debug 真进程日志、输入和延迟观察，包含原有 Release 空入口 |

## 常用命令

在仓库根目录的 PowerShell 7 中运行：

```powershell
./tools/testing/Run-Checks.ps1 -List -Group all
./tools/testing/Run-Checks.ps1 -Group regression
```

完整分组、环境要求和行为边界见 [测试说明](../tests/README.md)。脚本不安装环境、不自动修复、不自动重试，也不改变现有 CI 触发条件。

## Markdown 性能采样

```powershell
dotnet run --project tools/PaperTodo.MarkdownBenchmarks -c Release -- --iterations 21
if ($LASTEXITCODE -ne 0) { throw 'Markdown benchmark failed' }
```

工具直接调用当前产品的完整解析入口和 `MarkdownSemanticDocument` 编辑入口，不复制解析阶段的实现。涵盖普通文本、密集 Markdown、围栏变化和密集转义；打印字符数、运行配置、耗时中位数、P95 与当前线程分配量。采样次数可调整，`--help` 查看用法。数字只用于同环境比较，不作为 CI 性能门槛；默认正确性测试不做重复计时或输出性能报告。
