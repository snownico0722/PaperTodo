using System.Globalization;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.SampleClock;

internal static class PluginText
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        ["时钟"] = "Clock",
        ["本地时间"] = "Local time",
        ["北京时间"] = "Beijing time",
        ["东京时间"] = "Tokyo time",
        ["伦敦时间"] = "London time",
        ["纽约时间"] = "New York time",
        ["洛杉矶时间"] = "Los Angeles time"
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
