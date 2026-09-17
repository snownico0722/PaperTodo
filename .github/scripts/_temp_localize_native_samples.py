from __future__ import annotations

from pathlib import Path
import json

ROOT = Path('.')


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding='utf-8')


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding='utf-8')


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    text = read(path)
    if old in text:
        write(path, text.replace(old, new, count))
        return
    if new in text:
        return
    raise SystemExit(f'missing replacement in {path}: {old[:120]!r}')


def helper(directory: str, namespace: str, mapping: dict[str, str]) -> None:
    items = ',\n'.join(
        f'        [{json.dumps(key, ensure_ascii=False)}] = {json.dumps(value, ensure_ascii=False)}'
        for key, value in mapping.items()
    )
    content = f'''using System.Globalization;
using PaperTodo.Plugin;

namespace {namespace};

internal static class PluginText
{{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {{
{items}
        }};

    internal static bool IsChinese =>
        PaperPluginEnvironment.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    internal static CultureInfo Culture =>
        CultureInfo.GetCultureInfo(IsChinese ? "zh-CN" : "en-US");

    internal static string T(string zh) =>
        IsChinese ? zh : English.TryGetValue(zh, out var value) ? value : zh;

    internal static string Format(string zhFormat, params object?[] args) =>
        string.Format(Culture, T(zhFormat), args);
}}
'''
    write(f'{directory}/Localization.cs', content)


helper(
    'plugin-samples/PaperTodo.Plugin.CloudGenshin',
    'PaperTodo.Plugin.CloudGenshin',
    {
        '云·原神': 'Cloud Genshin',
        '云原神 · 加载中': 'Cloud Genshin · Loading',
        '正在启动云·原神…': 'Starting Cloud Genshin…',
        '重新加载': 'Reload',
        '正在初始化 WebView2…': 'Initializing WebView2…',
        '正在加载云·原神…': 'Loading Cloud Genshin…',
        '网页加载失败：': 'Web page failed to load: ',
        '云原神': 'Cloud Genshin',
        'WebView2 浏览器进程已退出，正在重建…': 'The WebView2 browser process exited; rebuilding…',
        '云原神 · 正在重启': 'Cloud Genshin · Restarting',
        'WebView2 渲染进程异常退出。': 'The WebView2 render process exited unexpectedly.',
        '云·原神加载失败': 'Cloud Genshin failed to load',
        '云原神 · 错误': 'Cloud Genshin · Error',
        '正在重新加载云·原神…': 'Reloading Cloud Genshin…',
        'WebView2 初始化后未返回 CoreWebView2。 ': 'WebView2 initialization returned no CoreWebView2 instance.',
    },
)

helper(
    'plugin-samples/PaperTodo.Plugin.FocusTimer',
    'PaperTodo.Plugin.FocusTimer',
    {
        '专注': 'Focus',
        '休息': 'Break',
        '选择本轮专注对应的 PaperTodo 待办': 'Choose the PaperTodo todo for this focus session',
        '开始': 'Start',
        '跳过': 'Skip',
        '重置': 'Reset',
        '插件未获得 todos.read 权限，无法关联待办。': 'The plugin does not have todos.read permission, so todos cannot be linked.',
        '关联待办已不存在。': 'The linked todo no longer exists.',
        '完成关联待办失败：': 'Failed to complete linked todo: ',
        '选择下一项失败：': 'Failed to select the next todo: ',
        '重置当前计时？': 'Reset the current timer?',
        '专注计时器': 'Focus Timer',
        '不关联待办': 'No linked todo',
        '关联待办已删除或不可访问。': 'The linked todo was deleted or is no longer accessible.',
        '（空待办）': '(Empty todo)',
        '待办纸': 'Todo paper',
        '保持专注': 'Stay focused',
        '准备开始': 'Ready to start',
        '放松一下': 'Take a break',
        '休息计时已暂停': 'Break timer paused',
        '今日 {0}/{1} · 总计 {2}': 'Today {0}/{1} · Total {2}',
        '今日 {0} · 总计 {1}': 'Today {0} · Total {1}',
        '{0} 分钟 · ±{1}': '{0} min · ±{1}',
        '暂停': 'Pause',
        '继续': 'Resume',
        '本轮未关联待办；选择后可在专注结束时自动完成。': 'No todo is linked to this session; choose one to optionally complete it when focus ends.',
        '关联待办已完成。': 'The linked todo is already complete.',
        '专注结束后将完成此待办。': 'This todo will be completed when the focus session ends.',
        '仅显示关联，不自动修改待办状态。': 'The todo is linked for display only; its state will not be changed automatically.',
        '{0}中': '{0} running',
        '{0}已暂停': '{0} paused',
        '进行中': 'Running',
        '已暂停': 'Paused',
        '待办': 'Todo',
    },
)

helper(
    'plugin-samples/PaperTodo.Plugin.SampleClock',
    'PaperTodo.Plugin.SampleClock',
    {
        '时钟': 'Clock',
        '本地时间': 'Local time',
        '北京时间': 'Beijing time',
        '东京时间': 'Tokyo time',
        '伦敦时间': 'London time',
        '纽约时间': 'New York time',
        '洛杉矶时间': 'Los Angeles time',
    },
)

helper(
    'plugin-samples/PaperTodo.Plugin.ReviewArchive',
    'PaperTodo.Plugin.ReviewArchive',
    {
        '今日': 'Today',
        '近 7 天': 'Last 7 days',
        '连续': 'Streak',
        '进行中': 'Open',
        '已完成': 'Completed',
        '今天完成': 'Completed today',
        '近 30 天': 'Last 30 days',
        '重新打开': 'Reopened',
        '有提醒': 'Has reminder',
        '源已删除': 'Source deleted',
        '全部记录': 'All records',
        '搜索待办正文或所属纸片': 'Search todo text or paper title',
        '当前筛选没有记录': 'No records match the current filter',
        '导入当前': 'Import current',
        '导出 CSV': 'Export CSV',
        '清空记录': 'Clear records',
        '完成 {0} 项 / {1} 次 · 重新打开 {2} 次': 'Completed {0} items / {1} events · Reopened {2} times',
        '{0} 天': '{0} days',
        '记录池暂时无法写入：': 'The archive cannot be written right now: ',
        '未来 24 小时有 {0} 个待办提醒；记录池独立保存在插件 .runtime 中。': '{0} todo reminders are due within 24 hours; the archive is stored independently in the plugin .runtime folder.',
        '记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。': 'The archive is stored independently in the plugin .runtime folder, so records remain reviewable and exportable after the source paper is deleted.',
        '（空待办）': '(Empty todo)',
        '完成 ': 'Completed ',
        '重新打开 ': 'Reopened ',
        '创建 ': 'Created ',
        '完成 {0} 次': 'Completed {0} times',
        '重开 {0} 次': 'Reopened {0} times',
        '提醒已到期 ': 'Reminder overdue ',
        '提醒 ': 'Reminder ',
        '提醒调整 {0} 次': 'Reminder changed {0} times',
        '时间为首次观察值': 'Time is based on first observation',
        '当前待办已经全部存在于记录池中。': 'All current todos are already present in the archive.',
        '导出 PaperTodo 复盘记录': 'Export PaperTodo review archive',
        'CSV 文件 (*.csv)|*.csv': 'CSV files (*.csv)|*.csv',
        '已导出 {0} 条：{1}': 'Exported {0} records: {1}',
        '导出失败': 'Export failed',
        '状态': 'Status',
        '待办': 'Todo',
        '所属纸片': 'Paper',
        '创建时间': 'Created',
        '最后完成时间': 'Last completed',
        '完成次数': 'Completion count',
        '最后重新打开': 'Last reopened',
        '提醒时间': 'Reminder',
        '提醒变更次数': 'Reminder changes',
        '创建时间精度': 'Creation time precision',
        '完成时间精度': 'Completion time precision',
        '来源': 'Origin',
        '是': 'Yes',
        '否': 'No',
        '首次观察': 'First observed',
        '精确': 'Exact',
        '确定清空全部复盘记录吗？此操作不会删除 PaperTodo 中的待办。': 'Clear all review records? This will not delete any todos in PaperTodo.',
        '清空复盘记录': 'Clear review archive',
        '今日完成 {0}': 'Completed today {0}',
        '连续 {0} 天': '{0}-day streak',
        '等待今日完成': 'Waiting for today’s completion',
        '进行中 {0}': 'Open {0}',
        '复盘记录': 'Review Archive',
        '复盘 · {0} 项': 'Review · {0} items',
        '{0} · 进行中 {1}': '{0} · Open {1}',
        '{0} 未完': '{0} open',
    },
)

# Cloud Genshin.
path = 'plugin-samples/PaperTodo.Plugin.CloudGenshin/CloudGenshinPlugin.cs'
for old, new in [
    ('private string _miniStatusText = "云原神 · 加载中";', 'private string _miniStatusText = PluginText.T("云原神 · 加载中");'),
    ('_context.Paper.SetTitle("云·原神");', '_context.Paper.SetTitle(PluginText.T("云·原神"));'),
    ('SetPaperStatus("云原神 · 加载中", PaperCapsuleTone.Muted);', 'SetPaperStatus(PluginText.T("云原神 · 加载中"), PaperCapsuleTone.Muted);'),
    ('Text = "正在启动云·原神…",', 'Text = PluginText.T("正在启动云·原神…"),'),
    ('Content = "重新加载",', 'Content = PluginText.T("重新加载"),'),
    ('Text = "云·原神",', 'Text = PluginText.T("云·原神"),'),
    ('ShowStatus("正在初始化 WebView2…");', 'ShowStatus(PluginText.T("正在初始化 WebView2…"));'),
    ('throw new InvalidOperationException("WebView2 初始化后未返回 CoreWebView2。 ");', 'throw new InvalidOperationException(PluginText.T("WebView2 初始化后未返回 CoreWebView2。 "));'),
    ('ShowStatus("正在加载云·原神…");', 'ShowStatus(PluginText.T("正在加载云·原神…"));'),
    ('ShowFailure($"网页加载失败：{e.WebErrorStatus}");', 'ShowFailure($"{PluginText.T("网页加载失败：")}{e.WebErrorStatus}");'),
    ('SetPaperStatus("云原神", PaperCapsuleTone.Accent);', 'SetPaperStatus(PluginText.T("云原神"), PaperCapsuleTone.Accent);'),
    ('ShowStatus("WebView2 浏览器进程已退出，正在重建…");', 'ShowStatus(PluginText.T("WebView2 浏览器进程已退出，正在重建…"));'),
    ('SetPaperStatus("云原神 · 正在重启", PaperCapsuleTone.Warning);', 'SetPaperStatus(PluginText.T("云原神 · 正在重启"), PaperCapsuleTone.Warning);'),
    ('"WebView2 渲染进程异常退出。",', 'PluginText.T("WebView2 渲染进程异常退出。"),'),
    ('_statusText.Text = $"云·原神加载失败\\n\\n{message}";', '_statusText.Text = $"{PluginText.T("云·原神加载失败")}\\n\\n{message}";'),
    ('SetPaperStatus("云原神 · 错误", PaperCapsuleTone.Danger);', 'SetPaperStatus(PluginText.T("云原神 · 错误"), PaperCapsuleTone.Danger);'),
    ('ShowStatus("正在重新加载云·原神…");', 'ShowStatus(PluginText.T("正在重新加载云·原神…"));'),
]:
    replace(path, old, new)

# Focus Timer.
path = 'plugin-samples/PaperTodo.Plugin.FocusTimer/FocusTimerPlugin.cs'
for old, new in [
    ('_focusButton = MakeButton("专注");', '_focusButton = MakeButton(PluginText.T("专注"));'),
    ('_breakButton = MakeButton("休息");', '_breakButton = MakeButton(PluginText.T("休息"));'),
    ('ToolTip = "选择本轮专注对应的 PaperTodo 待办"', 'ToolTip = PluginText.T("选择本轮专注对应的 PaperTodo 待办")'),
    ('_startButton = MakeButton("开始");', '_startButton = MakeButton(PluginText.T("开始"));'),
    ('_skipButton = MakeButton("跳过");', '_skipButton = MakeButton(PluginText.T("跳过"));'),
    ('_resetButton = MakeButton("重置");', '_resetButton = MakeButton(PluginText.T("重置"));'),
    ('_todoLoadError = "插件未获得 todos.read 权限，无法关联待办。";', '_todoLoadError = PluginText.T("插件未获得 todos.read 权限，无法关联待办。");'),
    ('_todoLoadError = "关联待办已不存在。";', '_todoLoadError = PluginText.T("关联待办已不存在。");'),
    ('_todoLoadError = "完成关联待办失败：" + ex.Message;', '_todoLoadError = PluginText.T("完成关联待办失败：") + ex.Message;'),
    ('_todoLoadError = "完成关联待办失败：" + ex.GetBaseException().Message;', '_todoLoadError = PluginText.T("完成关联待办失败：") + ex.GetBaseException().Message;'),
    ('_todoLoadError = "选择下一项失败：" + ex.GetBaseException().Message;', '_todoLoadError = PluginText.T("选择下一项失败：") + ex.GetBaseException().Message;'),
    ('"重置当前计时？",\n                    "专注计时器",', 'PluginText.T("重置当前计时？"),\n                    PluginText.T("专注计时器"),'),
    ('todos.Insert(0, new TodoOption("", "", "不关联待办", "", false));', 'todos.Insert(0, new TodoOption("", "", PluginText.T("不关联待办"), "", false));'),
    ('_todoLoadError = "关联待办已删除或不可访问。";', '_todoLoadError = PluginText.T("关联待办已删除或不可访问。");'),
    ('? "（空待办）"', '? PluginText.T("（空待办）")'),
    ('? "待办纸"', '? PluginText.T("待办纸")'),
    ('var modeName = _state.Mode == TimerMode.Focus ? "专注" : "休息";', 'var modeName = _state.Mode == TimerMode.Focus ? PluginText.T("专注") : PluginText.T("休息");'),
    ('? (_state.IsRunning ? "保持专注" : "准备开始")\n                : (_state.IsRunning ? "放松一下" : "休息计时已暂停");', '? (_state.IsRunning ? PluginText.T("保持专注") : PluginText.T("准备开始"))\n                : (_state.IsRunning ? PluginText.T("放松一下") : PluginText.T("休息计时已暂停"));'),
    ('? $"今日 {_state.CompletedToday}/{_settings.DailyGoal} · 总计 {_state.CompletedFocusSessions}"\n                : $"今日 {_state.CompletedToday} · 总计 {_state.CompletedFocusSessions}";', '? PluginText.Format("今日 {0}/{1} · 总计 {2}", _state.CompletedToday, _settings.DailyGoal, _state.CompletedFocusSessions)\n                : PluginText.Format("今日 {0} · 总计 {1}", _state.CompletedToday, _state.CompletedFocusSessions);'),
    ('_durationText.Text = $"{DurationMinutes()} 分钟 · ±{_settings.AdjustStep}";', '_durationText.Text = PluginText.Format("{0} 分钟 · ±{1}", DurationMinutes(), _settings.AdjustStep);'),
    ('? "暂停"\n                : remaining < full ? "继续" : "开始";', '? PluginText.T("暂停")\n                : remaining < full ? PluginText.T("继续") : PluginText.T("开始");'),
    ('_todoStatusText.Text = "本轮未关联待办；选择后可在专注结束时自动完成。";', '_todoStatusText.Text = PluginText.T("本轮未关联待办；选择后可在专注结束时自动完成。");'),
    ('_todoStatusText.Text = "关联待办已完成。";', '_todoStatusText.Text = PluginText.T("关联待办已完成。");'),
    ('? "专注结束后将完成此待办。"\n                    : "仅显示关联，不自动修改待办状态。";', '? PluginText.T("专注结束后将完成此待办。")\n                    : PluginText.T("仅显示关联，不自动修改待办状态。");'),
    ('"status" => _state.IsRunning ? $"{modeName}中" : $"{modeName}已暂停",', '"status" => _state.IsRunning\n                    ? PluginText.Format("{0}中", modeName)\n                    : PluginText.Format("{0}已暂停", modeName),'),
    ('"fixed" => "专注计时器",', '"fixed" => PluginText.T("专注计时器"),'),
    ('_state.IsRunning ? "进行中" : "已暂停",', '_state.IsRunning ? PluginText.T("进行中") : PluginText.T("已暂停"),'),
    ('_state.IsRunning ? "暂停" : remaining < full ? "继续" : "开始");', '_state.IsRunning ? PluginText.T("暂停") : remaining < full ? PluginText.T("继续") : PluginText.T("开始"));'),
    ('return "待办";', 'return PluginText.T("待办");'),
    ('var runningText = _state.IsRunning ? "进行中" : "已暂停";', 'var runningText = _state.IsRunning ? PluginText.T("进行中") : PluginText.T("已暂停");'),
]:
    replace(path, old, new)

# Native Clock.
path = 'plugin-samples/PaperTodo.Plugin.SampleClock/SampleClockPlugin.cs'
for old, new in [
    ('private string _capsuleTitle = "时钟";', 'private string _capsuleTitle = PluginText.T("时钟");'),
    ('_time.Text = now.ToString(timeFormat);', '_time.Text = now.ToString(timeFormat, PluginText.Culture);'),
    ('_meridiem.Text = twelveHour ? now.ToString("tt") : "";', '_meridiem.Text = twelveHour ? now.ToString("tt", PluginText.Culture) : "";'),
    ('dateParts.Add(now.ToString("dddd"));', 'dateParts.Add(now.ToString("dddd", PluginText.Culture));'),
    ('"beijing" => "北京时间",', '"beijing" => PluginText.T("北京时间"),'),
    ('"tokyo" => "东京时间",', '"tokyo" => PluginText.T("东京时间"),'),
    ('"london" => "伦敦时间",', '"london" => PluginText.T("伦敦时间"),'),
    ('"newYork" => "纽约时间",', '"newYork" => PluginText.T("纽约时间"),'),
    ('"losAngeles" => "洛杉矶时间",', '"losAngeles" => PluginText.T("洛杉矶时间"),'),
    ('_ => "本地时间"', '_ => PluginText.T("本地时间")'),
    ('_ => now.ToString("yyyy年M月d日")', '_ => PluginText.IsChinese\n                    ? now.ToString("yyyy年M月d日", PluginText.Culture)\n                    : now.ToString("MMMM d, yyyy", PluginText.Culture)'),
    (': "HH:mm");', ': "HH:mm",\n                PluginText.Culture);'),
    ('"fixed" => "时钟",', '"fixed" => PluginText.T("时钟"),'),
]:
    replace(path, old, new)

# Review Archive Runtime.
path = 'plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchivePlugin.cs'
for old, new in [
    ('"today" => $"今日完成 {todayCount}",', '"today" => PluginText.Format("今日完成 {0}", todayCount),'),
    ('"streak" => streak > 0 ? $"连续 {streak} 天" : "等待今日完成",', '"streak" => streak > 0 ? PluginText.Format("连续 {0} 天", streak) : PluginText.T("等待今日完成"),'),
    ('"open" => $"进行中 {openCount}",', '"open" => PluginText.Format("进行中 {0}", openCount),'),
    ('? "复盘记录"\n                    : settings.FixedTitle,', '? PluginText.T("复盘记录")\n                    : settings.FixedTitle,'),
    ('_ => $"复盘 · {completedRecords} 项"', '_ => PluginText.Format("复盘 · {0} 项", completedRecords)'),
    ('ToolTip = $"{title} · 进行中 {openCount}",', 'ToolTip = PluginText.Format("{0} · 进行中 {1}", title, openCount),'),
    ('Text = $"{openCount} 未完",', 'Text = PluginText.Format("{0} 未完", openCount),'),
]:
    replace(path, old, new)

# Review Archive body.
path = 'plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchiveSession.cs'
for old, new in [
    ('CreateInsightCard("今日")', 'CreateInsightCard(PluginText.T("今日"))'),
    ('CreateInsightCard("近 7 天")', 'CreateInsightCard(PluginText.T("近 7 天"))'),
    ('CreateInsightCard("连续")', 'CreateInsightCard(PluginText.T("连续"))'),
    ('CreateInsightCard("进行中")', 'CreateInsightCard(PluginText.T("进行中"))'),
    ('new FilterOption("completed", "已完成")', 'new FilterOption("completed", PluginText.T("已完成"))'),
    ('new FilterOption("today", "今天完成")', 'new FilterOption("today", PluginText.T("今天完成"))'),
    ('new FilterOption("week", "近 7 天")', 'new FilterOption("week", PluginText.T("近 7 天"))'),
    ('new FilterOption("month", "近 30 天")', 'new FilterOption("month", PluginText.T("近 30 天"))'),
    ('new FilterOption("open", "进行中")', 'new FilterOption("open", PluginText.T("进行中"))'),
    ('new FilterOption("reopened", "重新打开")', 'new FilterOption("reopened", PluginText.T("重新打开"))'),
    ('new FilterOption("reminders", "有提醒")', 'new FilterOption("reminders", PluginText.T("有提醒"))'),
    ('new FilterOption("deleted", "源已删除")', 'new FilterOption("deleted", PluginText.T("源已删除"))'),
    ('new FilterOption("all", "全部记录")', 'new FilterOption("all", PluginText.T("全部记录"))'),
    ('ToolTip = "搜索待办正文或所属纸片"', 'ToolTip = PluginText.T("搜索待办正文或所属纸片")'),
    ('Text = "当前筛选没有记录",', 'Text = PluginText.T("当前筛选没有记录"),'),
    ('_importButton = MakeButton("导入当前");', '_importButton = MakeButton(PluginText.T("导入当前"));'),
    ('_exportButton = MakeButton("导出 CSV");', '_exportButton = MakeButton(PluginText.T("导出 CSV"));'),
    ('_clearButton = MakeButton("清空记录");', '_clearButton = MakeButton(PluginText.T("清空记录"));'),
    ('$"完成 {completedRecords} 项 / {completionEvents.Length} 次 · 重新打开 {reopenedCount} 次";', 'PluginText.Format("完成 {0} 项 / {1} 次 · 重新打开 {2} 次", completedRecords, completionEvents.Length, reopenedCount);'),
    ('_streakValue.Text = streak > 0 ? streak + " 天" : "0";', '_streakValue.Text = streak > 0 ? PluginText.Format("{0} 天", streak) : "0";'),
    ('_hintText.Text = "记录池暂时无法写入：" + ReviewArchiveStore.LastSaveError;', '_hintText.Text = PluginText.T("记录池暂时无法写入：") + ReviewArchiveStore.LastSaveError;'),
    ('_hintText.Text = $"未来 24 小时有 {upcomingCount} 个待办提醒；记录池独立保存在插件 .runtime 中。";', '_hintText.Text = PluginText.Format("未来 24 小时有 {0} 个待办提醒；记录池独立保存在插件 .runtime 中。", upcomingCount);'),
    ('_hintText.Text = "记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。";', '_hintText.Text = PluginText.T("记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。");'),
    ('? "（空待办）" : record.Text', '? PluginText.T("（空待办）") : record.Text'),
    ('? "完成 " + FormatDate(lastCompleted.Value)', '? PluginText.T("完成 ") + FormatDate(lastCompleted.Value)'),
    ('? "重新打开 " + FormatDate(lastReopened.Value)', '? PluginText.T("重新打开 ") + FormatDate(lastReopened.Value)'),
    (': "创建 " + FormatDate(record.CreatedAt));', ': PluginText.T("创建 ") + FormatDate(record.CreatedAt));'),
    ('metadata.Add($"完成 {completionCount} 次");', 'metadata.Add(PluginText.Format("完成 {0} 次", completionCount));'),
    ('metadata.Add($"重开 {reopenedCount} 次");', 'metadata.Add(PluginText.Format("重开 {0} 次", reopenedCount));'),
    ('? "提醒已到期 " + FormatDate(reminder)\n                : "提醒 " + FormatDate(reminder));', '? PluginText.T("提醒已到期 ") + FormatDate(reminder)\n                : PluginText.T("提醒 ") + FormatDate(reminder));'),
    ('metadata.Add($"提醒调整 {reminderChanges} 次");', 'metadata.Add(PluginText.Format("提醒调整 {0} 次", reminderChanges));'),
    ('metadata.Add("时间为首次观察值");', 'metadata.Add(PluginText.T("时间为首次观察值"));'),
    ('metadata.Add("源已删除");', 'metadata.Add(PluginText.T("源已删除"));'),
    ('_hintText.Text = "当前待办已经全部存在于记录池中。";', '_hintText.Text = PluginText.T("当前待办已经全部存在于记录池中。");'),
    ('Title = "导出 PaperTodo 复盘记录",', 'Title = PluginText.T("导出 PaperTodo 复盘记录"),'),
    ('Filter = "CSV 文件 (*.csv)|*.csv",', 'Filter = PluginText.T("CSV 文件 (*.csv)|*.csv"),'),
    ('FileName = $"PaperTodo-复盘-{DateTime.Now:yyyyMMdd-HHmm}.csv",', 'FileName = PluginText.IsChinese\n                ? $"PaperTodo-复盘-{DateTime.Now:yyyyMMdd-HHmm}.csv"\n                : $"PaperTodo-Review-{DateTime.Now:yyyyMMdd-HHmm}.csv",'),
    ('_hintText.Text = $"已导出 {records.Length} 条：{dialog.FileName}";', '_hintText.Text = PluginText.Format("已导出 {0} 条：{1}", records.Length, dialog.FileName);'),
    ('"导出失败",', 'PluginText.T("导出失败"),'),
    ('var headers = new List<string> { "状态", "待办" };', 'var headers = new List<string> { PluginText.T("状态"), PluginText.T("待办") };'),
    ('headers.Add("所属纸片");', 'headers.Add(PluginText.T("所属纸片"));'),
    ('            "创建时间",\n            "最后完成时间",\n            "完成次数",\n            "最后重新打开",\n            "提醒时间",\n            "提醒变更次数",\n            "源已删除",\n            "创建时间精度",\n            "完成时间精度",\n            "来源"]);', '            PluginText.T("创建时间"),\n            PluginText.T("最后完成时间"),\n            PluginText.T("完成次数"),\n            PluginText.T("最后重新打开"),\n            PluginText.T("提醒时间"),\n            PluginText.T("提醒变更次数"),\n            PluginText.T("源已删除"),\n            PluginText.T("创建时间精度"),\n            PluginText.T("完成时间精度"),\n            PluginText.T("来源")]);'),
    ('record.Done ? "已完成" : "进行中",', 'record.Done ? PluginText.T("已完成") : PluginText.T("进行中"),'),
    ('row.Add(record.SourceDeleted ? "是" : "否");', 'row.Add(record.SourceDeleted ? PluginText.T("是") : PluginText.T("否"));'),
    ('row.Add(record.CreatedAtEstimated ? "首次观察" : "精确");', 'row.Add(record.CreatedAtEstimated ? PluginText.T("首次观察") : PluginText.T("精确"));'),
    ('row.Add(record.CompletedAtEstimated ? "首次观察" : record.CompletedAt.HasValue ? "精确" : "");', 'row.Add(record.CompletedAtEstimated ? PluginText.T("首次观察") : record.CompletedAt.HasValue ? PluginText.T("精确") : "");'),
    ('"确定清空全部复盘记录吗？此操作不会删除 PaperTodo 中的待办。",\n                "清空复盘记录",', 'PluginText.T("确定清空全部复盘记录吗？此操作不会删除 PaperTodo 中的待办。"),\n                PluginText.T("清空复盘记录"),'),
]:
    replace(path, old, new)
