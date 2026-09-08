namespace PaperTodo;

/// <summary>
/// 链接隐藏区间与转义反斜杠的唯一归属判断。位于 label 外侧链接语法格中的反斜杠由整条链接
/// 负责显隐；label 内的反斜杠仍作为独立控制符处理。
/// </summary>
internal static class MarkdownLinkEscapeOwnership
{
    internal static bool TryGetOwningLink(
        MarkdownSemanticSpan escape,
        MarkdownSemanticSnapshot snapshot,
        string source,
        out MarkdownSemanticLink link)
    {
        link = default;
        if (escape.Kind != MarkdownSemanticSpanKind.EscapeMarker ||
            escape.Length <= 0 ||
            !snapshot.TryGetLinkAtOffset(escape.Start, out var candidate) ||
            !candidate.HasVisibleSyntax)
        {
            return false;
        }

        var openingOwned =
            IsSingleLineCell(snapshot.LineStarts, source.Length, candidate.Start, candidate.LabelStart) &&
            escape.Start >= candidate.Start &&
            escape.End <= candidate.LabelStart;
        var closingOwned =
            IsSingleLineCell(snapshot.LineStarts, source.Length, candidate.LabelEnd, candidate.End) &&
            escape.Start >= candidate.LabelEnd &&
            escape.End <= candidate.End;
        if (!openingOwned && !closingOwned)
        {
            return false;
        }

        link = candidate;
        return true;
    }

    private static bool IsSingleLineCell(
        int[] lineStarts,
        int sourceLength,
        int start,
        int end)
    {
        if (end <= start)
        {
            return true;
        }
        if (lineStarts.Length == 0 || sourceLength <= 0)
        {
            return false;
        }

        return FindLine(lineStarts, start) ==
            FindLine(lineStarts, Math.Min(end - 1, sourceLength - 1));
    }

    private static int FindLine(int[] lineStarts, int offset)
    {
        var normalized = Math.Clamp(offset, 0, lineStarts[^1]);
        var index = Array.BinarySearch(lineStarts, normalized);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
