# 扩展 API 示例（Web）

将 `plugin.json` 和 `web/` 复制到 `plugins/sample.extension.web/`，重启后创建一张使用本示例的插件纸片。

Runtime 为当前 Markdown 添加操作。新建其他笔记后点击示例纸片里的“刷新纸片操作”。`panel.html` 是独立辅助前端，复用颜色变量，不是假 Paper，也没有 Body 的 `saveState`。HTML 内容可以访问同一权限的 Workspace，并使用 `papertodo.surface.post` 向创建者发消息；本例只读图片 MIME/字节长度。

Native 控件样式仅适用于 WPF；Web 下拉框仍然是 HTML 控件。第一版辅助前端不提供下载、嵌套开壳和持久化状态接口。详见上级 `README.md`。
