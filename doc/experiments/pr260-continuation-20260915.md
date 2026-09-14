# PR260 连续实验

Task key: `PAPERTODO-PR260-CONTINUATION-20260915`.

## 边界与续接入口

- 独立实验分支：`experiment/pr260-continuation-20260915`。初始来源为 #260 的 `953ae6266af2bc8c5d1696be7660e5006525202b`，其 main 基线为 `2cc019d08143ed07e8774782c0ecf733e7eb7892`。
- 本文和本实验 PR 的描述、阶段评论、commit/diff、CI 是持久化上下文。每轮先重新读取 #260 和本 PR，再从最后完成点继续。不要依赖聊天或其他轮次的本地文件仍存在。
- 只修改/提交/推送本实验分支。禁止写 main、写 #260 原分支、merge、force-push、删除远程分支。保持 Draft。
- 北京时间 2026-09-15 09:00、10:00、11:00、12:00、13:00、14:00、15:00、16:00 共八轮；当前执行为 R0，不占这八轮。
- 用户优先指定 GPT-6 Pro。但调度工具未提供模型字段，不能承诺或冒充实际模型。每轮记录可确认的模型名称；无法独立核验运行路由时明确说明。
- 每轮结束记录：本轮模型、上游/实验 HEAD、具体修改和测试命令/结果、前后量化数据或未测原因、证实/推翻的假设、失败方向、风险、下一步。新证据推翻旧结论须明确标记。
- 下一轮若上一轮仍有 CI 在运行，先读其结果再动代码；ref 更新仅 fast-forward，冲突时重新读取 HEAD 并合并，不覆盖他人的提交。

## 五份审查的独立归纳

来源为用户本轮提供的 `5.6sol模型审查4.md`、`astra模型审查1.md`、`astra模型审查2.md`、`astra模型审查3.md`、`astra模型审查4.md`。Astra 获较高初始置信度，但下面均是待验证假设，不因模型较新或多票重复而成为实测事实。

| 编号 | 来源与假设 | 本任务判定/下一步 |
| --- | --- | --- |
| H1 | Sol4：全局 Version 被无关队列 Request/Wake/Cancel 改写，使准备中 A 的完成不被消费、留在 Sleeping | 小而确定的回归靶点。先用真实 WPF Dispatcher 的 A/B 重入用例验证，再区分生命周期失效与候选身份；无需新增复杂管理框架。 |
| H2 | Astra3/4：采样先逐卡 Pointer invalidation，随后要求全员 settled，持续移动可阻止自动逐卡交还 | 最高交互优先级之一。需要从真实指针变化进入，不能预填 retained sample 跳过关键时序。不能直接取消稳定性检查。 |
| H3 | Astra2/4：启动验证闭包继续持有 predecessor | 托管存活问题，不等于 GPU 泄漏。核查回调最后使用点，用 WeakReference 验证退休后可收集。 |
| H4 | Astra2：无纸张 StartAsync 跳过预热就绪收尾 | 检查两种空启动分支，保留非空首轮 preview→Shell→native 的既有顺序。 |
| H5 | Astra1/2/3/4 + Sol4：无关桌面鼠标使全部预热暂停、逐卡刷新 | 区分原生准备让路与无关活动；保留真实应用交互优先，不顺便缩短 quiet delay。 |
| H6 | Astra3/4：PaperWindow 的旧 PrewarmLightweight 入口绕过 coordinator | 候选为删除重复入口、统一调度，不增加第二套许可判断。 |
| H7 | Astra3：容量恢复未更新当前 preview request 的受限 Size | 检查同请求/内容代次恢复，覆盖插件提供器及内容闭包，不能只测 HWND 容量。 |
| H8 | Astra2/3：DComp 独立动画与 UI timer 输入 region 可失配 | 需跨进程完整手势及受控 UI 延迟验证。静态高风险不冒充实机复现；禁止用整个 envelope 挡住空洞。 |
| H9 | Astra3：重试丢弃第一次点击 | 现有防迟到误触契约，不直接重放过期点击。先改善按下前交还，另测即时完整手势。 |
| H10 | 多份：常驻重复采样/逐卡交还整队成本 | 先归因，已有 SetWindowRgn 未变缓存、surface AddRef、单 spare host，不重复实现。 |
| H11 | Sol4：DPI 先清空 input region | 不能照抄：画面仍可见时清空可能泄漏点击；必须和 cover/真实窗口恢复一起设计并验收。 |

## 已否定/暂不重开

- E-017 dormant surface/visual cache 命中不等于减少 cloak/flush；额外 WaitForCommitCompletion 未显示可用收益。
- E-018 async successor 虽 transaction P95 更短，prepare→animation-clock 更晚，不能原样恢复。
- 路线3 atlas/native shape 当前接入未获采用；本轮不重开，也暂不采用先前聊天提出的大型队列宿主重写。
- 不删除 cover-before-cloak fence，不绕过 WPF 窗口位置同步，不放大透明空洞输入区域。
- Desktop Duplication 是桌面合成捕获，不是物理面板扫描验收。Rendering、RenderComplete、DComp Commit 完成均不是逐卡物理 present ack。

## 验证规则

冻结 baseline、candidate、运行时和测试输入；新缺陷先尝试 baseline 红 / candidate 绿。报告实际运行的断言/场景与 CI URL，不把编译、源码推演或原作者历史数据冒充本轮 GUI/延迟测试。延迟分别记录输入→首次正确桌面变化、输入→真实控件可交互、prepare→animation-clock、transaction 及空闲成本，禁止互相替代。

## R0（进行中）

- 模型：本会话标识 GPT-6 Astra Pro；没有独立运行时路由查询，未进行模型切换；不承诺定时轮次同模型。
- 已完成：重新读取 #260，确认仍为 Draft、HEAD 953ae626；阅读用户五份完整审查；创建八轮定时任务；创建独立分支和本记录。
- 环境：Linux 容器无 dotnet；git/curl 无法解析 GitHub 域名。GitHub 连接器读取和创建分支成功。将使用连接器提交并通过 Windows CI 验证，不能在当前容器实测 DWM。
- 首轮目标：H1 的精确跨队列重入回归、最小失效范围修复；根据结果再推进 H3/H4/H6。H2/H8 是下一阶段重要输入验收，不因 H1 通过而视为解决。
- 本轮尚无产品修改、Windows 测试或新延迟数据；结束前更新本节及 PR 阶段评论。
