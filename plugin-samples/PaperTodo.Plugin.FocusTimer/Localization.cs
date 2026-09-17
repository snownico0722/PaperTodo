using System.Globalization;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.FocusTimer;

internal static class PluginText
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        ["专注"] = "Focus",
        ["休息"] = "Break",
        ["选择本轮专注对应的 PaperTodo 待办"] = "Choose the PaperTodo todo for this focus session",
        ["开始"] = "Start",
        ["跳过"] = "Skip",
        ["重置"] = "Reset",
        ["插件未获得 todos.read 权限，无法关联待办。"] = "The plugin does not have todos.read permission, so todos cannot be linked.",
        ["关联待办已不存在。"] = "The linked todo no longer exists.",
        ["完成关联待办失败："] = "Failed to complete linked todo: ",
        ["选择下一项失败："] = "Failed to select the next todo: ",
        ["重置当前计时？"] = "Reset the current timer?",
        ["专注计时器"] = "Focus Timer",
        ["不关联待办"] = "No linked todo",
        ["关联待办已删除或不可访问。"] = "The linked todo was deleted or is no longer accessible.",
        ["（空待办）"] = "(Empty todo)",
        ["待办纸"] = "Todo paper",
        ["保持专注"] = "Stay focused",
        ["准备开始"] = "Ready to start",
        ["放松一下"] = "Take a break",
        ["休息计时已暂停"] = "Break timer paused",
        ["今日 {0}/{1} · 总计 {2}"] = "Today {0}/{1} · Total {2}",
        ["今日 {0} · 总计 {1}"] = "Today {0} · Total {1}",
        ["{0} 分钟 · ±{1}"] = "{0} min · ±{1}",
        ["暂停"] = "Pause",
        ["继续"] = "Resume",
        ["本轮未关联待办；选择后可在专注结束时自动完成。"] = "No todo is linked to this session; choose one to optionally complete it when focus ends.",
        ["关联待办已完成。"] = "The linked todo is already complete.",
        ["专注结束后将完成此待办。"] = "This todo will be completed when the focus session ends.",
        ["仅显示关联，不自动修改待办状态。"] = "The todo is linked for display only; its state will not be changed automatically.",
        ["{0}中"] = "{0} running",
        ["{0}已暂停"] = "{0} paused",
        ["进行中"] = "Running",
        ["已暂停"] = "Paused",
        ["待办"] = "Todo"
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
