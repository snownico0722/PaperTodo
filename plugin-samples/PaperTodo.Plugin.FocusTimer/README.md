# WPF 原生专注计时器

这是一个不依赖 WebView2、完全由 WPF 控件构成的番茄钟插件。当前按 PaperTodo 协议 2.0 构建；待办读写/监听来自 1.3 数据能力，胶囊展示使用 1.6 宿主模板，边缘快速浏览使用专属 WPF 迷你界面。

## 新增能力

- 在计时器中选择任意待办纸上的未完成项目；
- 待办正文、所属纸片、完成和删除状态会通过 `todos.observe` / `papers.observe` 实时同步；
- 可选择在完整完成一轮专注时，通过 `todos.update` 自动完成关联待办；
- 可在完成后自动选择下一条未完成待办；
- 1.6 胶囊显示运行/暂停状态点、当前待办/倒计时，并遵循“显示进度”设置决定是否显示阶段进度条；胶囊宽度按当前标题和可见组件自动适配；
- 1.8 迷你界面以 `300 × 210 DIP` 显示阶段、倒计时、关联待办和进度，并可直接开始、继续或暂停；
- 旧版计时进度、累计轮数和每日统计会从状态版本 1/2 无损迁移到版本 3。

跳过当前阶段不会完成关联待办。关闭“专注结束后完成待办”时，关联只作为上下文显示，不会修改待办数据。示例代码使用 canonical `context.Workspace` 访问待办，控件主题使用 `context.Body.Controls`。

## 原有能力

- 专注 / 休息阶段切换、暂停、继续、跳过和重置；
- 默认时长、加减步长、每日目标、自动轮转和结束提示音；
- 今日与累计完成轮数；
- UTC 截止时间恢复；
- 折叠成胶囊后继续计时，隐藏后暂停运行时刷新；
- 主题、字体、正文缩放和完整会话生命周期。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.FocusTimer\PaperTodo.Plugin.FocusTimer.csproj
```

安装目录：

```text
plugins\sample.focus-timer.native\
```

原生 DLL 已在当前进程加载后不能热替换；重新构建后需要重启 PaperTodo。
