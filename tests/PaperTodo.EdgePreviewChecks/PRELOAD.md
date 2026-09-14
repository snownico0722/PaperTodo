# 边缘预览预热检查

当前架构与 ownership 见 [ARCHITECTURE](../../doc/ARCHITECTURE.md)「内置笔记的边缘预览」，历史取舍见 D-033～D-035。本文只记录检查入口、实测口径与可追溯证据，不是另一份架构或发布批准。

## 预热规则

仅为存活、可见、实际属于边缘队列的内置 Markdown Note 排队；要求胶囊模式、边缘胶囊和悬停预览开启。有界摘要最多 16 块 / 6000 字符，满足任意一条才预热：

- 总源字符数 >400；
- 总源字符数 >200 且样式/链接覆盖字符 >100；
- 总源字符数 >200 且样式/链接片段 >3。

全部严格大于；总字符数按摘要各行长度之和、不另加换行，覆盖与片段按去掉语法标记后的语义内容计数。嵌套样式不重复累计，链接目标不冒充样式正文，围栏内非空行按代码样式计数。Off 只使用总字符数条件。

启动恢复结束、入队、形态或内容变化只提交最新延后读取请求；分类与准备在共享 500ms 一次性合并延迟后执行。每来源一份当前 artifact，无固定数量/LRU 淘汰、鼠标邻居预测、产品预解析队列或 idle 轮询。需求优先，中断的有效请求保留；旧完成回调不能删掉新请求或绕过新的合并延迟。

冷 miss 与预热共用唯一 artifact builder；预热准备到当前卡片上限，冷 miss 按实际可见区域生成。缓存不保留隐藏卡片/正文树，挂载使用绘制面与原生链接，较矮区域裁剪并禁用完全不可见链接。版本、实际摘要、宽度、字体、缩放、DPI 和资源失配拒绝复用。关闭、离队或清空后的迟到结果不得重入缓存；活动需求的源版本独立于缓存资格，清空缓存不妨碍当前正文生成。

## 当前检查

`ArtifactRenderingChecks` 覆盖严格摘要预算、Unicode、代码块、空行、截断、有限 block 词汇、冻结资源和装饰作用域；其中 44 组手写 WPF 期望图是独立内容参考，不调用 artifact builder 生成期望值，也不复制旧 Markdown renderer。

`PreloadChecks` 的 80 组像素矩阵覆盖四种渲染模式、两种字体/显示配置、两档缩放和五类正文，逐个确认真实命中并在截图前完成布局。冷/热现在共用 builder，因此这组检查证明的是缓存与现场生成一致，不能再称作“旧 WPF renderer 对新 artifact”。

`CompletionChecks`、原生链接和 worker/Host 检查覆盖：首次发布前不开放输入、普通冷短行也使用 worker、重排不生成 WPF 正文块、收起/恢复、编辑、卸载重挂、主题/宽度/DPI/源版本失效、清空缓存不阻塞需求、取消及迟到结果、链接独立命中/背景穿透/裁剪/键盘/焦点，以及 worker 被阻塞时真实 Host 外壳仍能完成动画。`PreloadAuditChecks` 保留严格 OR 边界、延后读取、启动式排队、取消续做、新请求合并延迟、异常隔离和来源撤销。四个 DPI 参数检查不冒充多显示器硬件手测。

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Debug
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --preload-profile
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --preload-profile --reverse
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --preload-memory
```

性能探针仅比较 `cold` / `layout`，不再保留已被否定的 `text` 预解析对照。每个样本输出真实命中数；`--preload-memory` 是托管存活堆增量，不是工作集或原生/GPU 内存。

## 统一 renderer 的收口验证

[Windows 专用验证 34703402596](https://github.com/snownico0722/PaperTodo/actions/runs/34703402596) 的受测源码已推送为 `1b8844d62a069eb263609029e712fea3a4938d04`，artifact 包内保存受测源码、推送 SHA、原始样本、成对统计及结构差异。七组 Release 检查（EdgePreview、MarkdownSemantic、MarkdownEditing、TodoNavigation、EdgeTitle、Threading、Persistence）和 Debug EdgePreview 均通过；独立期望图 44 组通过，缓存/现场生成 80/80 字节一致、最大通道差 0。临时移除源版本保护的反向对照确实触发对应回归失败，恢复保护后通过。

相对 #251 双 renderer 提交 `868c81e6`，生产 `src` 为 **+182/-1157，净减少 975 行**；相对 #249 基线 `2ad8ece9`，整个 #251 的生产 `src` 为 **+1090/-1247，净减少 157 行**。统计包含新增 artifact builder，不把测试删除混算为产品精简，也不拿 #246 某一次审查的局部差异与整个 PR 比较。移除了旧 WPF block renderer、段落控件桥及同步/iterator 入口；测试按新职责集中，不把旧实现复制到测试目录。

### 同机成对性能

同一 Windows runner 分别运行统一实现与 `868c81e6` 双 renderer 对照；旧产品源码不变，只统一探针计时入口与实际命中检查。真实 Host/Presenter、460×410 卡片、160ms 外壳动画，正反策略顺序各 9 次，剔除各自前 2 次后每组 14 个样本。每个版本的全部 108 个原始 `layout` 样本均命中，`cold` 均未命中。

下表为中位数，单位 ms。正文 Ready 指发布绘制面已完成布局，不是 GPU 呈现；Stage 起点是实际 Host staging 前，Total 起点是 Describe 前。

| 场景 | 模式 | 旧冷 Stage→Ready | 统一冷 Stage→Ready | 旧热 Stage→Ready | 统一热 Stage→Ready | 统一热 Total Ready |
|---|---|---:|---:|---:|---:|---:|
| 普通多行，少量样式 | Enhanced | 40.47 | 31.74 | 1.58 | 1.50 | 2.01 |
| 普通多行，少量样式 | Full | 20.19 | 19.92 | 1.00 | 1.17 | 1.41 |
| 密集长行 | Enhanced | 99.36 | 88.27 | 2.10 | 2.06 | 3.29 |
| 密集长行 | Full | 87.55 | 80.57 | 2.01 | 2.06 | 2.39 |
| 短密集多行 | Enhanced | 48.66 | 35.13 | 1.13 | 1.19 | 1.63 |
| 短密集多行 | Full | 34.70 | 33.50 | 0.91 | 0.95 | 1.17 |

统一后的预热墙钟中位数约 44–120ms，包含等待与调度；冷计算没有消失。这里验证的是人为确保预热完成后的命中收益，不是自然使用命中率或所有机器每次小于 3ms。几十分之一毫秒的组间波动不是稳定回退/提升证明。

UI 分配从 Describe 前量到正文就绪且外壳结束，包含该时间窗内 UI Dispatcher 工作、不含 worker 分配。统一热路径约 0.31–0.75MiB，与旧热路径接近；冷普通多行 Enhanced 从约 1.49MiB 降至 0.36MiB，Full 从约 1.02MiB 降至 0.32MiB。不能将 UI 分配下降解释为整个进程总分配等比例下降。

探针第 12 项另记 Stage→实际开放输入：统一热路径各场景中位数约 1.84–17.73ms，旧对照约 1.75–20.36ms。Host/祖先的交互门可能晚于正文布局就绪，因此不能把 1–3ms 的正文 Ready 宣称为每次 1–3ms 已可点击，更不等于物理屏幕呈现。输入门及动画机制未由本次 renderer 统一接管。

## 历史证据与边界

#251 双 renderer 阶段的 [34675779886](https://github.com/snownico0722/PaperTodo/actions/runs/34675779886) 完成旧 WPF 对 artifact 的 80 组像素对照；[34676344587](https://github.com/snownico0722/PaperTodo/actions/runs/34676344587) / [34693460918](https://github.com/snownico0722/PaperTodo/actions/runs/34693460918) 是当时清理后的普通 Release CI，不是统一 renderer 的新验证。该阶段 100 张重笔记的额外托管存活堆从 #249 对照约 105.49MiB 降至 92.57MiB（约 12.2%），不能标成此次重新测出的整进程内存。

#246 的 [34647584124](https://github.com/snownico0722/PaperTodo/actions/runs/34647584124) 保存完整 Body 预热的审查证据；[34644485740](https://github.com/snownico0722/PaperTodo/actions/runs/34644485740) 的旧阈值性能对照证明仅行内准备收益有限。原始历史表可在 `868c81e6` 的本文件查阅，不能与此次不同就绪边界的样本直接相减。

本次完成的是 Markdown Preview 唯一 renderer 与可选完整 artifact 预热，不取消预热、不删除 Host 的资源/DPI/真实交互边界，也不代表 #250 的所有恢复问题已自动解决。真机混合 DPI、真实指针/键盘与 GPU 呈现的发布前手测仍单独进行。
