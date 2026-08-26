# PaperTodo 2.0：插件快捷键与自身纸片控制

这页只说明协议 2.0 新增的两类能力：

- 插件在自己的设置中声明全局快捷键；
- paper body session 请求显示、隐藏、展开、折叠或激活承载自己的纸片。

核心边界不变：**插件只声明意图，PaperTodo 仍然拥有 Windows 全局热键、PaperWindow、动画、边缘胶囊、焦点与布局。**

## 1. 快捷键写在插件 settings

`plugin.json` 使用 `type: "shortcut"`：

```json
{
  "settings": [
    {
      "id": "togglePaperShortcut",
      "type": "shortcut",
      "name": "显示 / 隐藏纸片",
      "default": "Ctrl+Alt+Shift+U",
      "shortcutAction": "paper.toggle",
      "quick": true
    }
  ]
}
```

用户在插件自己的设置区域中录制或修改快捷键；启用 `advancedSettings` 的插件也可以在独立“更多设置”页中修改。它不会作为第三方动作塞进 PaperTodo 的全局快捷键设置页。

PaperTodo 负责：

- `RegisterHotKey` / `UnregisterHotKey`；
- 与 PaperTodo 自带快捷键的冲突；
- 插件之间的冲突；
- Windows 已占用快捷键检测；
- 主键盘数字 / 小键盘数字别名规则；
- 设置保存、恢复默认和插件卸载 / 退出清理。

插件不要自己调用 Windows `RegisterHotKey` 来绕过这套管理。无法映射到真实 Windows 虚拟键的键名会在解析阶段直接拒绝，例如未定义的数值型 `Key`，不会拖到 `RegisterHotKey` 时才失败。

### 1.1 宿主自带的 paper action

当前支持：

```text
paper.show
paper.hide
paper.toggle
paper.expand
paper.collapse
paper.activate
```

`shortcutAction` 省略时默认是：

```text
paper.toggle
```

这些动作由 PaperTodo 直接执行，不要求 body session 当前展开，也不需要插件 plugin runtime 接收回调。

同一个 provider 有多张纸片时，宿主按这个顺序找目标：

1. 当前激活的同 provider 纸片；
2. 最近激活过的同 provider 纸片；
3. 当前可见的同 provider 纸片；
4. 同 provider 的 `startupPaper` 所有者纸片；
5. 第一张同 provider 纸片。

完全没有该 provider 纸片时，不会凭空猜一个多实例目标。

## 2. 插件自己的快捷键 action

插件也可以声明自己的 action id，例如：

```json
{
  "capabilities": ["appRuntime"],
  "settings": [
    {
      "id": "refreshShortcut",
      "type": "shortcut",
      "name": "立即刷新",
      "default": "Ctrl+Alt+R",
      "shortcutAction": "weather.refresh"
    }
  ]
}
```

自定义 action 必须满足：

- 协议版本为 `2.0`；
- 插件声明 `appRuntime`；
- action id 为 1～80 个 ASCII 字母、数字、`.`、`_`、`-`；
- plugin runtime 注册快捷键 action handler。

自定义 action 是 **provider/appRuntime 全局动作**。宿主不会替它选择某张纸片，也不会在回调中偷偷附加“当前纸片”语义；如果插件业务需要目标纸片，应通过 Workspace 数据自行决定。

### Native plugin runtime

```csharp
public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context)
{
    context.GlobalShortcuts.SetActionHandler(invocation =>
    {
        if (invocation.ActionId == "weather.refresh")
        {
            RefreshWeather();
        }
    });

    return new Runtime();
}
```

回调参数：

```csharp
public sealed record PaperShortcutActionInvocation(
    string SettingId,
    string ActionId);
```

### Web plugin runtime

`runtime.html`：

```js
papertodo.onEvent(message => {
  if (message.type === 'shortcutInvoked' &&
      message.actionId === 'weather.refresh') {
    refreshWeather();
  }
});
```

自定义 action 的 Windows 热键只在对应 plugin runtime 有**有效 handler**时注册。Web runtime 导航、进程失败或销毁时，PaperTodo 会立即释放这些自定义热键；页面重新 ready 后再恢复。这样 runtime 坏掉时不会继续抢占一个“按了没反应”的系统快捷键。

## 3. Native：控制承载自己的纸片

paper body session 使用：

```csharp
context.Presentation.Show();
context.Presentation.Hide();
context.Presentation.ToggleVisibility();

context.Presentation.Expand();
context.Presentation.Collapse();
context.Presentation.ToggleCollapsed();

context.Presentation.Activate();
```

公开合同：

```csharp
public interface IPaperPresentationApi
{
    string PaperId { get; }

    void Show(bool activate = true);
    void Hide();
    void ToggleVisibility(bool activate = true);

    void Expand(bool activate = true);
    void Collapse();
    void ToggleCollapsed(bool activate = true);

    void Activate();
}
```

这些调用只能作用于**承载当前 session 的那张纸片**。插件拿不到 `Window` / HWND，也不能借这个接口操作别的 PaperTodo 纸片。

调用会排队回到 PaperTodo UI Dispatcher，再进入已有 `ShowPaper`、`HidePaper`、`SetPaperCollapsedRuntime`、`ArrangeDeepCapsules`、`BringPaperToFront` 等宿主流程，不绕过已有状态机。

**Presentation 是请求接口，不是动画完成接口。** Native 方法返回只表示请求已经交给宿主；不表示窗口动画已经结束，也不保证之后不会被新的宿主状态或生命周期变化覆盖。

## 4. Web：控制承载自己的纸片

body 页面直接使用：

```js
await papertodo.paper.show();
await papertodo.paper.hide();
await papertodo.paper.toggle();

await papertodo.paper.expand();
await papertodo.paper.collapse();
await papertodo.paper.toggleCollapsed();

await papertodo.paper.activate();
```

需要控制是否激活窗口时：

```js
await papertodo.paper.show({ activate: false });
await papertodo.paper.toggle({ activate: false });
await papertodo.paper.expand({ activate: false });
await papertodo.paper.toggleCollapsed({ activate: false });
```

这些方法仍通过 PaperTodo host request，不直接操作 WebView2 外层窗口。Promise resolve 表示宿主已经接受/处理这次请求，**不代表视觉动画已经完成**。

## 5. 快捷键录制期间的处理

用户在插件设置里进入快捷键录制时，PaperTodo 会临时释放：

- PaperTodo 自带全局快捷键；
- 所有插件全局快捷键。

录制结束或取消后立即恢复，再用正常冲突检查验证候选组合。

这么做是为了避免录制一个已经存在的组合时，Windows 先触发旧 `WM_HOTKEY`，导致“本来想录键，结果执行了隐藏全部 / 新建纸片 / 插件动作”。

## 6. 示例

完整覆盖看：

```text
plugin-samples/PaperTodo.Plugin.TopBarWeb/
```

其中：

- `plugin.json`：`paper.toggle` + 自定义 `runtime.ping`；
- `web/index.html`：Web body 自身纸片折叠 / 隐藏；
- `web/runtime.html`：plugin runtime 接收 `shortcutInvoked`。
