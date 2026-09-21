# Protocol 2.1 Top Bar 示例

这是一个 Web 插件示例，用于演示 Paper Top Bar、provider Runtime Global Top Bar、插件快捷键、自身纸片控制、只读 Workspace API，以及插件 manifest 多语言文案。

- `web/index.html`：注册当前 Body session 的 Paper action，并演示折叠、隐藏自身纸片。
- `web/runtime.html`：发布 Global action 和 provider 各 Paper 的长期 presentation，并接收自定义 `runtime.ping` 快捷键动作。
- `plugin.json`：声明 `capabilities: ["runtime"]`，但有意省略 `runtime` 路径；宿主会默认加载 `entry` 同目录中的 `runtime.html`（本例即 `web/runtime.html`）。
- `plugin.json` 的 `locales`：以原有 `name`、`description` 和 settings 文案为默认回退，为 `en-US`、`ja-JP`、`ko-KR` 覆盖宿主显示文本。语言包只覆盖显示文案，不改变 setting ID、select value、快捷键 action 等稳定协议字段。

完整 API、生命周期和部署规则见[插件开发手册](../README.md)，快捷键行为见[Protocol 2.1 快捷键说明](../PROTOCOL-2.1-SHORTCUTS.md)。安装、修改或删除插件文件后需要重启 PaperTodo。
