namespace PaperTodo;

// Content, resource budgets and interaction coefficients. Consumers read typed slots only.
internal static partial class InjectionParameters
{
    internal static readonly InjectionParameter<int> MarkdownLayoutLinesPerTurn =
        new("markdown.layout.maxLinesPerTurn", 4, "排版每轮行数", "后台排版工作线程一次调度最多处理的行数。", unit: "lines", application: "live");
    internal static readonly InjectionParameter<double> MarkdownLayoutTurnMs =
        new("markdown.layout.maxTurnMs", 1.5, "排版每轮时间", "后台排版工作线程一次调度的时间预算。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<int> MarkdownFullParseThreshold =
        new("markdown.parse.fullThresholdChars", 8_000, "整篇解析阈值", "正文短于此值时每次编辑整篇解析，否则尝试现有局部解析。", unit: "chars", application: "live");
    internal static readonly InjectionParameter<int> MarkdownIncrementalWindow =
        new("markdown.parse.localWindowChars", 1024, "局部解析窗口", "局部解析的初始字符窗口，仍由原语义边界规则扩展。", unit: "chars", application: "live");
    internal static readonly InjectionParameter<double> MarkdownRevealFadeMs =
        new("markdown.reveal.fadeMs", 140, "控制符淡入时长", "Markdown 编辑态控制符显现动画的基础时长。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> MarkdownRevealTickMs =
        new("markdown.reveal.tickMs", 16d, "控制符动画节拍", "控制符淡入计时器的调度间隔。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageResizeToleranceDip =
        new("note.images.resizeToleranceDip", 1d, "图片重排宽度容差", "图片视口宽度变化达到此距离时开始重排。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> NoteImageResizeSettleMs =
        new("note.images.resizeSettleMs", 200d, "图片重排静置时间", "停止改变图片视口宽度后恢复高质量解码的等待时间。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageCachePruneMs =
        new("note.images.cachePruneMs", 200d, "图片块缓存清理等待", "可视行变化后整理图片控件缓存的防抖时间。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<int> NoteClipboardImageBytes =
        new("note.clipboard.maxImageBytes", 32 * 1024 * 1024, "剪贴板图片输入上限", "读取剪贴板编码图片数据时使用的字节上限。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<double> NoteImageVerticalPadding =
        new("note.images.verticalPaddingDip", 7, "笔记图片上下留白", "图片块布局使用的竖向基础留白。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageHorizontalPadding =
        new("note.images.horizontalPaddingDip", 2, "笔记图片左右留白", "计算图片可用宽度时两侧预留的距离。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<int> NoteIndentationSize =
        new("note.editor.indentationSize", 4, "笔记缩进宽度", "AvalonEdit 的制表符与缩进显示宽度。", unit: "columns", application: "restart");
    internal static readonly InjectionParameter<long> NoteClipboardHtmlBytes =
        new("note.clipboard.maxHtmlImageBytes", 24L * 1024 * 1024, "HTML 剪贴板图片预算", "导出混排 HTML 时按每次图片出现累加的源图片字节预算。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<long> NoteClipboardRtfBytes =
        new("note.clipboard.maxRtfImageBytes", 16L * 1024 * 1024, "RTF 剪贴板图片预算", "导出混排 RTF 时按每次图片出现累加的源图片字节预算。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<double> NoteClipboardImageMaxWidth =
        new("note.clipboard.imageMaxWidthDip", 640d, "剪贴板图片最大宽度", "HTML 和 RTF 导出图片的默认及最大显示宽度。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> NoteClipboardImageMinWidth =
        new("note.clipboard.imageMinWidthDip", 24d, "剪贴板图片最小宽度", "HTML 和 RTF 导出图片显示宽度的下限。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> MarkdownTaskHitPadding =
        new("markdown.tasks.hitPaddingDip", 2.0, "Markdown 勾选热区", "渲染勾选框四周额外可点击的距离。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<int> NoteTextLimit =
        new("note.maxTextChars", 100000, "笔记文字长度上限", "笔记编辑器与宿主写入入口共同使用的字符上限。", unit: "chars", application: "restart");
    internal static readonly InjectionParameter<int> NoteImageStoredDimension =
        new("note.images.maxStoredDimensionPx", 4096, "图片存储最大边长", "导入及加载图片资产时接受的最大像素边长。", unit: "px", application: "next_use");
    internal static readonly InjectionParameter<int> NoteImageCompressedDimension =
        new("note.images.autoCompressedDimensionPx", 2048, "自动压缩目标边长", "开启自动压缩时大图片缩小后的最大边长。", unit: "px", application: "next_use");
    internal static readonly InjectionParameter<int> NoteImageStoredBytes =
        new("note.images.maxStoredBytes", 8 * 1024 * 1024, "单张图片存储预算", "单个图片资产编码后的字节上限。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<int> NoteImageInputBytes =
        new("note.images.maxInputBytes", 32 * 1024 * 1024, "图片导入输入预算", "导入图片原始输入数据的字节上限。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<int> NoteImageTotalBytes =
        new("note.images.maxTotalBytes", 120 * 1024 * 1024, "图片资产总预算", "当前图片存储所有编码图片的总字节上限。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<long> NoteImageDecodedBytes =
        new("note.images.maxDecodedBytes", 50L * 1024 * 1024, "解码图片缓存预算", "非当前可视图片的 LRU 解码缓存字节预算。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<int> NoteImageCacheEntries =
        new("note.images.maxCacheEntries", 20, "解码图片缓存数量", "非当前可视图片的 LRU 解码缓存条目预算。", unit: "items", application: "next_use");
    internal static readonly InjectionParameter<int> TodoFoldThreshold =
        new("todo.fold.thresholdLines", 4, "待办折叠触发行数", "待办排版后的显示行数超过此值时允许折叠。", unit: "lines", application: "next_use");
    internal static readonly InjectionParameter<int> TodoFoldVisibleLines =
        new("todo.fold.visibleLines", 2, "待办折叠可见行数", "折叠待办保留的可见正文行数。", unit: "lines", application: "next_use");
    internal static readonly InjectionParameter<int> TodoTextLimit =
        new("todo.maxTextChars", 5000, "待办文字长度上限", "待办编辑器与宿主写入入口共同使用的字符上限。", unit: "chars", application: "restart");
    internal static readonly InjectionParameter<int> TodoPasteLineLimit =
        new("todo.paste.maxItems", 200, "待办批量输入上限", "一次多行粘贴或宿主批量创建的最大待办数量。", unit: "items", application: "next_use");
    internal static readonly InjectionParameter<double> TodoStrikeInset =
        new("todo.strike.insetDip", 3, "待办删除线左右留白", "已完成待办删除线两端的内缩距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> TodoStrikeThickness =
        new("todo.strike.thicknessDip", 1.35, "待办删除线粗细", "已完成待办删除线的绘制粗细。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> TodoStrikeVerticalRatio =
        new("todo.strike.verticalRatio", 0.56, "待办删除线高度比例", "删除线在每行文字矩形内的竖向位置比例。", unit: "", application: "next_use");
    internal static readonly InjectionParameter<int> TodoPasteAnimationCount =
        new("todo.paste.animatedItems", 15, "粘贴动画待办数量", "多行粘贴时最多参加错峰入场动画的待办数量。", unit: "items", application: "next_use");
    internal static readonly InjectionParameter<int> TodoPasteStaggerMs =
        new("todo.paste.staggerMs", 40, "粘贴动画错峰间隔", "相邻待办开始入场动画的时间差。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> TodoPasteStartOffset =
        new("todo.paste.startOffsetDip", -15d, "粘贴动画初始偏移", "待办入场动画开始时的竖向位移。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<int> TodoPasteFadeMs =
        new("todo.paste.fadeMs", 200, "粘贴淡入时长", "多行粘贴待办的透明度过渡基础时长。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> TodoPasteMoveMs =
        new("todo.paste.moveMs", 220, "粘贴移动时长", "多行粘贴待办的竖向过渡基础时长。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> TodoDeleteMs =
        new("todo.delete.animationMs", 200, "待办删除动画时长", "单条待办删除时的淡出和横移基础时长。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> TodoDeleteOffset =
        new("todo.delete.offsetDip", 30d, "待办删除动画位移", "单条待办删除动画的横向目标位移。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> TodoGhostMinimumWidth =
        new("todo.drag.ghostMinWidthDip", 160d, "待办拖动影像最小宽度", "跨位置拖动时跟随鼠标的待办影像最小宽度。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> TodoGhostMinimumHeight =
        new("todo.drag.ghostMinHeightDip", 30d, "待办拖动影像最小高度", "跟随鼠标的待办影像最小高度。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> TodoDropIndicatorHeight =
        new("todo.drag.indicatorHeightDip", 3d, "待办落点线高度", "拖动排序时插入位置指示线的高度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoDropIndicatorRadius =
        new("todo.drag.indicatorRadiusDip", 1.5, "待办落点线圆角", "拖动排序指示线的圆角。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoDropIndicatorMinimumWidth =
        new("todo.drag.indicatorMinWidthDip", 24d, "待办落点线最小宽度", "插入位置指示线的最小宽度。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> TodoDropIndicatorWidthInset =
        new("todo.drag.indicatorWidthInsetDip", 8d, "待办落点线宽度收缩", "插入位置指示线相对待办行减去的总宽度。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionTickMs =
        new("todo.selection.tickMs", 32d, "待办多选跟踪节拍", "框选或扫选待办时鼠标跟踪定时器间隔。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> TodoSelectionEdgeDip =
        new("todo.selection.edgeDip", 28d, "待办多选滚动热区", "扫选自动滚动热区的基础距离，随后乘文字缩放。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionEdgeRatio =
        new("todo.selection.edgeHeightRatio", 0.25, "待办多选热区高度系数", "扫选热区占列表可视高度的最大比例。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionEdge =
        InjectionParameter<double>.Dynamic("todo.selection.effectiveEdgeDip", "待办多选最终热区", "覆盖尺寸与系数计算后的扫选滚动热区。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionStepDip =
        new("todo.selection.stepDip", 14d, "待办多选滚动加速距离", "增加一次滚动步数所需距离的基础下限。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionStepRatio =
        new("todo.selection.stepEdgeRatio", 0.7, "待办多选滚动加速系数", "滚动步数增加距离相对于热区的比例。", unit: "", application: "live");
    internal static readonly InjectionParameter<double> TodoSelectionStepDistance =
        InjectionParameter<double>.Dynamic("todo.selection.effectiveStepDip", "待办多选最终加速距离", "覆盖计算后增加一次滚动步数所需的距离。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "live");
    internal static readonly InjectionParameter<int> TodoSelectionMaximumSteps =
        new("todo.selection.maxSteps", 6, "待办多选最大滚动步数", "扫选自动滚动每次跟踪最多执行的行滚动次数。", unit: "steps", application: "live");
    internal static readonly InjectionParameter<int> PaperBackgroundDecodeDimension =
        new("background.maxDecodeDimensionPx", 4096, "纸片背景解码边长", "背景图片解码的最大像素边长。", unit: "px", application: "restart");
    internal static readonly InjectionParameter<double> PaperBackgroundBlendOpacity =
        new("background.blendedImageOpacity", 0.30, "纸片背景混合透明度", "启用背景色混合时图片画刷的透明度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<long> ReminderSoundBytes =
        new("reminder.maxCustomSoundBytes", 10 * 1024 * 1024, "提醒自定义音频预算", "加载自定义提醒声音时允许的文件大小。", unit: "bytes", application: "next_use");
    internal static readonly InjectionParameter<double> ReminderMaximumIntervalMs =
        new("reminder.maxTimerIntervalMs", 60000d, "提醒最长检查间隔", "下一次提醒检查定时器允许的最长间隔。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> ReminderMinimumIntervalMs =
        new("reminder.minTimerIntervalMs", 250d, "提醒最短检查间隔", "提醒调度和重试定时器允许的最短间隔。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> ReminderAdvanceMs =
        new("reminder.advanceNoticeMs", 5000d, "提醒提前通知时间", "向插件发送提醒预告相对于实际到期的提前量。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> ReminderRetryMs =
        new("reminder.retryMs", 30000d, "提醒失败重试间隔", "部分或全部提醒未送达后的默认重试等待时间。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> ReminderSummaryCharacters =
        new("reminder.summaryChars", 90, "提醒摘要长度", "托盘提醒摘要的最大字符数。", unit: "chars", application: "next_use");
    internal static readonly InjectionParameter<double> WindowBindingSampleMs =
        new("window.binding.sampleMs", 32, "窗口关联目标采样", "拖动关联按钮时重新识别目标窗口的时间间隔。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> WindowBindingDragOpacity =
        new("window.binding.dragOpacity", 0.72, "窗口关联拖动透明度", "拖动关联手势期间关联按钮的透明度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<int> ExperimentalOpacityMs =
        new("window.opacity.transitionMs", 120, "失焦透明过渡时长", "失焦纸片和静置胶囊透明度过渡的基础时长。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> ExperimentalAutoCollapseDelayMs =
        new("window.autoCollapse.delayMs", 140d, "失焦自动收起延迟", "失焦后执行自动收起前的等待时间。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<double> ExperimentalFollowSlideMs =
        new("window.follow.slideMs", 130, "跟随胶囊滑动时长", "绑定窗口的胶囊显示和收回滑动时长。", unit: "ms", application: "next_use");
    internal static readonly InjectionParameter<int> ExperimentalFollowRetractMs =
        new("window.follow.retractDelayMs", 180, "跟随胶囊收回延迟", "鼠标离开跟随胶囊后的收回等待时间。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> ExperimentalTetherDetachDip =
        new("window.tether.detachDip", 72, "窗口牵引脱离距离", "拖动达到此距离后解除牵引。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> ExperimentalTetherHandleWidth =
        new("window.tether.minHandleWidthDip", 48, "窗口牵引可见把手宽", "维持可拖动把手时使用的最小可见宽度。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<double> ExperimentalTetherHandleHeight =
        new("window.tether.minHandleHeightDip", 12, "窗口牵引可见把手高", "维持可拖动把手时使用的最小可见高度。", unit: "DIP", application: "live");
    internal static readonly InjectionParameter<int> ExperimentalFollowStableSamples =
        new("window.follow.stableSamples", 3, "窗口跟随稳定样本数", "结束持续跟随帧前需要的稳定位置样本数。", unit: "samples", application: "live");
    internal static readonly InjectionParameter<double> ExperimentalFollowStableMs =
        new("window.follow.stableMs", 100d, "窗口跟随稳定时长", "目标窗口静止后结束跟随帧所需的稳定时间。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> ExperimentalFollowMovingIdleMs =
        new("window.follow.movingIdleMs", 2000d, "窗口移动跟随闲置时间", "仍处于移动手势时结束跟随帧所需的闲置时间。", unit: "ms", application: "live");
    internal static readonly InjectionParameter<double> PluginCapsuleMinWidth =
        new("plugin.capsule.minContentWidthDip", 34, "插件胶囊最小内容宽", "插件胶囊内容期望宽度的下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleMaxWidth =
        new("plugin.capsule.maxContentWidthDip", 320, "插件胶囊最大内容宽", "插件胶囊内容期望宽度的上限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleGap =
        new("plugin.capsule.componentGapDip", 5, "插件胶囊组件间距", "插件模板内相邻组件间的距离。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleDotSize =
        new("plugin.capsule.statusDotSizeDip", 7, "插件胶囊状态点大小", "插件胶囊状态圆点的直径。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleRingSize =
        new("plugin.capsule.progressRingSizeDip", 18, "插件胶囊进度环大小", "插件未指定尺寸时的进度环直径。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleBarWidth =
        new("plugin.capsule.progressBarWidthDip", 30, "插件胶囊进度条宽", "插件未指定尺寸时的进度条宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginCapsuleBarMinWidth =
        new("plugin.capsule.fillProgressBarMinWidthDip", 18, "插件胶囊填充条最小宽", "填充式进度条的最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> PluginUserChangeDebounceMs =
        new("plugin.events.userChangeDebounceMs", 180d, "插件用户修改事件防抖", "聚合连续用户编辑再生成插件通知的等待时间。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> PluginTextSettingDebounceMs =
        new("plugin.settings.textPropagationMs", 180d, "插件文本设置传播防抖", "合并插件字符串与数字设置变更的等待时间。", unit: "ms", application: "restart");
    internal static readonly InjectionParameter<double> TodoMetricTextFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.textFontSize", "待办正文字号", "覆盖待办预设和全局文字缩放之后的正文字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricTextVerticalPadding =
        InjectionParameter<double>.Dynamic("todo.metrics.textVerticalPadding", "待办正文竖向留白", "覆盖待办预设和全局文字缩放之后的正文竖向留白，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricAppendMinHeight =
        InjectionParameter<double>.Dynamic("todo.metrics.appendMinHeight", "待办追加行高度", "覆盖待办预设和全局文字缩放之后的追加行高度，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricAppendGlyphFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.appendGlyphFontSize", "待办追加符号字号", "覆盖待办预设和全局文字缩放之后的追加符号字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricTrashGlyphFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.trashGlyphFontSize", "待办垃圾桶字号", "覆盖待办预设和全局文字缩放之后的垃圾桶字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricLinkedPaperNameFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.linkedPaperNameFontSize", "待办链接名称字号", "覆盖待办预设和全局文字缩放之后的链接名称字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricLinkedPaperIconFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.linkedPaperIconFontSize", "待办链接图标字号", "覆盖待办预设和全局文字缩放之后的链接图标字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricCheckColumnWidth =
        InjectionParameter<double>.Dynamic("todo.metrics.checkColumnWidth", "待办勾选列宽度", "覆盖待办预设和全局文字缩放之后的勾选列宽度，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricGhostTextFontSize =
        InjectionParameter<double>.Dynamic("todo.metrics.ghostTextFontSize", "待办拖动影像字号", "覆盖待办预设和全局文字缩放之后的拖动影像字号，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> TodoMetricRowMinHeight =
        InjectionParameter<double>.Dynamic("todo.metrics.rowMinHeight", "待办行最小高度", "覆盖待办预设和全局文字缩放之后的行最小高度，下次构建待办行时使用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "next_use");
    internal static readonly InjectionParameter<double> NoteBaseFontSize =
        new("note.typography.baseFontSize", 14d, "笔记正文基础字号", "字号预设修正与全局缩放之前的正文字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteComputedFontSize =
        InjectionParameter<double>.Dynamic("note.typography.fontSize", "笔记正文计算字号", "覆盖预设与全局缩放后的正文字号；单笔记缩放仍在之后应用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteBaseCodeFontSize =
        new("note.typography.baseCodeFontSize", 13d, "笔记代码基础字号", "字号预设修正与全局缩放之前的代码字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteComputedCodeFontSize =
        InjectionParameter<double>.Dynamic("note.typography.codeFontSize", "笔记代码计算字号", "覆盖预设与全局缩放后的代码字号；单笔记缩放仍在之后应用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteBaseHeading1FontSize =
        new("note.typography.baseHeading1FontSize", 17d, "笔记一级标题基础字号", "字号预设修正与全局缩放之前的一级标题字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteComputedHeading1FontSize =
        InjectionParameter<double>.Dynamic("note.typography.heading1FontSize", "笔记一级标题计算字号", "覆盖预设与全局缩放后的一级标题字号；单笔记缩放仍在之后应用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteBaseHeading2FontSize =
        new("note.typography.baseHeading2FontSize", 15d, "笔记二级标题基础字号", "字号预设修正与全局缩放之前的二级标题字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteComputedHeading2FontSize =
        InjectionParameter<double>.Dynamic("note.typography.heading2FontSize", "笔记二级标题计算字号", "覆盖预设与全局缩放后的二级标题字号；单笔记缩放仍在之后应用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteBaseHeading3FontSize =
        new("note.typography.baseHeading3FontSize", 14d, "笔记三级标题基础字号", "字号预设修正与全局缩放之前的三级标题字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteComputedHeading3FontSize =
        InjectionParameter<double>.Dynamic("note.typography.heading3FontSize", "笔记三级标题计算字号", "覆盖预设与全局缩放后的三级标题字号；单笔记缩放仍在之后应用。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteContentPaddingLeft =
        new("note.padding.leftDip", 13d, "笔记正文左留白", "笔记正文区域的内边距。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteContentPaddingTop =
        new("note.padding.topDip", 8d, "笔记正文上留白", "笔记正文区域的内边距。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteContentPaddingRight =
        new("note.padding.rightDip", 6d, "笔记正文右留白", "笔记正文区域的内边距。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteContentPaddingBottom =
        new("note.padding.bottomDip", 8d, "笔记正文下留白", "笔记正文区域的内边距。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageViewportReserve =
        new("note.images.viewportReserveDip", 10d, "笔记图片视口预留", "计算图片宽度时除左右留白外额外减去的空间。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageTargetWidth =
        InjectionParameter<double>.Dynamic("note.images.targetWidthDip", "笔记图片最终可用宽度", "覆盖根据正文视口计算的图片目标宽度。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageFallbackWidth =
        new("note.images.fallbackWidthDip", 240d, "笔记图片未布局宽度", "图片视口尚无有效宽度时使用的目标宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> NoteImageMinimumWidth =
        new("note.images.minTargetWidthDip", 80d, "笔记图片目标最小宽度", "视口计算完成后图片目标宽度的原生下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindBaseWidth =
        new("find.baseWidthDip", 296d, "查找框基础宽度", "乘全局文字缩放之前的查找框宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindMinimumWidth =
        new("find.minWidthDip", 236d, "查找框宽度下限", "查找框随文字缩放时的宽度下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindMaximumWidth =
        new("find.maxWidthDip", 420d, "查找框宽度上限", "查找框随文字缩放时的宽度上限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindComputedWidth =
        InjectionParameter<double>.Dynamic("find.widthDip", "查找框最终宽度", "覆盖查找框经过文字缩放、限幅和取整后的宽度。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> FindInputMinimumWidth =
        new("find.inputMinWidthDip", 72d, "查找输入框最小宽度", "查找框输入区域所在列的最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindInputFontSize =
        new("find.inputFontSizeDip", 11.5, "查找输入字号", "查找输入与按钮字符乘全局缩放之前的字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindCounterFontSize =
        new("find.counterFontSizeDip", 10.5, "查找计数字号", "匹配数量文字乘全局缩放之前的字号。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindControlHeight =
        new("find.controlSizeDip", 23d, "查找控件基础尺寸", "查找输入最小高度与按钮边长，随后按文字缩放适配。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindCounterWidth =
        new("find.counterMinWidthDip", 42d, "查找计数最小宽度", "查找匹配数量区域的基础最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindIconSize =
        new("find.iconSizeDip", 16d, "查找按钮图标尺寸", "查找前后项与关闭按钮的图标边长。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> FindTopOffset =
        new("find.topOffsetDip", 33d, "查找框竖向偏移", "默认位置相对标题栏下沿的偏移。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> FindLeftMinimum =
        new("find.minLeftDip", 6d, "查找框左侧最小位置", "查找框默认位置相对纸片左侧的最小距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> FindRightOverhang =
        new("find.rightOverhangDip", 20d, "查找框右侧伸出", "默认查找框相对纸片右侧向外伸出的距离。", unit: "DIP", application: "next_use");
    internal static readonly InjectionParameter<double> SettingsSidebarBaseWidth =
        new("settings.sidebar.baseWidthDip", 158d, "设置侧栏基础宽度", "设置导航栏宽度计算的基础下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsSidebarMaximumScale =
        new("settings.sidebar.maxWidthRatio", 1.2, "设置侧栏最大扩展系数", "侧栏按文字测量加宽时相对基础宽度的上限比例。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> SettingsSidebarWidth =
        InjectionParameter<double>.Dynamic("settings.sidebar.widthDip", "设置侧栏最终宽度", "覆盖根据翻译文本测量和限幅后得到的设置侧栏宽度。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> SettingsNavigationRowHeight =
        new("settings.sidebar.rowHeightDip", 34d, "设置导航行高度", "设置窗口左侧导航选项的固定高度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowBaseWidth =
        new("settings.window.baseWidthDip", 840d, "设置窗口基础宽度", "设置窗口加上侧栏扩展量之前的目标宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowBaseHeight =
        new("settings.window.baseHeightDip", 620d, "设置窗口基础高度", "设置窗口的常规目标高度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowMinimumWidth =
        new("settings.window.minWidthDip", 360d, "设置窗口参考最小宽度", "计算屏幕内设置窗口宽度时使用的参考下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowMinimumHeight =
        new("settings.window.minHeightDip", 320d, "设置窗口参考最小高度", "计算屏幕内设置窗口高度时使用的参考下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowPreferredMargin =
        new("settings.window.preferredMarginDip", 48d, "设置窗口首选空间预留", "计算设置目标尺寸时从屏幕可用空间减去的总余量。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> SettingsWindowBoundaryMargin =
        new("settings.window.boundaryMarginDip", 32d, "设置窗口边界空间预留", "最终屏幕内限幅时从可用空间减去的总余量。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathMultilineMinimumWidth =
        new("todo.pathLink.multilineMinWidthDip", 44d, "多行待办路径按钮最小宽度", "路径名称按钮在多行待办中的基础最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathSingleLineMinimumWidth =
        new("todo.pathLink.singleLineMinWidthDip", 50d, "单行待办路径按钮最小宽度", "路径名称按钮在单行待办中的基础最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathMultilineWidthRatio =
        new("todo.pathLink.multilineWidthRatio", 2d, "多行待办路径按钮宽度系数", "路径按钮宽度相对勾选列宽度的多行系数。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathSingleLineWidthRatio =
        new("todo.pathLink.singleLineWidthRatio", 2.2, "单行待办路径按钮宽度系数", "路径按钮宽度相对勾选列宽度的单行系数。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathNamePadding =
        new("todo.pathLink.namePaddingDip", 10d, "路径名称测量留白", "允许完整路径名称时在测量文本宽度外增加的总空间。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> TodoPathButtonWidth =
        InjectionParameter<double>.Dynamic("todo.pathLink.widthDip", "待办路径按钮最终宽度", "覆盖名称、图标和待办行宽度规则计算后的路径按钮宽度。", unit: "DIP", defaultDescription: "按当前设置与布局计算", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconOpacity =
        new("reminder.ui.iconOpacity", 0.44, "提醒图标静置透明度", "未悬停时提醒设置图标的透明度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconPressedOpacity =
        new("reminder.ui.pressedOpacity", 0.66, "提醒图标按下透明度", "按下提醒设置图标时的透明度。", unit: "", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconMinimumFont =
        new("reminder.ui.minIconFontDip", 10.5, "提醒图标最小字号", "提醒图标字号相对正文计算时的基础下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconFontOffset =
        new("reminder.ui.iconFontOffsetDip", 1.5, "提醒图标字号缩小", "提醒图标字号相对待办正文减去的基础值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconMinimumWidth =
        new("reminder.ui.minIconWidthDip", 17d, "提醒图标最小宽度", "提醒设置按钮的基础最小宽度。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderIconWidthOffset =
        new("reminder.ui.iconWidthOffsetDip", 6d, "提醒图标宽度缩小", "提醒设置按钮相对勾选列宽度减去的基础值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderCountdownMinimumFont =
        new("reminder.ui.minCountdownFontDip", 10d, "提醒倒计时最小字号", "倒计时字号相对正文计算时的基础下限。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderCountdownFontOffset =
        new("reminder.ui.countdownFontOffsetDip", 2d, "提醒倒计时字号缩小", "倒计时字号相对正文减去的基础值。", unit: "DIP", application: "restart");
    internal static readonly InjectionParameter<double> ReminderCountdownMinimumWidth =
        new("reminder.ui.minCountdownWidthDip", 24d, "提醒倒计时最小宽度", "倒计时区域的基础最小宽度。", unit: "DIP", application: "restart");
}
