---
name: papertodo-plugin-creator
description: 为 PaperTodo 制作、创建或开发可安装的 Web 或 Native 插件；用于“做个番茄钟插件”等桌面纸片微应用需求。
---

# 制作 PaperTodo 插件

把用户的需求做成 PaperTodo 可加载的插件，交付源码和可安装的插件目录。尊重用户指定的实现方式、目标目录和已有授权；普通待办、笔记编辑以及其他产品的插件不属于本 Skill。

## 开发资料与目标位置

- [插件开发手册](references/plugin-development.md) 随本 Skill 一起安装，涵盖当前 Protocol 2.1 的 API、状态、设置、胶囊、Mini、Runtime、构建与示例。按任务读取对应章节，不必全文加载。
- 手册来自 PaperTodo 仓库的 `plugin-samples/README.md`，由构建直接复制，不单独维护。手册中指向仓库其他文件的相对链接以仓库 `plugin-samples/` 为基准；本地只有安装包时，这些源码不一定存在。
- 有本地仓库时，先读根目录 `AGENTS.md`，以该版本的 `plugin-samples/README.md` 和 `PaperTodo.Plugin.Abstractions/` 为具体合同。涉及宿主内部结构再按仓库路由读取 `doc/ARCHITECTURE.md`，不用先通读宿主源码。
- 需要源码且本地没有时，使用 [PaperTodo 仓库](https://github.com/snownico0722/PaperTodo) 中与目标宿主兼容的版本。不要假设安装目录就是源码仓库。
- 本 Skill 位于 `<PaperTodo 安装目录>/plugins/tools.codex-cli-bridge.native/skills/papertodo-plugin-creator/` 时，可由这个路径定位正在使用的插件安装目录。若来自源码或被单独复制，先核实目标 PaperTodo 的位置。

## 选择实现

| 需求 | 选择与资料 |
| --- | --- |
| HTML/CSS/JS 小界面、轻量交互 | Web；手册第 1.1、10 节，无需编译 |
| WPF 控件、本机进程或原生依赖 | Native；手册第 1.2、1.3、11 节，.NET 10 + WPF |
| 折叠后计时、后台任务、全局按钮 | provider Runtime；第 3.3、4.6、7 节 |
| 读取或修改其他纸片、Todo、Note | Workspace + 对应 manifest permissions；第 6 节 |
| 胶囊、边缘悬停视图 | capsule / dedicated Mini；第 8、9 节 |
| 保存、恢复、设置 | 第 5 节；不要自行读写宿主 `data.json` |

从最接近的示例取所需部分。普通 Web 小界面不用额外引入 Native、Runtime 或前端构建工具。

## 最小 Web 起点

新插件使用独立 ID，例如 `com.example.hello`，最终目录名与 ID 一致。以下两个文件即可构成一个无后台状态的起点；按实际需求改名和补充交互。

`plugin.json`：

```json
{
  "kind": "web",
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "apiVersion": "2.1",
  "stateVersion": 1,
  "entry": "web/index.html"
}
```

`web/index.html`：

```html
<!doctype html>
<html lang="zh-CN">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
  body { margin: 0; padding: 16px; font: 14px system-ui; }
</style>
<p>Hello PaperTodo</p>
<script>
  papertodo.paper.setHeaderText('Hello');
  papertodo.paper.setCapsulePresentation({
    preferredWidth: 0,
    plainText: 'Hello',
    components: [{ kind: 'text', text: 'Hello', fill: true }]
  });
</script>
</html>
```

`window.papertodo` 由宿主注入本地顶层页面；普通浏览器不具备这条 bridge。需要状态和主题时按手册处理 `initialize`、`stateChanged`、`themeChanged`，并参考对应 Web 示例，避免自行猜测事件结构。

## 开发时保持的边界

- `plugin.json` 是元数据来源，当前协议为 `apiVersion: "2.1"`；不要沿用旧插件协议或杜撰能力名。
- Body / Mini 会被回收；需要持续工作的逻辑放在一个 provider Runtime 中。声明 `runtime` 后，Native 实现 `IPaperPluginRuntimeProvider`，Web 提供 Runtime 页面。Runtime 在至少存在一张真实插件纸片时才启动；需要自动启用时再配置 `startupPaper`。
- Runtime 拥有长期业务状态及纸片标题/胶囊展示，Body / Mini 通过消息提交用户操作。简单无 Runtime 插件可直接使用自身 Paper 与 frontend state。
- 用户状态变化后及时交给宿主保存。Runtime state、per-paper frontend state、settings 各有自己的用途；不要复制第二份主状态。升级保留已有用户内容，不把读取失败当作空状态回写。
- 使用 Workspace 的公开 API 和相应 permissions 读写其他纸片；需要界面贡献时使用 Top Bar / Todo action descriptor，不接管宿主窗口或边缘队列。
- Native WPF 元素不能同时属于多个视图；专属 Mini 使用独立视图。后台线程修改 WPF 控件时回到对应 Dispatcher。

## 构建、安装与验证

Web：源码保存在仓库的 `plugin-samples/<项目名>/`（独立开发时使用用户的工作目录），把 `plugin.json`、`web/` 和必要资源放进目标 `plugins/<id>/`。不需要 .NET 编译。

Native：参考手册第 1.2 节创建 `net10.0-windows` / `UseWPF` 项目，引用匹配版本的 `PaperTodo.Plugin.Abstractions`；入口 DLL 包含一个公开、非抽象、可无参构造的 `IPaperBodyPlugin` 实现。在仓库根目录使用：

```powershell
powershell -ExecutionPolicy Bypass -File .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.Hello\PaperTodo.Plugin.Hello.csproj
```

把示例项目路径换成实际项目。脚本生成 Release / win-x64 插件到仓库的 `plugins/<id>/`，它不等于其他位置正在运行的 PaperTodo 安装目录；需要部署到该目录时再复制完整插件文件。私有资源通过项目的 `CopyToPublishDirectory` 随包携带。插件包不重复分发宿主提供的共享程序集，也不包含无用 PDB/XML。

安装或更新插件文件后，需要重启 PaperTodo 生效；替换 Native DLL 前需要退出进程。保留现有 `plugins/data/<id>.json` 及插件 `.runtime/` 用户数据。除非用户已要求立即重启，不替用户强制结束正在运行的 PaperTodo。

按实际功能验证 manifest 与入口、构建或 JavaScript 语法、关键交互和状态恢复。声明 Runtime 时检查折叠后的行为；实现 Mini 时检查悬停视图。只有真实运行过的检查才报告通过；缺少 Windows 或目标宿主时明确剩余手测项。

交付时说明插件功能、源码及最终插件目录、安装方法、验证结果和需要重启的时机。仓库工作遵循其提交和 CI 规则；不要为制作一个插件改动无关宿主功能。
