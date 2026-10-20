# 行为检查

本目录保护结果和可观察行为，不保护某一版内部写法。复用现有 `dotnet run` 检查项目，不切换框架、不按行数指标删测试，也不为每个 Bug 新建项目或 CI。

## 运行入口

项目、配置和分组只在 `tools/testing/Run-Checks.ps1` 的 `$checks` 中登记。用 `-List` 查看当前清单，本文不重复维护项目名和用例数量。

```powershell
# 查看所有已登记入口，包括材质与插件示例；不执行测试、不要求 Windows。
./tools/testing/Run-Checks.ps1 -Group all -List

# 局部修改：只运行所选组中明确相关的项目，名称与 -List 一致。
./tools/testing/Run-Checks.ps1 -Group regression -Project MarkdownSemanticChecks,MarkdownEditingChecks
./tools/testing/Run-Checks.ps1 -Group regression -Project LifecycleChecks

# 按行为范围验证。
./tools/testing/Run-Checks.ps1 -Group regression
./tools/testing/Run-Checks.ps1 -Group plugins
./tools/testing/Run-Checks.ps1 -Group persistence
./tools/testing/Run-Checks.ps1 -Group diagnostics
./tools/testing/Run-Checks.ps1 -Group visual

# 全部已登记的正确性检查；不包含性能基准工具。
./tools/testing/Run-Checks.ps1 -Group all
```

`-Project` 只缩小 `-Group` 的范围，不偷偷加入其他组。未知名称或选错分组会报错，不能以零项执行冒充通过。`-Group all -Project EdgeTitleChecks` 会执行其已登记的 Release 与 Debug 配置；只需某个配置时选择对应组。插件示例可用 `-Group plugins -Project plugin-samples` 单独运行。

| 分组 | 何时运行 |
| --- | --- |
| regression | 编辑、撤销、排版、窗口、输入、焦点、生命周期及跨线程行为 |
| plugins | 公开插件合同、设置、桥接及示例源码与分发产物一致性 |
| persistence | 保存、失败回滚、恢复、最后编辑与原始数据保留 |
| diagnostics | Debug 采集器、输入观察及 Release 空入口行为 |
| visual | Windows 材质状态、WPF 离屏像素、Aero 反光、窗口生命周期；使用既有 MicaChecks，不再对 DWM 最终桌面截图做自动验收 |

实际执行需要 Windows、.NET 10 SDK、PowerShell 7 和固定的 `vendor/wpf-notifyicon` 子模块；插件组需要 Node.js。窗口、输入和渲染检查需要可用的 Windows/WPF 桌面，不能并行争用鼠标或前台窗口。数据故障注入必须使用测试隔离目录，不指向日常数据。

Markdown 编辑检查中的图片用例默认只使用内存 `DataObject` 和隔离 LMDB，覆盖 PNG 透明像素、格式优先级、旧 Bitmap 截图兼容、单步撤销／重做、数据库重开，以及添加图片格式时保留原有文本和富文本数据。实际系统剪贴板往返需单独运行 `dotnet run --project tests/PaperTodo.MarkdownEditingChecks -c Release -- --clipboard`。此模式会用合成图片替换当前剪贴板，必须与其他剪贴板操作串行；它检查复制方法返回后 PNG 与 Bitmap 都可读取，单图选区复制保留 Markdown、HTML 和 RTF，以及写入进程退出后 PNG 像素仍完整。

因共享 Windows CI 的 WPF/DWM 桌面合成截图可在未修改代码的基线上随机漏掉真实 WPF 内容，最终桌面截图不再属于自动化验收（D-057）。本组仍执行真实 Windows 窗口与材质状态、WPF 离屏像素、失效清理、Aero 反光等确定性检查；它们不等于真实 Windows 11／HDR／多屏外观验收。后者只在实际需要发布材质变更时人工确认，不改用其他 CI 机器跑同一组截图。命令行的 `--no-raster` 仍可跳过部分 WPF 离屏图像检查，但不能视为相关行为已通过。

现有 CI 调用同一入口，保持触发条件和配置差异；不因提供 `all` 就把它放进每次 CI。执行器顺序运行并汇总原始失败与耗时，不安装环境、不自动修复或重试、不把未执行项目报告为通过。插件示例检查会重建仓库内原生产物并检查 `plugins/` 差异，这是构建验证，不是只读命令，也不会安装到日常数据目录。

## 保留、增补与退出

先找已有用例。同一行为在一个主用例中验证，只有不同层确实保护不同失败边界时才保留两层；不能仅因名称相近、用了反射或来自旧 PR 就删除。新增回归先放进所属现有项目，优先覆盖真实编辑结果、保存失败、公开兼容、输入命中、窗口交接、取消和过期回调。

不以源码字符串、私有字段/类型存在、固定 helper 名、控件精确搬动次数、缓存命中/构建次数、对象必须同一引用或增量窗口长度定义正确性。焦点、选区、撤销、数据不丢和资源释放仍须验证；操作后读取当前控件检查结果，不把某种复用算法当合同。公开 API 签名本身是兼容边界，不属于可随意删除的实现断言。

减少参数全排列前，说明代表用例为何覆盖相同分支；边界不同就保留。计时器用于控制模拟时间、防挂起或证明退出不阻塞时不是跑分。真实像素、完整解析对照及取消后的资源释放也不是可一概删除的性能测试。

一次性探针验证后删除，证据留在 PR；高重复风险的最小回归融入已有项目，不保留专用脚手架。清理无效断言时连同无用辅助代码一起删除，不转移到“可选测试”中继续维护。性能和内存采样只在 `tools/` 按需运行，不加入正确性门槛；工具入口见 [工具说明](../tools/README.md)。

每次调整在 PR 里说明删掉了什么、哪些行为仍被覆盖、实际执行了哪些命令及其结果。先验证受影响项目；持久化、公开兼容、线程和窗口交接按实际风险扩展。既有 CI 验收仍须通过，不以重跑通过、延长等待或更换 CI 机器掩盖真实产品缺陷。对已证明在未修改基线中也不可靠的 DWM 最终截图，按 D-057 删除无法作为正确性门槛的整套检查及辅助代码；不将其改名为可选测试，也不把离屏渲染宣称为真实桌面通过。
