# 官方 Web 时钟插件源码

这个目录保存 `official.clock.web` 的源码。它与原生时钟提供接近的功能，用于对照同一产品如何分别使用 Web 与 WPF 实现：

- 12 / 24 小时制、秒数、日期、星期、周数和日进度；
- 本地、UTC 和多个常用城市时区；
- 多种日期格式、标题模式和显示缩放；
- `initialize`、`settingsChanged`、`themeChanged`、`visibilityChanged` 生命周期；
- `miniEntry` 提供独立轻量时钟，收到初始化后完成首帧再调用 `papertodo.mini.ready()`；
- Web Mini 默认把点击和拖拽交给 PaperTodo；网页需要自己处理指针的局部区域使用 `data-papertodo-interactive` 显式声明，本示例的“暂停 / 继续”按钮即为示例；
- Mini 加载期间使用透明内容占位；只有当前文档通过 ready challenge 并跨过真实 Rendering publication boundary 后才显示 Web surface；
- `paper.setHeaderText` 与 `paper.setCapsulePresentation` 分别同步纸片顶栏和标准胶囊 presentation，胶囊按当前标题和日进度组件自动适配宽度；
- 正文可用较高频率对齐秒边界，但对宿主胶囊写入做去重，避免无意义地重复重建同一模板。

Web 插件不需要编译，部署产物是 `plugin.json` 和 `web/` 的原样副本。自定义 WPF 胶囊只属于 Native 插件；本示例的紧凑胶囊使用宿主标准 presentation，边缘快速浏览使用 Web `miniEntry`。仓库中的可加载副本位于：

```text
plugins\official.clock.web\
```

修改源码后，将本目录的 `plugin.json` 和 `web/` 同步到上述目录即可重载。PaperTodo 的本地发布和 GitHub Release 不携带该插件。

## Paper Runtime

`requires: ["backgroundUpdates"]` 的 Web 插件通过 `paperRuntime` 声明每张 Paper 独立的后台入口。时钟的定时器、标题与胶囊更新运行在 `web/paper-runtime.html`；`web/index.html` 只负责展开后的可见 UI，因此 Body 重建不会重启后台计时。

Paper Runtime 的文档实例有独立 authority：宿主在初始化后发放 document token，renderer reload 时旧文档只能提交最后一次 state，不能继续写入新的 runtime 文档；成功保存的 state/version 同时更新 runtime 内存快照，因此恢复不会退回启动时的旧状态。
