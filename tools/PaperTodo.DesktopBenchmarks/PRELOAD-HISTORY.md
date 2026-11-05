# 预览性能历史记录

以下为历史提交的测量结果，不是当前性能承诺或当前测试矩阵。当前命令与测量限制见 [README](README.md)。产品架构见 [ARCHITECTURE](../../doc/ARCHITECTURE.md)。

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
