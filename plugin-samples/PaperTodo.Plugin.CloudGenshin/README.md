# PaperTodo 云·原神实验插件

这是一个**完全独立的协议 2.0 原生正文插件**。

插件使用 `WebView2CompositionControl` 顶层加载：

```text
https://ys.mihoyo.com/cloud/#/
```

## 已包含

- 云·原神网页的直接加载
- 独立且持久化的 WebView2 用户目录
- 登录 Cookie 和站点数据保留
- 米哈游 / HoYoverse 域名内导航
- 非相关外链交给系统浏览器
- 新窗口请求在当前纸片内继续打开
- 按 WebView2 进程类型恢复浏览器或渲染会话
- 纸片隐藏时不销毁网页会话
- 仅在完整正文可交互时通过 `context.Body.SetInputClaims` 占用 Esc 和正文右键菜单
- 外部导航通过 `context.Body.OpenExternal` 交还 PaperTodo 宿主处理
- 胶囊状态点区分加载、运行、重启和错误，并按当前状态文字自动适配宽度；不把 WebView2 塞进胶囊
- 边缘快速浏览使用 `240 × 140 DIP` 的纯 WPF 状态面板；不启动第二个 WebView2，也不迁移完整云游戏画面
- 插件升级时保留 `.runtime` 登录数据

## 放置位置

把整个目录放进 PaperTodo 仓库：

```text
PaperTodo\
└─ plugin-samples\
   └─ PaperTodo.Plugin.CloudGenshin\
```

目录中的项目通过相对路径引用：

```text
PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj
```

## 一键构建并安装

先完全退出 PaperTodo，然后在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.CloudGenshin\PaperTodo.Plugin.CloudGenshin.csproj
```

脚本会：

1. 读取插件旁边的 `plugin.json`；
2. 使用 .NET 10 和 `win-x64` 构建插件；
3. 清理 PDB、文档文件和宿主已提供的共享程序集；
4. 安装到 `plugins\sample.cloudgenshin.native`；
5. 保留旧的 `.runtime`，不清除登录状态。

也可以显式指定仓库目录：

```powershell
powershell -ExecutionPolicy Bypass -File `
  D:\Code\PaperTodo\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -PaperTodoRoot "D:\Code\PaperTodo" `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.CloudGenshin\PaperTodo.Plugin.CloudGenshin.csproj
```

## 在 PaperTodo 中启用

1. 启动 PaperTodo；
2. 打开「设置 → 插件」；
3. 点击重新扫描；
4. 新建或打开一张纸片；
5. 将正文插件切换为「云·原神（实验）」。

原生插件本次运行中一旦加载，覆盖升级后需要重启 PaperTodo。

## 当前实验边界

- 没有修改 PaperTodo，所以没有专门的 16:9、全屏或“游戏输入模式”。
- 云游戏的键盘、鼠标锁定、手柄和音频是否完全正常，需要在真实 PaperTodo 窗口中验证。
- 折叠纸片只会隐藏画面，不会主动退出云游戏；可能继续计时、联网和播放声音。
- 仅允许 `mihoyo.com`、`hoyoverse.com`、`hoyolab.com` 及其子域在纸片内顶层导航。若官方登录流程新增其他顶层域名，会被外部浏览器打开，需要再补白名单。

## 清除登录状态

完全退出 PaperTodo 后，删除：

```text
plugins\sample.cloudgenshin.native\.runtime\webview2
```
