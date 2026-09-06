# PaperTodo 官网

纯静态页面，直接部署 `website/`；没有构建步骤、前端框架、远程字体或运行时 CDN。现有 Pages 工作流只在主分支更新时发布，PR 不会替换线上官网。

## 本地预览

在仓库根目录运行：

```sh
python -m http.server 8000 --directory website
```

浏览器打开 `http://localhost:8000/`。`?lang=en` / `?lang=zh` 优先于浏览器保存的语言偏好；切换语言保留其他查询参数和锚点。

## 修改入口

- `index.html`：静态正文、语义结构、下载入口和 SEO 元数据。中文为无 JavaScript 时的默认内容；对应英文在 `data-en` / `data-en-label` / `data-en-placeholder` 中。
- `assets/site.css`：布局、纸张配色、窄屏和减少动态效果适配。页面使用原生滚动，不接管滚轮或 PageUp / PageDown。
- `assets/site.js`：单份演示状态及事件。手机和桌面复用相同纸片与预览，不维护两套内容。窄屏用显式按钮代替悬停和标题拖拽。

官网是产品介绍与交互模拟，**不是 PaperTodo Web 客户端**。演示输入仅在当前页面内存中保留，刷新会清除；只尝试保存语言偏好，存储不可用时仍可操作。计时器只有主动开始后才运行；脚本按钮只改变页面内的模拟结果，不执行 PowerShell、不访问示例站点、不调用系统桥接。Markdown 只演示一小部分语法，以 DOM 文本节点呈现输入，不接受用户 HTML。

新功能、插件与实验室能力需核对当前实现、`CHANGELOG.md` 和 Releases，尚未进入正式版的内容保持 Preview 标记。下载链接指向 Releases，不在网页里猜测版本号、文件名或接口返回。插件安装说明不能暗示随主程序捆绑；许可文案以根目录 `LICENSE.md` 为准，勿改成无条件商业免费或无条件开源。

## 浏览器回归检查

在仓库根目录运行（建议独立 Python 虚拟环境）：

```sh
python -m pip install -r scripts/website/requirements.txt
python -m playwright install chromium
python scripts/website/test_website.py
```

默认测试会启动临时 HTTP 服务，实际加载 HTML、CSS、JS，检查资源响应、语言 URL、键盘/触屏交互、共享数据、拖拽边界、计时与不执行脚本的边界。布局覆盖 320～1920 CSS 像素、中英双语、短屏和横竖布局；这是 Chromium 模拟，不等同于实机 Safari 或 Windows 原生应用测试。

可用 `--browser /path/to/chromium` 指定已有浏览器，用 `--screenshots /tmp/papertodo-website` 输出截图。严格离线环境可用 `--inline`：将相同 CSS/JS 注入页面渲染，**不验证 HTTP 资源交付、查询参数初始化或持久化**，对应测试明确跳过。正常开发和 CI 应使用默认 HTTP 模式。

网站回归工作流仅检查网站相关路径，不编译或发布 WPF 应用。新增完整浏览器依赖仅用于测试，不随官网部署。
