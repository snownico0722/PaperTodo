using System.Globalization;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.CloudGenshin;

internal static class PluginText
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        ["云·原神"] = "Cloud Genshin",
        ["云原神 · 加载中"] = "Cloud Genshin · Loading",
        ["正在启动云·原神…"] = "Starting Cloud Genshin…",
        ["重新加载"] = "Reload",
        ["正在初始化 WebView2…"] = "Initializing WebView2…",
        ["正在加载云·原神…"] = "Loading Cloud Genshin…",
        ["网页加载失败："] = "Web page failed to load: ",
        ["云原神"] = "Cloud Genshin",
        ["WebView2 浏览器进程已退出，正在重建…"] = "The WebView2 browser process exited; rebuilding…",
        ["云原神 · 正在重启"] = "Cloud Genshin · Restarting",
        ["WebView2 渲染进程异常退出。"] = "The WebView2 render process exited unexpectedly.",
        ["云·原神加载失败"] = "Cloud Genshin failed to load",
        ["云原神 · 错误"] = "Cloud Genshin · Error",
        ["正在重新加载云·原神…"] = "Reloading Cloud Genshin…",
        ["WebView2 初始化后未返回 CoreWebView2。 "] = "WebView2 initialization returned no CoreWebView2 instance."
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
