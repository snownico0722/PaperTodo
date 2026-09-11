# AvalonEdit 侧边预览实验

此分支是可回退的后端对照，不是已接受的架构变更。当前正式技术方向仍见 `../../doc/ARCHITECTURE.md`（从本目录为 `../../doc`）。不合并 #243 或 #238，不修改动画调度和 DComp 交接。

## 范围

复用 `MarkdownTextBox`、`MarkdownSemanticDocument`、`MarkdownSemanticPresentation` 与正文的链接命中方法。现有显示层依赖 MarkdownTextBox，因此先实测整个只读组件，不先把正文拆成另一个 TextView 框架。

预览拥有独立、最多 16 块/6000 字符的 TextDocument，不共享正文的可编辑文档，不接入图片存储或保存流程。使用无 ScrollViewer 的模板并移除编辑输入处理；内部图片仅保留占位。只在预览生命周期内存在，卸载时释放语义订阅，没有编辑器池或每篇笔记常驻缓存。

为隔离变量，仍复用原预览的尺寸估算和内容选择。旧渲染器暂时保留给尺寸计算及对照测试，不代表决定长期维护两个后端。若实验被接受，再独立清理旧实现。

## 执行

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release -- --profile --avalon
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release -- --profile
```

最后一条测当前工作树原来的预览后端。对照点为 main `13a2224c` 与 #243 `b88322fe`。测试程序集和实验提供器可覆盖到这两个只读工作副本，测旧后端时不实例化实验提供器；不把实验代码合入这些分支。

## 测量口径

同一 Windows runner、相同正文、固定 460×410 卡片、真实 EdgeCapsuleHost/Presenter、同样 160ms 动画。每组 2 次预热后记录 7 个独立新视图样本，输出全部样本、中位数、最大值。分别记录尺寸计算、构造、挂载、从尺寸计算开始到布局就绪、从挂载到布局就绪、应用侧帧间隔和 UI 线程分配。

这里的就绪是 WPF 布局完成，不是 GPU 呈现时间；不额外订阅 Rendering 驱动动画，不覆盖完整多 HWND DComp 交接。7 个样本的最大值不冒充可靠的 p95。不同后端的 Markdown 支持范围可能不同；样例输入保持一致，最终外观一致性仍需复核。

AvalonEdit 按文档行复用可见排版，但一条很长的源行可能仍需排版其全部折行。因此不预设它一定比 #243 的按可见折行绘制更快。

检查覆盖预算、真实布局、禁止滚动、禁止编辑、链接坐标/裁剪、收起交互、内容更新、缩放、空内容及卸载/重新挂载。正式选型前仍需真机检查字体、混合 DPI、图片占位和鼠标点击手感。
