namespace PaperTodo;

/// <summary>
/// A presentation-only quote prefix inserted into an AvalonEdit visual line. Offset is relative to
/// the physical source line; Text never enters the TextDocument or undo stack.
/// </summary>
internal readonly record struct MarkdownQuoteVirtualPrefix(int Offset, string Text);

internal static class MarkdownQuoteVirtualIndent
{
    /// <summary>纯逻辑测试入口；列表 marker 按源码语法识别。</summary>
    internal static bool TryCreate(
        string sourceLine,
        int quoteLevel,
        out MarkdownQuoteVirtualPrefix prefix) =>
        TryCreate(
            MarkdownContainerPrefix.Parse(sourceLine, quoteLevel),
            out prefix);

    /// <summary>
    /// 运行时入口；列表 marker 以 Markdig 语义 span 为准，引用 marker 与惰性层级由统一容器前缀读取。
    /// </summary>
    internal static bool TryCreate(
        string sourceLine,
        int quoteLevel,
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd,
        out MarkdownQuoteVirtualPrefix prefix) =>
        TryCreate(
            MarkdownContainerPrefix.Parse(
                sourceLine,
                quoteLevel,
                snapshot,
                absoluteLineStart,
                absoluteLineEnd),
            out prefix);

    private static bool TryCreate(
        MarkdownContainerPrefixInfo container,
        out MarkdownQuoteVirtualPrefix prefix)
    {
        prefix = default;
        if (container.MissingQuoteLevels <= 0)
        {
            return false;
        }

        prefix = new MarkdownQuoteVirtualPrefix(
            container.ContentStart,
            MarkdownQuoteMarkers.RepeatMarkerPrefix(container.MissingQuoteLevels));
        return true;
    }
}
