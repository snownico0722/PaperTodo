using System.Globalization;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ReviewArchive;

internal static class PluginText
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        ["今日"] = "Today",
        ["近 7 天"] = "Last 7 days",
        ["连续"] = "Streak",
        ["进行中"] = "Open",
        ["已完成"] = "Completed",
        ["今天完成"] = "Completed today",
        ["近 30 天"] = "Last 30 days",
        ["重新打开"] = "Reopened",
        ["有提醒"] = "Has reminder",
        ["源已删除"] = "Source deleted",
        ["全部记录"] = "All records",
        ["搜索待办正文或所属纸片"] = "Search todo text or paper title",
        ["当前筛选没有记录"] = "No records match the current filter",
        ["导入当前"] = "Import current",
        ["导出 CSV"] = "Export CSV",
        ["清空记录"] = "Clear records",
        ["完成 {0} 项 / {1} 次 · 重新打开 {2} 次"] = "Completed {0} items / {1} events · Reopened {2} times",
        ["{0} 天"] = "{0} days",
        ["记录池暂时无法写入："] = "The archive cannot be written right now: ",
        ["未来 24 小时有 {0} 个待办提醒；记录池独立保存在插件 .runtime 中。"] = "{0} todo reminders are due within 24 hours; the archive is stored independently in the plugin .runtime folder.",
        ["记录池独立保存在插件 .runtime 中；删除原待办纸片后仍可复盘和导出。"] = "The archive is stored independently in the plugin .runtime folder, so records remain reviewable and exportable after the source paper is deleted.",
        ["（空待办）"] = "(Empty todo)",
        ["完成 "] = "Completed ",
        ["重新打开 "] = "Reopened ",
        ["创建 "] = "Created ",
        ["完成 {0} 次"] = "Completed {0} times",
        ["重开 {0} 次"] = "Reopened {0} times",
        ["提醒已到期 "] = "Reminder overdue ",
        ["提醒 "] = "Reminder ",
        ["提醒调整 {0} 次"] = "Reminder changed {0} times",
        ["时间为首次观察值"] = "Time is based on first observation",
        ["当前待办已经全部存在于记录池中。"] = "All current todos are already present in the archive.",
        ["导出 PaperTodo 复盘记录"] = "Export PaperTodo review archive",
        ["CSV 文件 (*.csv)|*.csv"] = "CSV files (*.csv)|*.csv",
        ["已导出 {0} 条：{1}"] = "Exported {0} records: {1}",
        ["导出失败"] = "Export failed",
        ["状态"] = "Status",
        ["待办"] = "Todo",
        ["所属纸片"] = "Paper",
        ["创建时间"] = "Created",
        ["最后完成时间"] = "Last completed",
        ["完成次数"] = "Completion count",
        ["最后重新打开"] = "Last reopened",
        ["提醒时间"] = "Reminder",
        ["提醒变更次数"] = "Reminder changes",
        ["创建时间精度"] = "Creation time precision",
        ["完成时间精度"] = "Completion time precision",
        ["来源"] = "Origin",
        ["是"] = "Yes",
        ["否"] = "No",
        ["首次观察"] = "First observed",
        ["精确"] = "Exact",
        ["确定清空全部复盘记录吗？此操作不会删除 PaperTodo 中的待办。"] = "Clear all review records? This will not delete any todos in PaperTodo.",
        ["清空复盘记录"] = "Clear review archive",
        ["今日完成 {0}"] = "Completed today {0}",
        ["连续 {0} 天"] = "{0}-day streak",
        ["等待今日完成"] = "Waiting for today’s completion",
        ["进行中 {0}"] = "Open {0}",
        ["复盘记录"] = "Review Archive",
        ["复盘 · {0} 项"] = "Review · {0} items",
        ["{0} · 进行中 {1}"] = "{0} · Open {1}",
        ["{0} 未完"] = "{0} open"
        };

    internal static bool IsChinese =>
        PaperPluginEnvironment.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    internal static CultureInfo Culture =>
        CultureInfo.GetCultureInfo(IsChinese ? "zh-CN" : "en-US");

    internal static string T(string zh) =>
        IsChinese ? zh : English.TryGetValue(zh, out var value) ? value : zh;

    internal static string Format(string zhFormat, params object?[] args) =>
        string.Format(Culture, T(zhFormat), args);
}
