# WPF 原生时钟

这是一个完全由 WPF 控件构成的 PaperTodo 正文插件示例。它不再只是显示当前时间，还展示了宿主设置、主题和后台生命周期如何组合成一个完整插件：

- 12 / 24 小时制和秒数显示；
- 多种日期格式、星期和日进度；
- 本地、UTC、北京、东京、伦敦、纽约和洛杉矶时区；
- 时间、日期、时区或自定义胶囊标题；
- 正文缩放、主题和字体适配；
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
