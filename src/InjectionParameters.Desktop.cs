namespace PaperTodo;

// Desktop geometry and interaction injection points. No file or key lookup on the hot path.
internal static partial class InjectionParameters
{
    internal static readonly InjectionParameter<double> EdgeExpandedEdgeInset =
        new("edge.layout.expandedEdgeInset", 36, "展开纸片贴边留白", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeTopMargin =
        new("edge.layout.topMargin", 8, "边缘队列上下留白", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeStartTopMargin =
        new("edge.layout.startTopMargin", 48, "新队列起始位置", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeCornerRadius =
        new("edge.layout.cornerRadius", 12, "边缘胶囊圆角", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeWindowChromeMargin =
        new("edge.layout.windowChromeMargin", 8, "胶囊透明外沿", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeCapsuleCloseWidth =
        new("edge.layout.capsuleCloseWidth", 14, "边缘胶囊关闭区宽度", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeOutlineThickness =
        new("edge.layout.outlineThickness", 2, "边缘胶囊轮廓粗细", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeOutlineOverlap =
        new("edge.layout.outlineOverlap", 1, "边缘胶囊轮廓重叠", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> EdgeCrossQueueDragUnlockDistance =
        new("edge.layout.crossQueueDragUnlockDistance", 56, "胶囊脱离队列距离", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<int> EdgeHorizontalResizeMilliseconds =
        new("edge.layout.horizontalResizeMilliseconds", 160, "胶囊横向变化时长", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> EdgeSlotMoveMilliseconds =
        new("edge.layout.slotMoveMilliseconds", 200, "队列位移时长", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> EdgeSlotRetractMoveMilliseconds =
        new("edge.layout.slotRetractMoveMilliseconds", 120, "队列收回时长", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> EdgeDockingHandoffMilliseconds =
        new("edge.layout.dockingHandoffMilliseconds", 160, "胶囊回墙飞行时长", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> EdgeDockingRevealMilliseconds =
        new("edge.layout.dockingRevealMilliseconds", 80, "胶囊回墙显现时长", "原有边缘胶囊共享几何或过渡计算使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> LayoutMinWidth =
        new("window.layout.minWidthDip", 96, "纸片最小宽度", "原有布局和归一化共同使用的基础尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> LayoutMinHeight =
        new("window.layout.minHeightDip", 120, "纸片最小高度", "原有布局和归一化共同使用的基础尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> LayoutTopBarHeight =
        new("window.layout.topBarHeightDip", 23.5, "纸片顶栏基础高度", "原有布局和归一化共同使用的基础尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> LayoutCapsuleWidth =
        new("window.layout.capsuleWidthDip", 92, "胶囊缺省宽度", "原有布局和归一化共同使用的基础尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> LayoutCapsuleHeight =
        new("window.layout.capsuleHeightDip", 46, "胶囊整体高度", "原有布局和归一化共同使用的基础尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<int> PaperMaxUndoDepth =
        new("paper.maxUndoDepth", 100, "待办撤销深度", "纸片现有界面或交互路径使用的数值。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<int> PaperCollapseShellFadeMilliseconds =
        new("paper.collapseShellFadeMilliseconds", 70, "纸片收起淡出时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> PaperCollapseResizeMilliseconds =
        new("paper.collapseResizeMilliseconds", 150, "纸片收起缩放时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> PaperExpandAnimationMilliseconds =
        new("paper.expandAnimationMilliseconds", 220, "纸片展开时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> PaperExpandCapsuleFadeOutMilliseconds =
        new("paper.expandCapsuleFadeOutMilliseconds", 80, "展开时胶囊淡出时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> PaperExpandShellFadeInMilliseconds =
        new("paper.expandShellFadeInMilliseconds", 140, "展开时纸面淡入时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> PaperCapsuleNormalMinWidth =
        new("paper.capsuleNormalMinWidth", 76, "普通胶囊最小宽度", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperCapsuleLeftPadding =
        new("paper.capsuleLeftPadding", 6, "普通胶囊左内边距", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperCapsuleIconGap =
        new("paper.capsuleIconGap", 4, "胶囊图标文字间距", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperCapsuleNormalCloseWidth =
        new("paper.capsuleNormalCloseWidth", 21, "普通胶囊关闭区宽度", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperCapsuleRightPadding =
        new("paper.capsuleRightPadding", 6, "普通胶囊右内边距", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperTopBarCollisionGap =
        new("paper.topBarCollisionGap", 2, "顶栏按钮碰撞间隔", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperTitleBarDragThreshold =
        new("paper.titleBarDragThreshold", 1, "标题栏拖动阈值", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> PaperCapsuleCloseGlyphNormalOffset =
        new("paper.capsuleCloseGlyphNormalOffset", -1, "胶囊关闭符号偏移", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperDeepCapsuleReorderDragExtraThreshold =
        new("paper.deepCapsuleReorderDragExtraThreshold", 4, "胶囊重排额外阈值", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> PaperDeepCapsuleCrossQueueDragScaleFrom =
        new("paper.deepCapsuleCrossQueueDragScaleFrom", 0.97, "胶囊跨队列初始缩放", "纸片现有界面或交互路径使用的数值。", unit: "", application: "next_use", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<int> PaperDeepCapsuleCrossQueueDragMorphMilliseconds =
        new("paper.deepCapsuleCrossQueueDragMorphMilliseconds", 90, "胶囊跨队列变形时长", "纸片现有界面或交互路径使用的数值。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> PaperRadiusSmall =
        new("paper.radiusSmall", 4, "小元素圆角", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperRadiusControl =
        new("paper.radiusControl", 8, "控件圆角", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperRadiusBlock =
        new("paper.radiusBlock", 12, "面板圆角", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PaperRadiusShell =
        new("paper.radiusShell", 16, "纸片外壳圆角", "纸片现有界面或交互路径使用的数值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MasterLeftPadding =
        new("capsule.master.leftPaddingDip", 4, "主胶囊左内边距", "主胶囊内容测量与显示共同使用的尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MasterGlyphGap =
        new("capsule.master.glyphGapDip", 3, "主胶囊图标计数间隔", "主胶囊内容测量与显示共同使用的尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MasterRightPadding =
        new("capsule.master.rightPaddingDip", 3, "主胶囊右内边距", "主胶囊内容测量与显示共同使用的尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> MasterInteriorBorderThickness =
        new("capsule.master.interiorBorderThicknessDip", 1, "主胶囊内部边框", "主胶囊内容测量与显示共同使用的尺寸。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> CapsuleIconBase =
        new("capsule.iconBaseDip", 13, "胶囊图标基础字号", "应用当前预设及全局缩放前的基础字号。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> CapsuleLabelBase =
        new("capsule.labelBaseDip", 12, "胶囊标签基础字号", "应用当前预设及全局缩放前的基础字号。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> TitleFontBase =
        new("paper.titleBaseDip", 12, "纸片标题基础字号", "应用当前预设及全局缩放前的基础字号。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> MasterGlyphBase =
        new("capsule.master.glyphBaseDip", 12, "主胶囊箭头基础字号", "应用当前预设及全局缩放前的基础字号。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> CapsuleLabelFont =
        InjectionParameter<double>.Dynamic("capsule.labelFontDip", "胶囊标签最终字号", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "live", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> TitleFont =
        InjectionParameter<double>.Dynamic("paper.titleFontDip", "纸片标题最终字号", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "live", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> MasterLabelFont =
        InjectionParameter<double>.Dynamic("capsule.master.labelFontDip", "主胶囊计数最终字号", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "live", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> CapsuleNormalWidth =
        InjectionParameter<double>.Dynamic("capsule.normal.widthDip", "普通胶囊最终宽度", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "restart", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> MasterWidth =
        InjectionParameter<double>.Dynamic("capsule.master.widthDip", "主胶囊最终宽度", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "restart", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> TitleBarFinalHeight =
        InjectionParameter<double>.Dynamic("paper.titleBarHeightDip", "纸片顶栏最终高度", "未覆盖时沿用现有文字、内容或缩放计算；写入后替换这个计算结果。", unit: "DIP", application: "restart", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> OverallFontScale =
        InjectionParameter<double>.Dynamic("appearance.fontScale", "整体字号缩放", "当前正式字号缩放的最终覆盖；普通设置仍保留原选择。", unit: "", application: "live", defaultDescription: "按当前设置或运行状态计算", recommendedMinimum: 0.8, recommendedMaximum: 1.2);
    internal static readonly InjectionParameter<double> FastDurationScale =
        new("animation.fastDurationScale", 0.60, "快速动画倍率", "快速动画模式对原有时长的乘数。", unit: "", application: "next_use", recommendedMinimum: 0, recommendedMaximum: 1);
    internal static readonly InjectionParameter<double> AnimationSnapAmplitude =
        new("animation.snapAmplitude", 0.3, "回弹曲线幅度", "共享回弹动画曲线创建时使用的幅度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> QueueSnapDistance =
        new("edge.queue.snapDistanceDip", 18, "队列吸附距离", "自由队列现有吸附与落点计算使用的独立距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> QueueReleaseDistance =
        new("edge.queue.releaseDistanceDip", 30, "队列释放距离", "自由队列现有吸附与落点计算使用的独立距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> QueueExitSnapDistance =
        new("edge.queue.exitSnapDistanceDip", 18, "队列外沿脱离距离", "自由队列现有吸附与落点计算使用的独立距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> QueueTakeoverAdvantage =
        new("edge.queue.takeoverAdvantageDip", 6, "队列目标接管优势", "自由队列现有吸附与落点计算使用的独立距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> FullscreenPollMs =
        new("fullscreen.pollIntervalMs", 1000d, "全屏常规检查间隔", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> FullscreenDebounceMs =
        new("fullscreen.eventDebounceMs", 75d, "全屏事件防抖", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> FullscreenScanMs =
        new("fullscreen.globalScanIntervalMs", 5000d, "全屏全局扫描间隔", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> SaveIdleMs =
        new("app.saveIdleMs", 1000d, "保存空闲等待", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> SaveForceMs =
        new("app.saveForceMs", 10000d, "持续编辑强制保存间隔", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> DisplayRefreshMs =
        new("app.displayRefreshMs", 250d, "显示器变化防抖", "现有计时或节流入口的间隔；不改变其他计时器的节奏。", unit: "ms", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> NewTodoWidth =
        new("newPaper.todo.widthDip", 280d, "新建待办宽度", "新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> NewTodoHeight =
        new("newPaper.todo.heightDip", 340d, "新建待办高度", "新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> NewNoteWidth =
        new("newPaper.note.widthDip", 320d, "新建笔记宽度", "新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> NewNoteHeight =
        new("newPaper.note.heightDip", 360d, "新建笔记高度", "新建纸片时的最终初始尺寸；不改已有纸片和旧数据缺省值。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<bool> RestoreConstrainSize =
        new("window.restore.constrainSize", true, "恢复时限制纸片尺寸", "关闭后窗口恢复保留保存的宽高，仍使用原有位置救援。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> TodoAutoScrollEnabled =
        new("todo.drag.autoScroll", true, "拖动待办自动滚动", "只控制拖动中的滚动步骤，保留排序与鼠标捕获收尾。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollTickMs =
        new("todo.drag.scrollTickMs", 16d, "拖动滚动节拍", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live", recommendedMinimum: 1);
    internal static readonly InjectionParameter<double> TodoAutoScrollEdgeDip =
        new("todo.drag.scrollEdgeDip", 48d, "拖动滚动热区基值", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollViewportRatio =
        new("todo.drag.scrollViewportRatio", 0.25, "拖动滚动热区比例", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live", recommendedMinimum: 0, recommendedMaximum: 0.5);
    internal static readonly InjectionParameter<double> TodoAutoScrollMaxElapsed =
        new("todo.drag.scrollMaxElapsedSeconds", 0.05, "滚动单步时间上限", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollEaseSeconds =
        new("todo.drag.scrollEaseSeconds", 0.20, "滚动加速时间", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollBaseSpeed =
        new("todo.drag.scrollBaseSpeed", 28d, "滚动起步速度", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollAcceleration =
        new("todo.drag.scrollAcceleration", 520d, "滚动加速系数", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollPower =
        new("todo.drag.scrollProximityPower", 2d, "滚动距离指数", "拖动自动滚动原公式的独立参数，视口和指针位置仍实时计算。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoAutoScrollEdgeFinal =
        InjectionParameter<double>.Dynamic("todo.drag.scrollEdgeFinalDip", "拖动滚动最终热区", "替换每次按缩放和视口高度计算的热区距离。", unit: "DIP", application: "live", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<double> TodoAutoScrollSpeedFinal =
        InjectionParameter<double>.Dynamic("todo.drag.scrollSpeedFinal", "拖动滚动最终速度", "替换每次根据指针接近边缘程度计算的速度。", unit: "DIP/s", application: "live", defaultDescription: "按当前设置或运行状态计算");
    internal static readonly InjectionParameter<bool> MenuPaperNewTodo =
        new("menu.paper.newTodo", true, "纸片菜单：新建待办", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuPaperNewNote =
        new("menu.paper.newNote", true, "纸片菜单：新建笔记", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuPaperWorkspace =
        new("menu.paper.workspace", true, "纸片菜单：工作区", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuPaperAssociation =
        new("menu.paper.association", true, "纸片菜单：关联", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuPaperExternalEditor =
        new("menu.paper.externalEditor", true, "纸片菜单：外部编辑器", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuPaperRunScript =
        new("menu.paper.runScript", true, "纸片菜单：运行脚本", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuEdgeNewTodo =
        new("menu.edge.newTodo", true, "边缘胶囊菜单：新建待办", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuEdgeNewNote =
        new("menu.edge.newNote", true, "边缘胶囊菜单：新建笔记", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuEdgeWorkspace =
        new("menu.edge.workspace", true, "边缘胶囊菜单：工作区", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuEdgeAssociation =
        new("menu.edge.association", true, "边缘胶囊菜单：关联", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<bool> MenuEdgeExternalEditor =
        new("menu.edge.externalEditor", true, "边缘胶囊菜单：外部编辑器", "只控制这个扩展/右键菜单入口，保留其他菜单、顶栏与命令入口。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<double> CapsuleGapBase =
        new("capsule.gap.baseDip", 4d, "胶囊间距基础值", "普通间距预设的基础值；紧凑和宽松预设仍按差值计算。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> CapsuleGapDelta =
        new("capsule.gap.deltaDip", 4d, "胶囊间距预设差值", "紧凑预设从基础值减去此值，宽松预设加上此值。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> CapsuleGapFinal =
        InjectionParameter<double>.Dynamic("capsule.gap.finalDip", "胶囊最终间距", "覆盖当前间距预设与基础值计算的结果。", unit: "DIP", application: "live", defaultDescription: "按当前间距预设计算");
    internal static readonly InjectionParameter<double> CapsuleEdgeWidth =
        InjectionParameter<double>.Dynamic("capsule.edge.widthDip", "边缘胶囊最终宽度", "覆盖当前内容测量后的边缘胶囊宽度，包含内侧阴影留白。", unit: "DIP", application: "restart", defaultDescription: "按图标、标题或插件内容计算");
}
