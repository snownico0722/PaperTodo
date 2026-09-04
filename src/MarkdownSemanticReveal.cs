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
/// - 行内成对范围（强调/加粗/删除线/行内代码）：控制符随 span 显隐——只要光标落在该
///   span 的 [Start, End) 内就显示其两端分隔符，便于直接编辑。
/// - 行边界单元（标题 atx 开/闭 #、引用 &gt;、列表 -/1.、任务 [ ]、围栏行、setext、
///   分隔线、HTML 标签、转义反斜杠）：仅当光标与该单元同处一行且光标位于该单元起点
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
            return rangeStart >= 0 &&
                rangeEnd > rangeStart &&
                RevealRange(caret, rangeStart, rangeEnd);
        }

        return caret.CaretLineZeroBased == markerLineZeroBased &&
            caret.CaretOffset >= markerStart;
    }

    /// <summary>两端带分隔符、需整段显隐的行内 span 种类。</summary>
    public static bool IsRangeKind(MarkdownSemanticSpanKind kind)
    {
        return kind is MarkdownSemanticSpanKind.Emphasis or
            MarkdownSemanticSpanKind.Strong or
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode;
    }
}
