# 边缘 Markdown 预览检查

测试实际使用的有界预览后端；不再包含完整 AvalonEdit 对照控件。架构边界见 `../../doc/ARCHITECTURE.md`。历史实验与负面结果保留在 #245 的讨论和提交 `abc314de` 的原实验目录，不作为应用中的可选后端。

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Debug
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --profile
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --export output/edge-pixels
```

默认检查：四种显示档位、内容预算、首次一次发布与真实宿主取消、禁止滚动、链接回调、内容和缩放刷新、空状态、卸载/重挂；同一视图收起/恢复不重复准备，内容/宽度/主题/DPI 通知撤销复用；短密集文字按可见折行准备，普通短行保留简单路径。既有 MarkdownEditing 检查还验证字体、像素、取消、代码空行与准备结果复用。

DPI 检查调用实际通知边界，不模拟完整的多屏硬件环境。自动测试不能替代真实混合 DPI、鼠标捕获/跨胶囊手势或显卡最终呈现验证。

`--profile`：真实 Host/Presenter、固定 460×410 卡片、160ms 动画；每种输入每次进程 3 次预热、21 个新建视图样本。输出全部样本与中位数/最大值。指标包括尺寸计算、创建、挂载、布局就绪、应用动画回调间隔和 UI 线程分配。不是 GPU 呈现测量，分配不是常驻内存，21 样本不是线上 p95。重复悬停的复用资格由默认行为检查验证；新建视图探针不把这种复用当成冷启动收益。

比较版本须在同一 runner 使用相同探针与样本，按旧→新→新→旧交换顺序，保留负面结果。`--export` 输出四种档位、两种字体/清晰度、两档缩放、十个输入的 160 组原始像素；仅测试代码调用，不进入应用热路径。
