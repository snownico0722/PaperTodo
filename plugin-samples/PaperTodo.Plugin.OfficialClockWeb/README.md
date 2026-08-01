# 官方 Web 时钟插件源码

这个目录保存 `official.clock.web` 的源码。它与原生时钟提供接近的功能，用于对照同一产品如何分别使用 Web 与 WPF 实现：

- 12 / 24 小时制、秒数、日期、星期、周数和日进度；
- 本地、UTC 和多个常用城市时区；
- 多种日期格式、标题模式和显示缩放；
- `initialize`、`settingsChanged`、`themeChanged`、`visibilityChanged` 生命周期；
- `setDisplayTitle` 同步纸片与胶囊标题。

Web 插件不需要编译，部署产物是 `plugin.json` 和 `web/` 的原样副本。仓库中的可加载副本位于：

```text
plugins\official.clock.web\
```

修改源码后，将本目录的 `plugin.json` 和 `web/` 同步到上述目录即可重载。PaperTodo 的本地发布和 GitHub Release 不携带该插件。
