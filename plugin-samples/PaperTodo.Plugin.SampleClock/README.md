# WPF 原生时钟

这是一个完全由 WPF 控件构成、当前使用协议 2.0 的 PaperTodo 原生主示例。除正文外，它实现协议 1.7 `IPaperCapsuleViewProvider` 和协议 1.8 `IPaperMiniViewProvider`，并持续保留协议 1.6 模板作为启动、拖动交接和失败回退：

- 12 / 24 小时制和秒数显示；
- 多种日期格式、星期和日进度；
- 本地、UTC、北京、东京、伦敦、纽约和洛杉矶时区；
- 时间、日期、时区或自定义胶囊标题；
- 正文缩放、主题和字体适配；
- 1.6 胶囊模板在启用日进度时显示进度环 + 当前标题，并按时间、日期、时区或自定义标题的实际内容自动适配宽度；
- 1.7 普通/贴边胶囊分别持有独立 WPF View，使用完整 `Width × Height` 内容槽，并在主题变化时原地刷新；
- 1.8 边缘快速浏览使用 `300 × 190 DIP` 专属 WPF 迷你时钟，与正文共享时间、设置和主题，但创建独立控件实例；
- 自绘 View 不接管鼠标，点击、右键、拖动、Hover 和关闭仍由 PaperTodo 宿主管理；
- 折叠为胶囊后继续更新，隐藏后停止计时器。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

脚本会安装到：

```text
plugins\sample.clock.native\
```

新插件可立即识别；本次运行已经加载过的原生插件发生修改后，需要重启 PaperTodo。`PaperTodo.Plugin.Abstractions.dll` 由主程序提供，不会被复制进插件目录。
