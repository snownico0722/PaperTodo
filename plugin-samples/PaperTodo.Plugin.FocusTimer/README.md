# WPF 原生专注计时器

这是一个不依赖 WebView2、完全由 WPF 控件构成的完整番茄钟示例。它展示了：

- 专注 / 休息阶段切换、暂停、继续、跳过和重置；
- 可设置默认时长、加减步长、每日目标和结束提示音；
- 可选自动开始下一阶段；
- 今日与累计完成轮数；
- `SaveStateJson` 持久化和 UTC 截止时间恢复；
- 折叠成胶囊后继续计时，隐藏后暂停运行时刷新；
- `SetDisplayTitle` 同步纸片标题和胶囊；
- 主题、字体、正文缩放和完整会话生命周期。

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

启动 PaperTodo 后，在笔记纸正文类型菜单中选择“专注计时器”。原生 DLL 已在当前进程加载后不能热替换；重新构建后需要重启 PaperTodo。
