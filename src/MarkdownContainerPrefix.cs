using System.Collections.Generic;

namespace PaperTodo;

internal enum MarkdownContainerPrefixKind
{
    Quote,
    UnorderedList,
    OrderedList
}

internal readonly record struct MarkdownContainerPrefixToken(
    MarkdownContainerPrefixKind Kind,
    int MarkerStart,
    int MarkerEnd,
    int ContentStart)
{
    public bool IsQuote => Kind == MarkdownContainerPrefixKind.Quote;

    public bool IsList => Kind is
        MarkdownContainerPrefixKind.UnorderedList or
        MarkdownContainerPrefixKind.OrderedList;
}

/// <summary>
/// 一行物理源码开头的容器映射。容器种类和嵌套顺序来自 Markdig 生成的 semantic span；
/// 本对象只记录这一行真实存在的 marker 位置，以及惰性引用缺失的显示层级。
/// </summary>
internal sealed class MarkdownContainerPrefixInfo
{
    public MarkdownContainerPrefixInfo(
        MarkdownContainerPrefixToken[] tokens,
        int contentStart,
        int explicitQuoteLevels,
        int quoteLevel,
        int taskMarkerStart,
        int taskMarkerEnd,
        int visualIndentEnd,
        int taskOwnerTokenIndex)
    {
        Tokens = tokens;
        ContentStart = contentStart;
        ExplicitQuoteLevels = explicitQuoteLevels;
        QuoteLevel = Math.Max(0, quoteLevel);
        TaskMarkerStart = taskMarkerStart;
        TaskMarkerEnd = taskMarkerEnd;
        VisualIndentEnd = visualIndentEnd;
        TaskOwnerTokenIndex = taskOwnerTokenIndex;
    }

    public IReadOnlyList<MarkdownContainerPrefixToken> Tokens { get; }

    /// <summary>真实容器前缀之后，正文或惰性引用补位开始的位置。</summary>
    public int ContentStart { get; }

    public int ExplicitQuoteLevels { get; }

    public int QuoteLevel { get; }

    public int MissingQuoteLevels => Math.Max(0, QuoteLevel - ExplicitQuoteLevels);

    public int TaskMarkerStart { get; }

    public int TaskMarkerEnd { get; }

    /// <summary>软折行应继承到的位置；任务行包含 [ ]/[x] 及其后空白。</summary>
    public int VisualIndentEnd { get; }

    /// <summary>任务 marker 对应的最近一层真实列表 token；不存在时为 -1。</summary>
    public int TaskOwnerTokenIndex { get; }

    public int InnermostTokenIndex => Tokens.Count - 1;
}

/// <summary>
/// 把 Markdig 已经确认的容器结构映射回当前物理源码行。
///
/// 这里不再自行判断“某个 -/数字是不是列表”或“容器应该怎样嵌套”：
/// - Quote / List 的存在和外内顺序来自 snapshot 中 Markdig block span 的包含关系；
/// - List marker 的种类和精确范围来自 Markdig ListItemBlock 派生的 marker span；
/// - 唯一直接查看源码字符的地方，是在 Markdig 已确认某一层为 Quote 后寻找该层真实的 `>`，
///   以及跳过真实 marker 周围的空白以得到屏幕/光标坐标。
/// </summary>
internal static class MarkdownContainerPrefix
{
    private readonly record struct SemanticContainer(
        MarkdownContainerPrefixKind Kind,
        int Start,
        int End);

    private readonly record struct SemanticListMarker(
        int Start,
        int End,
        MarkdownContainerPrefixKind Kind);

    internal static MarkdownContainerPrefixInfo Parse(
        string sourceLine,
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);
        ArgumentNullException.ThrowIfNull(snapshot);

        var lineIndex = FindLine(snapshot.LineStarts, absoluteLineStart);
        var quoteLevel = snapshot.GetLine(lineIndex).QuoteLevel;
        var containers = new List<SemanticContainer>();
        var listMarkers = new List<SemanticListMarker>();
        var taskStart = -1;
        var taskEnd = -1;

        foreach (var span in snapshot.SpansForLine(lineIndex))
        {
            switch (span.Kind)
            {
                case MarkdownSemanticSpanKind.Quote:
                    containers.Add(new SemanticContainer(
                        MarkdownContainerPrefixKind.Quote,
                        span.Start,
                        span.End));
                    break;

                case MarkdownSemanticSpanKind.UnorderedList:
                    containers.Add(new SemanticContainer(
                        MarkdownContainerPrefixKind.UnorderedList,
                        span.Start,
                        span.End));
                    break;

                case MarkdownSemanticSpanKind.OrderedList:
                    containers.Add(new SemanticContainer(
                        MarkdownContainerPrefixKind.OrderedList,
                        span.Start,
                        span.End));
                    break;

                case MarkdownSemanticSpanKind.UnorderedListMarker
                    when span.Start >= absoluteLineStart && span.End <= absoluteLineEnd:
                    listMarkers.Add(new SemanticListMarker(
                        span.Start - absoluteLineStart,
                        span.End - absoluteLineStart,
                        MarkdownContainerPrefixKind.UnorderedList));
                    break;

                case MarkdownSemanticSpanKind.OrderedListMarker
                    when span.Start >= absoluteLineStart && span.End <= absoluteLineEnd:
                    listMarkers.Add(new SemanticListMarker(
                        span.Start - absoluteLineStart,
                        span.End - absoluteLineStart,
                        MarkdownContainerPrefixKind.OrderedList));
                    break;

                case MarkdownSemanticSpanKind.TaskListMarker
                    when taskStart < 0 &&
                         span.Start >= absoluteLineStart && span.End <= absoluteLineEnd:
                    taskStart = span.Start - absoluteLineStart;
                    taskEnd = span.End - absoluteLineStart;
                    break;
            }
        }

        // 同一行上能同时覆盖的块级容器必然构成祖先链。源码起点更早的是外层；若起点相同，
        // 覆盖范围更大者是外层。这个顺序完全由 Markdig block span 决定，而不是重新解析 marker。
        containers.Sort(static (left, right) =>
        {
            var comparison = left.Start.CompareTo(right.Start);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = right.End.CompareTo(left.End);
            return comparison != 0
                ? comparison
                : left.Kind.CompareTo(right.Kind);
        });
        listMarkers.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        return MapPhysicalPrefix(
            sourceLine,
            quoteLevel,
            containers,
            listMarkers,
            taskStart,
            taskEnd);
    }

    /// <summary>
    /// 把当前物理行上的真实列表 marker 分配给覆盖该行的 Markdig 列表容器。
    /// continuation 行可能仍处于多个外层列表中，却只含最内层真实 marker；因此必须从内向外匹配，
    /// 不能让外层容器先把内层 marker 消费掉。该规则同时覆盖同类型嵌套与有序/无序混合嵌套。
    /// </summary>
    private static SemanticListMarker?[] AssignListMarkers(
        IReadOnlyList<SemanticContainer> containers,
        IReadOnlyList<SemanticListMarker> listMarkers)
    {
        var assigned = new SemanticListMarker?[containers.Count];
        var containerIndex = containers.Count - 1;
        for (var markerIndex = listMarkers.Count - 1; markerIndex >= 0; markerIndex--)
        {
            var marker = listMarkers[markerIndex];
            for (; containerIndex >= 0; containerIndex--)
            {
                var container = containers[containerIndex];
                if (container.Kind is not (
                        MarkdownContainerPrefixKind.UnorderedList or
                        MarkdownContainerPrefixKind.OrderedList) ||
                    container.Kind != marker.Kind)
                {
                    continue;
                }

                assigned[containerIndex] = marker;
                containerIndex--;
                break;
            }
        }

        return assigned;
    }

    private static MarkdownContainerPrefixInfo MapPhysicalPrefix(
        string sourceLine,
        int quoteLevel,
        IReadOnlyList<SemanticContainer> containers,
        IReadOnlyList<SemanticListMarker> listMarkers,
        int taskMarkerStart,
        int taskMarkerEnd)
    {
        var tokens = new List<MarkdownContainerPrefixToken>();
        var index = 0;
        var explicitQuoteLevels = 0;
        var assignedListMarkers = AssignListMarkers(containers, listMarkers);

        for (var containerIndex = 0; containerIndex < containers.Count; containerIndex++)
        {
            var semanticContainer = containers[containerIndex];
            index = SkipWhitespace(sourceLine, index);

            if (semanticContainer.Kind == MarkdownContainerPrefixKind.Quote)
            {
                // Markdig 已确认这一层是 Quote。这里只定位真实 `>`；没有则是合法惰性续行，
                // 保持 index 不越过正文，由 MissingQuoteLevels 在显示层补位。
                if (index < sourceLine.Length && sourceLine[index] == '>')
                {
                    var quoteMarkerStart = index;
                    var markerEnd = ++index;
                    if (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
                    {
                        index++;
                    }

                    tokens.Add(new MarkdownContainerPrefixToken(
                        MarkdownContainerPrefixKind.Quote,
                        quoteMarkerStart,
                        markerEnd,
                        index));
                    explicitQuoteLevels++;
                }

                continue;
            }

            // continuation row 没有当前容器自己的真实 marker 时不猜；真实 marker 已在上面按
            // Markdig 容器层级从内向外分配，避免外层 continuation 容器吃掉内层 marker。
            if (assignedListMarkers[containerIndex] is not { } marker || marker.Start != index)
            {
                continue;
            }

            var markerStart = marker.Start;
            index = Math.Clamp(marker.End, markerStart, sourceLine.Length);
            index = SkipWhitespace(sourceLine, index);
            tokens.Add(new MarkdownContainerPrefixToken(
                semanticContainer.Kind,
                markerStart,
                marker.End,
                index));
        }

        var contentStart = index;
        var visualIndentEnd = contentStart;
        var taskOwnerTokenIndex = -1;
        if (taskMarkerStart == contentStart &&
            taskMarkerEnd > taskMarkerStart &&
            taskMarkerEnd <= sourceLine.Length)
        {
            for (var tokenIndex = tokens.Count - 1; tokenIndex >= 0; tokenIndex--)
            {
                if (tokens[tokenIndex].IsList &&
                    tokens[tokenIndex].MarkerStart < taskMarkerStart)
                {
                    taskOwnerTokenIndex = tokenIndex;
                    break;
                }
            }

            visualIndentEnd = SkipWhitespace(sourceLine, taskMarkerEnd);
        }
        else
        {
            taskMarkerStart = -1;
            taskMarkerEnd = -1;
        }

        return new MarkdownContainerPrefixInfo(
            tokens.ToArray(),
            contentStart,
            explicitQuoteLevels,
            quoteLevel,
            taskMarkerStart,
            taskMarkerEnd,
            visualIndentEnd,
            taskOwnerTokenIndex);
    }

    /// <summary>
    /// 构造显示层的逻辑前缀：列表 marker 中的非空白源码字符按等量空格计宽，引用 marker 与真实
    /// 空白保持原样。这样 `10. ` 与其 continuation 的四空格拥有相同逻辑宽度，同时不修改原文。
    /// </summary>
    internal static string BuildLogicalVisualPrefix(
        string sourceLine,
        MarkdownContainerPrefixInfo container,
        int endExclusive)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);
        ArgumentNullException.ThrowIfNull(container);

        var end = Math.Clamp(endExclusive, 0, sourceLine.Length);
        if (end == 0)
        {
            return string.Empty;
        }

        var chars = sourceLine[..end].ToCharArray();
        foreach (var token in container.Tokens)
        {
            if (!token.IsList || token.MarkerStart >= end)
            {
                continue;
            }

            var markerEnd = Math.Min(end, token.MarkerEnd);
            for (var index = Math.Max(0, token.MarkerStart); index < markerEnd; index++)
            {
                if (!char.IsWhiteSpace(chars[index]))
                {
                    chars[index] = ' ';
                }
            }
        }

        return new string(chars);
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = Math.Clamp(start, 0, text.Length);
        while (index < text.Length && text[index] is ' ' or '\t')
        {
            index++;
        }

        return index;
    }

    private static int FindLine(int[] lineStarts, int offset)
    {
        if (lineStarts.Length == 0)
        {
            return 0;
        }

        var normalized = Math.Clamp(offset, 0, lineStarts[^1]);
        var index = Array.BinarySearch(lineStarts, normalized);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
