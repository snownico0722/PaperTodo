# Protocol 2.1 插件多语言

PaperTodo 2.1 插件可以在 `plugin.json` 里用可选的 `locales` 覆盖宿主绘制的插件文案。没有 `locales` 的旧插件行为不变；没有匹配语言时继续使用原 manifest 文案。

```json
{
  "name": "天气",
  "settings": [
    {
      "id": "unit",
      "type": "select",
      "name": "温度单位",
      "category": "display",
      "options": [
        { "value": "c", "name": "摄氏度" },
        { "value": "f", "name": "华氏度" }
      ]
    }
  ],
  "advancedSettings": true,
  "settingCategories": [{ "name": "display" }],
  "locales": {
    "en": {
      "name": "Weather",
      "settingCategories": { "display": "Display" },
      "settings": {
        "unit": {
          "name": "Temperature unit",
          "options": {
            "c": "Celsius",
            "f": "Fahrenheit"
          }
        }
      }
    }
  }
}
```

## 匹配与覆盖

宿主按“完整 culture → 父级 culture → 两字母语言”匹配，例如 `en-GB` 可以回退到 `en`。没有命中时直接保留基础文案。

可以覆盖：

- 插件 `name`、`description`；
- `settingCategories`；
- setting 的 `name`、`description`、`suffix`、`placeholder`；
- select option 的显示名称；
- `startupPaper.title`。

`setting.id`、select option `value`、`shortcutAction`、权限、能力和入口路径不会被语言包修改。语言包中的未知 setting、option 或未使用的 locale 不影响插件加载。

## Native 插件读取当前语言

Body 和 provider Runtime 都可以直接读取当前 PaperTodo UI culture：

```csharp
var uiLanguage = context.UiLanguage;
```

`context.UiLanguage` 是该 Body/Runtime 创建时的语言快照；`PaperPluginEnvironment.UiLanguage` 则读取宿主当前实际生效的语言。普通界面语言修改仍在重启后生效，但完整切换工作区会先结束旧 session/Runtime，也可能在同一进程中应用目标工作区的语言。

插件应在每次创建 Body/Runtime，或收到 Web `initialize` 时读取宿主提供的语言，不要把它缓存为整个进程永远不变的值；这不表示宿主会向现有会话实时推送普通语言设置修改。

`locales` 只覆盖宿主绘制的 manifest/settings 文案；Native/Web 插件自己绘制的内容仍由插件自行组织翻译资源。

## Web 插件读取当前语言

Body、Mini、provider Runtime 和 Web Popup 的 `initialize` 消息都会包含 `uiLanguage`，值与 Native 的 `context.UiLanguage` 一致。

Body、Mini 和 provider Runtime 可以从宿主事件读取：

```js
window.addEventListener('papertodo', event => {
  const message = event.detail || {};
  if (message.type !== 'initialize') return;
  const uiLanguage = message.uiLanguage || 'en-US';
});
```

Web Popup 通过自己的 ready bridge 读取同一字段：

```js
const message = await window.papertodo.ready;
const uiLanguage = message.uiLanguage || 'en-US';
```

插件应优先使用宿主提供的 `uiLanguage`，不要依赖 WebView 或系统浏览器语言来判断 PaperTodo 当前界面语言。
