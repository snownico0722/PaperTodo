namespace PaperTodo;

// Typed hover and preview inputs. Profiles are built at startup and rebuilt only on Apply.
internal static partial class InjectionParameters
{
    internal static readonly InjectionParameter<int> HoverSampleCapacity =
        new("edge.hover.sampleCapacity", 20, "悬停指针样本容量", "创建预测器时分配的环形采样缓冲区长度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> HoverHistoryWindowMilliseconds =
        new("edge.hover.historyWindowMs", 64, "悬停运动历史窗口", "计算近期轨迹和速度趋势使用的时间窗口。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverStaleHistoryMilliseconds =
        new("edge.hover.staleHistoryMs", 180, "悬停历史失效时长", "较长采样间隔后不继续沿用旧轨迹。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverDuplicateSampleMilliseconds =
        new("edge.hover.duplicateSampleMs", 1.5, "悬停重复采样间隔", "合并短时间内重复位置样本时使用的间隔。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverBrakingRatio =
        new("edge.hover.brakingRatio", 0.72, "悬停减速速度比例", "当前竖直速度与先前速度比较时的减速比例。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverBrakingDeltaDipPerMillisecond =
        new("edge.hover.brakingDeltaDipPerMs", 0.05, "悬停减速速度差", "认定减速还需满足的绝对速度差。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverAccelerationRatio =
        new("edge.hover.accelerationRatio", 1.15, "悬停加速速度比例", "当前竖直速度与先前速度比较时的加速比例。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> HoverAccelerationDeltaDipPerMillisecond =
        new("edge.hover.accelerationDeltaDipPerMs", 0.04, "悬停加速速度差", "认定加速还需满足的绝对速度差。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverCorridorGeometryWeight =
        new("edge.hover.corridorGeometryWeight", 0.60, "跨空白几何证据权重", "合并跨胶囊空白区域的几何与运动证据时使用的几何权重。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverCorridorMotionWeight =
        new("edge.hover.corridorMotionWeight", 0.40, "跨空白运动证据权重", "合并跨胶囊空白区域的几何与运动证据时使用的运动权重。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverCorridorStrongEvidenceConfidence =
        new("edge.hover.corridorStrongConfidence", 0.84, "跨空白强证据阈值", "单一证据足以保留跨空白浏览意图的置信度。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverCorridorKeepEnterConfidence =
        new("edge.hover.corridorKeepEnterConfidence", 0.58, "跨空白保留进入阈值", "尚未锁存保留状态时所需的合并置信度。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverCorridorKeepExitConfidence =
        new("edge.hover.corridorKeepExitConfidence", 0.42, "跨空白保留退出阈值", "已锁存保留状态时继续保留所需的合并置信度。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverCorridorMeaningfulMovementDip =
        new("edge.hover.corridorMeaningfulMovementDip", 0.5, "跨空白有效移动距离", "更新最近有效指针移动时间戳的距离。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverCorridorGeometryStationaryGraceMilliseconds =
        new("edge.hover.corridorStationaryGraceMs", 160, "跨空白静止几何宽限", "指针停止后几何证据仍保持原强度的时间。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverCorridorMotionFullConfidenceHorizonMilliseconds =
        new("edge.hover.corridorMotionFullConfidenceMs", 250, "跨空白运动满置信度时间", "预计到达目标的时间不超过此值时保留完整运动置信度。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverCorridorMotionFadeHorizonMilliseconds =
        new("edge.hover.corridorMotionFadeMs", 900, "跨空白运动置信度衰减终点", "预计到达目标的时间达到此值时运动置信度衰减结束。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighInitialMinimumObservationMilliseconds =
        new("edge.hover.profile.veryHigh.initial.minimumObservationMs", 6, "悬停很高档·初次进入最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighInitialMinimumDelayMilliseconds =
        new("edge.hover.profile.veryHigh.initial.minimumDelayMs", 4, "悬停很高档·初次进入最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighInitialMaximumDelayMilliseconds =
        new("edge.hover.profile.veryHigh.initial.maximumDelayMs", 22, "悬停很高档·初次进入最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighInitialPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.veryHigh.initial.passThroughVetoHorizonMs", 55, "悬停很高档·初次进入穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighInitialDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.veryHigh.initial.dynamicDelayHorizonMs", 135, "悬停很高档·初次进入动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighTransferMinimumObservationMilliseconds =
        new("edge.hover.profile.veryHigh.transfer.minimumObservationMs", 10, "悬停很高档·跨胶囊切换最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighTransferMinimumDelayMilliseconds =
        new("edge.hover.profile.veryHigh.transfer.minimumDelayMs", 8, "悬停很高档·跨胶囊切换最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighTransferMaximumDelayMilliseconds =
        new("edge.hover.profile.veryHigh.transfer.maximumDelayMs", 38, "悬停很高档·跨胶囊切换最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighTransferPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.veryHigh.transfer.passThroughVetoHorizonMs", 90, "悬停很高档·跨胶囊切换穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighTransferDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.veryHigh.transfer.dynamicDelayHorizonMs", 190, "悬停很高档·跨胶囊切换动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighStableFallbackMilliseconds =
        new("edge.hover.profile.veryHigh.stableFallbackMs", 38, "悬停很高档·稳定停留直接放行时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighMinimumDirectionalSpeedDipPerMillisecond =
        new("edge.hover.profile.veryHigh.minimumDirectionalSpeedDipPerMs", 0.13, "悬停很高档·方向运动最小速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighMinimumDirectionConsistency =
        new("edge.hover.profile.veryHigh.minimumDirectionConsistency", 0.76, "悬停很高档·方向一致性阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryHighMinimumVerticalDominance =
        new("edge.hover.profile.veryHigh.minimumVerticalDominance", 0.6, "悬停很高档·竖直运动占比阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryHighCorridorMinimumSpeedDipPerMillisecond =
        new("edge.hover.profile.veryHigh.corridor.minimumSpeedDipPerMs", 0.06, "悬停很高档·跨空白最小运动速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighCorridorMinimumPathConsistency =
        new("edge.hover.profile.veryHigh.corridor.minimumPathConsistency", 0.62, "悬停很高档·跨空白路径一致性", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryHighCorridorTargetPaddingDip =
        new("edge.hover.profile.veryHigh.corridor.targetPaddingDip", 12, "悬停很高档·跨空白目标几何留白", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryHighCorridorNoTargetIntentCloseMilliseconds =
        new("edge.hover.profile.veryHigh.corridor.noTargetIntentCloseMs", 200, "悬停很高档·无目标意图关闭等待", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighInitialMinimumObservationMilliseconds =
        new("edge.hover.profile.high.initial.minimumObservationMs", 8, "悬停高档·初次进入最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighInitialMinimumDelayMilliseconds =
        new("edge.hover.profile.high.initial.minimumDelayMs", 8, "悬停高档·初次进入最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighInitialMaximumDelayMilliseconds =
        new("edge.hover.profile.high.initial.maximumDelayMs", 32, "悬停高档·初次进入最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighInitialPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.high.initial.passThroughVetoHorizonMs", 80, "悬停高档·初次进入穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighInitialDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.high.initial.dynamicDelayHorizonMs", 180, "悬停高档·初次进入动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighTransferMinimumObservationMilliseconds =
        new("edge.hover.profile.high.transfer.minimumObservationMs", 12, "悬停高档·跨胶囊切换最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighTransferMinimumDelayMilliseconds =
        new("edge.hover.profile.high.transfer.minimumDelayMs", 12, "悬停高档·跨胶囊切换最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighTransferMaximumDelayMilliseconds =
        new("edge.hover.profile.high.transfer.maximumDelayMs", 50, "悬停高档·跨胶囊切换最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighTransferPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.high.transfer.passThroughVetoHorizonMs", 120, "悬停高档·跨胶囊切换穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighTransferDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.high.transfer.dynamicDelayHorizonMs", 240, "悬停高档·跨胶囊切换动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighStableFallbackMilliseconds =
        new("edge.hover.profile.high.stableFallbackMs", 50, "悬停高档·稳定停留直接放行时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighMinimumDirectionalSpeedDipPerMillisecond =
        new("edge.hover.profile.high.minimumDirectionalSpeedDipPerMs", 0.1, "悬停高档·方向运动最小速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighMinimumDirectionConsistency =
        new("edge.hover.profile.high.minimumDirectionConsistency", 0.72, "悬停高档·方向一致性阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverHighMinimumVerticalDominance =
        new("edge.hover.profile.high.minimumVerticalDominance", 0.55, "悬停高档·竖直运动占比阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverHighCorridorMinimumSpeedDipPerMillisecond =
        new("edge.hover.profile.high.corridor.minimumSpeedDipPerMs", 0.075, "悬停高档·跨空白最小运动速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverHighCorridorMinimumPathConsistency =
        new("edge.hover.profile.high.corridor.minimumPathConsistency", 0.68, "悬停高档·跨空白路径一致性", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverHighCorridorTargetPaddingDip =
        new("edge.hover.profile.high.corridor.targetPaddingDip", 16, "悬停高档·跨空白目标几何留白", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverHighCorridorNoTargetIntentCloseMilliseconds =
        new("edge.hover.profile.high.corridor.noTargetIntentCloseMs", 350, "悬停高档·无目标意图关闭等待", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumInitialMinimumObservationMilliseconds =
        new("edge.hover.profile.medium.initial.minimumObservationMs", 8, "悬停中档·初次进入最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumInitialMinimumDelayMilliseconds =
        new("edge.hover.profile.medium.initial.minimumDelayMs", 10, "悬停中档·初次进入最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumInitialMaximumDelayMilliseconds =
        new("edge.hover.profile.medium.initial.maximumDelayMs", 36, "悬停中档·初次进入最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumInitialPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.medium.initial.passThroughVetoHorizonMs", 90, "悬停中档·初次进入穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumInitialDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.medium.initial.dynamicDelayHorizonMs", 200, "悬停中档·初次进入动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumTransferMinimumObservationMilliseconds =
        new("edge.hover.profile.medium.transfer.minimumObservationMs", 14, "悬停中档·跨胶囊切换最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumTransferMinimumDelayMilliseconds =
        new("edge.hover.profile.medium.transfer.minimumDelayMs", 20, "悬停中档·跨胶囊切换最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumTransferMaximumDelayMilliseconds =
        new("edge.hover.profile.medium.transfer.maximumDelayMs", 66, "悬停中档·跨胶囊切换最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumTransferPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.medium.transfer.passThroughVetoHorizonMs", 155, "悬停中档·跨胶囊切换穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumTransferDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.medium.transfer.dynamicDelayHorizonMs", 310, "悬停中档·跨胶囊切换动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumStableFallbackMilliseconds =
        new("edge.hover.profile.medium.stableFallbackMs", 60, "悬停中档·稳定停留直接放行时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumMinimumDirectionalSpeedDipPerMillisecond =
        new("edge.hover.profile.medium.minimumDirectionalSpeedDipPerMs", 0.075, "悬停中档·方向运动最小速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumMinimumDirectionConsistency =
        new("edge.hover.profile.medium.minimumDirectionConsistency", 0.68, "悬停中档·方向一致性阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverMediumMinimumVerticalDominance =
        new("edge.hover.profile.medium.minimumVerticalDominance", 0.5, "悬停中档·竖直运动占比阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverMediumCorridorMinimumSpeedDipPerMillisecond =
        new("edge.hover.profile.medium.corridor.minimumSpeedDipPerMs", 0.09, "悬停中档·跨空白最小运动速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumCorridorMinimumPathConsistency =
        new("edge.hover.profile.medium.corridor.minimumPathConsistency", 0.74, "悬停中档·跨空白路径一致性", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverMediumCorridorTargetPaddingDip =
        new("edge.hover.profile.medium.corridor.targetPaddingDip", 20, "悬停中档·跨空白目标几何留白", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverMediumCorridorNoTargetIntentCloseMilliseconds =
        new("edge.hover.profile.medium.corridor.noTargetIntentCloseMs", 500, "悬停中档·无目标意图关闭等待", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowInitialMinimumObservationMilliseconds =
        new("edge.hover.profile.low.initial.minimumObservationMs", 10, "悬停低档·初次进入最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowInitialMinimumDelayMilliseconds =
        new("edge.hover.profile.low.initial.minimumDelayMs", 14, "悬停低档·初次进入最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowInitialMaximumDelayMilliseconds =
        new("edge.hover.profile.low.initial.maximumDelayMs", 44, "悬停低档·初次进入最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowInitialPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.low.initial.passThroughVetoHorizonMs", 110, "悬停低档·初次进入穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowInitialDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.low.initial.dynamicDelayHorizonMs", 240, "悬停低档·初次进入动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowTransferMinimumObservationMilliseconds =
        new("edge.hover.profile.low.transfer.minimumObservationMs", 18, "悬停低档·跨胶囊切换最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowTransferMinimumDelayMilliseconds =
        new("edge.hover.profile.low.transfer.minimumDelayMs", 34, "悬停低档·跨胶囊切换最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowTransferMaximumDelayMilliseconds =
        new("edge.hover.profile.low.transfer.maximumDelayMs", 90, "悬停低档·跨胶囊切换最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowTransferPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.low.transfer.passThroughVetoHorizonMs", 205, "悬停低档·跨胶囊切换穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowTransferDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.low.transfer.dynamicDelayHorizonMs", 410, "悬停低档·跨胶囊切换动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowStableFallbackMilliseconds =
        new("edge.hover.profile.low.stableFallbackMs", 85, "悬停低档·稳定停留直接放行时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowMinimumDirectionalSpeedDipPerMillisecond =
        new("edge.hover.profile.low.minimumDirectionalSpeedDipPerMs", 0.055, "悬停低档·方向运动最小速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowMinimumDirectionConsistency =
        new("edge.hover.profile.low.minimumDirectionConsistency", 0.64, "悬停低档·方向一致性阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverLowMinimumVerticalDominance =
        new("edge.hover.profile.low.minimumVerticalDominance", 0.45, "悬停低档·竖直运动占比阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverLowCorridorMinimumSpeedDipPerMillisecond =
        new("edge.hover.profile.low.corridor.minimumSpeedDipPerMs", 0.11, "悬停低档·跨空白最小运动速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverLowCorridorMinimumPathConsistency =
        new("edge.hover.profile.low.corridor.minimumPathConsistency", 0.79, "悬停低档·跨空白路径一致性", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverLowCorridorTargetPaddingDip =
        new("edge.hover.profile.low.corridor.targetPaddingDip", 24, "悬停低档·跨空白目标几何留白", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverLowCorridorNoTargetIntentCloseMilliseconds =
        new("edge.hover.profile.low.corridor.noTargetIntentCloseMs", 650, "悬停低档·无目标意图关闭等待", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowInitialMinimumObservationMilliseconds =
        new("edge.hover.profile.veryLow.initial.minimumObservationMs", 12, "悬停很低档·初次进入最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowInitialMinimumDelayMilliseconds =
        new("edge.hover.profile.veryLow.initial.minimumDelayMs", 18, "悬停很低档·初次进入最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowInitialMaximumDelayMilliseconds =
        new("edge.hover.profile.veryLow.initial.maximumDelayMs", 54, "悬停很低档·初次进入最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowInitialPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.veryLow.initial.passThroughVetoHorizonMs", 135, "悬停很低档·初次进入穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowInitialDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.veryLow.initial.dynamicDelayHorizonMs", 300, "悬停很低档·初次进入动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowTransferMinimumObservationMilliseconds =
        new("edge.hover.profile.veryLow.transfer.minimumObservationMs", 22, "悬停很低档·跨胶囊切换最短观察时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowTransferMinimumDelayMilliseconds =
        new("edge.hover.profile.veryLow.transfer.minimumDelayMs", 48, "悬停很低档·跨胶囊切换最短动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowTransferMaximumDelayMilliseconds =
        new("edge.hover.profile.veryLow.transfer.maximumDelayMs", 115, "悬停很低档·跨胶囊切换最长动态驻留", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowTransferPassThroughVetoHorizonMilliseconds =
        new("edge.hover.profile.veryLow.transfer.passThroughVetoHorizonMs", 255, "悬停很低档·跨胶囊切换穿越否决时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowTransferDynamicDelayHorizonMilliseconds =
        new("edge.hover.profile.veryLow.transfer.dynamicDelayHorizonMs", 500, "悬停很低档·跨胶囊切换动态驻留风险时间范围", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowStableFallbackMilliseconds =
        new("edge.hover.profile.veryLow.stableFallbackMs", 110, "悬停很低档·稳定停留直接放行时间", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowMinimumDirectionalSpeedDipPerMillisecond =
        new("edge.hover.profile.veryLow.minimumDirectionalSpeedDipPerMs", 0.04, "悬停很低档·方向运动最小速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowMinimumDirectionConsistency =
        new("edge.hover.profile.veryLow.minimumDirectionConsistency", 0.6, "悬停很低档·方向一致性阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryLowMinimumVerticalDominance =
        new("edge.hover.profile.veryLow.minimumVerticalDominance", 0.4, "悬停很低档·竖直运动占比阈值", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryLowCorridorMinimumSpeedDipPerMillisecond =
        new("edge.hover.profile.veryLow.corridor.minimumSpeedDipPerMs", 0.14, "悬停很低档·跨空白最小运动速度", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP/ms", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowCorridorMinimumPathConsistency =
        new("edge.hover.profile.veryLow.corridor.minimumPathConsistency", 0.84, "悬停很低档·跨空白路径一致性", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverVeryLowCorridorTargetPaddingDip =
        new("edge.hover.profile.veryLow.corridor.targetPaddingDip", 30, "悬停很低档·跨空白目标几何留白", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> HoverVeryLowCorridorNoTargetIntentCloseMilliseconds =
        new("edge.hover.profile.veryLow.corridor.noTargetIntentCloseMs", 800, "悬停很低档·无目标意图关闭等待", "Apply 时更新当前档位缓存；之后的意图评估读取该数值。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> HoverAcceleratingVetoHorizonScale =
        new("edge.hover.acceleratingVetoHorizonScale", 1.2, "加速时穿越否决时间倍率", "现有悬停意图公式中的系数；下一次评估直接读取。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> HoverAccelerationRiskIncrement =
        new("edge.hover.accelerationRiskIncrement", 0.2, "加速时驻留风险增量", "现有悬停意图公式中的系数；下一次评估直接读取。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverBrakingRiskScale =
        new("edge.hover.brakingRiskScale", 0.25, "减速时驻留风险倍率", "现有悬停意图公式中的系数；下一次评估直接读取。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverUncertainTrendRiskScale =
        new("edge.hover.uncertainTrendRiskScale", 0.85, "无速度趋势时驻留风险倍率", "现有悬停意图公式中的系数；下一次评估直接读取。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> HoverStationaryGeometryConfidenceCap =
        new("edge.hover.stationaryGeometryConfidenceCap", 0.55, "静止后几何证据上限", "现有悬停意图公式中的系数；下一次评估直接读取。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> PreviewTransferResidenceMilliseconds =
        new("edge.preview.activation.transferResidenceMs", 32, "预览切换最低驻留", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> PreviewPostDragResidenceMilliseconds =
        new("edge.preview.activation.postDragResidenceMs", 140, "拖动后预览最低驻留", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> PreviewPostDragStableMilliseconds =
        new("edge.preview.activation.postDragStableMs", 48, "拖动后预览稳定停留", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> PreviewPointerToleranceDip =
        new("edge.preview.activation.pointerToleranceDip", 2, "预览指针稳定容差", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> PreviewCorridorTrackingIntervalMilliseconds =
        new("edge.preview.activation.corridorTrackingIntervalMs", 24, "跨空白预览检查间隔", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> PreviewActivationIntentTrackingMilliseconds =
        new("edge.preview.activation.activationTrackingIntervalMs", 16, "预览激活意图检查间隔", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> PreviewCursorReadRetryCount =
        new("edge.preview.activation.cursorReadRetryCount", 1, "预览物理指针读取重试次数", "由原有预览意图与计时路径读取；已有计时任务在下一次调度时采用新间隔。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<double> PreviewLayoutSuppressionMarginMs =
        new("edge.preview.activation.layoutSuppressionMarginMs", 50, "队列位移后预览额外抑制时间", "加在当前队列位移动画时长之后。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> PreviewLayoutSuppressionMs =
        InjectionParameter<double>.Dynamic("edge.preview.activation.layoutSuppressionMs", "队列位移后预览最终抑制时间", "当前队列位移动画时长加额外抑制时间。",
            unit: "ms", defaultDescription: "当前队列位移动画时长加额外抑制时间。", application: "live");
    internal static readonly InjectionParameter<double> PreviewApproximateGlyphWidthDip =
        new("edge.preview.measure.glyphWidthDip", 6.4, "预览估算基础字宽", "用于预览宽度和折行的轻量估算，实际绘制仍由原控件负责。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PreviewChromeReserveWidthDip =
        new("edge.preview.measure.chromeReserveWidthDip", 72, "预览缺省横向留白", "用于预览宽度和折行的轻量估算，实际绘制仍由原控件负责。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<int> PreviewMeasureWidthLineBudget =
        new("edge.preview.measure.widthLineBudget", 32, "预览宽度估算行数预算", "限制轻量文本测量的输入或每行估算单位。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> PreviewMeasureWidthUnitBudget =
        new("edge.preview.measure.widthUnitBudget", 64, "预览宽度估算字符宽度预算", "限制轻量文本测量的输入或每行估算单位。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> PreviewMeasureMinimumLineUnits =
        new("edge.preview.measure.minimumLineUnits", 12, "预览折行估算最少字符单位", "限制轻量文本测量的输入或每行估算单位。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> PreviewMeasureWrappedLineBudget =
        new("edge.preview.measure.wrappedLineBudget", 80, "预览折行估算输入行预算", "限制轻量文本测量的输入或每行估算单位。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> TodoPreviewItemBudget =
        new("edge.preview.todo.itemBudget", 12, "待办预览条目预算", "待办预览捕获、估算和绘制共同读取的值；重启后生效。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> TodoPreviewItemCharacterBudget =
        new("edge.preview.todo.itemCharacterBudget", 512, "待办预览单项字符预算", "待办预览捕获、估算和绘制共同读取的值；重启后生效。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewLinkedTargetButtonSizeDip =
        new("edge.preview.todo.linkedTargetButtonSizeDip", 20, "待办预览链接按钮尺寸", "待办预览捕获、估算和绘制共同读取的值；重启后生效。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewTextReserveDip =
        new("edge.preview.todo.textReserveDip", 80, "待办预览文字通道固定留白", "宽度估算添加、折行估算扣除的同一个固定宽度；不改变实际 Grid 布局。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewMinimumWidth =
        new("edge.preview.todo.minimumWidthDip", 120, "待办预览最小宽度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewMaximumWidth =
        new("edge.preview.todo.maximumWidthDip", 450, "待办预览最大宽度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewMinimumHeight =
        new("edge.preview.todo.minimumHeightDip", 150, "待办预览非空最小高度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewMaximumHeight =
        new("edge.preview.todo.maximumHeightDip", 320, "待办预览非空最大高度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewEmptyHeight =
        new("edge.preview.todo.emptyHeightDip", 120, "待办预览空内容高度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewEmptyMinimumWidth =
        new("edge.preview.todo.emptyMinimumWidthDip", 130, "待办预览空内容最小宽度", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<int> TodoPreviewItemEstimatedLineCap =
        new("edge.preview.todo.itemEstimatedLineCap", 3, "待办预览单项估算行数上限", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewHeightBase =
        new("edge.preview.todo.heightBaseDip", 62, "待办预览高度估算固定部分", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewEstimatedLineHeight =
        new("edge.preview.todo.estimatedLineHeightDip", 28, "待办预览高度估算基础行高", "沿用现有待办预览公式及裁切；尺寸缓存和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewWidth =
        InjectionParameter<double>.Dynamic("edge.preview.todo.widthDip", "待办预览最终宽度", "按标题、受限待办文本及标记宽度估算，并套用当前空内容或非空宽度范围。",
            unit: "DIP", defaultDescription: "按标题、受限待办文本及标记宽度估算，并套用当前空内容或非空宽度范围。", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewHeight =
        InjectionParameter<double>.Dynamic("edge.preview.todo.heightDip", "待办预览最终高度", "按当前受限待办的折行数、固定高度与行高计算，并套用当前高度范围。",
            unit: "DIP", defaultDescription: "按当前受限待办的折行数、固定高度与行高计算，并套用当前高度范围。", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewHostHeight =
        InjectionParameter<double>.Dynamic("edge.preview.todo.hostHeightDip", "待办预览宿主预留高度", "原有 400 DIP 预留高度与当前待办预览最大可能高度的较大值。",
            unit: "DIP", defaultDescription: "原有 400 DIP 预留高度与当前待办预览最大可能高度的较大值。", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewTitleFont =
        new("edge.preview.todo.titleFontDip", 13, "待办预览标题基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewSummaryFont =
        new("edge.preview.todo.summaryFontDip", 11, "待办预览计数基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewEmptyFont =
        new("edge.preview.todo.emptyFontDip", 16, "待办预览空内容基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewItemFont =
        new("edge.preview.todo.itemFontDip", 12, "待办预览条目基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewMarkerFont =
        new("edge.preview.todo.markerFontDip", 10.5, "待办预览提醒标记基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPreviewTruncationFont =
        new("edge.preview.todo.truncationFontDip", 14, "待办预览截断提示基础字号", "应用全局字体缩放前的预览控件字号；现有文本裁切逻辑保持不变。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewMinimumWidth =
        new("edge.preview.markdown.minimumWidthDip", 120, "笔记预览最小宽度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewMaximumWidth =
        new("edge.preview.markdown.maximumWidthDip", 460, "笔记预览最大宽度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewMinimumHeight =
        new("edge.preview.markdown.minimumHeightDip", 150, "笔记预览非空最小高度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewMaximumHeight =
        new("edge.preview.markdown.maximumHeightDip", 320, "笔记预览非空最大高度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewEmptyHeight =
        new("edge.preview.markdown.emptyHeightDip", 120, "笔记预览空内容高度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewEmptyMinimumWidth =
        new("edge.preview.markdown.emptyMinimumWidthDip", 130, "笔记预览空内容最小宽度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewWidthReserve =
        new("edge.preview.markdown.widthReserveDip", 72, "笔记预览宽度估算固定留白", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewBodyWidthReserve =
        new("edge.preview.markdown.bodyWidthReserveDip", 44, "笔记预览折行估算扣除宽度", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewHeightBase =
        new("edge.preview.markdown.heightBaseDip", 74, "笔记预览高度估算固定部分", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewEstimatedLineHeight =
        new("edge.preview.markdown.estimatedLineHeightDip", 22, "笔记预览高度估算基础行高", "沿用现有笔记预览公式及裁切；尺寸缓存、预渲染和宿主预留容量在重启后采用该值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewWidth =
        InjectionParameter<double>.Dynamic("edge.preview.markdown.widthDip", "笔记预览最终宽度", "按标题、受限 Markdown 文本及当前缩放估算，并套用当前宽度范围。",
            unit: "DIP", defaultDescription: "按标题、受限 Markdown 文本及当前缩放估算，并套用当前宽度范围。", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewHeight =
        InjectionParameter<double>.Dynamic("edge.preview.markdown.heightDip", "笔记预览最终高度", "按当前受限 Markdown 的估算行数、固定高度、行高与缩放计算，并套用当前高度范围。",
            unit: "DIP", defaultDescription: "按当前受限 Markdown 的估算行数、固定高度、行高与缩放计算，并套用当前高度范围。", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewTextScale =
        InjectionParameter<double>.Dynamic("edge.preview.markdown.textScale", "笔记预览估算文字倍率", "当前笔记基础字号乘单篇缩放，四舍五入后除以应用缩放后的 14 DIP。",
            unit: "", defaultDescription: "当前笔记基础字号乘单篇缩放，四舍五入后除以应用缩放后的 14 DIP。", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewHostHeight =
        InjectionParameter<double>.Dynamic("edge.preview.markdown.hostHeightDip", "笔记预览宿主预留高度", "原有 410 DIP 预留高度与当前笔记预览最大可能高度的较大值。",
            unit: "DIP", defaultDescription: "原有 410 DIP 预留高度与当前笔记预览最大可能高度的较大值。", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewArtifactBodyHeight =
        InjectionParameter<double>.Dynamic("edge.preview.markdown.artifactBodyHeightDip", "笔记预览预渲染正文高度预算", "原有 410 DIP 预渲染高度与当前笔记预览最大可能高度的较大值。",
            unit: "DIP", defaultDescription: "原有 410 DIP 预渲染高度与当前笔记预览最大可能高度的较大值。", application: "restart");
    internal static readonly InjectionParameter<int> MarkdownPreviewBlockCharacterBudget =
        InjectionParameter<int>.Dynamic("edge.preview.markdown.blockCharacterBudget", "笔记预览单块扫描字符预算", "默认跟随当前预览总字符预算；明确填写后单独覆盖。",
            unit: "", defaultDescription: "默认跟随当前预览总字符预算；明确填写后单独覆盖。", application: "restart");
    internal static readonly InjectionParameter<int> MarkdownPreviewBlockBudget =
        new("edge.preview.markdown.blockBudget", 16, "笔记预览块数预算", "捕获预览内容时接纳的块数；Full 模式围栏代码仍按原规则计块。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> MarkdownPreviewCharacterBudget =
        new("edge.preview.markdown.characterBudget", 6000, "笔记预览总字符预算", "捕获与测量共同使用的总字符预算，包括接纳行之间的分隔符。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> MarkdownPreviewTitleFont =
        new("edge.preview.markdown.titleFontDip", 13, "笔记预览标题基础字号", "应用全局字体缩放前的笔记预览标题字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewMinimumWidth =
        new("edge.preview.fallback.minimumWidthDip", 120, "缺省状态预览最小宽度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewMaximumWidth =
        new("edge.preview.fallback.maximumWidthDip", 440, "缺省状态预览最大宽度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewMinimumHeight =
        new("edge.preview.fallback.minimumHeightDip", 160, "缺省状态预览最小高度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewMaximumHeight =
        new("edge.preview.fallback.maximumHeightDip", 280, "缺省状态预览最大高度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewHeightBase =
        new("edge.preview.fallback.heightBaseDip", 150, "缺省状态预览高度估算固定部分", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewMinimumTextWidth =
        new("edge.preview.fallback.minimumTextWidthDip", 72, "缺省状态预览折行估算最小文字宽度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewTextReserve =
        new("edge.preview.fallback.textReserveDip", 40, "缺省状态预览折行估算扣除宽度", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewEstimatedLineHeight =
        new("edge.preview.fallback.estimatedLineHeightDip", 20, "缺省状态预览高度估算基础行高", "缺少专属预览内容时的状态卡片估算参数。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewWidth =
        InjectionParameter<double>.Dynamic("edge.preview.fallback.widthDip", "缺省状态预览最终宽度", "按当前标题和状态文本估算并套用缺省状态卡片宽度范围。",
            unit: "DIP", defaultDescription: "按当前标题和状态文本估算并套用缺省状态卡片宽度范围。", application: "restart");
    internal static readonly InjectionParameter<double> FallbackPreviewHeight =
        InjectionParameter<double>.Dynamic("edge.preview.fallback.heightDip", "缺省状态预览最终高度", "按当前状态文本折行估算并套用缺省状态卡片高度范围。",
            unit: "DIP", defaultDescription: "按当前状态文本折行估算并套用缺省状态卡片高度范围。", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewDefaultMaximumWidth =
        new("edge.preview.plugin.defaultMaximumWidthDip", 800, "插件 Mini 兼容最大宽度", "没有声明 miniMaxSize 时，用来锁定会话有效最大宽度的兼容基线。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewDefaultMaximumHeight =
        new("edge.preview.plugin.defaultMaximumHeightDip", 600, "插件 Mini 兼容最大高度", "没有声明 miniMaxSize 时，用来锁定会话有效最大高度的兼容基线。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackHeight =
        new("edge.preview.plugin.fallbackHeightDip", 140, "插件胶囊后备预览高度", "插件没有专属 Mini 时，从胶囊内容构造后备预览使用的高度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackMinimumWidth =
        new("edge.preview.plugin.fallbackMinimumWidthDip", 220, "插件胶囊后备预览最小宽度", "插件没有专属 Mini 时，从胶囊内容估算后备预览的宽度范围。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackMaximumWidth =
        new("edge.preview.plugin.fallbackMaximumWidthDip", 360, "插件胶囊后备预览最大宽度", "插件没有专属 Mini 时，从胶囊内容估算后备预览的宽度范围。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackWidthScale =
        new("edge.preview.plugin.fallbackWidthScale", 1.65, "插件胶囊后备预览宽度倍率", "胶囊内容宽度加关闭区与外沿后，用于估算后备预览宽度的倍率。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackContentWidth =
        new("edge.preview.plugin.fallbackContentWidthDip", 110, "插件胶囊后备内容缺省宽度", "胶囊未提供内容宽度时用于后备预览估算的基础宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginPreviewFallbackWidth =
        InjectionParameter<double>.Dynamic("edge.preview.plugin.fallbackWidthDip", "插件胶囊后备预览最终宽度", "按当前胶囊内容宽度、关闭区、外沿和后备倍率计算，再套用后备宽度范围。",
            unit: "DIP", defaultDescription: "按当前胶囊内容宽度、关闭区、外沿和后备倍率计算，再套用后备宽度范围。", application: "restart");
    internal static readonly InjectionParameter<double> PreviewMonitorInsetDip =
        new("edge.preview.monitorInsetDip", 16, "预览显示器工作区扣除宽度与高度", "计算当前显示器可用 Mini 尺寸时，从工作区宽和高分别扣除的数值。", unit: "DIP", application: "restart");
}
