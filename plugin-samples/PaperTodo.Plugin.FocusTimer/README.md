# WPF 原生专注计时器

这是一个不依赖 WebView2、完全由 WPF 控件构成的 PaperTodo 正文插件示例。它展示了：

- 原生按钮、文本和进度条；
- `SaveStateJson` 状态持久化；
- 折叠成胶囊后继续计时；
- 重新打开 PaperTodo 后按 UTC 截止时间恢复；
- `SetDisplayTitle` 同步纸片标题和胶囊；
- 主题、字体与正文缩放适配；
- `Commit`、可见性和销毁生命周期处理。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.FocusTimer\PaperTodo.Plugin.FocusTimer.csproj
```

脚本会安装到：

```text
plugins\sample.focus-timer.native\
```

启动 PaperTodo 后，在纸片正文类型菜单中选择“专注计时器”。原生 DLL 已在当前进程加载后不能热替换；重新构建插件后需要重启 PaperTodo。
