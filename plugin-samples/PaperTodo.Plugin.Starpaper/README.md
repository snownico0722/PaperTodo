# 星笺 · Starpaper

把知识放成星图，把待办变成有画面的行动卡片。

这是一个独立的 **PaperTodo API 2.1 Web 插件**，不是新的主管理页，不修改主程序、原始待办行、Markdown 编辑器或 Edge 窗口。没有账号、云端服务、联网请求和第三方前端依赖。可创建多张独立星图。

## 安装与开始

退出 PaperTodo，把安装包里的 `com.papertodo.starpaper` 文件夹放入 PaperTodo 的 `plugins/`，然后重新启动。最终位置必须是：

```text
PaperTodo.exe
plugins/
  com.papertodo.starpaper/
    plugin.json
    NOTICE.txt
    web/
      index.html
      ...
```

在 PaperTodo 新建一张笔记纸，在正文插件选择中选择 **星笺 · Starpaper**。首次打开是空星图；先点“新建知识”，或点“选择纸片”勾选现有待办纸、内置 Markdown 笔记纸。没有选择前，只读取纸片列表，不扫描待办或笔记正文。

需要宿主支持 API **2.1**；仅凭“4.0”版本字样不能判断协议兼容。插件文件变更后需要重启，不依赖热重载。更新时替换本插件文件即可，不要删除 `plugins/data/`；卸下插件文件并不等于备份或删除插件数据。

## 知识星图

- 新建、编辑、删除本地知识节点，使用纯文本正文和 `[[知识标题]]` 引用。可引用已选择的 Markdown 笔记标题。同名标题不猜测目标；引用按 Unicode NFC 归一化后精确匹配。
- 选中的待办纸生成中心节点，原始待办成为周围节点。Markdown 笔记显示为只读引用节点，正文从原纸片读取，不另外保存副本。
- 拖动节点摆放，拖动背景平移，滚轮缩放；双击空白处新建。选中一个节点后 Shift+单击另一节点连线，或用“关联”填写关系说明。
- 搜索标题与正文，筛选完成状态，聚焦相邻节点；“全图”定位所有匹配节点，“整理布局”恢复自动布局。
- Ctrl+Z / Ctrl+Y 撤销、重做星图编辑，包括知识、连线、布局和封面；**不会撤销原始待办的修改**。文本输入框保留浏览器自己的文本撤销。

自动布局是确定性的静态布局，不持续运行力导向动画。渲染会裁掉屏幕之外的几何，但不会截断数据；大量节点适合通过搜索、筛选和聚焦缩小浏览范围。没有未经测量的“无限节点流畅”承诺。

## 待办图鉴与插图

图鉴显示所选纸片的真实待办。可新建待办、编辑文字、标记完成或恢复未完成，修改全部通过宿主 Workspace API，使用原始 Paper ID + Todo ID。没有删除原始待办、删除纸片或写入笔记正文的权限。

八种原创离线矢量插画：星轨、书页、代码、生长、远行、活力、创作、专注。根据文字关键词选取默认主题，可手动切换。**这是确定性的本地插画，不是 AI 绘画，也不会把待办发送给模型。** 图片显示在星笺的图鉴和详情中，不会插入原始待办行。

知识、引用笔记和待办均可使用本地 PNG / JPEG / WebP / GIF，也支持粘贴或拖入单张图片。导入时转为静态 WebP 缩略图，最长边不超过 1024 像素；存储副本不依赖原文件路径。GIF 使用解码时的静态帧，不保留动画；输入 SVG / HTML、外部图片 URL 不作为封面执行或加载。为控制解码内存，输入上限为 20 MiB、3200 万像素；压缩后的每张封面不超过 512 KiB。

可以导出 **960×1120 PNG 插画卡片**和 **SVG 星图**。SVG 是节点、标题和关系的矢量示意，不是包含所有照片的画布截图。界面标签和图片卡片会按可视空间省略长标题，完整标题仍在详情、SVG 的 title 和原始数据中。

## 保存、同步与失败处理

知识、关联、位置、封面、所选来源和视图设置属于 per-paper frontend state，通过 `papertodo.saveState` 及时提交，由 `PaperBodyPluginDataStore` 保存。没有 localStorage、IndexedDB 或第二份主数据文件。`registerStateProvider` 是边界补交，不是唯一保存机制；Web bridge 的提交返回不等于磁盘持久化 ACK。

原始待办和引用笔记只存在于 Workspace 快照，不进入插件备份。原始对象被删除、自动清理或无法读取时，有关系的引用会成为“引用暂不可用”，不会自动删除关系和封面。取消选择来源也不删除原纸片或历史封面。局部编辑历史最多 30 步，并有独立内存预算；不是重启后持久化的撤销日志。

读取失败会保留上次完整快照，并禁用原待办编辑。刷新队列只发布最新完整结果，过期请求不能覆盖新选择。待办写入失败或确认丢失后不自动重发，只重新读取；用户应核对原待办后决定是否重试。文字编辑提交前还会检查最新快照，发现变化就拒绝覆盖；宿主 API 没有条件写入版本号，因此这不是跨进程原子 compare-and-swap。

导入备份前必须确认；只替换这张星图，可撤销，不会创建或覆盖原始待办。跨设备、重新创建的原始纸片可能具有不同 ID，备份不能自动恢复它们。损坏数据或未来版本数据会阻止写入，并提供原始数据导出，不会用空白状态覆盖。

宿主 API 2.1 的每张纸状态上限为 **10 MiB UTF-8 JSON**。提交前检测实际字节数，超限拒绝本次修改，不静默截断。大型图片集合建议分成多张星图；封面是预览，不是原始照片备份。

## Edge、生命周期和主题

正文使用宿主绘制的标准胶囊。独立 Edge Mini 为 **只读摘要**：未完成数量、知识数量和最多三项预览。它不保存状态、不注册顶栏或占用键盘输入，不把整个页面标记为交互区。`mini.ready()` 在首个工作区结果或错误视图完成布局后发送；窗口、队列、大小和 publication 仍由宿主控制。

不声明 provider Runtime、不做后台轮询。正文可见时通过 Workspace 事件更新，重新激活/显示时刷新；Mini 每次显示刷新。正文不存在时，胶囊展示的是最后一次正文发布的摘要，**不承诺常驻后台实时统计**。隐藏时停止绘制，销毁时释放订阅、刷新队列和 ResizeObserver。

正文与 Mini 接收 `stateChanged`，不原样回写造成回声。跟随宿主颜色与字体设置，支持缩小窗口和系统减少动效偏好。界面提供中文、英语、日语和韩语，可在插件设置选择；默认跟随系统语言。

## 开发与打包

源代码在本目录；`plugins/com.papertodo.starpaper/` 是可加载副本。API 和宿主边界以仓库的 `plugin-samples/README.md`、`doc/ARCHITECTURE.md`、`PaperTodo.Plugin.Abstractions` 为准，本文件不是另一份宿主架构。

在仓库根目录运行以下完整命令。Python 3.10+、Node 22+；不需要 npm install，也不编译 .NET 插件：

```powershell
node --test .\plugin-samples\PaperTodo.Plugin.Starpaper\tests\core.test.cjs
python .\plugin-samples\PaperTodo.Plugin.Starpaper\package.py --sync --output .\dist
```

打包脚本仅更新这个插件的文件，不删除 `.runtime/` 或 `plugins/data/`，也不会启动或终止 PaperTodo。更新已加载的插件前仍须自己退出主程序。`--check` 检查源代码与部署副本一致。

浏览器行为测试：

```powershell
python -m pip install playwright==1.57.0
python -m playwright install chromium
python .\plugin-samples\PaperTodo.Plugin.Starpaper\tests\browser_test.py --screenshots .\dist\starpaper-screenshots
```

测试用 API 2.1 mock 验证 DOM 交互、调用参数、失败与生命周期，不等于 Windows/WPF/WebView2 真机验证。默认测试直接通过本地 HTTP 加载未修改的 HTML/CSP；只有导航受限的测试环境才使用 `--inline`，该模式不验证 CSP 和资源加载。`CHROMIUM_EXECUTABLE` 可指定已有 Chromium。

直接打开 `web/preview.html` 可用内存演示数据体验；刷新即重置，不会修改 PaperTodo。`index.html` 脱离宿主只显示说明，绝不自动伪造用户数据。

## 许可与知识影响

沿用原项目授权，见 `NOTICE.txt` 与根目录 `LICENSE.md`。没有改变现有架构、ownership、插件合同、已确立技术路线或 Agent 执行规则，因此不改动 Architecture、Decisions 或 AGENTS，也不把独立分发的插件写成主程序内置功能。
