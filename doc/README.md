# PaperTodo 文档入口

| 文档 | 用途 |
| --- | --- |
| [用户指南](USER_GUIDE.md) / [User guide](USER_GUIDE.en.md) | 产品使用说明 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 当前有效的技术方向、结构和职责边界 |
| [DECISIONS.md](DECISIONS.md) | 历史技术选择、失败路线及取舍原因 |
| [EXPERIMENTS.md](EXPERIMENTS.md) | 实验方法、数据、证据范围与前序实验索引 |
| [Agent 入口](../AGENTS.md) | 任务路由、执行规则和修改约束 |

## 局部实验复盘

- [路线 3：独立 WPF atlas 与原生 shape 接入（2026-09-15）](experiments/edge-route3-20260915.md)：从 #265 拆出的实验过程、OFF/ON 数据、独立动画能力、未完成验收和可复用经验。当前实现停止采用；不包含失败实验代码，不改变当前架构或主线 D-037 / D-038 状态。

局部复盘只补充对应实验的历史证据，不替代当前架构、主线决策或源码。目录内的实验数据文件继续由各自记录解释，不把历史候选或单次测量自动升级为产品能力。
