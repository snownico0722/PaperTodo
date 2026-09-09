namespace PaperTodo;

/// <summary>
/// 当前光标所在的（文档绝对偏移 + 零基行号）。预览态或模式关闭时使用 <see cref="None"/>，
/// 此时所有控制符都不显灵。
/// </summary>
internal readonly record struct MarkdownCaretReveal(int CaretOffset, int CaretLineZeroBased)
{
    public static MarkdownCaretReveal None { get; } = new(-1, -1);

    public bool Active => CaretOffset >= 0 && CaretLineZeroBased >= 0;
}

/// <summary>
/// 「Full（WYSIWYG 块级编辑态）」档下控制符是否显灵的纯判定，与 WPF 无关，可被
/// MarkdownSemanticChecks 直接链接测试。
/// </summary>
internal static class MarkdownSemanticReveal
{
    public static bool RevealRange(
        MarkdownCaretReveal caret,
        int rangeStart,
        int rangeEnd)
    {
        // 闭区间：光标“到达可见内容的左右边界点”也视为进入该行内格式化区间。
        return caret.Active &&
            rangeStart >= 0 &&
            rangeEnd > rangeStart &&
            caret.CaretOffset >= rangeStart &&
            caret.CaretOffset <= rangeEnd;
    }

    public static bool RevealMarker(
        MarkdownCaretReveal caret,
        int markerLineZeroBased,
        int markerStart,
        int markerLength,
        MarkdownSemanticSpanKind kind,
        int rangeStart = -1,
        int rangeEnd = -1)
    {
        if (!caret.Active || markerLength <= 0)
        {
            return false;
        }

        if (IsRangeKind(kind))
        {
            return RevealRange(caret, rangeStart, rangeEnd);
        }

        return caret.CaretLineZeroBased == markerLineZeroBased &&
            caret.CaretOffset >= markerStart;
    }

    /// <summary>两端带分隔符、需整段显隐的行内或块级 span 种类。</summary>
    public static bool IsRangeKind(MarkdownSemanticSpanKind kind)
    {
        return kind is MarkdownSemanticSpanKind.Emphasis or
            MarkdownSemanticSpanKind.Strong or
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode or
            MarkdownSemanticSpanKind.InlineMath or
            MarkdownSemanticSpanKind.BlockMath or
            MarkdownSemanticSpanKind.HtmlContainer;
    }

    /// <summary>
    /// caret 行是否至少有一个控制符显灵，作为「进入编辑态」淡入的上升沿判定。
    /// </summary>
    public static bool HasRevealOnLine(
        MarkdownSemanticSnapshot snapshot,
        string lineText,
        int lineAbsStart,
        int lineZeroBased,
        MarkdownCaretReveal caret)
    {
        if (!caret.Active || caret.CaretLineZeroBased != lineZeroBased)
        {
            return false;
        }

        foreach (var span in snapshot.SpansForLine(lineZeroBased))
        {
            if (span.Length <= 0)
            {
                continue;
            }

            if (IsRangeKind(span.Kind))
            {
                if (RevealRange(caret, span.Start, span.End))
                {
                    return true;
                }

                continue;
            }

            if ((span.Kind is MarkdownSemanticSpanKind.Heading or
                    MarkdownSemanticSpanKind.FencedCodeOpening or
                    MarkdownSemanticSpanKind.FencedCodeClosing or
                    MarkdownSemanticSpanKind.SetextMarker or
                    MarkdownSemanticSpanKind.HorizontalRule or
                    MarkdownSemanticSpanKind.UnorderedListMarker or
                    MarkdownSemanticSpanKind.OrderedListMarker or
                    MarkdownSemanticSpanKind.TaskListMarker or
                    MarkdownSemanticSpanKind.EscapeMarker) &&
                RevealMarker(caret, lineZeroBased, span.Start, span.Length, span.Kind))
            {
                return true;
            }
        }

        foreach (var link in snapshot.LinksForLine(lineZeroBased))
        {
            if (link.HasVisibleSyntax && RevealRange(caret, link.Start, link.End))
            {
                return true;
            }
        }

        if (snapshot.GetLine(lineZeroBased).IsQuoted &&
            HasRevealedQuoteCell(
                snapshot,
                lineText,
                lineAbsStart,
                lineZeroBased,
                caret))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 引用行是否存在已显灵的真实 `>` 单元。统一容器前缀可识别列表之后的嵌套引用 marker。
    /// </summary>
    private static bool HasRevealedQuoteCell(
        MarkdownSemanticSnapshot snapshot,
        string lineText,
        int lineAbsStart,
        int lineZeroBased,
        MarkdownCaretReveal caret)
    {
        var container = MarkdownContainerPrefix.Parse(
            lineText,
            snapshot,
            lineAbsStart,
            lineAbsStart + lineText.Length);
        foreach (var token in container.Tokens)
        {
            if (token.IsQuote &&
                caret.CaretOffset >= lineAbsStart + token.MarkerStart)
            {
                return true;
            }
        }

        return false;
    }
}
