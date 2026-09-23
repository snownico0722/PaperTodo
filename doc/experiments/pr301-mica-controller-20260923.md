# PR #301 — MicaController 可调云母实验（2026-09-23）

## 结论

**路线成立。** PaperTodo 可以在保留现有单 WPF `PaperWindow` HWND、正文/控件 100% 不透明的前提下，使用 Windows App SDK `MicaController.TintOpacity` / `LuminosityOpacity` 调整真正的 Mica 背景强度。

不使用 `Window.Opacity` 模拟材质透明，也不新增第二个顶层窗口。

## 实现边界

- 仅 `PaperSkins.Mica` 使用实验 MicaController；Acrylic / Clear Acrylic / Aero 和胶囊/菜单静态快照保持 #227 原路径。
- 只有真实 `DwmMicaApi` 窗口启用 MicaController；FakeNative 单测继续验证 #227 的 DWM recipe。
- 初始化或运行时失败时仍可回退 `DWMWA_SYSTEMBACKDROP_TYPE` Mica。
- 正常非实验构建不引入该后端，便于独立评估依赖和包体成本。

## WPF 互操作关键点

早期实验曾错误得到：

```
0x88980800
DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED
```

根因不是“WPF 顶层 HWND 无法使用 MicaController”，而是 COM ABI 声明不正确。

最终可行接法：

- `ICompositorDesktopInterop.CreateDesktopWindowTarget` 按 ABI 使用 `[PreserveSig] int`。
- target 通过 `out IntPtr` 返回，再用 `DesktopWindowTarget.FromAbi` 投影。
- UI 线程 DispatcherQueue 使用当前线程模式，并按公开 WPF/Win32 interop 示例采用 `DQTAT_COM_NONE`。
- MicaController target 连接到现有 `PaperWindow` HWND；不替换 WPF 编辑器树和窗口 ownership。

## 五档实测

Windows CI 的真实 WPF PaperWindow 验证结果：

```
中：
TintOpacity        0.500
LuminosityOpacity  1.000

最低透明度：
TintOpacity        0.650
LuminosityOpacity  1.000

最高透明度：
TintOpacity        0.350
LuminosityOpacity  0.700

深色中档：
重新读取 SDK 默认
TintOpacity        0.500
LuminosityOpacity  1.000
```

“中”不主动写 opacity，保留 Windows App SDK 当前主题默认值；切换深浅色时重建 Controller 并重新读取对应默认基准。

## 前景与窗口不变量

真实窗口测试确认：

- `PaperWindow.Opacity == 1`
- paper chrome `Opacity == 1`
- Markdown / Todo / 图标 / 输入框不跟随 Mica 透明度变化
- HWND 不重建
- 编辑器树不重建
- 切换 Mica / Acrylic / Clear Acrylic / Aero / Pixel 后仍保持同一 HWND 和正文树

## 切皮肤崩溃修复

实机发现原生材质窗口切皮肤时可能抛：

```
Cannot animate the 'Opacity' property on a 'PaperTodo.PaperWindow'
using a 'System.Windows.Media.Animation.DoubleAnimation'.
```

根因是 #227 原有显隐流程仍可能给 `AllowsTransparency=false` 的原生 PaperWindow 安装整窗 `Window.Opacity` 动画时钟。原生合成路径切换后，旧动画时钟在后续 Render tick 上可能变成非法状态。

修复后：

- 只有 `AllowsTransparency=true` 的旧 layered WPF 纸片保留整窗淡入淡出；
- native / MicaController PaperWindow 始终保持整窗 `Opacity=1`；
- controller-managed 真实窗口回归会连续切换 Acrylic → Clear Acrylic → Aero → Pixel → Mica，并实际运行 Dispatcher 动画 tick；
- Hide → Show 也验证同一 HWND、同一编辑器树、MicaController 恢复。

## 构建与发布

实验依赖：

```
Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.9
```

使用 component package 而不是 WindowsAppSDK 元包，避免破坏 PaperTodo 现有 WebView2 WPF 编译资产。

当前 self-contained single-file 实验包：

- `PaperTodo.exe`：94,699,973 bytes，约 90.3 MiB
- 不要求另装 .NET Runtime
- Windows App SDK 原生依赖按单文件自解压机制使用

## 最终验证

提交 `8504fd0f` 的专项验证：

- 普通 #227 构建：SUCCESS
- MicaController 实验构建：SUCCESS
- 真实 WPF 五档 probe：SUCCESS
- controller-managed 切皮肤 / Hide / Show 回归：SUCCESS
- 完整材质回归：SUCCESS
- self-contained single-file publish：SUCCESS
- Edge diagnostics：SUCCESS
- Plugin samples：SUCCESS

普通 PR Build 另行保留完整 Markdown / Todo / WindowStack / Threading / Lifecycle / Native material 回归。
