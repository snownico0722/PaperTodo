from pathlib import Path

def replace(path, old, new):
    p = Path(path)
    s = p.read_text(encoding='utf-8')
    assert s.count(old) == 1, (path, old[:80], s.count(old))
    p.write_text(s.replace(old, new), encoding='utf-8', newline='\n')

replace('tests/PaperTodo.LifecycleChecks/Program.cs',
'''    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target)!;''',
'''    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().FullName, name);''')
replace('src/AppController.PluginStartup.cs',
'''        try
        {
            // The shell queue knows when it finished.''',
'''        try
        {
            // Even an already-complete shell queue must not initialize a plugin inline in
            // StartAsync. Let the existing surfaces present and return startup command control.
            await Application.Current.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            // The shell queue knows when it finished.''')
replace('doc/ARCHITECTURE.md',
'''`AppController` 尚未完成启动时收到的单实例命令先排队，待 controller 可用后再执行。普通纸片窗口全部关闭不等于退出应用，进程使用显式 shutdown 生命周期。''',
'''`AppController` 尚未完成启动时收到的单实例命令先排队，待 controller 可用后再执行。普通纸片窗口全部关闭不等于退出应用，进程使用显式 shutdown 生命周期。

启动恢复先建立已知显示器上的 Edge Host 和可见纸片。只有显示器归属尚不确定的普通纸片延后恢复，等待期间不改写其坐标；显示器稳定或限时到达后仍由既有离屏救援处理。显式显示、隐藏、删除和退出优先于迟到的恢复结果。`AppController.StartupPrewarm` 在 UI Dispatcher 上按短批次补建 Shell，完成 Task 供插件 startupPaper 等待，不使用 Shell-ready 轮询；插件初始化本身仍在 idle 阶段，不同步阻挡 StartAsync 返回。

正常退出先提交当前编辑并完成既有同步保存，再撤下可见 surface；撤下界面不改变持久化 IsVisible。WPF/插件 UI 仍由原 Dispatcher 释放，脚本进程的停止请求和限时等待在非 UI 任务中并发执行，与界面清理重叠，最终统一等待完成。退出不为即将销毁的图片缓存执行额外回收，也不重复提交已由 controller 保存的编辑内容。''')
replace('doc/ARCHITECTURE.md',
'''`MarkdownEdgePreviewPreload` 只筛选、排队并缓存合格来源的完整 artifact，沿用共享一次性合并延迟。''',
'''`MarkdownEdgePreviewPreload` 只筛选、排队并缓存合格来源的完整 artifact。边缘浏览开启时，小工作集包含轻内容在内的非空内置 Markdown 笔记都可预热，较多来源仍沿用重内容筛选；计数只包含当前存活、可见且已进入边缘队列的内置 Markdown 纸片。启动恢复后的首批工作直接唤醒既有队列，后续编辑仍使用共享一次性合并延迟。''')
replace('CHANGELOG.md',
'''启动恢复和内容变化后，会在空闲期提前排好较长或样式较多的边缘笔记预览，反复切换时复用结果、减少首次浏览等待，不额外保留隐藏卡片；轻内容不主动预热，离开边缘队列后释放对应缓存。''',
'''启动恢复和内容变化后，会提前排好边缘笔记预览，反复切换时复用结果、减少首次浏览等待，不额外保留隐藏卡片；开启边缘浏览且合格笔记不超过 10 张时，短笔记也全部预热，更多笔记时优先准备较长或样式较多的内容。启动首批不额外等待编辑合并延迟，连续编辑仍合并处理，离开边缘队列后释放对应缓存。''')
replace('CHANGELOG.md',
'''### Unreleased

**边缘预览卡片**''',
'''### Unreleased

- **启动与退出响应**：显示器尚未就绪时，只延后相关纸片，其他胶囊和纸片可先使用；少量纸片按短批次预建，插件启动纸片不再轮询等待。正常退出保留最后一次保存，随后先撤下界面，并同时停止脚本进程，减少逐个等待。

**边缘预览卡片**''')
replace('doc/CHANGELOG.en.md',
'''After startup restoration and content changes, longer or more styled edge-note previews are prepared during idle time to reduce first-hover waiting; light content is not actively preloaded, and leaving the edge queue releases its cache.''',
'''After startup restoration and content changes, whole-preview drawing results are prepared ahead of use without retaining hidden cards. With Edge Browse enabled and no more than 10 eligible edge Markdown notes, short notes are preloaded too; larger worksets retain the heavy-content filter. The initial startup batch does not wait for the editing debounce; continuous edits remain coalesced, and leaving the edge queue releases its cache.''')
replace('doc/CHANGELOG.en.md',
'''### Unreleased (4.0.0-preview)

**Edge Preview Cards (Edge Browse)**''',
'''### Unreleased (4.0.0-preview)

- **Startup and Exit Responsiveness**: Only papers on not-yet-available displays defer restoration; other capsules and papers become available first. Small shell worksets are prepared in short batches, and plugin startup papers await completion instead of polling. Normal exit preserves the final save, withdraws visible surfaces before slower cleanup, and stops script processes concurrently rather than waiting for each in sequence.

**Edge Preview Cards (Edge Browse)**''')
