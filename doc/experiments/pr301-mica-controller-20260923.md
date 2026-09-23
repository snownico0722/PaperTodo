# PR #301 — MicaController 可调云母实验（2026-09-23）

## 目标

验证 PaperTodo 是否能在保留当前单 WPF HWND、正文/控件 100% 不透明的前提下，使用 Windows App SDK `MicaController.TintOpacity` / `LuminosityOpacity` 为展开窗口提供真正可调的 Mica。

## 已验证

- 正常 PaperTodo 构建不启用实验宏，继续使用 #227 的 `DWMWA_SYSTEMBACKDROP_TYPE` 路线。
- 实验构建使用 `Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.9`；相比 WindowsAppSDK 元包，它不会破坏现有 WebView2 WPF 编译资产。
- Windows App SDK self-contained 实验构建可以编译。
- `MicaController`、`SystemBackdropConfiguration`、DispatcherQueue 和现有 WPF `PaperWindow` 可以在同一进程中初始化到创建 Composition target 之前。
- 前景 WPF `Window.Opacity`、纸面 `Opacity` 与编辑器树在实验中始终保持不变；不使用整体窗口透明度。

## 阻塞结论

对 PaperTodo 的现有顶层 WPF HWND 调用 `ICompositorDesktopInterop.CreateDesktopWindowTarget` 时，Windows 返回：

```
0x88980800
DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED
```

已分别验证：

1. `isTopmost = false`：失败；
2. `isTopmost = true`：同样失败。

这说明 WPF 已经拥有该顶层 HWND 的 DirectComposition 表面；公开 API 没有把 WPF 的 `HwndTarget/HwndSource.CompositionTarget` 转换成 Windows.UI.Composition `CompositionTarget` 或 `ICompositionSupportsSystemBackdrop` 的桥。

因此，官方 Win32 `MicaController.SetTarget(WindowId, CompositionTarget)` 路线无法直接挂到 PaperTodo 当前单 WPF HWND 上。

## 为什么不继续绕

- `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW` 可以在 WPF 上正常使用，但只选择系统 Mica 类型，不暴露 `TintOpacity` / `LuminosityOpacity`。
- `DWMWA_USE_HOSTBACKDROPBRUSH` / `ACCENT_ENABLE_HOSTBACKDROP` 只允许应用创建 HostBackdropBrush，本身不是可调 Mica；真正使用 brush 仍需要应用自己的 Composition target，因此会回到同一所有权冲突。
- 新建第二个顶层 HWND 专门承载 MicaController，再跟随 PaperWindow 的移动、缩放、DPI、Snap、Z-order、激活和生命周期，技术上可以继续试，但会恢复 #227 刚清掉的额外窗口/同步职责，当前实验明确不采用。
- 私有反射、Compositor VMT hook 或夺取 WPF 内部 DirectComposition 对象属于版本脆弱方案，不作为 PaperTodo 正式实现候选。

## 当前可行边界

在保持单 WPF HWND 的前提下：

- **系统默认 Mica**：继续使用 #227 的 DWM 路线；
- **比默认更实**：可以在 Mica 上叠加可调 WPF tint/cover，前景仍完全不透明；
- **比系统默认 Mica 更“透”**：现有公开 DWM Mica API没有可降低系统 Mica 自身 tint/luminosity 的参数。叠加层只能增加覆盖，无法从系统 Mica 中减去覆盖。
- 改成 Acrylic / Clear Acrylic 可以产生更透明的视觉，但语义已经不是 Mica。

## 测试策略

专项测试接受两种结果：

1. 如果某个未来 Windows/WPF 组合允许 MicaController target：继续验证 Medium=SDK 默认、VeryLow 增加覆盖、VeryHigh 降低覆盖、深浅色重新读取默认基准；
2. 当前环境返回 `0x88980800`：要求 #227 DWM Mica fallback 仍保持有效、同一 HWND/编辑器树不变、前景 opacity=1。

这不是把失败改成忽略，而是把已经确认的 WPF 组合边界变成可重复验证的兼容性结论。
