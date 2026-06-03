# PaperTodo Developer Design README

这份文档给后续开发者和接手项目的大模型使用，不面向普通用户。

目标是让没有上下文的人能直接理解 PaperTodo 的代码结构、产品边界、已经验证过的技术取舍、踩过的坑，以及继续开发时需要优先保护的行为。

## 1. 项目定位

PaperTodo 是一个原生 WPF 桌面纸片工具。它没有主窗口，启动后只保留桌面上的纸片窗口和系统托盘入口。

核心目标不是做任务管理系统，而是做“桌面上几张轻量纸”：

- 快速记录。
- 快速勾选。
- 桌面直接可见。
- 不引入账号、同步、分类、标签、搜索、归档、统计。
- 尽量减少管理界面，不让软件变成另一个复杂工作台。

这个边界很重要。开发新功能时要先判断它是不是会把产品推向“完整任务管理器”。如果是，默认不做，或者只做成非常轻的局部能力。

## 2. 技术栈

- 语言：C#。
- UI：WPF。
- 目标框架：`net10.0-windows`。
- Markdown 编辑 / 浏览：`AvalonEdit`。
- 托盘图标：`Hardcodet.NotifyIcon.Wpf`。
- 状态文件：程序目录下的 `data.json` 和 `data.backup.json`。
- 程序没有数据库、后台服务、WebView、Electron、Tauri 或浏览器内核。

项目文件是 `PaperTodo.csproj`。当前版本号由这里统一控制：

```xml
<Version>1.5</Version>
<AssemblyVersion>1.5.0.0</AssemblyVersion>
<FileVersion>1.5.0.0</FileVersion>
<InformationalVersion>1.5</InformationalVersion>
```

托盘菜单顶部显示的版本号来自程序集元数据，不要再做成手写字符串。

## 3. 运行模型

程序启动入口在 `App.xaml.cs`。

启动流程：

1. 注册全局异常处理。
2. 通过 `SingleInstanceHelper` 抢单实例锁。
3. 如果已经有主实例，向主实例发送激活信号，然后当前进程退出。
4. 创建 `AppController`。
5. 设置 `ShutdownMode = OnExplicitShutdown`。
6. 调用 `AppController.Start()`。
7. 启动命名管道监听，后续实例启动时会触发显示全部纸片。

因为没有主窗口，进程生命周期由 `AppController`、托盘图标和纸片窗口共同维持。退出时不能只隐藏托盘或关闭窗口，必须保证进程真正结束。

当前退出策略：

- `AppController.Exit()` 会先同步保存。
- 释放托盘资源。
- 调用 `Application.Current.Shutdown()`。
- 最后 `Environment.Exit(0)`，用于避免“右下角托盘图标没了但进程残留”的情况。

这是一种偏硬的退出方式，但当前项目里是有意保留的，因为用户已经遇到过退出后残留进程的问题。

## 4. 主要文件职责

### `App.xaml.cs`

负责应用级生命周期：

- 单实例控制。
- 全局未处理异常处理。
- 崩溃日志 `PaperTodo.crash.log`。
- 崩溃恢复文件 `data.crash_recovery.json`。
- 退出时释放控制器和单实例资源。

崩溃日志最大约 100 KB。超过后保留末尾约 80 KB 并写入裁剪标记。不要让日志无限增长。

### `AppController.cs`

这是项目的中心协调器。它负责：

- 持有 `AppState`。
- 创建、显示、隐藏、删除纸片。
- 管理 `Dictionary<string, PaperWindow>`。
- 管理托盘图标和托盘菜单。
- 管理主题。
- 管理胶囊模式和胶囊自动贴边。
- 管理自动保存。
- 处理保存失败提示。
- 拉回屏幕外纸片。
- 处理开机自启动。
- 退出程序。

大部分跨窗口、跨纸片、跨全局状态的逻辑都应该放在这里，而不是塞进 `PaperWindow`。

### `PaperWindow.cs`

这是单张纸片窗口。它负责：

- 无边框 WPF 窗口。
- 顶栏、正文、缩放手柄。
- 待办纸 UI。
- 笔记纸 UI。
- Markdown 编辑和只读浏览切换。
- 纸片右键菜单。
- 待办项拖动排序。
- 待办项拖到末尾删除区删除。
- 待办撤销/重做。
- 胶囊形态 UI、动画和自动贴边。
- 纸片大小、位置变化时通知 `AppController.UpdateGeometry()`。

注意：`PaperWindow` 直接绑定的是同一个 `PaperData` 实例，不是 ViewModel 副本。因此 UI 操作修改数据后要记得调用 `_controller.MarkDirty()` 或经过会标脏的控制器方法。

### `StateStore.cs`

负责状态读写。

状态路径：

- 主文件：`data.json`
- 备份：`data.backup.json`

读：

- 优先读主文件。
- 主文件失败时尝试备份文件。
- 两者都失败时抛出本地化错误。

写：

- 先序列化。
- 写到 `data.json.tmp`。
- 尝试把旧 `data.json` 复制为 `data.backup.json`。
- 再把临时文件移动为正式 `data.json`。

写入有版本号和锁：

- `AppController` 每次保存递增 `_saveVersion`。
- `StateStore` 用 `_latestWrittenVersion` 避免旧的异步保存覆盖新的状态。
- `SemaphoreSlim` 保证同一时间只有一个写入。

### `Models.cs`

定义持久化数据结构：

- `AppState`
  - `Papers`
  - `Theme`
  - `UseCapsuleMode`
- `PaperData`
  - `Id`
  - `Type`
  - `Title`
  - `X`
  - `Y`
  - `Width`
  - `Height`
  - `IsVisible`
  - `AlwaysOnTop`
  - `IsCollapsed`
  - `Items`
  - `Content`
- `PaperItem`
  - `Id`
  - `Text`
  - `Done`
  - `Order`

继续开发时要把 `data.json` 当成用户数据兼容协议。新增字段可以有默认值；删除或重命名字段要非常谨慎。

### `Theme.cs`

集中定义浅色、深色和系统主题。

主题模式保存在 `AppState.Theme`。`AppController.SetTheme()` 修改该字段后，会刷新托盘菜单和所有纸片窗口。WPF 控件里的颜色大多通过资源 key 和动态资源刷新。

### `Strings.cs` 和 `Resources/*.resx`

`Strings.cs` 是资源访问入口。

当前资源：

- `Resources/Strings.resx`：默认中文。
- `Resources/Strings.en.resx`：英文。
- `Resources/Strings.ja.resx`：日文。
- `Resources/Strings.ko.resx`：韩文。

新增任何用户可见文本，都要同步补齐这几个资源文件。

`ResourceTextVersion` 是人工检查用的资源版本标记，不是运行时逻辑。不要把它做成启动校验、版本拒绝或资源同步机制。

### `MarkdownTextBox.cs`

笔记编辑和浏览共用的 AvalonEdit 扩展，主要处理 Markdown 快捷键、轻量高亮、换行、粘贴长度限制和点击定位。

### `TodoTextBox.cs`

待办项编辑用 TextBox 扩展，配合 `PaperWindow` 的待办逻辑处理输入、粘贴和快捷键。

### `SingleInstanceHelper.cs`

单实例工具：

- Mutex 判断是否已有实例。
- 命名管道通知主实例。
- 主实例收到信号后显示全部纸片。

### `SystemSettingsHelper.cs`

处理开机自启动相关系统设置。

### `ClipboardHelper.cs`

剪贴板辅助。

## 5. 数据模型和保存语义

PaperTodo 的状态就是纸片本身。

重要语义：

- 删除才是真删除。
- 隐藏不是删除。
- 关闭单张纸片在产品语义上通常是隐藏。
- 重新启动时会恢复所有非删除纸片。
- 纸片位置、大小、置顶、可见性、胶囊状态都属于状态。
- 待办项顺序由 `Order` 表示，但 `StateStore.Normalize()` 会按当前列表顺序重排。

保存节流：

- `AppController.MarkDirty()` 启动 450 ms 定时器。
- 定时器触发后调用 `SaveNow()`。
- 退出时使用同步保存。

启动恢复：

- `AppController.Start()` 会先创建托盘图标。
- 如果没有纸片，创建一张待办纸。
- 如果有纸片，先用 `EnsurePapersOnScreen()` 拉回屏幕外纸片。
- 然后显示所有已有纸片。
- 启动恢复期间 `_suppressDirty = true`，避免恢复显示本身触发大量无意义保存。
- 如果确实拉回了屏幕外纸片，才保存新位置。

## 6. 托盘实现

托盘是全局入口，代码集中在 `AppController.cs`。

当前正确路径：

```csharp
_trayMenu = CreateTrayMenu();
_trayMenu.Opened += (_, _) => RebuildTrayMenu();

_trayIcon = new TaskbarIcon
{
    ToolTipText = "PaperTodo",
    IconSource = LoadTrayIconSource(),
    ContextMenu = _trayMenu,
    Visibility = Visibility.Visible
};
```

`RebuildTrayMenu()` 在菜单打开时重建内容，确保纸片状态、主题、自启动状态、胶囊状态都是最新的。

### 托盘图标规则

托盘图标加载顺序：

1. 程序目录下的外部 `PaperTodo.ico`。
2. 程序内嵌资源 `assets/icons/PaperTodo.ico`。
3. 代码绘制的 fallback vector icon。

外部 `PaperTodo.ico` 是允许用户自定义托盘图标，不是兜底文件。也就是说：如果外部文件存在，就优先用外部文件。

### Hardcodet.NotifyIcon.Wpf 的关键坑

这里曾经有一个非常隐蔽的 v1.3 回归：

- 把托盘图标从 `IconSource = LoadTrayIconSource()` 改成 `Icon = System.Drawing.Icon` 后，第一次右键托盘菜单会从桌面最右下角弹出。
- 随后第一次点击主程序纸片会被吞掉。
- 后续再打开菜单又看似正常。

根因不是删除纸片多态，也不是菜单确认态本身，而是 Hardcodet 的 WPF `IconSource` 路径和 WinForms `Icon` 路径在首次弹出 ContextMenu 时行为不同。

当前结论：

- 对这个项目，托盘图标必须走 `IconSource`。
- 不要改回 `System.Drawing.Icon`。
- 不要为了图标内嵌或自定义托盘图标重新引入 `Icon` 属性。

### 不要重新引入的托盘菜单修复

以下方案都试过，结果要么无效，要么引入更差的点击问题：

- 手动用 `PlacementMode.MousePoint` 打开托盘菜单。
- 调用 `SetForegroundWindow`。
- `PostMessage` 发送空消息。
- `ThreadFilterMessage`。
- 菜单预热。
- 启动瞬间屏幕外预打开。
- 外部点击轮询关闭菜单。
- 鼠标移到纸片上就强行关闭菜单。

这些都不要作为“第一次点击被吞”的修复重新加回来。

如果后续再次出现托盘菜单第一次位置错误或第一次点击被吞，先检查是否有人改动了：

- `TaskbarIcon.IconSource`
- `TaskbarIcon.Icon`
- `ContextMenu` 自动绑定方式
- `CreateTrayIcon()`
- `LoadTrayIconSource()`

## 7. 托盘菜单删除确认设计

托盘菜单中的纸片列表行支持内联删除确认。

当前设计：

- 普通态：左边是纸片标题和预览，右边是 `×` 删除入口。
- 点击 `×` 不直接删除，而是进入确认态。
- 确认态左侧显示警示语义，例如 `⚠ 删除`。
- 右侧有两个可点击区域：`确认` 和 `取消`。
- 两个操作都需要有 hover/pressed 预先选择态。
- 确认和取消的位置要清晰，避免误删。

相关资源 key：

- `TrayInlineConfirmDelete`
- `TrayInlineConfirmAction`
- `CommonCancel`

新增或修改这块 UI 时，必须同步四种语言资源。

## 8. 纸片窗口设计

纸片是 `PaperWindow`，每张纸都是独立的 WPF 无边框窗口。

窗口基本结构：

- 外层带圆角、边框和阴影的 `Border`。
- 顶栏。
- 内容区。
- 底部缩放手柄。
- 拖拽 overlay 层。
- 胶囊 overlay 层。

窗口属性：

- `ShowInTaskbar = false`
- `WindowStartupLocation = Manual`
- `WindowStyle = None`
- `AllowsTransparency = true`
- `Background = Transparent`
- `Topmost` 来自 `PaperData.AlwaysOnTop`，胶囊形态下会临时有效置顶

### 顶栏

顶栏负责：

- 拖动窗口。
- 左侧纸片类型图标，同时作为置顶开关。
- 右侧新建待办、新建笔记、隐藏/折叠按钮。

当前顶栏做了轻微压暗和底部分隔线，用于增强纸片层次。不要把它做成明显工具栏或重型标题栏，纸片感要保留。

### 显示和隐藏

`AppController.ShowPaper()` 显示隐藏窗口时，有一个透明首帧处理：

```csharp
double originalOpacity = window.Opacity;
window.Opacity = 0;
window.Show();
window.Dispatcher.InvokeAsync(() => window.Opacity = originalOpacity, DispatcherPriority.Render);
```

目的是避免隐藏窗口尺寸变化后重新显示时出现 DWM 缓存闪烁。不要轻易删除，除非你能验证隐藏/显示、胶囊恢复、显示全部都没有一帧错乱。

### 胶囊模式

胶囊模式由全局 `AppState.UseCapsuleMode` 控制。

单张纸还有 `PaperData.IsCollapsed`，表示这张纸当前是否折叠成胶囊。

胶囊自动贴边由 `AppState.UseDeepCapsuleMode` 控制。它是胶囊模式的附加行为：开启时会自动启用胶囊模式，并将当前可见的折叠胶囊按纸片顺序排列到屏幕右上角。

设计要求：

- 启用胶囊模式后，右上角关闭按钮折叠成胶囊，而不是隐藏纸片。
- 折叠成胶囊时默认有效置顶；恢复普通纸片后回到用户原本的置顶状态。
- 关闭胶囊模式后，所有纸片恢复普通纸片形态。
- 隐藏全部纸片时，必须清掉 `IsCollapsed`，避免之后显示全部出现尺寸或状态 bug。
- 拖动胶囊时，待点击状态应该消失，避免拖动结束误触发。
- 胶囊左侧和右侧热区高度、视觉反馈要一致。
- 启用胶囊自动贴边时，普通胶囊 UI 不改变，只改变位置：常态半隐藏到右侧屏幕边缘，悬浮时用位移动画滑出，点击恢复后贴近右侧边框展开。
- 胶囊自动贴边的位置是运行时位置，不要把半隐藏坐标写入 `paper.X` / `paper.Y`。

这块很容易发生“隐藏纸片后再恢复显示尺寸错乱”的回归。改动后必须手动测。

## 9. 待办纸逻辑

待办纸数据来自 `PaperData.Items`。

主要行为：

- 至少保持一个空待办项。
- Enter 在当前项下方新增。
- 空项 Backspace 删除。
- 多行粘贴拆分为多条待办。
- 粘贴时清理常见列表前缀。
- 勾选完成只改变显示状态，不自动移动到底部。
- 支持拖动排序。
- 支持拖到末尾追加区变成的删除区删除。
- 支持 `Ctrl+Z` / `Ctrl+Y`。

待办撤销/重做：

- `PaperWindow` 内维护 `_undoStack` 和 `_redoStack`。
- 快照是 `List<PaperItem>` 的克隆。
- 最大深度 `MaxUndoDepth = 100`。

拖动排序相关状态：

- `TodoDragState`
- `_dragLayer`
- `_activeDropRow`
- `_appendArea`

拖动逻辑比较集中但脆弱。改动时要覆盖：

- 同一列表中上移。
- 同一列表中下移。
- 拖到最后。
- 拖到删除区。
- 拖动中取消。
- 窗口失焦时取消拖动。

## 10. 笔记纸和 Markdown 逻辑

笔记纸使用同一个 `MarkdownTextBox` 在编辑和只读浏览之间切换。

基本模型：

- 内容区只有一个 `MarkdownTextBox` 实例。
- 编辑时切换为可编辑状态。
- 浏览时切换为只读状态。
- 内容保存在 `PaperData.Content`。
- 两态共用同一份 AvalonEdit 文档、换行、缩进和滚动模型，避免浏览 / 编辑切换时文本跳动或滚动条长度变化。

Markdown 支持范围应该保持轻量：

- 标题。
- 加粗、斜体、删除线。
- 无序/有序列表。
- 引用。
- 行内代码。
- 代码块。
- 链接。

不建议加入：

- 图片。
- 表格。
- HTML。
- 附件。
- 嵌入式内容。
- 复杂块编辑器。

加入这些能力会让一张纸变成文档编辑器，和产品边界冲突。

## 11. 主题与资源刷新

主题模式在 `AppState.Theme` 中保存，值通常为：

- `system`
- `light`
- `dark`

主题变化后要做三件事：

1. 将 `AppState.Theme` 设为 `system`、`light` 或 `dark`。
2. 保存状态。
3. 刷新所有 `PaperWindow.UpdateTheme()`。
4. 刷新托盘菜单。

WPF 动态资源能处理部分刷新，但不是全部。尤其是代码里动态生成的控件、AvalonEdit 文本视图、托盘菜单项，都需要主动刷新视觉样式。

## 12. 多语言规则

用户可见文本必须走资源文件。

新增文本时至少修改：

- `Resources/Strings.resx`
- `Resources/Strings.en.resx`
- `Resources/Strings.ja.resx`
- `Resources/Strings.ko.resx`

不要只改中文。

资源版本：

- `ResourceTextVersion` 是人工检查用。
- 它不参与运行时逻辑。
- 不要写启动时资源版本校验。
- 不要因为资源版本不匹配阻止程序运行。

## 13. 打包和发布策略

当前官方发布思路是两个直接发布的 Windows x64 单文件 exe，由 `.github/workflows/release.yml` 在 GitHub Actions 中构建。

- 推送 `v*` tag 或手动触发 workflow 时创建 / 更新 GitHub Release。
- 推送 `main` 时构建并上传 Actions artifact，用于提前检查软件包。
- Release 资产直接上传 exe，并附带 `SHA256SUMS.txt`、`.sig` 和 `.crt`，不再套 zip。
- 两个 exe 都在 GitHub Actions 中使用 Sigstore/cosign keyless 签名；这是基于 GitHub OIDC 身份的云端签名，不是 Windows Authenticode 代码签名。

### 主发布版

自包含、单文件、R2R、压缩、不 Trim。

参考参数：

```powershell
dotnet publish .\PaperTodo.csproj -c Release -r win-x64 --self-contained true -o 输出\PaperTodo-v1.5-win-x64-self-contained-compressed\ -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false
```

特点：

- 用户不需要安装 .NET Desktop Runtime。
- 文件更大。
- 单文件压缩主要影响冷启动，运行中影响很小。
- R2R 对冷启动有一定帮助，但收益有限，需要实测。

### 轻量版

框架依赖、单文件、不 R2R、不压缩、不 Trim。

参考参数：

```powershell
dotnet publish .\PaperTodo.csproj -c Release -r win-x64 --self-contained false -o 输出\PaperTodo-v1.5-win-x64-no-runtime-uncompressed\ -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false
```

特点：

- 用户机器需要安装对应 .NET Desktop Runtime。
- 体积更小。
- 框架依赖单文件不能用自包含那套 R2R 预期。
- 不要对它开压缩来期待收益；依赖不被一起打进单文件时，压缩意义很小。

### 不建议使用 Trim 和 Native AOT

不要给 WPF 版本开启：

- `PublishTrimmed=true`
- Native AOT

原因：

- WPF 本身和部分库依赖反射、资源、XAML 编译产物。
- 强开 Trim 已经出现过运行崩溃。
- Native AOT 对 WPF 桌面应用并不是正常支持路径。
- “Trim + Native AOT 一起强开”不是合理优化方向。

如果未来要探索，只能作为独立实验分支，不要合入正式发布参数。

## 14. 构建和验证建议

普通编译检查：

```powershell
dotnet build -p:OutputPath=输出\_codex-build-check\
```

注意：`输出\_codex-build-check\` 是编译检查目录，不等同正式发布包。某些托盘、单文件、资源、图标、运行时加载行为必须在正式发布输出里测。

正式验证建议：

- 至少测一次主发布版。
- 至少测一次轻量版。
- 不要只看 `dotnet build` 通过就认为发布行为没问题。

## 15. 回归测试清单

每次改 UI、托盘、保存、发布参数后，至少检查这些：

1. 首次启动没有数据文件时，会创建一张待办纸。
2. 新建待办纸和笔记纸正常。
3. 退出后重新启动，纸片位置、大小、内容、置顶、主题恢复。
4. 隐藏单张纸片后，托盘能重新显示。
5. 隐藏全部后，显示全部正常。
6. 启用胶囊模式，单张纸折叠和恢复正常。
7. 胶囊模式下隐藏全部，再显示全部，不出现尺寸错误或空白。
8. 拖动胶囊不会保留待点击态。
9. 待办项 Enter、Backspace、多行粘贴、拖动排序、拖动删除都正常。
10. 待办撤销/重做正常。
11. 笔记纸编辑和 Markdown 预览正常。
12. 主题在系统/浅色/深色之间切换，纸片和托盘都刷新。
13. 托盘首次右键菜单位置正确。
14. 托盘菜单打开后，第一次点击纸片不会被吞。
15. 托盘菜单失焦行为没有破坏主程序点击。
16. 托盘纸片列表删除确认中，确认和取消都能点击，hover/pressed 状态明显。
17. 删除最后一张纸片后，会自动补一张待办纸。
18. 外部 `PaperTodo.ico` 存在时优先作为托盘图标。
19. 外部 `PaperTodo.ico` 不存在时使用内嵌图标。
20. 外部图标损坏时不会导致程序启动失败。
21. 退出后没有残留 `PaperTodo.exe` 进程。
22. `PaperTodo.crash.log` 不会无限增长。
23. 正式发布包内不要混入 `data.json`、`data.backup.json` 这类开发运行数据。

## 16. 已知高风险区域

### 托盘菜单

风险最高。Hardcodet 的自动 ContextMenu 行为、WPF ContextMenu 焦点模型、任务栏图标首次打开行为之间耦合很深。

改动原则：

- 优先保持库默认路径。
- 优先用 `IconSource`。
- 不要手动模拟右键菜单弹出。
- 不要加全局鼠标轮询关闭菜单。
- 不要用“鼠标移到纸片就关闭菜单”这种方案。

### 胶囊模式

风险来自窗口尺寸、普通模式尺寸、胶囊尺寸、隐藏/显示之间的状态同步。

改动原则：

- `paper.Width` / `paper.Height` 只保存普通纸片尺寸。
- 胶囊尺寸来自 `PaperLayoutDefaults.CapsuleWidth` / `CapsuleHeight`。
- 折叠状态改变时要避免 `UpdateGeometry()` 把胶囊尺寸写回普通尺寸。
- 隐藏全部要清理折叠状态。

### 异步保存

风险来自旧保存覆盖新保存。

改动原则：

- 保留 `_saveVersion`。
- 保留 `StateStore` 的写入锁。
- 保留版本判断。
- 退出时同步保存。

### 用户数据兼容

`data.json` 是用户数据。不要随意破坏字段兼容。

新增字段时：

- 给默认值。
- 在 `Normalize()` 中处理 null、非法值、历史数据。

删除字段时：

- 先确认没有旧数据迁移问题。
- 尽量保留兼容读取。

## 17. 不要做的事情

除非用户明确改变产品方向，否则不要做这些：

- 主窗口。
- 账号系统。
- 同步系统。
- 分类、标签、搜索、归档。
- 复杂任务管理功能。
- 项目、优先级、截止日期、提醒、统计。
- 内置数据库。
- WebView/Electron/Tauri 迁移。
- 大型设置页。
- 把 Markdown 纸扩展成完整文档编辑器。
- 为了性能牺牲纸片视觉效果，例如显示空白帧、闪烁、先低质量后恢复。

## 18. 可以考虑的改进方向

这些方向相对符合产品边界，但仍需要保持克制：

- 发布后自动校验两个正式 exe 的 SHA256 和 cosign 签名。
- 发布 workflow 继续防止 `data.json`、`data.backup.json` 这类运行数据混入正式资产。
- 更轻的首次启动性能记录脚本。
- 对 `data.json` 增加显式 schema/version 字段，用于未来迁移。
- 给外部自定义图标加载失败增加一次性日志，而不是静默失败。
- 增加“复制当前纸片内容”的轻量操作。
- 增加“复制全部待办纯文本”的轻量操作。
- 增加极简导出为 Markdown，但不要做导入管理器。
- 给 Markdown 渲染失败提供更明确的错误提示。
- 把过大的 `PaperWindow.cs` 拆成 partial 文件，但只按真实边界拆，例如 Todo、Note、Capsule、Menus，不要为了形式拆出复杂架构。

## 19. 代码风格和架构注意事项

当前项目偏单文件、直接 WPF 代码生成控件，没有 MVVM 框架。

这不是失误，而是和项目规模、交互密度有关：

- 控件大量动态生成。
- 纸片窗口状态直接对应 `PaperData`。
- 功能集中在少数窗口和托盘入口。
- 引入完整 MVVM 框架会增加样板和复杂度。

可以逐步整理，但不要一次性架构重写。

如果要拆分：

- 优先用 `partial class PaperWindow` 按功能拆文件。
- 优先保留现有状态模型。
- 不要引入依赖注入容器。
- 不要引入复杂消息总线。
- 不要引入数据库或 ORM。

## 20. 和用户协作时的风格总结

用户偏好非常具体、结果导向，协作时要注意：

- 用户不喜欢只给建议，通常希望直接改、直接试、直接验证。
- 用户会连续追问原因，不满足于“应该是库问题”这种模糊回答。
- 用户会用实际体验验收，尤其是 UI 细节、首次点击、菜单位置、胶囊状态、启动速度。
- 用户能接受试错，但不能接受重复引入已经证明无效或愚蠢的 workaround。
- 用户讨厌把问题绕开，例如用粗糙关闭菜单、牺牲视觉效果、隐藏闪烁、吞点击来换表面正常。
- 用户希望每个功能改动同步多语言资源。
- 用户重视正式发布包，不只看 Debug 或普通 build。
- 用户喜欢把发布产物放在根目录 `输出` 下，并且命名清楚。
- 用户希望 README 和版本日志跟实现同步，不要留下过期描述。
- 用户不需要过度礼貌和空泛鼓励，更需要清晰判断和可执行操作。
- 用户会指出“类似但不一样”的项目或目录，接手时必须严格区分参考源和当前项目。
- 用户对性能问题会要求实测，但如果差异很小，也接受停止测试。
- 用户在 UI 设计上重视“看上去对称、自然、不怪”，很多小差异都会被认为是真 bug。
- 用户会要求解释“为什么会这样”，回答时要给机制层面的原因，而不是只说改好了。

给这个用户工作时，建议采用这种节奏：

1. 先读当前代码和相关历史，不要凭空改。
2. 简短说明你要改哪里。
3. 直接实施。
4. 做最相关的验证。
5. 明确说明改了什么、为什么、有没有没验证的点。

## 21. 给下一个接手模型的最短提示

如果你只来得及读一小段，读这里：

- 这是 WPF 桌面纸片工具，不是任务管理系统。
- `AppController.cs` 是全局控制器；`PaperWindow.cs` 是单张纸片；`StateStore.cs` 管 `data.json`。
- 托盘图标必须用 `TaskbarIcon.IconSource = LoadTrayIconSource()`，不要改成 `System.Drawing.Icon`，否则会复发首次右键菜单位置错误和首次点击被吞。
- 外部 `PaperTodo.ico` 是用户自定义托盘图标，存在时优先；没有才用内嵌资源。
- 不要开 `PublishTrimmed` 或 Native AOT。
- 胶囊模式和隐藏/显示状态很容易回归，改后必须手测。
- 新增用户可见文本要同步中文、英文、日文、韩文资源。
- 文档和发布说明要跟代码保持一致。
- 用户要的是实际可用、视觉不怪、行为稳定的桌面小工具，不要用复杂架构或粗糙 workaround 换表面修复。
