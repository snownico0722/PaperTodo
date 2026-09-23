# PR #301 — MicaController 可调云母实验（2026-09-23）

## 最终结论

**不把 Windows App SDK `MicaController` 接入 PaperTodo 的 `PaperWindow`。**

实验确认 `MicaController` 可以在独立 WPF / Win32 HWND 上创建并设置 `TintOpacity` / `LuminosityOpacity`，但把它直接挂到 PaperTodo 现有顶层 WPF HWND 后，会改变 WPF 前景最终合成结果。真实测试已经出现正文/控件被材质面覆盖、纸片变成纯白块的现象；CI 的独立前景探针也记录到纯白标记被改成 `R=243 G=243 B=243`。

这个结果违反 PaperTodo 的硬约束：

- 文字、图标、Markdown、Todo、输入框必须保持 100% 不透明；
- 不新增第二个顶层窗口；
- 不重建正文树；
- 不为了材质强度破坏 #227 已验证的单 HWND / DWM Mica 路线。

因此 #301 的产品结论是：**可调 MicaController 路线否决，PaperWindow 保持 #227 的 DWM Mica。**

## 最终实现

### PaperWindow

正式窗口只使用：

```text
DWMWA_SYSTEMBACKDROP_TYPE
        ↓
      DWM Mica
        ↓
WPF 正文继续由原来的重定向表面负责
```

`NativeMicaBackdrop` 不再持有 `AdjustableMicaControllerBackdrop`，也不再为它订阅窗口激活事件或保留运行时切换分支。

实验构建仍保留独立的 `AdjustableMicaControllerBackdrop`，只用于验证 Windows App SDK 互操作本身；它不会被产品 `PaperWindow` 创建或持有。

### 白块问题

白块不是普通颜色参数问题，而是 composition ownership 问题。

早期实验让 `MicaController` 直接 targeting PaperTodo 的顶层 WPF HWND。该 controller 创建的 composition surface 会参与最终窗口合成，实机出现过整片材质覆盖 WPF 前景的结果。

最终处理不是继续给这条路径加补偿，而是：

- PaperWindow 完全退出 MicaController；
- 回到已验证的 DWM Mica；
- 回归测试明确要求实际 Mica 必须来自 DWM type 2；
- 不再允许测试把 “DWM 或 MicaController 任一成立” 当作通过条件。

## 同时修复：原生材质窗口的 Opacity 动画

#301 测试过程中还发现 #227 原有显隐流程存在另一个独立问题：

```text
Cannot animate the 'Opacity' property on a 'PaperTodo.PaperWindow'
using a 'System.Windows.Media.Animation.DoubleAnimation'.
```

根因是 `AllowsTransparency=false` 的原生材质窗口仍可能收到整窗 `Window.Opacity` 动画。

修复后：

- 只有 `AllowsTransparency=true` 的旧 layered WPF 纸片保留整窗淡入淡出；
- native material PaperWindow 始终保持 `Window.Opacity == 1`；
- Hide / Show 对原生窗口直接显隐，不安装整窗透明度动画时钟；
- 切换 Acrylic → Clear Acrylic → Aero → Pixel → Mica 时保持同一 HWND、同一编辑器树和不透明前景。

## 回归边界

普通 `PaperTodo.MicaChecks` 不引用实验类型，因此不开 `PaperTodoMicaControllerExperiment` 时也必须正常编译并运行。

实验构建额外验证：

1. 独立 MicaController 探针仍能创建，证明依赖和 ABI 接法没有失效；
2. PaperWindow 的实际 Mica 来源必须是 DWM；
3. PaperWindow 产品路径不存在 `_adjustableMica` owner；
4. 白/黑前景标记保持不透明；
5. 原生材质窗口连续切皮肤不会安装 whole-window opacity animation；
6. Hide → Show 后仍是同一 HWND / 编辑器树，并恢复 DWM Mica。

## 依赖

实验配置继续使用：

```text
Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.9
```

它只属于 #301 的实验构建，不进入普通产品构建。

## 收尾标准

- 普通 Release build：必须通过；
- 普通 Native material / skin checks：必须通过；
- MicaController experiment：允许独立探针存在，但 PaperWindow 必须固定走 DWM；
- 文档和构建产物不得再宣称 MicaController 已接管 PaperWindow。
