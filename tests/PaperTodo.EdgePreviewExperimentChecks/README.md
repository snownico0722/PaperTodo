# AvalonEdit 侧边预览实验

此分支是可回退的后端对照，不是已接受的架构变更。正式技术方向仍见 [ARCHITECTURE.md](../../doc/ARCHITECTURE.md)。基于 main `13a2224c549fcc7d6fbbd5c58a9b666d9f75086f`；不合并 #243 或 #238，不修改动画调度和 DComp 交接。

**结论：原型可运行，普通短文更快，但密集样式的应用侧动画回调间隔明显恶化。不能据此直接替换正式后端。**

## 范围

复用 `MarkdownTextBox`、`MarkdownSemanticDocument`、`MarkdownSemanticPresentation` 与正文的链接命中方法。现有显示层依赖 MarkdownTextBox，因此先实测整个只读组件，不先把正文拆成另一个 TextView 框架。

预览拥有独立、最多 16 块/6000 字符的 TextDocument，不共享正文的可编辑文档，不接入图片存储或保存流程。使用无 ScrollViewer 的模板并移除编辑输入处理；独立内部图片引用行只保留占位。只在预览生命周期内存在，卸载时释放语义订阅，没有编辑器池或每篇笔记常驻缓存。

本分支的 GUI 已将内置笔记预览提供器切到 AvalonEdit；待办和插件提供器不变。宿主只增加一个按坐标询问现有文字链接命中的入口，继续持有实际显示区域、鼠标路由和窗口生命周期。

为隔离变量，仍复用原预览的尺寸估算和内容选择。旧渲染器暂时保留给尺寸计算及对照测试，不代表决定长期维护两个后端。若实验被接受，再独立清理旧实现。

## 执行

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release -- --profile --avalon
dotnet run --project tests/PaperTodo.EdgePreviewExperimentChecks -c Release -- --profile
```

最后一条测当前工作树原来的预览后端。对照点为 main `13a2224c` 与 #243 `b88322fe`。测试程序集和实验提供器可复制到这两个独立工作副本，测旧后端时不实例化实验提供器；不要修改原分支或提交这些覆盖文件。

## 测量口径

同一 Windows runner、相同正文、固定 460×410 卡片、真实 EdgeCapsuleHost/Presenter、同样 160ms 动画。每组 2 次预热后记录 7 个独立新视图样本，输出全部样本、中位数、最大值。分别记录尺寸计算、构造、挂载、从尺寸计算开始到布局就绪、从挂载到布局就绪、应用侧帧间隔和 UI 线程分配。

这里的就绪是 WPF 布局完成，不是 GPU 呈现时间；不额外订阅 Rendering 驱动动画，不覆盖完整多 HWND DComp 交接。7 个样本的最大值不冒充可靠的 p95。不同后端的 Markdown 支持范围可能不同；样例输入保持一致，最终外观一致性仍需复核。

AvalonEdit 按文档行复用可见排版，但一条很长的源行可能仍需排版其全部折行。因此不能预设它一定比 #243 的按可见折行绘制更快。

## 2026-09-11 同机结果

来源：[Windows 对照运行 34623945732](https://github.com/snownico0722/PaperTodo/actions/runs/34623945732)，artifact `edge-preview-avalonedit-results`。原始 `baseline.log`、`pr243.log`、`avalon.log` 含全部样本，不只保留汇总。此轮对比基线与计时起点和此前 #243 描述里的旧 #238 比较不同，不直接拼接两轮数字。

### 从尺寸计算开始到内容布局就绪，中位数 ms

| 样本 | 模式 | main | #243 | AvalonEdit |
|---|---|---:|---:|---:|
| 重复短行 | 增强 | 55.55 | 53.49 | 21.50 |
| 不重复短行 | 增强 | 57.37 | 67.43 | 26.88 |
| 密集样式 | 增强 | 450.58 | 109.14 | 258.61 |
| 长代码行 | 增强 | 62.26 | 17.64 | 26.18 |
| 多链接 | 增强 | 193.36 | 44.12 | 71.50 |
| 重复短行 | 完全渲染 | 23.41 | 24.58 | 12.02 |
| 不重复短行 | 完全渲染 | 24.34 | 51.54 | 19.87 |
| 密集样式 | 完全渲染 | 239.11 | 129.94 | 121.42 |
| 长代码行 | 完全渲染 | 41.03 | 7.88 | 20.90 |
| 多链接 | 完全渲染 | 40.57 | 28.75 | 20.94 |

### 每轮最大应用侧帧回调间隔的中位数，ms

| 样本 | 模式 | main | #243 | AvalonEdit |
|---|---|---:|---:|---:|
| 不重复短行 | 增强 | 21.33 | 20.11 | 36.58 |
| 密集样式 | 增强 | 272.34 | 18.40 | 524.80 |
| 多链接 | 增强 | 104.70 | 18.95 | 125.94 |
| 密集样式 | 完全渲染 | 132.64 | 22.41 | 237.84 |

布局就绪更早不等于动画更顺。此原型将正文控件接回通常的 WPF 布局/绘制流程；密集样式的可见延迟与回调间隔结果不能支持“换成 AvalonEdit 就解决卡顿”。到底有多少时间花在首次排版、重复布局、实际绘制或其他调度上，尚未做分阶段归因，不猜测具体比例。

#243 也不是所有项目都赢：不重复短行的同步尺寸计算，增强模式从 main 0.16ms 增至 19.22ms，完全渲染从 0.19ms 增至 11.88ms；但它对密集长段落的分批处理显著改善本轮回调间隔。

UI 线程分配不是常驻内存。增强密集样式一轮的分配中位数分别为 main 18491.68KiB、#243 3181.52KiB、AvalonEdit 10318.66KiB。

## 验证与限制

本轮 Release 的实验检查、MarkdownSemantic、MarkdownEditing、TodoNavigation、EdgeTitle、Threading、Persistence 退出码全部为 0；Debug 实验检查通过；同机 main MarkdownEditing 对照也通过。检查覆盖预算、真实布局、禁止滚动、禁止编辑、链接坐标/裁剪、收起交互、内容更新、缩放、空内容及卸载/重新挂载。

前一轮 `34623523468` 的既有 MarkdownEditing 检查曾在 `Single-line fade frames match full rendering without rebuilding distant lines` 的 heading/alpha=0.2 像素比较失败。本轮未修改正文实现或该断言，候选与 main 对照均通过。这是一次未稳定复现的失败，不把它宣称为已修复或已证明仅是原版问题。

接线文件使用本轮实际测试过的内容哈希提交；一次性对照工作流随后移除，分支后续构建使用已有标准流程。

正式选型前仍需真机检查字体、混合 DPI、图片占位、复杂 Markdown 语义差异和鼠标点击手感。此实验没有替换主正文，没有合并文字优化或动画层，也没有修改主分支。
