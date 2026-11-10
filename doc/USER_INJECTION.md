# 全局参数注入与 Good Lock

[English](USER_INJECTION.en.md) · [用户指南](USER_GUIDE.md#84-插件安装与配置) · [插件 API](../plugin-samples/README.zh.md#56-全局-userjson-注入-api24) · [机器可读目录](user-injection-catalog.json)

PaperTodo 的官方注入接口使用程序目录中的一份 `user.json`。所有工作区共用它，普通设置仍保存各自原来的选择。它可以覆盖固定数值、动态公式的系数，以及指定位置的中间计算结果。只写需要修改的键即可。

## 编辑与应用

1. 按[用户指南](USER_GUIDE.md#84-插件安装与配置)安装 `plugins/official.good-lock.native/`，在笔记纸的“正文类型”中选择 **Good Lock**。插件要求 API 2.4；主程序发布包与插件分别提供。
2. 点击 **打开修改文件**，使用外部编辑器修改 `user.json`。第一次打开时建立空对象；已有文件不会因打开而被改写。
3. 保存后回到 Good Lock。页面重新读取文件，只显示修改项和仍待恢复的运行覆盖。点击右上 **应用**。
4. 重启项会显示红点。点击 **快速重启**会先应用本页草稿，再走正常保存退出流程，并在启动后回到这张纸片。

不安装 Good Lock 也可以编辑程序目录的同一文件，并通过重启采用。程序不持续监听这个文件，单独保存外部文件不会立即改变运行值。

```json
{
  "fullscreen.pollIntervalMs": 1500,
  "todo.drag.autoScroll": false,
  "capsule.labelFontDip": 15,
  "menu.paper.newTodo": false
}
```

键区分大小写，点号是键名的一部分。JSON 顶层是对象；支持注释和尾随逗号。数值、整数和布尔值按目录所列类型填写，不把它们写成字符串。缺失文件、空白文件或 `{}` 都表示没有文件覆盖。

**键存在就代表覆盖。** 即使填入的值恰好等于默认值，仍会保持覆盖。删除键并应用，才恢复原取值来源；重启项的删除也需要重启。普通设置页的“恢复默认”只恢复该页普通设置，保留 `user.json`。

## 读懂修改项

| 页面元素 | 含义 |
| --- | --- |
| 左侧默认值 | 固定值、当前计算方式的简短说明，或空白。它不一定是可以直接抄入文件的常量。 |
| 右侧修改值 | 文件值或本页尚未应用的草稿值。悬停名称可查看完整键名。 |
| 灰点 | 文件或草稿尚待应用；当前版本未知的键也保持未采用状态。 |
| 绿点 | 当前运行已经采用；`next_use` 参数会在下一次相应操作或构建时读取。 |
| 红点 | 应用请求已接受，需要重启才能改变当前值。 |
| 修改数值标红 | 超出该项声明的建议范围。它仍然可以应用，建议范围不是硬限制。 |
| 单项重置 | 形成删除这个键的草稿，点击应用后写入。 |
| 魔棒 | 仅在点击时为本项给出有限建议，并形成草稿；没有适用规则时保持原值。 |
| 全部重置 | 形成清空全局覆盖的草稿；应用时包括未知键一并清除。 |
| 另存为图标 | 将最新文件加本页草稿组成的稀疏覆盖另存为 JSON；不会采用或替换活动文件。 |

本页修改与外部编辑发生冲突时，Good Lock 会重读最新文件，保留按键草稿并提示检查，再次点击应用才提交。单纯采用外部文件不重写其格式；应用重置或优化草稿时，会重新格式化整个 JSON 对象，保留其他键及其值。

## 动态公式与普通设置

例如 `capsule.labelBaseDip` 改的是字号计算的基础值，当前文字大小预设仍参与计算；`capsule.labelFontDip` 则覆盖该计算的最终结果。没有最终覆盖时，原公式继续根据当前设置运行。类似地，预览大小、滚动热区、滚动速度及排版度量也提供指定中间结果的覆盖点。

完全被最终覆盖接管的普通设置显示为只读，并保留原选择。删除并应用覆盖后，原选择重新生效。只覆盖个别系数或部分待办/笔记度量时，普通设置仍可影响未覆盖部分。全局 `user.json` 不按工作区分份，也不因副工作区“跟随主设置”而另建一份。

新建宽高作用于新建模型和首次显示，GUI、插件与 MCP 新建使用相同入口；它不改已有纸片。已有窗口之后的恢复尺寸限制由 `window.restore.constrainSize` 独立控制，窗口最小尺寸和当前布局规则也各自有对应含义。

## 生效时机与成本

| 目录值 | 含义 |
| --- | --- |
| `live` | 应用后更新缓存或由现有运行步骤读取，包括必要的计时器和预测历史刷新。 |
| `next_use` | 应用后写入参数槽；下次创建对象、执行操作或读取该参数时使用。已启动的动画或对象不自动重建。 |
| `restart` | 本次运行保持旧绑定，下次启动在 controller 和窗口构造之前采用。 |

固定参数直接读取类型化内存字段，动态结果保留原公式并增加覆盖判断。反射目录、文件读写、字符串键查询和本地化目录处理在启动、检查或应用边界进行。没有文件监听、额外轮询或每帧字典扫描；未配置的启动路径也不建立反射目录。参数槽本身仍有一次性初始化与少量读取成本，不承诺绝对零开销。

提高样本、内容、缓存或图片预算会改变对应算法的工作量。官方魔棒只提供有限建议：如队列吸附/释放距离的关系、压缩目标与图片存储边长的关系，以及胶囊字号与内容高度的粗略关系。它不会自动调整全部关联项，也不保证避免裁切、卡顿、崩溃或数据损坏；自定义结果由使用者承担，程序不为注入增加自动恢复或回滚层。

当前预览继续保留原来的默认布局和裁切方式。目录中的 `maximum` 是上限、`minimum` 是下限，最终结果覆盖点在说明中明确指出；增大上限不等于强制所有内容都使用该尺寸。内置预览的宿主容量与预热预算已跟随相关最大/最终尺寸参数。

## Agent 与插件调用

Native Body 和 provider Runtime 使用 `context.InjectionApi`。先调用 `Read()` 获取此版本实际目录和最新文件 revision；外部修改后调用 `Apply()` 采用，按键编辑则携带 revision、`Set` 和 `Remove`。`Read` / `Suggest` 使用 `settings.read`，`Apply` / `Restart` 使用 `settings.update`。Body 请求重启后回到调用纸片，Runtime 没有纸片返回目标。

`Suggest` 的可选草稿字典代表完整候选文档：缺失的键表示恢复默认来源。建议只返回本项的新值及解释，不写文件。更多接口示例见[插件文档](../plugin-samples/README.zh.md#56-全局-userjson-注入-api24)。内部注入键可能随模块演进变化；Agent 应查询当前目录，保留未知键，避免把整份默认目录写成覆盖文件。

## 完整参数目录

下表直接根据 typed catalog 导出。机器目录提供同一组键、类型、默认来源、生效时机、建议范围和中英文说明，便于 Agent 检索；编辑 `user.json` 时仅选择需要的 `id` 并填写其值。

当前共 **434 项**，其中 **45 项**默认来源是动态计算；`live` 168, `next_use` 89, `restart` 177.

### 整体外观 (1)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `appearance.fontScale` · 整体字号缩放 | `number` | 按当前设置或运行状态计算 | `live` | 当前正式字号缩放的最终覆盖；普通设置仍保留原选择。 建议：0.8–1.2. |

### 动画 (2)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `animation.fastDurationScale` · 快速动画倍率 | `number` | 0.6 | `next_use` | 快速动画模式对原有时长的乘数。 建议：0–1. |
| `animation.snapAmplitude` · 回弹曲线幅度 | `number` | 0.3 | `restart` | 共享回弹动画曲线创建时使用的幅度。 |

### 保存与显示器事件 (3)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `app.displayRefreshMs` · 显示器变化防抖 | `number` | 250 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |
| `app.saveForceMs` · 持续编辑强制保存间隔 | `number` | 10000 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |
| `app.saveIdleMs` · 保存空闲等待 | `number` | 1000 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |

### 纸片背景 (2)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `background.blendedImageOpacity` · 纸片背景混合透明度 | `number` | 0.3 | `restart` | 启用背景色混合时图片画刷的透明度。 |
| `background.maxDecodeDimensionPx` · 纸片背景解码边长 | `integer` | 4096 px | `restart` | 背景图片解码的最大像素边长。 |

### 胶囊文字、尺寸与间距 (15)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `capsule.edge.widthDip` · 边缘胶囊最终宽度 | `number` | 按图标、标题或插件内容计算 | `restart` | 覆盖当前内容测量后的边缘胶囊宽度，包含内侧阴影留白。 |
| `capsule.gap.baseDip` · 胶囊间距基础值 | `number` | 4 DIP | `live` | 普通间距预设的基础值；紧凑和宽松预设仍按差值计算。 |
| `capsule.gap.deltaDip` · 胶囊间距预设差值 | `number` | 4 DIP | `live` | 紧凑预设从基础值减去此值，宽松预设加上此值。 |
| `capsule.gap.finalDip` · 胶囊最终间距 | `number` | 按当前间距预设计算 | `live` | 覆盖当前间距预设与基础值计算的结果。 |
| `capsule.iconBaseDip` · 胶囊图标基础字号 | `number` | 13 DIP | `live` | 应用当前预设及全局缩放前的基础字号。 |
| `capsule.labelBaseDip` · 胶囊标签基础字号 | `number` | 12 DIP | `live` | 应用当前预设及全局缩放前的基础字号。 |
| `capsule.labelFontDip` · 胶囊标签最终字号 | `number` | 按当前设置或运行状态计算 | `live` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |
| `capsule.master.glyphBaseDip` · 主胶囊箭头基础字号 | `number` | 12 DIP | `live` | 应用当前预设及全局缩放前的基础字号。 |
| `capsule.master.glyphGapDip` · 主胶囊图标计数间隔 | `number` | 3 DIP | `restart` | 主胶囊内容测量与显示共同使用的尺寸。 |
| `capsule.master.interiorBorderThicknessDip` · 主胶囊内部边框 | `number` | 1 DIP | `restart` | 主胶囊内容测量与显示共同使用的尺寸。 |
| `capsule.master.labelFontDip` · 主胶囊计数最终字号 | `number` | 按当前设置或运行状态计算 | `live` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |
| `capsule.master.leftPaddingDip` · 主胶囊左内边距 | `number` | 4 DIP | `restart` | 主胶囊内容测量与显示共同使用的尺寸。 |
| `capsule.master.rightPaddingDip` · 主胶囊右内边距 | `number` | 3 DIP | `restart` | 主胶囊内容测量与显示共同使用的尺寸。 |
| `capsule.master.widthDip` · 主胶囊最终宽度 | `number` | 按当前设置或运行状态计算 | `restart` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |
| `capsule.normal.widthDip` · 普通胶囊最终宽度 | `number` | 按当前设置或运行状态计算 | `restart` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |

### 边缘胶囊、悬停预测与预览 (205)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `edge.hover.acceleratingVetoHorizonScale` · 加速时穿越否决时间倍率 | `number` | 1.2 | `live` | 现有悬停意图公式中的系数；下一次评估直接读取。 |
| `edge.hover.accelerationDeltaDipPerMs` · 悬停加速速度差 | `number` | 0.04 DIP/ms | `live` | 认定加速还需满足的绝对速度差。 |
| `edge.hover.accelerationRatio` · 悬停加速速度比例 | `number` | 1.15 | `live` | 当前竖直速度与先前速度比较时的加速比例。 |
| `edge.hover.accelerationRiskIncrement` · 加速时驻留风险增量 | `number` | 0.2 | `live` | 现有悬停意图公式中的系数；下一次评估直接读取。 建议：0–1. |
| `edge.hover.brakingDeltaDipPerMs` · 悬停减速速度差 | `number` | 0.05 DIP/ms | `live` | 认定减速还需满足的绝对速度差。 |
| `edge.hover.brakingRatio` · 悬停减速速度比例 | `number` | 0.72 | `live` | 当前竖直速度与先前速度比较时的减速比例。 建议：0–1. |
| `edge.hover.brakingRiskScale` · 减速时驻留风险倍率 | `number` | 0.25 | `live` | 现有悬停意图公式中的系数；下一次评估直接读取。 建议：0–1. |
| `edge.hover.corridorGeometryWeight` · 跨空白几何证据权重 | `number` | 0.6 | `live` | 合并跨胶囊空白区域的几何与运动证据时使用的几何权重。 建议：0–1. |
| `edge.hover.corridorKeepEnterConfidence` · 跨空白保留进入阈值 | `number` | 0.58 | `live` | 尚未锁存保留状态时所需的合并置信度。 建议：0–1. |
| `edge.hover.corridorKeepExitConfidence` · 跨空白保留退出阈值 | `number` | 0.42 | `live` | 已锁存保留状态时继续保留所需的合并置信度。 建议：0–1. |
| `edge.hover.corridorMeaningfulMovementDip` · 跨空白有效移动距离 | `number` | 0.5 DIP | `live` | 更新最近有效指针移动时间戳的距离。 |
| `edge.hover.corridorMotionFadeMs` · 跨空白运动置信度衰减终点 | `number` | 900 ms | `live` | 预计到达目标的时间达到此值时运动置信度衰减结束。 |
| `edge.hover.corridorMotionFullConfidenceMs` · 跨空白运动满置信度时间 | `number` | 250 ms | `live` | 预计到达目标的时间不超过此值时保留完整运动置信度。 |
| `edge.hover.corridorMotionWeight` · 跨空白运动证据权重 | `number` | 0.4 | `live` | 合并跨胶囊空白区域的几何与运动证据时使用的运动权重。 建议：0–1. |
| `edge.hover.corridorStationaryGraceMs` · 跨空白静止几何宽限 | `number` | 160 ms | `live` | 指针停止后几何证据仍保持原强度的时间。 |
| `edge.hover.corridorStrongConfidence` · 跨空白强证据阈值 | `number` | 0.84 | `live` | 单一证据足以保留跨空白浏览意图的置信度。 建议：0–1. |
| `edge.hover.duplicateSampleMs` · 悬停重复采样间隔 | `number` | 1.5 ms | `live` | 合并短时间内重复位置样本时使用的间隔。 |
| `edge.hover.historyWindowMs` · 悬停运动历史窗口 | `number` | 64 ms | `live` | 计算近期轨迹和速度趋势使用的时间窗口。 |
| `edge.hover.profile.high.corridor.minimumPathConsistency` · 悬停高档·跨空白路径一致性 | `number` | 0.68 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.high.corridor.minimumSpeedDipPerMs` · 悬停高档·跨空白最小运动速度 | `number` | 0.075 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.corridor.noTargetIntentCloseMs` · 悬停高档·无目标意图关闭等待 | `number` | 350 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.corridor.targetPaddingDip` · 悬停高档·跨空白目标几何留白 | `number` | 16 DIP | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.initial.dynamicDelayHorizonMs` · 悬停高档·初次进入动态驻留风险时间范围 | `number` | 180 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.initial.maximumDelayMs` · 悬停高档·初次进入最长动态驻留 | `number` | 32 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.initial.minimumDelayMs` · 悬停高档·初次进入最短动态驻留 | `number` | 8 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.initial.minimumObservationMs` · 悬停高档·初次进入最短观察时间 | `number` | 8 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.initial.passThroughVetoHorizonMs` · 悬停高档·初次进入穿越否决时间范围 | `number` | 80 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.minimumDirectionalSpeedDipPerMs` · 悬停高档·方向运动最小速度 | `number` | 0.1 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.minimumDirectionConsistency` · 悬停高档·方向一致性阈值 | `number` | 0.72 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.high.minimumVerticalDominance` · 悬停高档·竖直运动占比阈值 | `number` | 0.55 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.high.stableFallbackMs` · 悬停高档·稳定停留直接放行时间 | `number` | 50 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.transfer.dynamicDelayHorizonMs` · 悬停高档·跨胶囊切换动态驻留风险时间范围 | `number` | 240 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.transfer.maximumDelayMs` · 悬停高档·跨胶囊切换最长动态驻留 | `number` | 50 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.transfer.minimumDelayMs` · 悬停高档·跨胶囊切换最短动态驻留 | `number` | 12 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.transfer.minimumObservationMs` · 悬停高档·跨胶囊切换最短观察时间 | `number` | 12 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.high.transfer.passThroughVetoHorizonMs` · 悬停高档·跨胶囊切换穿越否决时间范围 | `number` | 120 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.corridor.minimumPathConsistency` · 悬停低档·跨空白路径一致性 | `number` | 0.79 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.low.corridor.minimumSpeedDipPerMs` · 悬停低档·跨空白最小运动速度 | `number` | 0.11 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.corridor.noTargetIntentCloseMs` · 悬停低档·无目标意图关闭等待 | `number` | 650 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.corridor.targetPaddingDip` · 悬停低档·跨空白目标几何留白 | `number` | 24 DIP | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.initial.dynamicDelayHorizonMs` · 悬停低档·初次进入动态驻留风险时间范围 | `number` | 240 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.initial.maximumDelayMs` · 悬停低档·初次进入最长动态驻留 | `number` | 44 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.initial.minimumDelayMs` · 悬停低档·初次进入最短动态驻留 | `number` | 14 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.initial.minimumObservationMs` · 悬停低档·初次进入最短观察时间 | `number` | 10 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.initial.passThroughVetoHorizonMs` · 悬停低档·初次进入穿越否决时间范围 | `number` | 110 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.minimumDirectionalSpeedDipPerMs` · 悬停低档·方向运动最小速度 | `number` | 0.055 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.minimumDirectionConsistency` · 悬停低档·方向一致性阈值 | `number` | 0.64 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.low.minimumVerticalDominance` · 悬停低档·竖直运动占比阈值 | `number` | 0.45 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.low.stableFallbackMs` · 悬停低档·稳定停留直接放行时间 | `number` | 85 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.transfer.dynamicDelayHorizonMs` · 悬停低档·跨胶囊切换动态驻留风险时间范围 | `number` | 410 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.transfer.maximumDelayMs` · 悬停低档·跨胶囊切换最长动态驻留 | `number` | 90 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.transfer.minimumDelayMs` · 悬停低档·跨胶囊切换最短动态驻留 | `number` | 34 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.transfer.minimumObservationMs` · 悬停低档·跨胶囊切换最短观察时间 | `number` | 18 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.low.transfer.passThroughVetoHorizonMs` · 悬停低档·跨胶囊切换穿越否决时间范围 | `number` | 205 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.corridor.minimumPathConsistency` · 悬停中档·跨空白路径一致性 | `number` | 0.74 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.medium.corridor.minimumSpeedDipPerMs` · 悬停中档·跨空白最小运动速度 | `number` | 0.09 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.corridor.noTargetIntentCloseMs` · 悬停中档·无目标意图关闭等待 | `number` | 500 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.corridor.targetPaddingDip` · 悬停中档·跨空白目标几何留白 | `number` | 20 DIP | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.initial.dynamicDelayHorizonMs` · 悬停中档·初次进入动态驻留风险时间范围 | `number` | 200 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.initial.maximumDelayMs` · 悬停中档·初次进入最长动态驻留 | `number` | 36 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.initial.minimumDelayMs` · 悬停中档·初次进入最短动态驻留 | `number` | 10 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.initial.minimumObservationMs` · 悬停中档·初次进入最短观察时间 | `number` | 8 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.initial.passThroughVetoHorizonMs` · 悬停中档·初次进入穿越否决时间范围 | `number` | 90 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.minimumDirectionalSpeedDipPerMs` · 悬停中档·方向运动最小速度 | `number` | 0.075 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.minimumDirectionConsistency` · 悬停中档·方向一致性阈值 | `number` | 0.68 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.medium.minimumVerticalDominance` · 悬停中档·竖直运动占比阈值 | `number` | 0.5 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.medium.stableFallbackMs` · 悬停中档·稳定停留直接放行时间 | `number` | 60 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.transfer.dynamicDelayHorizonMs` · 悬停中档·跨胶囊切换动态驻留风险时间范围 | `number` | 310 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.transfer.maximumDelayMs` · 悬停中档·跨胶囊切换最长动态驻留 | `number` | 66 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.transfer.minimumDelayMs` · 悬停中档·跨胶囊切换最短动态驻留 | `number` | 20 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.transfer.minimumObservationMs` · 悬停中档·跨胶囊切换最短观察时间 | `number` | 14 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.medium.transfer.passThroughVetoHorizonMs` · 悬停中档·跨胶囊切换穿越否决时间范围 | `number` | 155 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.corridor.minimumPathConsistency` · 悬停很高档·跨空白路径一致性 | `number` | 0.62 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryHigh.corridor.minimumSpeedDipPerMs` · 悬停很高档·跨空白最小运动速度 | `number` | 0.06 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.corridor.noTargetIntentCloseMs` · 悬停很高档·无目标意图关闭等待 | `number` | 200 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.corridor.targetPaddingDip` · 悬停很高档·跨空白目标几何留白 | `number` | 12 DIP | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.initial.dynamicDelayHorizonMs` · 悬停很高档·初次进入动态驻留风险时间范围 | `number` | 135 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.initial.maximumDelayMs` · 悬停很高档·初次进入最长动态驻留 | `number` | 22 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.initial.minimumDelayMs` · 悬停很高档·初次进入最短动态驻留 | `number` | 4 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.initial.minimumObservationMs` · 悬停很高档·初次进入最短观察时间 | `number` | 6 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.initial.passThroughVetoHorizonMs` · 悬停很高档·初次进入穿越否决时间范围 | `number` | 55 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.minimumDirectionalSpeedDipPerMs` · 悬停很高档·方向运动最小速度 | `number` | 0.13 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.minimumDirectionConsistency` · 悬停很高档·方向一致性阈值 | `number` | 0.76 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryHigh.minimumVerticalDominance` · 悬停很高档·竖直运动占比阈值 | `number` | 0.6 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryHigh.stableFallbackMs` · 悬停很高档·稳定停留直接放行时间 | `number` | 38 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.transfer.dynamicDelayHorizonMs` · 悬停很高档·跨胶囊切换动态驻留风险时间范围 | `number` | 190 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.transfer.maximumDelayMs` · 悬停很高档·跨胶囊切换最长动态驻留 | `number` | 38 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.transfer.minimumDelayMs` · 悬停很高档·跨胶囊切换最短动态驻留 | `number` | 8 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.transfer.minimumObservationMs` · 悬停很高档·跨胶囊切换最短观察时间 | `number` | 10 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryHigh.transfer.passThroughVetoHorizonMs` · 悬停很高档·跨胶囊切换穿越否决时间范围 | `number` | 90 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.corridor.minimumPathConsistency` · 悬停很低档·跨空白路径一致性 | `number` | 0.84 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryLow.corridor.minimumSpeedDipPerMs` · 悬停很低档·跨空白最小运动速度 | `number` | 0.14 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.corridor.noTargetIntentCloseMs` · 悬停很低档·无目标意图关闭等待 | `number` | 800 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.corridor.targetPaddingDip` · 悬停很低档·跨空白目标几何留白 | `number` | 30 DIP | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.initial.dynamicDelayHorizonMs` · 悬停很低档·初次进入动态驻留风险时间范围 | `number` | 300 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.initial.maximumDelayMs` · 悬停很低档·初次进入最长动态驻留 | `number` | 54 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.initial.minimumDelayMs` · 悬停很低档·初次进入最短动态驻留 | `number` | 18 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.initial.minimumObservationMs` · 悬停很低档·初次进入最短观察时间 | `number` | 12 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.initial.passThroughVetoHorizonMs` · 悬停很低档·初次进入穿越否决时间范围 | `number` | 135 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.minimumDirectionalSpeedDipPerMs` · 悬停很低档·方向运动最小速度 | `number` | 0.04 DIP/ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.minimumDirectionConsistency` · 悬停很低档·方向一致性阈值 | `number` | 0.6 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryLow.minimumVerticalDominance` · 悬停很低档·竖直运动占比阈值 | `number` | 0.4 | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 建议：0–1. |
| `edge.hover.profile.veryLow.stableFallbackMs` · 悬停很低档·稳定停留直接放行时间 | `number` | 110 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.transfer.dynamicDelayHorizonMs` · 悬停很低档·跨胶囊切换动态驻留风险时间范围 | `number` | 500 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.transfer.maximumDelayMs` · 悬停很低档·跨胶囊切换最长动态驻留 | `number` | 115 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.transfer.minimumDelayMs` · 悬停很低档·跨胶囊切换最短动态驻留 | `number` | 48 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.transfer.minimumObservationMs` · 悬停很低档·跨胶囊切换最短观察时间 | `number` | 22 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.profile.veryLow.transfer.passThroughVetoHorizonMs` · 悬停很低档·跨胶囊切换穿越否决时间范围 | `number` | 255 ms | `live` | Apply 时更新当前档位缓存；之后的意图评估读取该数值。 |
| `edge.hover.sampleCapacity` · 悬停指针样本容量 | `integer` | 20 | `restart` | 创建预测器时分配的环形采样缓冲区长度。 |
| `edge.hover.staleHistoryMs` · 悬停历史失效时长 | `number` | 180 ms | `live` | 较长采样间隔后不继续沿用旧轨迹。 |
| `edge.hover.stationaryGeometryConfidenceCap` · 静止后几何证据上限 | `number` | 0.55 | `live` | 现有悬停意图公式中的系数；下一次评估直接读取。 建议：0–1. |
| `edge.hover.uncertainTrendRiskScale` · 无速度趋势时驻留风险倍率 | `number` | 0.85 | `live` | 现有悬停意图公式中的系数；下一次评估直接读取。 建议：0–1. |
| `edge.layout.capsuleCloseWidth` · 边缘胶囊关闭区宽度 | `number` | 14 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.cornerRadius` · 边缘胶囊圆角 | `number` | 12 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.crossQueueDragUnlockDistance` · 胶囊脱离队列距离 | `number` | 56 DIP | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.dockingHandoffMilliseconds` · 胶囊回墙飞行时长 | `integer` | 160 ms | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.dockingRevealMilliseconds` · 胶囊回墙显现时长 | `integer` | 80 ms | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.expandedEdgeInset` · 展开纸片贴边留白 | `number` | 36 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.horizontalResizeMilliseconds` · 胶囊横向变化时长 | `integer` | 160 ms | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.outlineOverlap` · 边缘胶囊轮廓重叠 | `number` | 1 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.outlineThickness` · 边缘胶囊轮廓粗细 | `number` | 2 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.slotMoveMilliseconds` · 队列位移时长 | `integer` | 200 ms | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.slotRetractMoveMilliseconds` · 队列收回时长 | `integer` | 120 ms | `next_use` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.startTopMargin` · 新队列起始位置 | `number` | 48 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.topMargin` · 边缘队列上下留白 | `number` | 8 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.layout.windowChromeMargin` · 胶囊透明外沿 | `number` | 8 DIP | `restart` | 原有边缘胶囊共享几何或过渡计算使用的数值。 |
| `edge.preview.activation.activationTrackingIntervalMs` · 预览激活意图检查间隔 | `number` | 16 ms | `next_use` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.corridorTrackingIntervalMs` · 跨空白预览检查间隔 | `number` | 24 ms | `next_use` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.cursorReadRetryCount` · 预览物理指针读取重试次数 | `integer` | 1 | `next_use` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.layoutSuppressionMarginMs` · 队列位移后预览额外抑制时间 | `number` | 50 ms | `live` | 加在当前队列位移动画时长之后。 |
| `edge.preview.activation.layoutSuppressionMs` · 队列位移后预览最终抑制时间 | `number` | 当前队列位移动画时长加额外抑制时间。 | `live` | 当前队列位移动画时长加额外抑制时间。 |
| `edge.preview.activation.pointerToleranceDip` · 预览指针稳定容差 | `number` | 2 DIP | `live` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.postDragResidenceMs` · 拖动后预览最低驻留 | `number` | 140 ms | `live` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.postDragStableMs` · 拖动后预览稳定停留 | `number` | 48 ms | `live` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.activation.transferResidenceMs` · 预览切换最低驻留 | `number` | 32 ms | `live` | 由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。 |
| `edge.preview.fallback.estimatedLineHeightDip` · 缺省状态预览高度估算基础行高 | `number` | 20 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.heightBaseDip` · 缺省状态预览高度估算固定部分 | `number` | 150 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.heightDip` · 缺省状态预览最终高度 | `number` | 按当前状态文本折行估算并套用缺省状态卡片高度范围。 | `restart` | 按当前状态文本折行估算并套用缺省状态卡片高度范围。 |
| `edge.preview.fallback.maximumHeightDip` · 缺省状态预览最大高度 | `number` | 280 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.maximumWidthDip` · 缺省状态预览最大宽度 | `number` | 440 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.minimumHeightDip` · 缺省状态预览最小高度 | `number` | 160 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.minimumTextWidthDip` · 缺省状态预览折行估算最小文字宽度 | `number` | 72 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.minimumWidthDip` · 缺省状态预览最小宽度 | `number` | 120 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.textReserveDip` · 缺省状态预览折行估算扣除宽度 | `number` | 40 DIP | `restart` | 缺少专属预览内容时的状态卡片估算参数。 |
| `edge.preview.fallback.widthDip` · 缺省状态预览最终宽度 | `number` | 按当前标题和状态文本估算并套用缺省状态卡片宽度范围。 | `restart` | 按当前标题和状态文本估算并套用缺省状态卡片宽度范围。 |
| `edge.preview.markdown.artifactBodyHeightDip` · 笔记预览预渲染正文高度预算 | `number` | 原有 410 DIP 预渲染高度与当前笔记预览最大可能高度的较大值。 | `restart` | 原有 410 DIP 预渲染高度与当前笔记预览最大可能高度的较大值。 |
| `edge.preview.markdown.blockBudget` · 笔记预览块数预算 | `integer` | 16 | `restart` | 捕获预览内容时接纳的块数；Full 模式围栏代码仍按原规则计块。 |
| `edge.preview.markdown.blockCharacterBudget` · 笔记预览单块扫描字符预算 | `integer` | 默认跟随当前预览总字符预算；明确填写后单独覆盖。 | `restart` | 默认跟随当前预览总字符预算；明确填写后单独覆盖。 |
| `edge.preview.markdown.bodyWidthReserveDip` · 笔记预览折行估算扣除宽度 | `number` | 44 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.characterBudget` · 笔记预览总字符预算 | `integer` | 6000 | `restart` | 捕获与测量共同使用的总字符预算，包括接纳行之间的分隔符。 |
| `edge.preview.markdown.emptyHeightDip` · 笔记预览空内容高度 | `number` | 120 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.emptyMinimumWidthDip` · 笔记预览空内容最小宽度 | `number` | 130 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.estimatedLineHeightDip` · 笔记预览高度估算基础行高 | `number` | 22 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.heightBaseDip` · 笔记预览高度估算固定部分 | `number` | 74 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.heightDip` · 笔记预览最终高度 | `number` | 按当前受限 Markdown 的估算行数、固定高度、行高与缩放计算，并套用当前高度范围。 | `restart` | 按当前受限 Markdown 的估算行数、固定高度、行高与缩放计算，并套用当前高度范围。 |
| `edge.preview.markdown.hostHeightDip` · 笔记预览宿主预留高度 | `number` | 原有 410 DIP 预留高度与当前笔记预览最大可能高度的较大值。 | `restart` | 原有 410 DIP 预留高度与当前笔记预览最大可能高度的较大值。 |
| `edge.preview.markdown.maximumHeightDip` · 笔记预览非空最大高度 | `number` | 320 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.maximumWidthDip` · 笔记预览最大宽度 | `number` | 460 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.minimumHeightDip` · 笔记预览非空最小高度 | `number` | 150 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.minimumWidthDip` · 笔记预览最小宽度 | `number` | 120 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.markdown.textScale` · 笔记预览估算文字倍率 | `number` | 当前笔记基础字号乘单篇缩放，四舍五入后除以应用缩放后的 14 DIP。 | `restart` | 当前笔记基础字号乘单篇缩放，四舍五入后除以应用缩放后的 14 DIP。 |
| `edge.preview.markdown.titleFontDip` · 笔记预览标题基础字号 | `number` | 13 DIP | `restart` | 应用全局字体缩放前的笔记预览标题字号。 |
| `edge.preview.markdown.widthDip` · 笔记预览最终宽度 | `number` | 按标题、受限 Markdown 文本及当前缩放估算，并套用当前宽度范围。 | `restart` | 按标题、受限 Markdown 文本及当前缩放估算，并套用当前宽度范围。 |
| `edge.preview.markdown.widthReserveDip` · 笔记预览宽度估算固定留白 | `number` | 72 DIP | `restart` | 沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。 |
| `edge.preview.measure.chromeReserveWidthDip` · 预览缺省横向留白 | `number` | 72 DIP | `restart` | 用于预览宽度和折行的轻量估算，实际绘制仍由原控件负责。 |
| `edge.preview.measure.glyphWidthDip` · 预览估算基础字宽 | `number` | 6.4 DIP | `restart` | 用于预览宽度和折行的轻量估算，实际绘制仍由原控件负责。 |
| `edge.preview.measure.minimumLineUnits` · 预览折行估算最少字符单位 | `integer` | 12 | `restart` | 限制轻量文本测量的输入或每行估算单位。 |
| `edge.preview.measure.widthLineBudget` · 预览宽度估算行数预算 | `integer` | 32 | `restart` | 限制轻量文本测量的输入或每行估算单位。 |
| `edge.preview.measure.widthUnitBudget` · 预览宽度估算字符宽度预算 | `number` | 64 | `restart` | 限制轻量文本测量的输入或每行估算单位。 |
| `edge.preview.measure.wrappedLineBudget` · 预览折行估算输入行预算 | `integer` | 80 | `restart` | 限制轻量文本测量的输入或每行估算单位。 |
| `edge.preview.monitorInsetDip` · 预览显示器工作区扣除宽度与高度 | `number` | 16 DIP | `restart` | 计算当前显示器可用 Mini 尺寸时，从工作区宽和高分别扣除的数值。 |
| `edge.preview.plugin.defaultMaximumHeightDip` · 插件 Mini 兼容最大高度 | `number` | 600 DIP | `restart` | 没有声明 miniMaxSize 时，用来锁定会话有效最大高度的兼容基线。 |
| `edge.preview.plugin.defaultMaximumWidthDip` · 插件 Mini 兼容最大宽度 | `number` | 800 DIP | `restart` | 没有声明 miniMaxSize 时，用来锁定会话有效最大宽度的兼容基线。 |
| `edge.preview.plugin.fallbackContentWidthDip` · 插件胶囊后备内容缺省宽度 | `number` | 110 DIP | `restart` | 胶囊未提供内容宽度时用于后备预览估算的基础宽度。 |
| `edge.preview.plugin.fallbackHeightDip` · 插件胶囊后备预览高度 | `number` | 140 DIP | `restart` | 插件没有专属 Mini 时，从胶囊内容构造后备预览使用的高度。 |
| `edge.preview.plugin.fallbackMaximumWidthDip` · 插件胶囊后备预览最大宽度 | `number` | 360 DIP | `restart` | 插件没有专属 Mini 时，从胶囊内容估算后备预览的宽度范围。 |
| `edge.preview.plugin.fallbackMinimumWidthDip` · 插件胶囊后备预览最小宽度 | `number` | 220 DIP | `restart` | 插件没有专属 Mini 时，从胶囊内容估算后备预览的宽度范围。 |
| `edge.preview.plugin.fallbackWidthDip` · 插件胶囊后备预览最终宽度 | `number` | 按当前胶囊内容宽度、关闭区、外沿和后备倍率计算，再套用后备宽度范围。 | `restart` | 按当前胶囊内容宽度、关闭区、外沿和后备倍率计算，再套用后备宽度范围。 |
| `edge.preview.plugin.fallbackWidthScale` · 插件胶囊后备预览宽度倍率 | `number` | 1.65 | `restart` | 胶囊内容宽度加关闭区与外沿后，用于估算后备预览宽度的倍率。 |
| `edge.preview.todo.emptyFontDip` · 待办预览空内容基础字号 | `number` | 16 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.emptyHeightDip` · 待办预览空内容高度 | `number` | 120 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.emptyMinimumWidthDip` · 待办预览空内容最小宽度 | `number` | 130 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.estimatedLineHeightDip` · 待办预览高度估算基础行高 | `number` | 28 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.heightBaseDip` · 待办预览高度估算固定部分 | `number` | 62 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.heightDip` · 待办预览最终高度 | `number` | 按当前受限待办的折行数、固定高度与行高计算，并套用当前高度范围。 | `restart` | 按当前受限待办的折行数、固定高度与行高计算，并套用当前高度范围。 |
| `edge.preview.todo.hostHeightDip` · 待办预览宿主预留高度 | `number` | 原有 400 DIP 预留高度与当前待办预览最大可能高度的较大值。 | `restart` | 原有 400 DIP 预留高度与当前待办预览最大可能高度的较大值。 |
| `edge.preview.todo.itemBudget` · 待办预览条目预算 | `integer` | 12 | `restart` | 待办预览捕获、估算和绘制共同读取的值；重启后生效。 |
| `edge.preview.todo.itemCharacterBudget` · 待办预览单项字符预算 | `integer` | 512 | `restart` | 待办预览捕获、估算和绘制共同读取的值；重启后生效。 |
| `edge.preview.todo.itemEstimatedLineCap` · 待办预览单项估算行数上限 | `integer` | 3 | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.itemFontDip` · 待办预览条目基础字号 | `number` | 12 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.linkedTargetButtonSizeDip` · 待办预览链接按钮尺寸 | `number` | 20 DIP | `restart` | 待办预览捕获、估算和绘制共同读取的值；重启后生效。 |
| `edge.preview.todo.markerFontDip` · 待办预览提醒标记基础字号 | `number` | 10.5 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.maximumHeightDip` · 待办预览非空最大高度 | `number` | 320 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.maximumWidthDip` · 待办预览最大宽度 | `number` | 450 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.minimumHeightDip` · 待办预览非空最小高度 | `number` | 150 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.minimumWidthDip` · 待办预览最小宽度 | `number` | 120 DIP | `restart` | 沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。 |
| `edge.preview.todo.summaryFontDip` · 待办预览计数基础字号 | `number` | 11 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.textReserveDip` · 待办预览文字通道固定留白 | `number` | 80 DIP | `restart` | 宽度估算添加、折行估算扣除的同一个固定宽度；不改变实际 Grid 布局。 |
| `edge.preview.todo.titleFontDip` · 待办预览标题基础字号 | `number` | 13 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.truncationFontDip` · 待办预览截断提示基础字号 | `number` | 14 DIP | `restart` | 应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。 |
| `edge.preview.todo.widthDip` · 待办预览最终宽度 | `number` | 按标题、受限待办文本及标记宽度估算，并套用当前空内容或非空宽度范围。 | `restart` | 按标题、受限待办文本及标记宽度估算，并套用当前空内容或非空宽度范围。 |
| `edge.queue.exitSnapDistanceDip` · 队列外沿脱离距离 | `number` | 18 DIP | `next_use` | 自由队列现有吸附与落点计算使用的独立距离。 |
| `edge.queue.releaseDistanceDip` · 队列释放距离 | `number` | 30 DIP | `next_use` | 自由队列现有吸附与落点计算使用的独立距离。 |
| `edge.queue.snapDistanceDip` · 队列吸附距离 | `number` | 18 DIP | `next_use` | 自由队列现有吸附与落点计算使用的独立距离。 |
| `edge.queue.takeoverAdvantageDip` · 队列目标接管优势 | `number` | 6 DIP | `next_use` | 自由队列现有吸附与落点计算使用的独立距离。 |

### 查找浮窗 (13)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `find.baseWidthDip` · 查找框基础宽度 | `number` | 296 DIP | `restart` | 乘全局文字缩放之前的查找框宽度。 |
| `find.controlSizeDip` · 查找控件基础尺寸 | `number` | 23 DIP | `restart` | 查找输入最小高度与按钮边长，随后按文字缩放适配。 |
| `find.counterFontSizeDip` · 查找计数字号 | `number` | 10.5 DIP | `restart` | 匹配数量文字乘全局缩放之前的字号。 |
| `find.counterMinWidthDip` · 查找计数最小宽度 | `number` | 42 DIP | `restart` | 查找匹配数量区域的基础最小宽度。 |
| `find.iconSizeDip` · 查找按钮图标尺寸 | `number` | 16 DIP | `restart` | 查找前后项与关闭按钮的图标边长。 |
| `find.inputFontSizeDip` · 查找输入字号 | `number` | 11.5 DIP | `restart` | 查找输入与按钮字符乘全局缩放之前的字号。 |
| `find.inputMinWidthDip` · 查找输入框最小宽度 | `number` | 72 DIP | `restart` | 查找框输入区域所在列的最小宽度。 |
| `find.maxWidthDip` · 查找框宽度上限 | `number` | 420 DIP | `restart` | 查找框随文字缩放时的宽度上限。 |
| `find.minLeftDip` · 查找框左侧最小位置 | `number` | 6 DIP | `next_use` | 查找框默认位置相对纸片左侧的最小距离。 |
| `find.minWidthDip` · 查找框宽度下限 | `number` | 236 DIP | `restart` | 查找框随文字缩放时的宽度下限。 |
| `find.rightOverhangDip` · 查找框右侧伸出 | `number` | 20 DIP | `next_use` | 默认查找框相对纸片右侧向外伸出的距离。 |
| `find.topOffsetDip` · 查找框竖向偏移 | `number` | 33 DIP | `next_use` | 默认位置相对标题栏下沿的偏移。 |
| `find.widthDip` · 查找框最终宽度 | `number` | 按当前设置与布局计算 | `restart` | 覆盖查找框经过文字缩放、限幅和取整后的宽度。 |

### 全屏检测 (3)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `fullscreen.eventDebounceMs` · 全屏事件防抖 | `number` | 75 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |
| `fullscreen.globalScanIntervalMs` · 全屏全局扫描间隔 | `number` | 5000 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |
| `fullscreen.pollIntervalMs` · 全屏常规检查间隔 | `number` | 1000 ms | `live` | 现有计时或节流入口的间隔；不改变其他计时器的节奏。 建议：≥ 1. |

### Markdown 解析、排版与交互 (7)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `markdown.layout.maxLinesPerTurn` · 排版每轮行数 | `integer` | 4 lines | `live` | 后台排版工作线程一次调度最多处理的行数。 |
| `markdown.layout.maxTurnMs` · 排版每轮时间 | `number` | 1.5 ms | `live` | 后台排版工作线程一次调度的时间预算。 |
| `markdown.parse.fullThresholdChars` · 整篇解析阈值 | `integer` | 8000 chars | `live` | 正文短于此值时每次编辑整篇解析，否则尝试现有局部解析。 |
| `markdown.parse.localWindowChars` · 局部解析窗口 | `integer` | 1024 chars | `live` | 局部解析的初始字符窗口，仍由原语义边界规则扩展。 |
| `markdown.reveal.fadeMs` · 控制符淡入时长 | `number` | 140 ms | `live` | Markdown 编辑态控制符显现动画的基础时长。 |
| `markdown.reveal.tickMs` · 控制符动画节拍 | `number` | 16 ms | `restart` | 控制符淡入计时器的调度间隔。 |
| `markdown.tasks.hitPaddingDip` · Markdown 勾选热区 | `number` | 2 DIP | `live` | 渲染勾选框四周额外可点击的距离。 |

### 按入口控制菜单 (11)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `menu.edge.association` · 边缘胶囊菜单：关联 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.edge.externalEditor` · 边缘胶囊菜单：外部编辑器 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.edge.newNote` · 边缘胶囊菜单：新建笔记 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.edge.newTodo` · 边缘胶囊菜单：新建待办 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.edge.workspace` · 边缘胶囊菜单：工作区 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.association` · 纸片菜单：关联 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.externalEditor` · 纸片菜单：外部编辑器 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.newNote` · 纸片菜单：新建笔记 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.newTodo` · 纸片菜单：新建待办 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.runScript` · 纸片菜单：运行脚本 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |
| `menu.paper.workspace` · 纸片菜单：工作区 | `boolean` | true | `next_use` | 只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。 |

### 新建纸片 (4)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `newPaper.note.heightDip` · 新建笔记高度 | `number` | 360 DIP | `next_use` | 新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。 |
| `newPaper.note.widthDip` · 新建笔记宽度 | `number` | 320 DIP | `next_use` | 新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。 |
| `newPaper.todo.heightDip` · 新建待办高度 | `number` | 340 DIP | `next_use` | 新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。 |
| `newPaper.todo.widthDip` · 新建待办宽度 | `number` | 280 DIP | `next_use` | 新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。 |

### 笔记、图片与排版 (37)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `note.clipboard.imageMaxWidthDip` · 剪贴板图片最大宽度 | `number` | 640 DIP | `next_use` | HTML 和 RTF 导出图片的默认及最大显示宽度。 |
| `note.clipboard.imageMinWidthDip` · 剪贴板图片最小宽度 | `number` | 24 DIP | `next_use` | HTML 和 RTF 导出图片显示宽度的下限。 |
| `note.clipboard.maxHtmlImageBytes` · HTML 剪贴板图片预算 | `integer` | 25165824 bytes | `next_use` | 导出混排 HTML 时按每次图片出现累加的源图片字节预算。 |
| `note.clipboard.maxImageBytes` · 剪贴板图片输入上限 | `integer` | 33554432 bytes | `next_use` | 读取剪贴板编码图片数据时使用的字节上限。 |
| `note.clipboard.maxRtfImageBytes` · RTF 剪贴板图片预算 | `integer` | 16777216 bytes | `next_use` | 导出混排 RTF 时按每次图片出现累加的源图片字节预算。 |
| `note.editor.indentationSize` · 笔记缩进宽度 | `integer` | 4 columns | `restart` | AvalonEdit 的制表符与缩进显示宽度。 |
| `note.images.autoCompressedDimensionPx` · 自动压缩目标边长 | `integer` | 2048 px | `next_use` | 开启自动压缩时大图片缩小后的最大边长。 |
| `note.images.cachePruneMs` · 图片块缓存清理等待 | `number` | 200 ms | `restart` | 可视行变化后整理图片控件缓存的防抖时间。 |
| `note.images.fallbackWidthDip` · 笔记图片未布局宽度 | `number` | 240 DIP | `restart` | 图片视口尚无有效宽度时使用的目标宽度。 |
| `note.images.horizontalPaddingDip` · 笔记图片左右留白 | `number` | 2 DIP | `restart` | 计算图片可用宽度时两侧预留的距离。 |
| `note.images.maxCacheEntries` · 解码图片缓存数量 | `integer` | 20 items | `next_use` | 非当前可视图片的 LRU 解码缓存条目预算。 |
| `note.images.maxDecodedBytes` · 解码图片缓存预算 | `integer` | 52428800 bytes | `next_use` | 非当前可视图片的 LRU 解码缓存字节预算。 |
| `note.images.maxInputBytes` · 图片导入输入预算 | `integer` | 33554432 bytes | `next_use` | 导入图片原始输入数据的字节上限。 |
| `note.images.maxStoredBytes` · 单张图片存储预算 | `integer` | 8388608 bytes | `next_use` | 单个图片资产编码后的字节上限。 |
| `note.images.maxStoredDimensionPx` · 图片存储最大边长 | `integer` | 4096 px | `next_use` | 导入及加载图片资产时接受的最大像素边长。 |
| `note.images.maxTotalBytes` · 图片资产总预算 | `integer` | 125829120 bytes | `next_use` | 当前图片存储所有编码图片的总字节上限。 |
| `note.images.minTargetWidthDip` · 笔记图片目标最小宽度 | `number` | 80 DIP | `restart` | 视口计算完成后图片目标宽度的原生下限。 |
| `note.images.resizeSettleMs` · 图片重排静置时间 | `number` | 200 ms | `restart` | 停止改变图片视口宽度后恢复高质量解码的等待时间。 |
| `note.images.resizeToleranceDip` · 图片重排宽度容差 | `number` | 1 DIP | `live` | 图片视口宽度变化达到此距离时开始重排。 |
| `note.images.targetWidthDip` · 笔记图片最终可用宽度 | `number` | 按当前设置与布局计算 | `restart` | 覆盖根据正文视口计算的图片目标宽度。 |
| `note.images.verticalPaddingDip` · 笔记图片上下留白 | `number` | 7 DIP | `restart` | 图片块布局使用的竖向基础留白。 |
| `note.images.viewportReserveDip` · 笔记图片视口预留 | `number` | 10 DIP | `restart` | 计算图片宽度时除左右留白外额外减去的空间。 |
| `note.maxTextChars` · 笔记文字长度上限 | `integer` | 100000 chars | `restart` | 笔记编辑器与宿主写入入口共同使用的字符上限。 |
| `note.padding.bottomDip` · 笔记正文下留白 | `number` | 8 DIP | `restart` | 笔记正文区域的内边距。 |
| `note.padding.leftDip` · 笔记正文左留白 | `number` | 13 DIP | `restart` | 笔记正文区域的内边距。 |
| `note.padding.rightDip` · 笔记正文右留白 | `number` | 6 DIP | `restart` | 笔记正文区域的内边距。 |
| `note.padding.topDip` · 笔记正文上留白 | `number` | 8 DIP | `restart` | 笔记正文区域的内边距。 |
| `note.typography.baseCodeFontSize` · 笔记代码基础字号 | `number` | 13 DIP | `restart` | 字号预设修正与全局缩放之前的代码字号。 |
| `note.typography.baseFontSize` · 笔记正文基础字号 | `number` | 14 DIP | `restart` | 字号预设修正与全局缩放之前的正文字号。 |
| `note.typography.baseHeading1FontSize` · 笔记一级标题基础字号 | `number` | 17 DIP | `restart` | 字号预设修正与全局缩放之前的一级标题字号。 |
| `note.typography.baseHeading2FontSize` · 笔记二级标题基础字号 | `number` | 15 DIP | `restart` | 字号预设修正与全局缩放之前的二级标题字号。 |
| `note.typography.baseHeading3FontSize` · 笔记三级标题基础字号 | `number` | 14 DIP | `restart` | 字号预设修正与全局缩放之前的三级标题字号。 |
| `note.typography.codeFontSize` · 笔记代码计算字号 | `number` | 按当前设置与布局计算 | `restart` | 覆盖预设与全局缩放后的代码字号；单笔记缩放仍在之后应用。 |
| `note.typography.fontSize` · 笔记正文计算字号 | `number` | 按当前设置与布局计算 | `restart` | 覆盖预设与全局缩放后的正文字号；单笔记缩放仍在之后应用。 |
| `note.typography.heading1FontSize` · 笔记一级标题计算字号 | `number` | 按当前设置与布局计算 | `restart` | 覆盖预设与全局缩放后的一级标题字号；单笔记缩放仍在之后应用。 |
| `note.typography.heading2FontSize` · 笔记二级标题计算字号 | `number` | 按当前设置与布局计算 | `restart` | 覆盖预设与全局缩放后的二级标题字号；单笔记缩放仍在之后应用。 |
| `note.typography.heading3FontSize` · 笔记三级标题计算字号 | `number` | 按当前设置与布局计算 | `restart` | 覆盖预设与全局缩放后的三级标题字号；单笔记缩放仍在之后应用。 |

### 纸片外壳与交互 (24)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `paper.capsuleCloseGlyphNormalOffset` · 胶囊关闭符号偏移 | `number` | -1 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.capsuleIconGap` · 胶囊图标文字间距 | `number` | 4 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.capsuleLeftPadding` · 普通胶囊左内边距 | `number` | 6 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.capsuleNormalCloseWidth` · 普通胶囊关闭区宽度 | `number` | 21 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.capsuleNormalMinWidth` · 普通胶囊最小宽度 | `number` | 76 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.capsuleRightPadding` · 普通胶囊右内边距 | `number` | 6 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.collapseResizeMilliseconds` · 纸片收起缩放时长 | `integer` | 150 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.collapseShellFadeMilliseconds` · 纸片收起淡出时长 | `integer` | 70 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.deepCapsuleCrossQueueDragMorphMilliseconds` · 胶囊跨队列变形时长 | `integer` | 90 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.deepCapsuleCrossQueueDragScaleFrom` · 胶囊跨队列初始缩放 | `number` | 0.97 | `next_use` | 纸片现有界面或交互路径使用的数值。 建议：0–1. |
| `paper.deepCapsuleReorderDragExtraThreshold` · 胶囊重排额外阈值 | `number` | 4 DIP | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.expandAnimationMilliseconds` · 纸片展开时长 | `integer` | 220 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.expandCapsuleFadeOutMilliseconds` · 展开时胶囊淡出时长 | `integer` | 80 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.expandShellFadeInMilliseconds` · 展开时纸面淡入时长 | `integer` | 140 ms | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.maxUndoDepth` · 待办撤销深度 | `integer` | 100 | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.radiusBlock` · 面板圆角 | `number` | 12 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.radiusControl` · 控件圆角 | `number` | 8 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.radiusShell` · 纸片外壳圆角 | `number` | 16 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.radiusSmall` · 小元素圆角 | `number` | 4 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |
| `paper.titleBarDragThreshold` · 标题栏拖动阈值 | `number` | 1 DIP | `next_use` | 纸片现有界面或交互路径使用的数值。 |
| `paper.titleBarHeightDip` · 纸片顶栏最终高度 | `number` | 按当前设置或运行状态计算 | `restart` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |
| `paper.titleBaseDip` · 纸片标题基础字号 | `number` | 12 DIP | `live` | 应用当前预设及全局缩放前的基础字号。 |
| `paper.titleFontDip` · 纸片标题最终字号 | `number` | 按当前设置或运行状态计算 | `live` | 未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。 |
| `paper.topBarCollisionGap` · 顶栏按钮碰撞间隔 | `number` | 2 DIP | `restart` | 纸片现有界面或交互路径使用的数值。 |

### 插件呈现与事件 (9)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `plugin.capsule.componentGapDip` · 插件胶囊组件间距 | `number` | 5 DIP | `restart` | 插件模板内相邻组件间的距离。 |
| `plugin.capsule.fillProgressBarMinWidthDip` · 插件胶囊填充条最小宽 | `number` | 18 DIP | `restart` | 填充式进度条的最小宽度。 |
| `plugin.capsule.maxContentWidthDip` · 插件胶囊最大内容宽 | `number` | 320 DIP | `restart` | 插件胶囊内容期望宽度的上限。 |
| `plugin.capsule.minContentWidthDip` · 插件胶囊最小内容宽 | `number` | 34 DIP | `restart` | 插件胶囊内容期望宽度的下限。 |
| `plugin.capsule.progressBarWidthDip` · 插件胶囊进度条宽 | `number` | 30 DIP | `restart` | 插件未指定尺寸时的进度条宽度。 |
| `plugin.capsule.progressRingSizeDip` · 插件胶囊进度环大小 | `number` | 18 DIP | `restart` | 插件未指定尺寸时的进度环直径。 |
| `plugin.capsule.statusDotSizeDip` · 插件胶囊状态点大小 | `number` | 7 DIP | `restart` | 插件胶囊状态圆点的直径。 |
| `plugin.events.userChangeDebounceMs` · 插件用户修改事件防抖 | `number` | 180 ms | `restart` | 聚合连续用户编辑再生成插件通知的等待时间。 |
| `plugin.settings.textPropagationMs` · 插件文本设置传播防抖 | `number` | 180 ms | `restart` | 合并插件字符串与数字设置变更的等待时间。 |

### 待办提醒 (15)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `reminder.advanceNoticeMs` · 提醒提前通知时间 | `number` | 5000 ms | `next_use` | 向插件发送提醒预告相对于实际到期的提前量。 |
| `reminder.maxCustomSoundBytes` · 提醒自定义音频预算 | `integer` | 10485760 bytes | `next_use` | 加载自定义提醒声音时允许的文件大小。 |
| `reminder.maxTimerIntervalMs` · 提醒最长检查间隔 | `number` | 60000 ms | `next_use` | 下一次提醒检查定时器允许的最长间隔。 |
| `reminder.minTimerIntervalMs` · 提醒最短检查间隔 | `number` | 250 ms | `next_use` | 提醒调度和重试定时器允许的最短间隔。 |
| `reminder.retryMs` · 提醒失败重试间隔 | `number` | 30000 ms | `next_use` | 部分或全部提醒未送达后的默认重试等待时间。 |
| `reminder.summaryChars` · 提醒摘要长度 | `integer` | 90 chars | `next_use` | 托盘提醒摘要的最大字符数。 |
| `reminder.ui.countdownFontOffsetDip` · 提醒倒计时字号缩小 | `number` | 2 DIP | `restart` | 倒计时字号相对正文减去的基础值。 |
| `reminder.ui.iconFontOffsetDip` · 提醒图标字号缩小 | `number` | 1.5 DIP | `restart` | 提醒图标字号相对待办正文减去的基础值。 |
| `reminder.ui.iconOpacity` · 提醒图标静置透明度 | `number` | 0.44 | `restart` | 未悬停时提醒设置图标的透明度。 |
| `reminder.ui.iconWidthOffsetDip` · 提醒图标宽度缩小 | `number` | 6 DIP | `restart` | 提醒设置按钮相对勾选列宽度减去的基础值。 |
| `reminder.ui.minCountdownFontDip` · 提醒倒计时最小字号 | `number` | 10 DIP | `restart` | 倒计时字号相对正文计算时的基础下限。 |
| `reminder.ui.minCountdownWidthDip` · 提醒倒计时最小宽度 | `number` | 24 DIP | `restart` | 倒计时区域的基础最小宽度。 |
| `reminder.ui.minIconFontDip` · 提醒图标最小字号 | `number` | 10.5 DIP | `restart` | 提醒图标字号相对正文计算时的基础下限。 |
| `reminder.ui.minIconWidthDip` · 提醒图标最小宽度 | `number` | 17 DIP | `restart` | 提醒设置按钮的基础最小宽度。 |
| `reminder.ui.pressedOpacity` · 提醒图标按下透明度 | `number` | 0.66 | `restart` | 按下提醒设置图标时的透明度。 |

### 设置界面 (10)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `settings.sidebar.baseWidthDip` · 设置侧栏基础宽度 | `number` | 158 DIP | `restart` | 设置导航栏宽度计算的基础下限。 |
| `settings.sidebar.maxWidthRatio` · 设置侧栏最大扩展系数 | `number` | 1.2 | `restart` | 侧栏按文字测量加宽时相对基础宽度的上限比例。 |
| `settings.sidebar.rowHeightDip` · 设置导航行高度 | `number` | 34 DIP | `restart` | 设置窗口左侧导航选项的固定高度。 |
| `settings.sidebar.widthDip` · 设置侧栏最终宽度 | `number` | 按当前设置与布局计算 | `restart` | 覆盖根据翻译文本测量和限幅后得到的设置侧栏宽度。 |
| `settings.window.baseHeightDip` · 设置窗口基础高度 | `number` | 620 DIP | `restart` | 设置窗口的常规目标高度。 |
| `settings.window.baseWidthDip` · 设置窗口基础宽度 | `number` | 840 DIP | `restart` | 设置窗口加上侧栏扩展量之前的目标宽度。 |
| `settings.window.boundaryMarginDip` · 设置窗口边界空间预留 | `number` | 32 DIP | `restart` | 最终屏幕内限幅时从可用空间减去的总余量。 |
| `settings.window.minHeightDip` · 设置窗口参考最小高度 | `number` | 320 DIP | `restart` | 计算屏幕内设置窗口高度时使用的参考下限。 |
| `settings.window.minWidthDip` · 设置窗口参考最小宽度 | `number` | 360 DIP | `restart` | 计算屏幕内设置窗口宽度时使用的参考下限。 |
| `settings.window.preferredMarginDip` · 设置窗口首选空间预留 | `number` | 48 DIP | `restart` | 计算设置目标尺寸时从屏幕可用空间减去的总余量。 |

### 待办输入、排版、折叠与拖动 (55)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `todo.delete.animationMs` · 待办删除动画时长 | `integer` | 200 ms | `next_use` | 单条待办删除时的淡出和横移基础时长。 |
| `todo.delete.offsetDip` · 待办删除动画位移 | `number` | 30 DIP | `next_use` | 单条待办删除动画的横向目标位移。 |
| `todo.drag.autoScroll` · 拖动待办自动滚动 | `boolean` | true | `live` | 只控制拖动中的滚动步骤，保留排序与鼠标捕获收尾。 |
| `todo.drag.ghostMinHeightDip` · 待办拖动影像最小高度 | `number` | 30 DIP | `next_use` | 跟随鼠标的待办影像最小高度。 |
| `todo.drag.ghostMinWidthDip` · 待办拖动影像最小宽度 | `number` | 160 DIP | `next_use` | 跨位置拖动时跟随鼠标的待办影像最小宽度。 |
| `todo.drag.indicatorHeightDip` · 待办落点线高度 | `number` | 3 DIP | `restart` | 拖动排序时插入位置指示线的高度。 |
| `todo.drag.indicatorMinWidthDip` · 待办落点线最小宽度 | `number` | 24 DIP | `live` | 插入位置指示线的最小宽度。 |
| `todo.drag.indicatorRadiusDip` · 待办落点线圆角 | `number` | 1.5 DIP | `restart` | 拖动排序指示线的圆角。 |
| `todo.drag.indicatorWidthInsetDip` · 待办落点线宽度收缩 | `number` | 8 DIP | `live` | 插入位置指示线相对待办行减去的总宽度。 |
| `todo.drag.scrollAcceleration` · 滚动加速系数 | `number` | 520 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollBaseSpeed` · 滚动起步速度 | `number` | 28 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollEaseSeconds` · 滚动加速时间 | `number` | 0.2 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollEdgeDip` · 拖动滚动热区基值 | `number` | 48 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollEdgeFinalDip` · 拖动滚动最终热区 | `number` | 按当前设置或运行状态计算 | `live` | 替换每次按缩放和视口高度计算的热区距离。 |
| `todo.drag.scrollMaxElapsedSeconds` · 滚动单步时间上限 | `number` | 0.05 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollProximityPower` · 滚动距离指数 | `number` | 2 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 |
| `todo.drag.scrollSpeedFinal` · 拖动滚动最终速度 | `number` | 按当前设置或运行状态计算 | `live` | 替换每次根据指针接近边缘程度计算的速度。 |
| `todo.drag.scrollTickMs` · 拖动滚动节拍 | `number` | 16 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 建议：≥ 1. |
| `todo.drag.scrollViewportRatio` · 拖动滚动热区比例 | `number` | 0.25 | `live` | 拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。 建议：0–0.5. |
| `todo.fold.thresholdLines` · 待办折叠触发行数 | `integer` | 4 lines | `next_use` | 待办排版后的显示行数超过此值时允许折叠。 |
| `todo.fold.visibleLines` · 待办折叠可见行数 | `integer` | 2 lines | `next_use` | 折叠待办保留的可见正文行数。 |
| `todo.maxTextChars` · 待办文字长度上限 | `integer` | 5000 chars | `restart` | 待办编辑器与宿主写入入口共同使用的字符上限。 |
| `todo.metrics.appendGlyphFontSize` · 待办追加符号字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的追加符号字号，下次构建待办行时使用。 |
| `todo.metrics.appendMinHeight` · 待办追加行高度 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的追加行高度，下次构建待办行时使用。 |
| `todo.metrics.checkColumnWidth` · 待办勾选列宽度 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的勾选列宽度，下次构建待办行时使用。 |
| `todo.metrics.ghostTextFontSize` · 待办拖动影像字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的拖动影像字号，下次构建待办行时使用。 |
| `todo.metrics.linkedPaperIconFontSize` · 待办链接图标字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的链接图标字号，下次构建待办行时使用。 |
| `todo.metrics.linkedPaperNameFontSize` · 待办链接名称字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的链接名称字号，下次构建待办行时使用。 |
| `todo.metrics.rowMinHeight` · 待办行最小高度 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的行最小高度，下次构建待办行时使用。 |
| `todo.metrics.textFontSize` · 待办正文字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的正文字号，下次构建待办行时使用。 |
| `todo.metrics.textVerticalPadding` · 待办正文竖向留白 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的正文竖向留白，下次构建待办行时使用。 |
| `todo.metrics.trashGlyphFontSize` · 待办垃圾桶字号 | `number` | 按当前设置与布局计算 | `next_use` | 覆盖待办预设和全局文字缩放之后的垃圾桶字号，下次构建待办行时使用。 |
| `todo.paste.animatedItems` · 粘贴动画待办数量 | `integer` | 15 items | `next_use` | 多行粘贴时最多参加错峰入场动画的待办数量。 |
| `todo.paste.fadeMs` · 粘贴淡入时长 | `integer` | 200 ms | `next_use` | 多行粘贴待办的透明度过渡基础时长。 |
| `todo.paste.maxItems` · 待办批量输入上限 | `integer` | 200 items | `next_use` | 一次多行粘贴或宿主批量创建的最大待办数量。 |
| `todo.paste.moveMs` · 粘贴移动时长 | `integer` | 220 ms | `next_use` | 多行粘贴待办的竖向过渡基础时长。 |
| `todo.paste.staggerMs` · 粘贴动画错峰间隔 | `integer` | 40 ms | `next_use` | 相邻待办开始入场动画的时间差。 |
| `todo.paste.startOffsetDip` · 粘贴动画初始偏移 | `number` | -15 DIP | `next_use` | 待办入场动画开始时的竖向位移。 |
| `todo.pathLink.multilineMinWidthDip` · 多行待办路径按钮最小宽度 | `number` | 44 DIP | `restart` | 路径名称按钮在多行待办中的基础最小宽度。 |
| `todo.pathLink.multilineWidthRatio` · 多行待办路径按钮宽度系数 | `number` | 2 | `restart` | 路径按钮宽度相对勾选列宽度的多行系数。 |
| `todo.pathLink.namePaddingDip` · 路径名称测量留白 | `number` | 10 DIP | `restart` | 允许完整路径名称时在测量文本宽度外增加的总空间。 |
| `todo.pathLink.singleLineMinWidthDip` · 单行待办路径按钮最小宽度 | `number` | 50 DIP | `restart` | 路径名称按钮在单行待办中的基础最小宽度。 |
| `todo.pathLink.singleLineWidthRatio` · 单行待办路径按钮宽度系数 | `number` | 2.2 | `restart` | 路径按钮宽度相对勾选列宽度的单行系数。 |
| `todo.pathLink.widthDip` · 待办路径按钮最终宽度 | `number` | 按当前设置与布局计算 | `restart` | 覆盖名称、图标和待办行宽度规则计算后的路径按钮宽度。 |
| `todo.selection.edgeDip` · 待办多选滚动热区 | `number` | 28 DIP | `live` | 扫选自动滚动热区的基础距离，随后乘文字缩放。 |
| `todo.selection.edgeHeightRatio` · 待办多选热区高度系数 | `number` | 0.25 | `live` | 扫选热区占列表可视高度的最大比例。 |
| `todo.selection.effectiveEdgeDip` · 待办多选最终热区 | `number` | 按当前设置与布局计算 | `live` | 覆盖尺寸与系数计算后的扫选滚动热区。 |
| `todo.selection.effectiveStepDip` · 待办多选最终加速距离 | `number` | 按当前设置与布局计算 | `live` | 覆盖计算后增加一次滚动步数所需的距离。 |
| `todo.selection.maxSteps` · 待办多选最大滚动步数 | `integer` | 6 steps | `live` | 扫选自动滚动每次跟踪最多执行的行滚动次数。 |
| `todo.selection.stepDip` · 待办多选滚动加速距离 | `number` | 14 DIP | `live` | 增加一次滚动步数所需距离的基础下限。 |
| `todo.selection.stepEdgeRatio` · 待办多选滚动加速系数 | `number` | 0.7 | `live` | 滚动步数增加距离相对于热区的比例。 |
| `todo.selection.tickMs` · 待办多选跟踪节拍 | `number` | 32 ms | `restart` | 框选或扫选待办时鼠标跟踪定时器间隔。 |
| `todo.strike.insetDip` · 待办删除线左右留白 | `number` | 3 DIP | `next_use` | 已完成待办删除线两端的内缩距离。 |
| `todo.strike.thicknessDip` · 待办删除线粗细 | `number` | 1.35 DIP | `next_use` | 已完成待办删除线的绘制粗细。 |
| `todo.strike.verticalRatio` · 待办删除线高度比例 | `number` | 0.56 | `next_use` | 删除线在每行文字矩形内的竖向位置比例。 |

### 窗口尺寸、恢复、关联与跟随 (18)

| 键 / 名称 | 类型 | 默认 / 计算来源 | 生效 | 说明 |
| --- | --- | --- | --- | --- |
| `window.autoCollapse.delayMs` · 失焦自动收起延迟 | `number` | 140 ms | `next_use` | 失焦后执行自动收起前的等待时间。 |
| `window.binding.dragOpacity` · 窗口关联拖动透明度 | `number` | 0.72 | `restart` | 拖动关联手势期间关联按钮的透明度。 |
| `window.binding.sampleMs` · 窗口关联目标采样 | `number` | 32 ms | `live` | 拖动关联按钮时重新识别目标窗口的时间间隔。 |
| `window.follow.movingIdleMs` · 窗口移动跟随闲置时间 | `number` | 2000 ms | `live` | 仍处于移动手势时结束跟随帧所需的闲置时间。 |
| `window.follow.retractDelayMs` · 跟随胶囊收回延迟 | `integer` | 180 ms | `restart` | 鼠标离开跟随胶囊后的收回等待时间。 |
| `window.follow.slideMs` · 跟随胶囊滑动时长 | `number` | 130 ms | `next_use` | 绑定窗口的胶囊显示和收回滑动时长。 |
| `window.follow.stableMs` · 窗口跟随稳定时长 | `number` | 100 ms | `live` | 目标窗口静止后结束跟随帧所需的稳定时间。 |
| `window.follow.stableSamples` · 窗口跟随稳定样本数 | `integer` | 3 samples | `live` | 结束持续跟随帧前需要的稳定位置样本数。 |
| `window.layout.capsuleHeightDip` · 胶囊整体高度 | `number` | 46 DIP | `restart` | 原有布局和归一化共同使用的基础尺寸。 |
| `window.layout.capsuleWidthDip` · 胶囊缺省宽度 | `number` | 92 DIP | `restart` | 原有布局和归一化共同使用的基础尺寸。 |
| `window.layout.minHeightDip` · 纸片最小高度 | `number` | 120 DIP | `restart` | 原有布局和归一化共同使用的基础尺寸。 |
| `window.layout.minWidthDip` · 纸片最小宽度 | `number` | 96 DIP | `restart` | 原有布局和归一化共同使用的基础尺寸。 |
| `window.layout.topBarHeightDip` · 纸片顶栏基础高度 | `number` | 23.5 DIP | `restart` | 原有布局和归一化共同使用的基础尺寸。 |
| `window.opacity.transitionMs` · 失焦透明过渡时长 | `integer` | 120 ms | `next_use` | 失焦纸片和静置胶囊透明度过渡的基础时长。 |
| `window.restore.constrainSize` · 恢复时限制纸片尺寸 | `boolean` | true | `next_use` | 关闭后窗口恢复保留保存的宽高，仍使用原有位置救援。 |
| `window.tether.detachDip` · 窗口牵引脱离距离 | `number` | 72 DIP | `live` | 拖动达到此距离后解除牵引。 |
| `window.tether.minHandleHeightDip` · 窗口牵引可见把手高 | `number` | 12 DIP | `live` | 维持可拖动把手时使用的最小可见高度。 |
| `window.tether.minHandleWidthDip` · 窗口牵引可见把手宽 | `number` | 48 DIP | `live` | 维持可拖动把手时使用的最小可见宽度。 |

目录来源：[Desktop](../src/InjectionParameters.Desktop.cs), [Content](../src/InjectionParameters.Content.cs), [Preview](../src/InjectionParameters.Preview.cs).
