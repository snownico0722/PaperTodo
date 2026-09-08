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
///
/// 采用两级规则，避免为 reveal 建立第二份块区间注解：
/// - 行内成对范围（强调/加粗/删除线/行内代码、HTML 开闭标签对）：控制符随 span/container 显隐——
///   只要光标落在该区间内就成对显示两端分隔符/标签，便于直接编辑。
/// - 行边界单元（标题 atx 开/闭 #、引用 &gt;、列表 -/1.、任务 [ ]、围栏行、setext、
///   分隔线、转义反斜杠）：仅当光标与该单元同处一行且光标位于该单元起点
///   之后（进入单元即显灵）才显示。围栏内容行与围栏行不在同一行，因此编辑代码内容时
///   围栏天然保持隐藏。
/// </summary>
internal static class MarkdownSemanticReveal
{
    public static bool RevealRange(
        MarkdownCaretReveal caret,
        int rangeStart,
        int rangeEnd)
    {
        // 闭区间：光标“到达可见内容的左右边界点”也视为进入该行内格式化区间，立即显灵，
        // 便于停在边界退格去格式。再外一格（真正进入相邻普通文本）才回到隐藏态。
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

    /// <summary>两端带分隔符、需整段显隐的行内 span 种类。</summary>
    public static bool IsRangeKind(MarkdownSemanticSpanKind kind)
    {
        // HtmlContainer：HTML 对（<b>…</b>/<a>…</a> 等）按成对区间整段显隐——与行内成对
        // markdown 一致；两枚 HtmlMarker 是否显灵统一由所属 container 区间判定，不再单格判定。
        return kind is MarkdownSemanticSpanKind.Emphasis or
            MarkdownSemanticSpanKind.Strong or
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode or
            MarkdownSemanticSpanKind.HtmlContainer;
    }

    /// <summary>
    /// caret 行是否至少有一个控制符显灵，作为「进入编辑态」淡入的上升沿判定。判定参数与各取色点
    /// 保持一致；漏判只使对应标记失去淡入（仍瞬显），不产生错误显示。
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

            // HTML 标签（HtmlMarker）不进“行边界单格”清单：其显灵由所属 HtmlContainer 的成对区间
            // 判定（上方 IsRangeKind 分支），避免光标停在容器右邻文本同行时被误判为“有显灵”。
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

        // 只计“带可见语法的链接”（显式 [label](url)、带 <> 的 autolink、<a> anchor）：
        // 裸链无控制符、永不显灵，不参与淡入上升沿判定。
        foreach (var link in snapshot.LinksForLine(lineZeroBased))
        {
            if (link.HasVisibleSyntax && RevealRange(caret, link.Start, link.End))
            {
                return true;
            }
        }

        // 引用 `>` 单元不进 spans，按文本逐格显灵（与 SemanticColorizer.ExplicitQuoteMarkers 规则一致）。
        if (snapshot.GetLine(lineZeroBased).IsQuoted &&
            HasRevealedQuoteCell(lineText, lineAbsStart, caret))
        {
            return true;
        }

        return false;
    }

    /// <summary>引用行是否存在已显灵的 `>` 单元（caret 位于任一 marker 起点之后）。</summary>
    private static bool HasRevealedQuoteCell(
        string lineText,
        int lineAbsStart,
        MarkdownCaretReveal caret)
    {
        foreach (var marker in MarkdownQuoteMarkers.EnumerateMarkers(lineText, 0, lineText.Length))
        {
            if (caret.CaretOffset >= lineAbsStart + marker.Start)
            {
                return true;
            }
        }

        return false;
    }
}
