# PaperTodo 插件源码

仓库中的插件目录有明确边界：

- `plugin-samples/` 保存插件源码、清单源文件和构建说明；
- `plugins/` 只保存已经构建、可由 PaperTodo 直接加载的最终产物；
- PaperTodo 的本地发布和 GitHub Release 都不携带插件，插件需要单独分发。

最终原生插件目录只保留 `plugin.json`、入口 DLL、必要的 `.deps.json`、插件私有依赖和原生库。不要放入 PDB、XML 文档、重复 DLL 或 PaperTodo 宿主已经提供的共享程序集。

## 原生插件构建与安装

所有原生 DLL 插件共用 `plugin-samples/Build-And-Install-NativePlugin.ps1`。脚本从项目同目录读取 `plugin.json`，执行 Release 发布，清理宿主共享程序集，并保留目标插件原有的 `.runtime` 数据。

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj

.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.FocusTimer\PaperTodo.Plugin.FocusTimer.csproj

.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.CloudGenshin\PaperTodo.Plugin.CloudGenshin.csproj
```

`PaperTodo.Plugin.FocusTimer` 是不依赖 WebView2 的完整 WPF 示例，包含原生交互、状态保存、后台运行、运行时标题和主题适配。纯 Web 插件不需要编译，直接将清单和 `web/` 静态文件复制到对应的 `plugins/<插件 ID>/` 目录。

## 部署目录

> **信任边界：PaperTodo 不为插件提供沙箱。** 原生插件以当前用户权限在主进程中运行，Web 插件允许联网；宿主仅拦截 Web 外部导航、远程 iframe、弹窗、下载和权限请求等轻度防误用行为，这些限制不构成安全隔离。只安装你完全信任的插件及其远程依赖。

每个插件使用一个与插件 ID 同名的自包含目录，不再区分 `web` 和 `native` 总目录：

```text
plugins\com.example.weather\
├─ plugin.json
├─ web\                      # Web 插件的静态根；原生插件不需要
│  ├─ index.html
│  └─ CSS、脚本与图片
├─ WeatherPlugin.dll / 依赖 DLL / 原生库   # 原生插件内容；Web 插件不需要
└─ .runtime\                 # PaperTodo 自动创建的该插件运行数据
```

当前 PaperTodo 插件协议为 **1.1**。`plugin.json` 必须包含 `kind`（`web` 或 `native`）、`id`、`entry`、`apiVersion` 和 `stateVersion`；`apiVersion` 必须是带引号的 `major.minor` 字符串，并精确声明为 `"1.1"`。协议不兼容的插件会被拒绝加载。目录名必须与 `id` 一致。

需要在纸片折叠为可见胶囊后继续运行的插件，必须声明 `"requires": ["backgroundUpdates"]`。未声明时，宿主会在完整正文不显示时通知插件暂停运行；未知的必需能力会拒绝加载。

Web 示例：

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "天气",
  "description": "天气信息面板",
  "version": "1.0.0",
  "apiVersion": "1.1",
  "stateVersion": 1,
  "entry": "web/index.html",
  "capabilities": ["textZoom"],
  "requires": ["backgroundUpdates"]
}
```

Web 插件的 `entry` 所在目录会成为唯一静态根；建议固定使用 `web/`，使同一插件目录下的 `.runtime/` 不会被网页映射。Web 插件仅能在自己的 `https://<id>.papertodo.local/` 虚拟源中运行。顶层外链会交给默认浏览器，远程 iframe、弹窗、下载和权限请求会被拦截。可通过 `window.papertodo` 调用：

```js
papertodo.saveState({ city: "Shanghai" }); // 每次状态变化后立即调用
papertodo.registerStateProvider(() => currentState); // 关闭前的辅助快照，不能替代即时保存
papertodo.setTitle("上海天气"); // 持久化、可由用户编辑的正式标题
papertodo.setDisplayTitle("26°C 晴"); // 运行时标题，同时显示在顶栏和胶囊
papertodo.setInputClaims(["escapeKey", "contextMenu"]); // 进入交互模式前声明
papertodo.setInputClaims([]); // 离开交互模式后释放
papertodo.markDirty();
papertodo.openExternal("https://example.com");
papertodo.onEvent(message => console.log(message));
```

协议 1.1 中，`setDisplayTitle` 是纸片顶栏和胶囊共用的运行时显示标题，不写入 `data.json`；传入空字符串会取消覆盖，恢复 `setTitle` 保存的正式标题。`setCapsuleText` 仍作为兼容别名，但新插件应使用 `setDisplayTitle`。

宿主发送 `initialize`、`stateChanged`、`activated`、`deactivated`、`visibilityChanged`、`presentationChanged`、`themeChanged`、`typographyChanged`、`dpiChanged`、`commitRequested` 和 `cancelInteractions`。`initialize` 提供 `apiVersion`、`stateVersion`、`targetStateVersion`、`visible` 和 `presentationVisible`。`visibilityChanged` 表示插件运行时是否应保持活动；只有声明 `backgroundUpdates` 的插件在折叠为可见胶囊时仍为 `true`。`presentationChanged` 只表示完整正文是否正在显示和可交互。Web 插件可迁移旧状态后立即 `saveState`。

`setInputClaims` 是动态输入占用声明，不是权限。声明 `escapeKey` 时，PaperTodo 不再用 Esc 折叠纸片；声明 `contextMenu` 时，只阻止插件正文区域继承的 PaperTodo 右键菜单，网页自己的右键事件仍会收到。插件应在进入输入模式前声明并在退出时释放；切换插件、重载、失败或销毁会话时，宿主会自动清空声明。

原生插件目录的 `entry` 指向实现 `PaperTodo.Plugin.IPaperBodyPlugin` 的入口 DLL，依赖、`.deps.json`、资源和本地库全部放在同一插件目录。协议 1.1 要求 DLL 显式实现 `ApiVersion` 和 `RuntimeRequirements`，并与 `plugin.json` 完全一致；不一致时拒绝加载。PaperTodo 为每个纸片创建新的插件工厂对象，`IPaperBodyPlugin` 不应保存纸片实例状态。原生会话可通过 `OnPresentationChanged` 判断完整正文是否显示，通过 `OnVisibilityChanged` 判断运行时是否应保持活动，并通过 `PaperBodyContext.SetInputClaims` 动态占用 Esc 或正文右键菜单；正文会话必须在 `Dispose` 中停止计时器、取消任务并解除事件。
未被任何纸片使用的原生插件在启动时只读取 `plugin.json`，不会加载 DLL 或调用构造函数；入口程序集会在首次创建对应正文时加载并校验。
