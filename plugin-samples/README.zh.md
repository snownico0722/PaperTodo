# PaperTodo 插件开发手册

**语言：简体中文 | [English](README.md)**

> **相关导航**：[项目主页](../README.zh.md) · [用户手册](../doc/USER_GUIDE.md) · [版本更新日志](../CHANGELOG.zh.md)

本文面向希望为 PaperTodo 编写扩展插件的开发者，系统介绍当前插件协议的运行模型、`plugin.json` 配置、API 边界、构建方式与官方示例。

> [!WARNING]
> **信任边界：PaperTodo 不为插件提供安全沙箱。** 无论是 Web 插件还是 Native 插件，运行时都拥有与当前登录用户一致的系统权限。请仅安装来源可信的插件，开发时也请妥善处理外部输入与本地进程调用。

---

## 协议版本与兼容规则

新开发的插件请统一声明当前最新协议版本：

```json
"apiVersion": "2.3"
```

`apiVersion` 表示**该插件运行所需的最低宿主 API 版本**，而不是插件自身的版本号。只有满足以下条件时，PaperTodo 才会加载插件：

```text
2.1 (最低兼容版本) <= plugin.apiVersion <= 2.3 (当前宿主版本)
```

也就是说，新版 PaperTodo 可以直接运行面向 `2.1` 或 `2.2` 编写的旧插件；而旧版宿主遇到声明更高 `apiVersion` 的新插件时会明确拒绝加载，避免运行期缺接口报错。实验过渡期的 `2.0` 及早期 `1.x` 插件已不再兼容。

| 协议版本 | 当前状态 | 核心能力演进 |
| :--- | :--- | :--- |
| **1.x** | 已退役 | 早期正文扩展协议（1.2 独立插件数据目录，1.8 引入宿主托管的 Edge Mini） |
| **2.0** | 已退役 | 实验过渡版本，后续被收敛后的单后台 Runtime 模型取代 |
| **2.1** | 最低兼容 | 收敛为每插件单一 Provider Runtime + 轻量 Body/Mini 前端；提供受控 Workspace 数据读写、顶栏与待办行按钮、全局快捷键、右键菜单、笔记图片读取与临时弹窗 |
| **2.2** | 继续兼容 | 新增跨纸片显隐/收展控制、公共软件设置 API（`settings.read/update`）、设置页 `action` 按钮、`startupPaper.presentation: "hidden"`、待办提醒 5 秒前置事件、Web Runtime 事件订阅及 `data.json` 显式重载/三方合并 |
| **2.3** | **当前最新** | 顶栏动作新增 `MenuOnly` / `menuOnly`，支持将操作仅放入插件菜单而不占用顶栏按钮位 |

**版本号演进约定**：当前次版本正式发布后，只要后续新增了向后兼容、插件可感知的契约（新字段、新权限、新事件或新公开接口），就会递增 minor 版本号（尚未发布前的多项兼容增强会合并进同一个次版本）；纯宿主内部重构与问题修复不升协议号；破坏现有兼容契约才会升 major 版本号。

编译期公开接口定义位于 [`PaperTodo.Plugin.Abstractions/`](../PaperTodo.Plugin.Abstractions/)；平时开发插件按本手册即可上手，只有需要深入了解主程序内部职责划分时再查阅 [架构文档](../doc/ARCHITECTURE.md)。

---

## 1. 快速开始

PaperTodo 支持 **Web** 和 **Native** 两种插件形态，你可以按功能复杂度自由挑选：

| 插件类型 | 适用场景 | 入口文件 | 构建方式 |
| :--- | :--- | :--- | :--- |
| **Web** | HTML/CSS/JS 小面板、计时器、轻量状态工具 | 本地 `entry` 网页；可选后台页面（默认同目录 `runtime.html`） | **无需编译**，复制目录即可运行 |
| **Native** | .NET 10 + WPF 复杂交互、调用本机进程/原生库、自定义 WPF 胶囊或 Mini | 实现 `IPaperBodyPlugin` 的 DLL；可选实现 `IPaperPluginRuntimeProvider` | `dotnet publish`（推荐使用仓库自带构建脚本） |

无论哪种类型，最终都安装在主程序旁的 `plugins/<插件 ID>/` 目录下，且**文件夹名称必须与 `plugin.json` 中的 `id` 完全一致**。

> [!NOTE]
> **没有热重载机制**：安装、删除或修改插件文件后，都需要重启 PaperTodo（或点击「设置 → 插件」右上角的**重启 PaperTodo 并重新扫描插件**）才能生效。`PaperBodyContext.Body.RequestReload()` 只会重建当前纸片的界面会话，不会重扫 `plugin.json`、卸载已加载的 Native DLL 或重启后台 Runtime。

### 1.1 最小 Web 插件

目录结构：

```text
plugins/com.example.hello/
├─ plugin.json
└─ web/
   └─ index.html
```

`plugin.json`：

```json
{
  "kind": "web",
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "apiVersion": "2.3",
  "stateVersion": 1,
  "entry": "web/index.html"
}
```

`web/index.html`（页面在 PaperTodo 本地专属域名下运行时，会自动注入 `window.papertodo`）：

```html
<!doctype html>
<meta charset="utf-8">
<button id="hello">点我记录时间</button>
<script>
  papertodo.paper.setTitle('Hello');
  papertodo.paper.setHeaderText('Hello 插件');
  papertodo.paper.setCapsulePresentation({
    preferredWidth: 0,
    plainText: 'Hello',
    components: [{ kind: 'text', text: 'Hello', fill: true }]
  });

  document.querySelector('#hello').addEventListener('click', () => {
    papertodo.saveState({ clickedAt: Date.now() });
  });
</script>
```

### 1.2 最小 Native 插件

Native 插件基于 `.NET 10` + `WPF`，项目文件引用 `PaperTodo.Plugin.Abstractions` 即可：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

入口 DLL 中**有且只能有一个**公开、非抽象、带无参构造函数的 `IPaperBodyPlugin` 实现类：

```csharp
using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

public sealed class HelloPlugin : IPaperBodyPlugin
{
    public IPaperBodySession Create(PaperBodyContext context) => new Session(context);

    private sealed class Session : IPaperBodySession
    {
        public Session(PaperBodyContext context)
        {
            View = new TextBlock
            {
                Text = "Hello PaperTodo",
                Margin = new Thickness(16)
            };
            context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "Hello",
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = "Hello",
                        Fill = true
                    }
                ]
            });
        }

        public FrameworkElement View { get; }
        public void Dispose() { }
    }
}
```

配套的 `plugin.json`（所有插件元数据均以 `plugin.json` 为唯一来源，无需在 C# 代码里重复声明 ID 或版本号）：

```json
{
  "kind": "native",
  "id": "com.example.hello-native",
  "name": "Hello Native",
  "version": "1.0.0",
  "apiVersion": "2.3",
  "stateVersion": 1,
  "entry": "HelloPlugin.dll"
}
```

### 1.3 使用一键脚本构建并安装 Native 插件

先退出正在运行的 PaperTodo，然后在仓库根目录执行统一构建脚本：

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

该脚本会自动完成 `Release` / `win-x64` 发布、复制 `plugin.json`、剔除多余的 `PDB` / `XML` / WebView2 Loader 及宿主已自带的共享程序集、保留目标目录下已有的 `.runtime/` 私有数据，并把干净的成品输出到 `plugins/<插件 ID>/`。

---

## 2. 目录与部署边界

仓库与运行目录的职责划分如下：

- `plugin-samples/`：存放插件源码、示例说明与构建脚本（不会在软件运行时被直接加载）；
- `plugins/<插件 ID>/`：存放可直接被 PaperTodo 识别加载的插件成品（主程序发布包默认不捆绑插件，插件独立分发）；
- `plugins/data/`（主工作区）与 `plugins/data2/`～`plugins/data5/`（副工作区 2～5）：存放由宿主代管的插件设置、Runtime 状态与单张纸片前端状态，按工作区自动隔离；
- `plugins/<插件 ID>/.runtime/`：供插件自行存放 WebView2 用户目录、缓存或跨纸片私有数据库（各工作区共用同一插件安装目录，不随工作区自动拆分）。

典型的插件成品目录结构：

```text
plugins/
├─ data/
│  └─ com.example.weather.json
└─ com.example.weather/
   ├─ plugin.json
   ├─ web/
   │  ├─ index.html
   │  ├─ mini.html
   │  └─ runtime.html       # Web Runtime 默认后台页（也可在 manifest 中改名）
   ├─ WeatherPlugin.dll     # Native 插件入口与私有依赖
   ├─ WeatherPlugin.deps.json
   └─ .runtime/             # 插件自管缓存或持久目录
```

**命名约束**：插件 `id` 须由 3～120 个 ASCII 字母、数字、`.`、`_`、`-` 组成；以 `.` 或 `_` 开头的文件夹在扫描时会被跳过；`builtin.markdown` 以及不区分大小写的 `data`、`data1`～`data5` 为宿主保留名称，不可用作插件 ID。

---

## 3. `plugin.json` 清单详解

| 字段 | 是否必填 | 说明 |
| :--- | :--- | :--- |
| `kind` | 必填 | `"web"` 或 `"native"` |
| `id` | 必填 | 插件唯一标识，安装目录名必须与之一致 |
| `name` | 建议填写 | 显示名称（留空时回退显示 `id`） |
| `description` | 可选 | 在「设置 → 插件」卡片上展示的功能简介 |
| `locales` | 可选 | 覆盖宿主渲染的插件名称、简介、设置项与分类的多语言翻译（见 [3.4](#34-多语言与界面语言读取)） |
| `version` | 必填 | 插件自身版本号（需符合标准 `Version` 格式，如 `"1.0.0"`） |
| `apiVersion` | 必填 | 所需最低宿主协议号，新插件填 `"2.3"`（兼容 `"2.1"`、`"2.2"`） |
| `stateVersion` | 必填 | 宿主代管 JSON 状态的目标版本号（最小为 `1`，同时作用于纸片状态与 Runtime 状态） |
| `entry` | 必填 | Web 主页面相对路径或 Native 入口 DLL 名称，必须位于插件目录内部 |
| `maxPaperInstances` | 可选 | 同一工作区最多允许创建的该插件纸片数；省略时默认为 `1`，填 `0` 表示不限数量（折叠或隐藏的纸片同样计入） |
| `miniEntry` | 可选 | 仅 Web 可用；边缘悬停专属页面路径（须位于 `entry` 所在的静态目录内） |
| `miniSize` | 可选 | 仅配合 `miniEntry` 使用；悬停卡片首选宽高 |
| `miniMaxSize` | 可选 | 插件承诺的 Mini 最大宽高上界，便于宿主按需分配承载空间 |
| `runtime` | 可选 | 仅 Web 且声明 `runtime` 能力时使用；指定后台页面路径，省略时默认取 `entry` 同目录的 `runtime.html` |
| `capabilities` | 可选 | 支持 `"textZoom"`、`"noteLinks"` 以及后台生命周期声明 `"runtime"` |
| `permissions` | 可选 | 访问工作区纸片、待办、笔记、软件设置或数据导入的权限声明（未知能力或权限会被直接拒绝加载） |
| `settings` | 可选 | 由宿主统一绘制和保存的插件设置项列表 |
| `advancedSettings` | 可选 | 默认 `false`；设为 `true` 时启用独立完整设置页 |
| `primarySettings` | 可选 | 仅 `advancedSettings: true` 时生效（`1`～`3`，默认 `3`），控制插件卡片上直接露出的前几项设置 |
| `settingCategories` | 可选 | 仅 `advancedSettings: true` 时生效，定义完整设置页的分组标题及可选的 `"left"` / `"right"` 分栏倾向 |
| `startupPaper` | 可选 | 按布尔设置项在启动时自动创建或恢复一张插件纸片 |

> **说明**：如果插件升级时调小了 `maxPaperInstances`，宿主不会强行删掉用户已有的旧纸片，只会阻止继续新建。

### 3.1 Web `entry` 与 `miniEntry`

`entry` 和 `miniEntry` 都不能越出插件目录，且 `miniEntry` 必须放在 `entry` 所在的同一静态目录（建议统一放在 `web/` 下，避免把 `.runtime/` 暴露进本地静态映射）：

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "version": "1.0.0",
  "apiVersion": "2.3",
  "stateVersion": 1,
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 },
  "miniMaxSize": { "width": 360, "height": 240 }
}
```

未配置 `miniEntry` 的 Web 插件不能声明 `miniSize` 或 `miniMaxSize`，且 `miniSize` 不能大于 `miniMaxSize`。

### 3.2 `startupPaper`（自动创建入口纸片）

如果希望插件在启用后自动准备一张纸片（例如桌面时钟，或承载后台 Runtime 的隐藏纸片），可以将其绑定到某项布尔设置上：

```json
{
  "startupPaper": {
    "enabledSetting": "autoStart",
    "instanceKey": "main",
    "presentation": "capsule",
    "title": "天气"
  },
  "settings": [
    {
      "id": "autoStart",
      "type": "boolean",
      "name": "启动后自动准备纸片",
      "default": true
    }
  ]
}
```

关键规则：

- `enabledSetting` 必须指向同一个 `plugin.json` 里的 `boolean` 设置项；`instanceKey` 为 1～80 个 ASCII 字母、数字、`.`、`_`、`-`；`title` 最长 120 字符。
- `presentation` 支持 `"capsule"`（折叠为胶囊）、`"expanded"`（直接展开）或 `"hidden"`（API 2.2 新增：创建或恢复真实插件纸片以维持后台 Runtime，但启动时不弹出窗口）。
- 如果用户已经把这张自动创建的纸片手动切换成了普通 Markdown 或其他插件，宿主会尊重用户选择，不会强行改回来或偷偷再建一张副本。
- 关闭 `autoStart` 只代表下次不再自动补建纸片，不会删掉已有纸片或停掉正在跑的后台；真正全局停用插件由「设置 → 插件」里的宿主开关控制。

### 3.3 `runtime` 后台能力与宿主启停机制

如果插件在纸片折叠成胶囊、隐藏窗口、甚至没有任何打开的前端界面时，仍需要继续跑计时器、监听待办变化或挂载全局顶栏按钮，请在 `capabilities` 中声明 `"runtime"`：

```json
"capabilities": ["runtime"]
```

它的生命周期非常清晰：

- **单实例共享后端**：只要该插件在宿主中处于**已启用**状态，且**当前活动工作区里至少存在 1 张属于该插件的实体纸片**（哪怕这张纸片是折叠或隐藏的），宿主就会为整个插件启动**唯一一个** Provider Runtime；多张同插件纸片共用这一个 Runtime，通过 `paperId` 区分各自的数据。
- **何时停止**：当最后一张该插件纸片被删除或切走正文类型、用户切换工作区、或者在「设置 → 插件」里**禁用插件**时，宿主会销毁该 Runtime 并自动撤下它注册的全局按钮、菜单项和快捷键。
- **禁用与重扫的区别**：在设置页拨动开关禁用插件时，已有纸片和宿主代管数据全部保留，纸片界面显示停用提示；重新打开开关就会按现有纸片恢复运行。但这不等于 DLL 热替换——修改了磁盘上的插件代码后，仍需重启 PaperTodo 生效。

### 3.4 多语言与界面语言读取

- **宿主渲染文案翻译**：通过 `plugin.json` 的 `locales` 字段即可按 `"en"`、`"ja"`、`"ko"` 等语言代码覆盖插件名称、说明、设置项、下拉选项和启动纸片标题（匹配顺序为“完整区域码 → 父级语言 → 默认原文”）。
- **插件自绘界面翻译**：插件内部界面需自行准备多语言文案，并读取宿主当前生效的界面语言：
  - **Native**：从 `context.UiLanguage` 读取会话创建时的语言，或从 `PaperPluginEnvironment.UiLanguage` 读取实时宿主语言。
  - **Web**：Body、Mini 与 Runtime 的 `initialize` 事件均携带 `message.uiLanguage`；Web 弹窗则从 `const { uiLanguage } = await window.papertodo.ready` 读取。
  - 每次新建会话或收到 `initialize` 时都应重新读取 `uiLanguage`，不要写死成进程级全局变量（切换工作区时有可能在同一进程内应用目标工作区的语言）。完整示例见 [PROTOCOL-2.1-LOCALIZATION.md](PROTOCOL-2.1-LOCALIZATION.md)。

---

## 4. 插件运行模型

Web 与 Native 插件遵循完全一致的前后端分层架构：

```text
Plugin Provider（插件提供方）
├─ Runtime × 0 或 1（唯一后台服务，按需声明）
│  ├─ Settings（当前工作区插件设置）
│  ├─ Provider State（插件级持久状态）
│  ├─ Papers[paperId]（管理所属各张纸片的标题与胶囊展示）
│  ├─ Workspace（按权限读写当前工作区的纸片/待办/笔记）
│  └─ Global Top Bar / Shortcuts / TodoActions / PaperActions
└─ Paper × N（具体纸片实例）
   ├─ Body（展开态完整前端视图）
   └─ Mini（边缘悬停轻量前端视图）
```

简单来说：**Runtime 管长期逻辑和跨界面状态，Body 和 Mini 只管眼前的界面展示与交互。** 没有声明 `runtime` 的轻量插件，直接在 Body 里更新标题、胶囊和状态即可。

### 4.1 `PaperBodyContext.Paper`

用于控制当前纸片的标题、副标题文字与胶囊展示。注意：一旦插件声明了 `"runtime"`，长期胶囊和标题展示就改由后台 Runtime 的 `context.Papers` 统一接管，避免前端关闭后状态断流。

### 4.2 `PaperBodyContext.Body`

提供完整正文界面所需的辅助能力：`Controls`（复用宿主风格的下拉框等控件样式）、`Theme`（当前主题色板）、`SetInputClaims(...)`（按需接管 `Esc` 键或右键菜单）、`MarkDirty()`、`OpenExternal(...)`（交由系统打开外部网址）和 `RequestReload()`。

### 4.3 `PaperBodyContext.Runtime`（前后端通信）

前端 Body 或 Mini 可以通过轻量通道把用户操作发给后台 Runtime：

```csharp
// Native
context.Runtime.Post(message);
```

```js
// Web
await papertodo.runtime.post(message);
```

返回值只代表当前 Runtime 是否收到了这条投递，不包含业务层回执、持久化队列或失败重试。

### 4.4 `TopBar` 与 `Workspace` 的职责分工

- **单纸片顶栏**（`PaperBodyContext.TopBar`）挂在当前 Body 会话上，只出现在这张插件纸片自己顶部；
- **全局顶栏**（`PaperPluginRuntimeContext.GlobalTopBar`）挂在后台 Runtime 上，会出现在所有纸片顶部；
- **数据读写**统一走 `Workspace` 接口（按 `permissions` 鉴权），两边写法一致。

### 4.5 前端 Body / Mini 的生命周期

当用户折叠、隐藏纸片或划过边缘胶囊时，Body 和 Mini 会随时被创建、隐藏或回收。Native 的 `OnVisibilityChanged` / `OnPresentationChanged` 只用来启停界面刷新动画，不要把需要在折叠后继续跑的计时或监听塞进前端会话里。

### 4.6 Provider Runtime 生命周期与容错规则

Native 插件通过额外实现 `IPaperPluginRuntimeProvider` 提供后台服务：

```csharp
public sealed class MyPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Body(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new Runtime(context);
}
```

使用 Runtime 时需留意以下几点：

- **先拉快照再订增量**：`context.Papers.Subscribe(...)` 不会回放订阅前已存在的纸片，初始化时请先调用 `Papers.List()` 拿到当前纸片列表，再建立订阅。
- **区分两个 `papers.list`（Web Runtime）**：在 Web Runtime 中，`papertodo.papers.list()` 只返回**归属本插件自己的纸片**；而 `papertodo.workspace.request('papers.list')` 返回**整个工作区里权限可见的所有纸片**。日常编码请直接调用分组好的对象，少用底层 `papertodo.request(...)` 以免混名。
- **切换工作区是完整的生命周期边界**：用户切换工作区时，宿主会先完整销毁旧工作区的所有 Body、Mini 和 Runtime，切好插件数据目录后，再按目标工作区的纸片和配置重新创建实例。所有定时器、异步任务和事件订阅都应在 `Dispose` 时干净释放。
- **故障处理**：Runtime 首次启动如果直接抛错失败，宿主不会无限自动重试（修改设置或下次启动时会再次尝试）；已成功运行起来的 Web 后台若遇到渲染进程崩溃，仍会走有限次自动恢复。普通主题/字号变更通知出错只会记录日志，不会把已经打开的正常正文刷成报错页。

---

## 5. 状态、设置与公共软件设置 API

### 5.1 宿主代管状态与版本迁移

宿主为每个插件在当前工作区维护一份独立的 JSON 文件：

| 所属工作区 | 磁盘路径 |
| :--- | :--- |
| 主工作区（1） | `plugins/data/<插件 ID>.json` |
| 副工作区 2～5 | `plugins/data2/<插件 ID>.json` ～ `plugins/data5/<插件 ID>.json` |

文件内部自动划分为三个互不覆盖的区域（哪怕副工作区开启了“跟随主设置”，这三个区域的插件数据依然在各工作区之间严格隔离）：

1. `settings`：当前工作区内该插件共享的设置值；
2. `runtime`：Provider Runtime 的后端持久状态（上限 **20 MiB** UTF-8 JSON）；
3. `papers`：按 `paperId` 分别保存的每张纸片前端状态（单张上限 **10 MiB** UTF-8 JSON）。

读写与迁移方式：

- **Native Body**：读取 `context.StateJson` / `StateVersion` / `TargetStateVersion`，状态变化后及时调用 `context.SaveStateJson(json)`。当磁盘上的旧版本号小于 `plugin.json` 的 `stateVersion` 时，宿主会在创建会话前先调用 `IPaperBodyPlugin.MigrateState(...)`。
- **Native Runtime**：在初始化时自行比对 `context.State.StateVersion` 与 `TargetStateVersion`，迁移完成后显式调用 `context.State.Save(...)`。
- **Web（Body / Mini / Runtime）**：从 `initialize` 事件获取 `state`、`stateVersion` 和 `targetStateVersion`，自行将旧结构兼容升级为新结构后调用 `papertodo.saveState(nextState)`（Runtime 调用 `papertodo.state.save(nextState)`），并可用 `papertodo.registerStateProvider(() => currentState)` 兜底关闭前刷新。
- 如果磁盘数据版本高于当前插件声明的 `stateVersion`（例如用户回退了旧版插件），宿主会拒绝加载该实例以防降级写坏新数据。

### 5.2 存储完整性与读取失败保护

只有当状态文件尚不存在时，宿主才会使用空默认状态初始化；**如果文件已经存在但 JSON 损坏或无法读取，宿主会直接报错并完整保留原文件，绝不会静默当成空数据覆盖掉用户的旧文件**。宿主本身不提供多版本历史恢复机制（旧版遗留的 `.json.recovered` 也不再自动读取），如果你的插件维护了格外重要的独立业务库，建议在插件内部自行做好校验与备份。

### 5.3 插件设置、快捷键与 `action` 命令按钮

`plugin.json` 的 `settings` 数组支持六种控件类型：`boolean`、`string`、`number`、`select`、`shortcut`（全局快捷键，由宿主统一管理热键注册与冲突检测）以及 API 2.2 新增的 `action`（点击即触发动作的命令按钮，不占用持久化字段；宿主自带的 `paper.show` / `paper.expand` 等可直接执行，自定义动作则转发给插件 Runtime 处理）。快捷键与动作按钮的详细写法请参阅 [PROTOCOL-2.1-SHORTCUTS.md](PROTOCOL-2.1-SHORTCUTS.md)。

设置面板有两种布局模式：

- **紧凑卡片模式（默认，`advancedSettings` 未开启）**：标记了 `"quick": true` 的前 3 项设置直接显示在插件卡片上，其余收在卡片内部的「更多设置」折叠区里。
- **独立完整设置页（`"advancedSettings": true`）**：插件卡片默认露出前 3 项设置（可通过 `primarySettings: 1..3` 调整），点击「更多设置」会进入独立的完整设置页，支持按 `category` 分组并指定左右栏位（内容较少时优先单列排布，放不下时自动分左右两栏且不会拆散同一分组）：

```json
{
  "advancedSettings": true,
  "primarySettings": 2,
  "settingCategories": [
    { "name": "常规", "column": "left" },
    { "name": "网络", "column": "right" },
    { "name": "调试" }
  ],
  "settings": [
    { "id": "enabled", "type": "boolean", "name": "启用同步", "category": "常规" },
    { "id": "mode", "type": "select", "name": "同步模式", "category": "常规", "options": [
      { "value": "auto", "name": "自动" },
      { "value": "manual", "name": "手动" }
    ] },
    { "id": "timeout", "type": "number", "name": "超时秒数", "category": "网络" },
    { "id": "debug", "type": "boolean", "name": "输出调试日志", "category": "调试" }
  ]
}
```

前端会话从 `SettingsJson` / `OnSettingsChanged`（Web 为 `initialize.settings` / `settingsChanged`）获取设置；后台 Runtime 则直接使用自己的 `context.Settings.Json` + `Settings.Subscribe(...)`（Web 为 `papertodo.settings.get()` + `settingsChanged`），无需借道前端纸片。

### 5.4 插件私有 `.runtime/` 目录

`plugins/<插件 ID>/.runtime/` 不经过宿主的 JSON 状态协议，适合存放 WebView2 独立浏览器缓存、可重建索引或插件自管的持久化文件（例如 `ReviewArchive` 的记录池）。升级安装脚本会自动保留该目录，但请不要把普通的单纸片状态同时写进 `plugins/data/` 和 `.runtime/`，以免两边状态打架。

### 5.5 公共软件设置 API（API 2.2）

如果插件需要读取或按用户明确指令调整 **PaperTodo 软件本身的公开设置**（例如先检查用户是否开启了“待办关联纸片”），可以使用公共软件设置接口：

- **调用入口**：Native 使用 `context.SettingsApi`（`IPaperSettingsApi`），Web 使用 `papertodo.settingsApi`（MCP 对应 `list_settings`、`get_setting`、`set_setting`）。
- **所需权限**：查询目录与读取单值需声明 `"settings.read"`，修改设置需声明 `"settings.update"`。

```csharp
// Native 示例
var links = context.SettingsApi.Get("todo.paper_links");
var todoCatalog = context.SettingsApi.List("todo");
// 仅在用户明确需要修改软件设置时调用：
context.SettingsApi.Set("todo.paper_links", true);
```

```javascript
// Web 示例
const links = await papertodo.settingsApi.get('todo.paper_links');
const todoCatalog = await papertodo.settingsApi.list('todo');
await papertodo.settingsApi.set('todo.paper_links', true);
```

使用前请先用 `List` 查询正式注册的设置 `id`、当前值、可写状态（例如副工作区跟随主设置时为只读）、取值范围及是否需要重启（如 `requires_restart: true`），不要猜测内部字段名。常见错误码包括 `setting_not_found`、`invalid_setting_value`、`setting_dependency`、`settings_busy`、`setting_read_only`、`shortcut_conflict` 和 `save_failed`。

---

## 6. Workspace 权限、提醒、跨纸片控制与数据合并

Workspace API 用于在授权范围内访问**当前活动工作区**的纸片、待办与内置笔记，无法跨工作区读写后台数据。

### 6.1 权限声明、数据方法与事件订阅

在 `plugin.json` 的 `permissions` 中按最小够用原则声明所需权限：

| 资源域 | 可声明权限 | 对应 Web 请求方法（`papertodo.workspace.request`） |
| :--- | :--- | :--- |
| **纸片（Papers）** | `papers.read`、`papers.create`、`papers.delete` | `papers.list`、`papers.get`、`papers.create`、`papers.delete` |
| **待办（Todos）** | `todos.read`、`todos.append`、`todos.update`、`todos.delete` | `todos.list`、`todos.append`、`todos.update`、`todos.setReminder`、`todos.delete` |
| **笔记（Notes）** | `notes.read`、`notes.append`、`notes.replace` | `notes.get`、`notes.write` |

开发时容易踩到的几处权限细节：

- **一步创建带正文的笔记**：除了 `papers.create`，还需同时声明 `notes.append`。
- **新建待办的初始属性**：新建或追加待办时直接传入初始完成状态、提醒时间或 `linkedPaperId` 只需 `todos.append`；事后修改已有待办（含设置提醒或通过 `UpdateLinkedPaper = true, LinkedPaperId = null` 解绑纸片）则需要 `todos.update`。
- **笔记追加与覆盖**：`notes.write` 选择追加或仅填充空白笔记时需 `notes.append`；整篇替换需 `notes.replace`。
- **禁止自删当前前端纸片**：Paper Body 会话不能删除正承载它自己的那张纸片（后台 Runtime 不绑定单张窗口，不受此限）。
- **事件订阅**：拥有 `*.read` 权限即可同时查询数据和订阅对应变更事件（`paper.created/changed/deleted`、`todo.created/changed/deleted`、`note.changed`；旧版遗留的 `*.observe` 权限仍兼容，但仅能收脱敏事件、不能主动查询）：

```csharp
// Native 订阅（ExcludeOwnOperations = true 可过滤插件自己刚发起的修改）
using var subscription = context.Workspace.Subscribe(
    new PaperTodoEventFilter
    {
        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged },
        ExcludeOwnOperations = true
    },
    evt => { /* 刷新本地状态 */ });
```

```js
// Web Body 或 Runtime 订阅（Web Mini 不支持 onHostEvent）
const dispose = papertodo.onHostEvent(
  ['todo.changed'],
  event => console.log(event),
  { excludeOwnOperations: true }
);
```

### 6.2 正文读写边界与实时编辑协同

插件和 MCP 的所有数据写入都经过宿主统一的 `PaperCommandService` 事务通道：

- **各司其职**：内置 Markdown 笔记走 `notes.get` / `notes.write`；待办纸走结构化的 `todos.*`（不要把待办当纯文本拼装）；其他第三方插件纸片的正文数据归各自插件的 `state` 管理，宿主不会把自定义插件强行当文本读写。
- **实时内容一致**：读取内置 Markdown 笔记时，哪怕用户正在打字还没触发定时落盘，读到的也是包含最新输入的内容；如果外部写入的目标恰好就是这张正在编辑的内置笔记，宿主会先把用户刚敲的字同步进模型再执行修改，而不会去无端触发其他无关第三方插件会话的 `Commit()`。

### 6.3 待办提醒前置接管 API（API 2.2）

如果你在开发桌面宠物、语音播报或自定义通知插件，可以在待办提醒**到期前约 5 秒**收到一次前置事件，并按需接管本次提醒展示：

- **订阅前置事件**：声明 `"todos.read"` 后，即可在 Native Runtime 或 Web Body/Runtime 中订阅 `PaperTodoEventKind.TodoReminderUpcoming`（Web 事件名为 `'todo.reminderUpcoming'`；旧 `todos.observe` 权限不支持此事件）。事件内含当前 `todo` 快照与一次性令牌 `notificationId`。
- **接管并屏蔽默认弹窗**：如果插件决定由自己来提示用户，还需声明 `"todos.update"`，并在到期前调用取消接口——成功后宿主本次不再弹出默认气泡和提示音，但**不会删除或改期这条待办**；如果插件在 5 秒窗口内没回复、取消失败或未接管，宿主到点后照常弹出默认提醒：

```csharp
// Native Runtime 示例（需 todos.read + todos.update）
var stop = context.Workspace.Subscribe(
    new PaperTodoEventFilter
    {
        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoReminderUpcoming }
    },
    evt =>
    {
        if (evt is TodoReminderUpcomingEvent upcoming &&
            context.Workspace is IPaperReminderNotificationControl control)
        {
            if (control.SuppressDefaultReminderNotification(upcoming.NotificationId))
            {
                // 展示插件自己的提醒界面
            }
        }
    });
```

```js
// Web Runtime 示例（需 permissions: ["todos.read", "todos.update"]）
const stop = papertodo.onHostEvent(['todo.reminderUpcoming'], async event => {
  const suppressed = await papertodo.workspace.request(
    'reminders.suppressDefault',
    { notificationId: event.notificationId }
  );
  if (suppressed) {
    // 展示插件自己的提醒效果
  }
});
```

- **如何识别“提醒已真正触发”**：无论是宿主正常弹出了通知，还是被插件成功接管屏蔽，到期结算后该待办的 `reminderTriggered` 都会变为 `true`，并派发一条 `metadata.origin == "system"` 的 `TodoChanged` 事件。外部监听端（如 Webhook）只需同时判断 `origin == "system"` 且 `!before.reminderTriggered && after.reminderTriggered`，就能准确识别一次提醒触发。

### 6.4 跨纸片显隐与收展控制（API 2.2）

面向 `2.2` 及以上的插件可以直接根据 `paperId` 控制工作区内任意一张纸片（包括待办纸、笔记纸或其他插件的纸片）的显示、隐藏、展开、折叠与激活，无需额外申请展示权限（如需先查列表找 `paperId`，仍需 `papers.read`）：

```csharp
// Native（Body 与 Runtime 均提供 context.WorkspacePresentation）
context.WorkspacePresentation.ShowPaper(paperId, activate: false);
context.WorkspacePresentation.HidePaper(paperId);
context.WorkspacePresentation.TogglePaperVisibility(paperId, activate: false);
context.WorkspacePresentation.ExpandPaper(paperId, activate: false);
context.WorkspacePresentation.CollapsePaper(paperId);
context.WorkspacePresentation.TogglePaperCollapsed(paperId, activate: false);
context.WorkspacePresentation.ActivatePaper(paperId);
```

```js
// Web（Body / Mini / Runtime 共用 workspace 请求通道）
await papertodo.workspace.request('papers.show', { paperId, activate: false });
await papertodo.workspace.request('papers.hide', { paperId });
await papertodo.workspace.request('papers.toggle', { paperId, activate: false });
await papertodo.workspace.request('papers.expand', { paperId, activate: false });
await papertodo.workspace.request('papers.collapse', { paperId });
await papertodo.workspace.request('papers.toggleCollapsed', { paperId, activate: false });
await papertodo.workspace.request('papers.activate', { paperId });
```

调用后会返回 `{ paper_id, is_visible, is_collapsed }` 逻辑状态（代表宿主已受理状态变更，不代表展开/收起过渡动画已经播完；若当前关闭了胶囊模式导致无法折叠，会返回 `presentation_unavailable`；纸片不存在则返回 `paper_not_found`）。只控制插件**自身承载纸片**时，继续用原有的 `context.Presentation` / `papertodo.paper.*` 即可。

### 6.5 活动工作区 `data.json` 显式重载与三方合并（API 2.2）

日常读写纸片、待办与笔记时，**请始终优先使用前面介绍的结构化领域 API**。只有在编写外部同步、备份对比或整包导入工具，确实需要直接改动磁盘上的核心 JSON 文件时，才使用本节的高权限重载与合并接口。

- **权限与作用域**：插件需在 `plugin.json` 中显式声明高权限 `"data.import"`（查询状态 `data.status` 无需此权限；MCP 侧对应 `reload_data`、`merge_data` 与只读的 `get_data_reload_status`）。所有操作**只针对当前活动工作区**，不会替你切换工作区。
- **先查当前工作区文件名**：调用 `Workspace.GetDataReloadStatus()`（Web 为 `papertodo.workspace.request('data.status', {})`），返回值包含 `WorkspaceNumber`、`DataFileName`（主工作区固定为 `data.json`，副工作区为 `data2.json`～`data5.json`）以及 `FollowsMainSettings`。修改磁盘前务必先核对当前活动文件名。
- **两种载入方式**：
  1. **直接重载（`ReloadData()` / `'data.reload'`）**：校验并直接采用磁盘上的当前工作区文件替换内存状态，会覆盖尚未保存的内存修改，同时使此前排队中的旧自动保存失效。
  2. **带基线的三方合并（`MergeData(originalJson)` / `'data.merge'`）**：外部工具在动手改文件前，必须先把当时读到的**完整原始 JSON 字符串**保存下来，改完磁盘后将其作为 `baselineJson` 传入（缺失或无效基线一律返回 `invalid`，MCP 请求受 8,000,000 字符上限保护）。宿主会对比“原始基线、内存修改、外部文件修改”三方：互不冲突的改动自动保留，冲突字段按用户在「工作区与数据 → 合并数据时遇到冲突」中设定的策略（`ask` 弹窗询问、`external` 外部优先、`internal` 当前优先）按字段级合并，并在提交前自动归档冲突记录（返回 `pending` 时可通过 `data.status` 轮询最终结果）。

---

## 7. 顶栏、待办行、右键菜单与轻量弹窗扩展

在所有宿主界面扩展点上，核心原则只有一条：**PaperTodo 始终掌控顶栏、菜单和待办行的原生控件树、字号、DPI 缩放、主题配色与窄窗口收纳；插件只需提交轻量的动作描述（Descriptor），不能直接往宿主视觉树里塞入自定义 `Button` 或 `WebView`。**

顶栏按钮按作用域分为两类：

- **单纸片顶栏（Paper Top Bar）**：由当前纸片的 Body 会话注册，只显示在这张插件纸片上，单个会话最多 **4 个**；
- **全局顶栏（Global Top Bar）**：由后台 Provider Runtime 注册，显示在工作区所有纸片上，单个插件最多 **256 个**。多个插件的全局按钮按 `Priority` 从大到小排列，但**宿主自带按钮始终拥有最高优先级**，窗口变窄时插件按钮会先一步让位收起。全局按钮点击回调会附带 `TargetPaperId`、`TargetPaperType` 与 `TargetBodyProviderId`，方便插件结合 Workspace API 读取或处理当前被点击的那张纸片。

### 7.1 图标规范

顶栏与待办行动作支持两种轻量图标描述：

1. **短字符（`Character`）**：1～8 个 UTF-16 字符（如 `"↻"`、`">_"`，不含控制字符）；
2. **单条矢量路径（`SvgPath`）**：标准 SVG / WPF Path Data 字符串（最长 4096 字符；不接受完整 `<svg>` 标签、滤镜或脚本），可选 `Fill`（前景色填充）或 `Stroke`（前景色描边，线宽 `0.1`～`4.0`）。

### 7.2 单纸片顶栏动作（Paper Action）

Native Body 会话示例（插件纸片还可以顺带请求在自身顶栏隐藏宿主的 `NewTodoPaper` / `NewNotePaper` 新建入口，但置顶、拖动、关闭等核心窗口按钮不可隐藏）：

```csharp
context.TopBar.SetActionHandler(invocation =>
{
    // 处理当前插件纸片的顶栏按钮点击
});

context.TopBar.SetPaperActions(
    [
        new PaperTopBarAction
        {
            Id = "refresh",
            Icon = PaperTopBarIcon.Character("↻"),
            ToolTip = "刷新数据"
        }
    ],
    PaperHostTopBarActions.NewNotePaper);
```

Web Body 会话示例（页面导航或会话销毁时会自动撤下旧按钮）：

```js
await papertodo.request('topbar.paper.set', {
  actions: [
    {
      id: 'refresh',
      icon: { kind: 'character', value: '↻' },
      toolTip: '刷新数据'
    }
  ],
  hiddenHostActions: ['newNotePaper']
});
```

### 7.3 全局顶栏动作（Global Action）

全局动作只能在后台 Runtime 中注册（每次调用 `SetActions` 均为整组替换语义，传空数组即可清空）：

```csharp
// Native Runtime
public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context)
{
    context.GlobalTopBar.SetActionHandler(invocation =>
    {
        var target = context.Workspace.GetPaper(invocation.TargetPaperId);
        // 按目标纸片执行操作
    });

    context.GlobalTopBar.SetActions([
        new PaperTopBarAction
        {
            Id = "inspect-current",
            Icon = PaperTopBarIcon.SvgPath(
                "M3,3 L13,3 13,13 3,13 Z M6,8 L10,8",
                PaperTopBarSvgRenderMode.Stroke,
                1.5),
            ToolTip = "读取当前纸片",
            Priority = 100
        }
    ]);

    return new Runtime();
}
```

```js
// Web Runtime
await papertodo.globalTopBar.setActions([
  {
    id: 'inspect-current',
    icon: {
      kind: 'svgPath',
      value: 'M3,3 L13,3 13,13 3,13 Z M6,8 L10,8',
      renderMode: 'stroke',
      strokeWidth: 1.5
    },
    toolTip: '读取当前纸片',
    priority: 100
  }
]);

papertodo.onEvent(async message => {
  if (message.type !== 'topBarActionInvoked') return;
  const paper = await papertodo.workspace.request('papers.get', {
    paperId: message.action.targetPaperId
  });
});
```

### 7.4 仅显示在菜单的操作（`MenuOnly`，API 2.3）

如果某个插件操作使用频率不高，不想平时挤占宝贵的纸片顶栏宽度，只需将 `apiVersion` 设为 `"2.3"`，并在单纸片或全局动作上加上 `MenuOnly = true`（Web 为 `menuOnly: true`）：

```csharp
context.GlobalTopBar.SetActions([
    new PaperTopBarAction { Id = "inspect", ToolTip = "查看纸片统计", MenuOnly = true }
]);
```

```js
await papertodo.globalTopBar.setActions([
  { id: 'inspect', toolTip: '查看纸片统计', menuOnly: true }
]);
```

开启后无需提供图标，宿主会直接用 `ToolTip`（为空时回退 `Id`）把它放进纸片 `···` 菜单的「插件操作」区，其余显隐、启用状态和回调逻辑与普通顶栏动作完全一致。可运行的参考工程见 `PaperTodo.Plugin.TopBarWeb`。

### 7.5 待办行内按钮与顶栏只读标签

后台 Provider Runtime 还可以向已有纸片投递两类动态展示元素（随 Runtime 生命周期自动清理，详见示例 `PaperTodo.Plugin.Protocol21Web`）：

- **`TodoActions`**（Web 为 `papertodo.todoActions.set(paperId, todoId, actions)`）：在指定待办事项右侧和待办右键菜单中挂载操作按钮，点击时返回 `PaperId`、`TodoId` 与最新 `TodoSnapshot`；
- **`TopBarLabels`**（Web 为 `papertodo.topBarLabels.set(paperId, labels)`）：在指定纸片顶栏展示不可点击的轻量状态标签。

### 7.6 纸片右键菜单、笔记图片读取与轻量弹窗

当点击顶栏或右键菜单后需要弹出一个小巧的交互面板（比如选图器、快捷表单或详情卡片），可以组合使用以下三项能力：

| 功能 | Native 入口 | Web 入口 | 权限与适用范围 |
| :--- | :--- | :--- | :--- |
| **纸片右键菜单项** | `runtimeContext.PaperActions` | Runtime `papertodo.paperActions` | 需 `papers.read`；由 Runtime 按 `paperId` 注册纯文字菜单项 |
| **读取笔记内图片** | `context.NoteAssets` | `papertodo.noteAssets` | 需 `notes.read`；按 `paperId` + `imageId` 读取内置笔记图片字节（上限 16 MiB） |
| **点击处临时弹窗** | `context.Popups` | Body / Runtime `papertodo.popups` | 在点击坐标旁弹出一个失焦即关的轻量面板（每会话同时最多 1 个，新开自动顶替旧弹窗） |

顶栏动作与右键菜单回调都会附带点击瞬间的屏幕物理像素坐标快照 `Position`，直接原样传给 `Popups.Open` 即可让宿主自动完成多屏工作区防越界定位：

```csharp
// Native Runtime 示例：右键纸片菜单弹出自定义纯 WPF 面板
var menus = context.PaperActions!;
var popups = context.Popups!;
menus.SetActionHandler(action =>
    popups.Open(action.Position, new() { Width = 320, Height = 240 },
        popup => new MyPanel(popup, action.Paper.Id)));
menus.SetActions(paperId, [new() { Id = "details", Text = "查看详情" }]);
```

```javascript
// Web Runtime（创建者页面）打开本地弹窗页 panel.html
papertodo.onEvent(event => {
  if (event.type === 'paperActionInvoked') {
    const { position, paper } = event.action;
    papertodo.popups.open(position, {
      entry: 'panel.html',
      width: 320,
      height: 240,
      data: { paperId: paper.id }
    }).catch(console.error);
  }
});
await papertodo.paperActions.set(paperId, [{ id: 'details', text: '查看详情' }]);
```

在 Web 弹窗页面 `panel.html` 内部，通过 `await papertodo.ready` 接收初始参数（初始 `data` 与单条 `popup.post` 消息上限均为 **64 KiB**；如需展示笔记图片，请只传 `imageId` 并在弹窗内调用 `papertodo.noteAssets.readImage` 获取 Base64，不要把大图塞进初始 `data`）：

```javascript
const { data, theme, uiLanguage } = await window.papertodo.ready;
const image = await papertodo.noteAssets.readImage(data.paperId, imageId);
document.querySelector('img').src = `data:${image.mime};base64,${image.bytes}`;
await papertodo.popup.post({ selected: imageId }); // 创建者会收到 popupMessage 事件
papertodo.popup.close();
```

弹窗本身只是临时前端，点击弹窗外部会自动关闭，且不直接持有 Workspace 写入权限——用户在弹窗里选好内容后，通过 `popup.post` 发回给创建它的 Body 或 Runtime 完成后续保存即可。

---

## 8. 胶囊展示（Capsule Presentation）

插件纸片折叠后，胶囊外壳、关闭按钮、拖拽贴边、跨屏动画与鼠标命中全部由 PaperTodo 统一托管，插件只需负责里面的内容呈现。

### 8.1 宿主绘制的标准胶囊（Web 与 Native 通用）

提交一份 `PaperCapsulePresentation`，最多组合 **3 个**标准小组件（按数组顺序横向排布）：

- 支持的组件类型：`text`（文字）、`glyph`（字符/小符号）、`statusDot`（状态圆点）、`progressRing`（环形进度）、`progressBar`（条形进度）；
- 宽度设置：Native 传 `PreferredWidth = PaperCapsulePresentation.AutomaticWidth`、Web 传 `preferredWidth: 0` 时，宿主会按实际内容自动测算最合适的胶囊宽度；也可以传入期望的正数逻辑宽度（DIP）；
- **务必填写有意义的 `plainText`**：当窄空间回退或自定义视图未就绪时，宿主会用 `plainText` 保证胶囊始终可读。

### 8.2 Native 专属自定义 WPF 胶囊

如果标准组件不够用（例如想自己画指针表盘或特殊状态条），Native 会话可以额外实现 `IPaperCapsuleViewProvider`：

- `CreateCapsuleView(PaperCapsuleViewContext)` 会按 `Regular`（桌面浮动胶囊）和 `Docked`（屏幕边缘胶囊）分别调用，**每次必须返回全新的、没有父级容器的纯 WPF 控件实例**（不可传入 `Window`、`HwndHost`、`WebView2` 或已经挂在正文上的同一个控件）；
- 自定义胶囊视图只负责视觉展示，不拦截鼠标点击与拖拽；同一会话与尺寸下宿主会复用该控件实例，因此动态数据更新时请**直接修改控件属性原地刷新**，不要靠反复重建整个 View。

---

## 9. 边缘悬停卡片（Edge Mini）

当鼠标悬停在边缘胶囊上时，宿主会按以下优先级决定展示什么内容：

1. Native 会话实现了 `IPaperMiniViewProvider` → 展示 **Native 专属 Mini 视图**；
2. Web 插件在 `plugin.json` 声明了 `miniEntry` → 展示 **Web 专属 Mini 页面**；
3. 未提供专属 Mini → 宿主自动用插件的胶囊内容或 `plainText` 生成只读速览卡片。

### 9.1 卡片尺寸与 `miniMaxSize`

- `PaperMiniViewSize` / `miniSize` 指的是**包含宿主外框与关闭区在内的整张卡片宽高**（单位 DIP，未指定时默认首选 `320 × 220 DIP`，正有限数值即可，最终由当前显示器可用工作区自动兜底限制）。
- 可选的 `miniMaxSize` 用于声明该插件 Mini 在运行期可能请求的最大尺寸上界，方便宿主提前按合理容量准备承载层；请避免在悬停卡片已经展开显示时高频动态改尺寸（更不要拿尺寸变化做动画），以免引发窗口重排卡顿。

### 9.2 Native 专属 Mini

实现 `IPaperMiniViewProvider` 时：

- `CreateMiniView` 同样必须返回全新的、未挂载的纯 WPF 视觉树（不含 `WebView2` 或子句柄控件；需要与正文共享计时或数据时，共享底层 C# Model 即可）；
- 边缘卡片不获取键盘输入焦点，因此 Mini 界面应设计为纯点击/滑动交互，不要放需要打字输入的文本框；
- 标准 WPF `Button`、滚动条、`Hyperlink` 等可直接响应点击；如果自绘的 `Border` / `Canvas` 也需要自己消费鼠标点击（而不是点下去直接展开大纸片），请显式标记：

```csharp
PaperMiniViewInteraction.SetConsumesPointer(element, true);
```

### 9.3 Web 专属 Mini 与局部交互声明

`miniEntry` 运行在独立的轻量 WebView2 中，请使用纯本地静态页面以便秒开，并在完成首轮 DOM 布局后调用 `papertodo.mini.ready()` 通知宿主可以上屏展示。

默认情况下，在 Web Mini 卡片上点击空白处或按住拖动都归 PaperTodo 宿主处理（用于点开完整纸片或拖拽）。**网页里真正需要自己响应点击或拖动的按钮/滑块，请加上 `data-papertodo-interactive` 属性**：

```html
<button type="button" data-papertodo-interactive>开始 / 暂停</button>
```

宿主会自动把带有该标记的元素矩形同步到输入命中层（切勿图省事直接标在 `<body>` 根节点上，否则会废掉点击背景展开纸片等宿主手势）。此外，Body 与 Mini 共享同一份纸片 `state`，任一端调用 `saveState` 后另一端都会收到 `stateChanged`——接收端只刷新界面即可，**不要在 `stateChanged` 回调里原样再调一次 `saveState`**，以免造成死循环回声。

---

## 10. Web 插件桥接速查

### 10.1 本地专属域名与导航规则

Web 插件的本地静态目录会被映射到专属虚拟域名：

```text
https://<plugin-id>.papertodo.local/
```

只有这个域名下的**顶层本地文档**才会获得 `window.papertodo` 桥接对象（`iframe` 或远程网页均不会注入）。页面内点击外部网页/邮件链接或触发普通 HTTP(S) 下载时，宿主会自动转交给系统默认浏览器打开。

### 10.2 正文页（Body Bridge）

```js
papertodo.surface;                           // 'body'
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.setInputClaims(['escapeKey', 'contextMenu']);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.runtime.post(message);
papertodo.settingsApi.list(category);        // 需 settings.read / update
papertodo.noteAssets.readImage(paperId, id); // 需 notes.read
papertodo.popups.open(position, options);
papertodo.workspace.request(method, params);
papertodo.request(method, params);           // 单纸片顶栏等底层调用
papertodo.onHostEvent(types, listener, options);
papertodo.onEvent(listener);
```

宿主向页面推送的事件类型包括：`initialize`、`stateChanged`、`settingsChanged`、`activated`、`deactivated`、`visibilityChanged`、`presentationChanged`、`themeChanged`、`typographyChanged`、`dpiChanged`、`commitRequested`、`cancelInteractions`、`hostResponse`、`hostEvent`、`hostSubscriptionError`、`topBarActionInvoked`、`runtimeMessage`、`popupMessage`、`popupError`。

### 10.3 边缘卡片页（Mini Bridge）

```js
papertodo.surface;                           // 'mini'
papertodo.mini.ready();                      // 首帧排版完成后调用
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.runtime.post(message);
papertodo.settingsApi.list(category);
papertodo.workspace.request(method, params);
papertodo.onEvent(listener);
```

### 10.4 后台页（Runtime Bridge）

```js
papertodo.surface;                           // 'runtime'
papertodo.settings.get();
papertodo.state.get();
papertodo.state.save(state);
papertodo.papers.list();                     // 仅本插件所属的纸片
papertodo.papers.setTitle(paperId, title);
papertodo.papers.setHeaderText(paperId, text);
papertodo.papers.setCapsulePresentation(paperId, presentation);
papertodo.papers.postBody(paperId, message);
papertodo.globalTopBar.setActions(actions);
papertodo.todoActions.set(paperId, todoId, actions);
papertodo.todoActions.clear(paperId, todoId);
papertodo.todoActions.clearAll();
papertodo.topBarLabels.set(paperId, labels);
papertodo.topBarLabels.clear(paperId);
papertodo.topBarLabels.clearAll();
papertodo.paperActions.set(paperId, actions);
papertodo.popups.open(position, options);
papertodo.settingsApi.list(category);
papertodo.workspace.request(method, params);
papertodo.onHostEvent(types, listener, options);
papertodo.onEvent(listener);
```

### 10.5 状态保存的好习惯

每次用户操作产生了真实状态变化，都请**立即调用 `saveState`**（Runtime 调用 `state.save`）。`registerStateProvider` 只是宿主在窗口关闭或卸载前尽力帮你补刷一次的保险手段，不要只靠它在退出瞬间存盘（Runtime 也没有卸载自动补刷机制）。

---

## 11. Native 插件开发要点

编写 `.NET 10` + `WPF` 原生插件时，请牢记以下工程准则：

- **`IPaperBodyPlugin` 只是轻量工厂**：不要把某一张纸片的状态存成 `IPaperBodyPlugin` 的实例字段；每打开一张纸片，宿主都会新建独立的 `IPaperBodySession`。
- **按需延迟加载 DLL**：没有声明 `"runtime"` 的 Native 插件，启动扫描阶段只读 `plugin.json`，直到真正需要展示该插件纸片时才会把 DLL 加载进内存。
- **前后端职责分清**：`IPaperPluginRuntime` 与 `IPaperBodySession` 生命周期完全独立，全局按钮与后台任务统一放在 Runtime，不要挂在某张可能随时折叠销毁的纸片会话上。
- **复用宿主控件视觉**：下拉选择框等基础控件可通过 `context.Body.Controls` 应用与 PaperTodo 一致的主题和 DPI 样式，省去自己重造弹窗样式的麻烦。
- **保持发布包干净**：第三方私有依赖随插件目录携带，但不要把宿主已提供的 `PaperTodo.Plugin.Abstractions.dll`、Windows SDK 或多余的 `.pdb` / `.xml` 塞进 `plugins/<id>/`。

---

## 12. 官方示例工程索引

动手写新插件前，最快的方式是在 `plugin-samples/` 里挑一个结构最接近的示例改起（不要把所有高级特性一股脑全拷进同一个小插件里）：

| 示例目录 | 类型与侧重点 |
| :--- | :--- |
| [PaperTodo.Plugin.SampleClock](PaperTodo.Plugin.SampleClock/README.md) | **Native 标准入门示例**：涵盖插件设置、后台刷新、标准胶囊、自定义 WPF 胶囊与专属 WPF Mini |
| [PaperTodo.Plugin.OfficialClockWeb](PaperTodo.Plugin.OfficialClockWeb/README.md) | **Web 标准入门示例**：演示 `index.html` + `mini.html` + `runtime.html` 三端配合与 `startupPaper` |
| [PaperTodo.Plugin.FocusTimer](PaperTodo.Plugin.FocusTimer/README.md) | **Native 有状态交互示例**：番茄钟正文与专属 Mini 共享计时模型，联动 Workspace 待办自动完成 |
| [PaperTodo.Plugin.ReviewArchive](PaperTodo.Plugin.ReviewArchive/README.md) | **Workspace 监听与自管存储示例**：后台持续记录待办历史并落盘至 `.runtime/`，支持筛选与导出 CSV |
| [PaperTodo.Plugin.Webhook](PaperTodo.Plugin.Webhook/README.md) | **后台事件转发示例（API 2.2）**：隐藏启动纸片维持 Runtime，将纸片/待办/笔记及提醒前置事件 POST 至外部地址 |
| [PaperTodo.Plugin.CodexCliBridge](PaperTodo.Plugin.CodexCliBridge/README.md) | **全局顶栏、待办按钮与本机 CLI 桥接示例**：演示隐藏提示词纸片、多语言配置、`action` 设置按钮及本机进程联动 |
| [PaperTodo.Plugin.TopBarWeb](PaperTodo.Plugin.TopBarWeb/README.md) | **顶栏与仅菜单动作专项示例（API 2.3）**：涵盖单纸片/全局按钮、`menuOnly: true`、快捷键与 `locales` 多语言 |
| [PaperTodo.Plugin.Protocol21Web](PaperTodo.Plugin.Protocol21Web/README.md) | **待办行动作与顶栏标签专项示例**：最小化演示 `todoActions` 与 `topBarLabels` 的动态挂载与回收 |
| [PaperTodo.Plugin.CloudGenshin](PaperTodo.Plugin.CloudGenshin/README.md) | **内嵌重型 WebView2 示例**：完整远程网页留在展开正文，胶囊与 Edge Mini 改用轻量纯 WPF 状态卡片 |

---

## 13. 常见踩坑排查

- **清单与启动问题**：文件夹名与 `plugin.json` 的 `id` 不一致；使用了保留名 `data`～`data5`；声明了 `"runtime"` 却漏放默认 `runtime.html`（或 Native 未实现 `IPaperPluginRuntimeProvider`）；误以为只放进 `plugins/`、一张实体插件纸片都没有时也会自动跑后台 Runtime（需要自动建纸片请配 `startupPaper`）；改完文件没重启 PaperTodo。
- **顶栏与菜单问题**：试图从普通 Body 会话注册全局顶栏按钮（报 `global_topbar_app_runtime_only`）；把整段 `<svg>` 标签而不是纯 `Path Data` 传给图标；误以为插件按钮 `Priority` 能排到宿主固定按钮前面。
- **WPF 视图与 Web Mini 问题**：把同一个 WPF 控件实例同时交给正文、胶囊和 Mini（触发已存在父级异常）；把 `WebView2` 或 `HwndHost` 塞进自定义胶囊或 Native Mini；Web Mini 里的按钮忘了加 `data-papertodo-interactive` 导致点下去直接展开了大纸片，或者反过来把整个根节点都标成了 interactive。
- **状态与生命周期问题**：收到 `stateChanged` 后又调 `saveState` 引发无限互刷；状态迁移报错时拿空对象覆盖了用户的旧数据；`Dispose` 后没有停掉定时器或事件监听；工作区切换后仍缓存使用上一个工作区的旧设置或语言。

---

## 14. 发布与提交前自查清单

- [ ] 新插件 `plugin.json` 已声明 `"apiVersion": "2.3"`，且目录名与 `id` 完全一致；
- [ ] 只申请了实际用到的最小 `permissions` 与 `capabilities`；
- [ ] 如声明 `"runtime"`，确认零纸片不启动、首张纸片创建启动、末张删除/禁用插件/切换工作区时完整释放所有计时器、订阅和全局按钮；
- [ ] Native 插件已通过 `Build-And-Install-NativePlugin.ps1` 构建验证，成品目录内不含多余的 `.pdb`、`.xml` 或重复的宿主共享 DLL，且升级不误删 `.runtime/`；
- [ ] 胶囊提供了清晰可读的 `plainText` 回退文本；自定义 WPF 视图均为全新未挂载的纯 WPF 控件；
- [ ] Web Mini 首帧就绪后正确调用了 `papertodo.mini.ready()`，仅对交互按钮局部标记了 `data-papertodo-interactive`，且与 Body 同步状态时无回声死循环；
- [ ] 界面语言与多工作区切换后，新建的会话与 Runtime 均能正确读取当前工作区的最新数据与 `uiLanguage`。