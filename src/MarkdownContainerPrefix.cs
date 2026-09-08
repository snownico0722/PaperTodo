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
/// 一行物理源码开头的容器结构。只描述真实存在的空白、列表 marker、引用 marker 与任务 marker；
/// 缺少的惰性引用层级由 MissingQuoteLevels 表示，不写回 TextDocument。
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

    /// <summary>所有已识别真实容器 marker 之后，正文或惰性引用补位应该开始的位置。</summary>
    public int ContentStart { get; }

    public int ExplicitQuoteLevels { get; }

    public int QuoteLevel { get; }

    public int MissingQuoteLevels => Math.Max(0, QuoteLevel - ExplicitQuoteLevels);

    public int TaskMarkerStart { get; }

    public int TaskMarkerEnd { get; }

    /// <summary>软折行应继承到的位置；任务行包含 [ ]/[x] 及其后空白。</summary>
    public int VisualIndentEnd { get; }

    /// <summary>任务 marker 对应的最近一层列表 token；不存在时为 -1。</summary>
    public int TaskOwnerTokenIndex { get; }

    public int InnermostTokenIndex => Tokens.Count - 1;
}

/// <summary>
/// 统一读取 Markdown 行首容器。显示缩进、引用 marker、引用竖线、列表/引用 Enter、任务框归属与
/// 容器内横线都应复用这一结果，避免各自维护一套“行首是什么”的判断。
/// </summary>
internal static class MarkdownContainerPrefix
{
    private readonly record struct SemanticListMarker(
        int Start,
        int End,
        MarkdownContainerPrefixKind Kind);

    internal static MarkdownContainerPrefixInfo Parse(
        string sourceLine,
        int quoteLevel) =>
        ParseCore(
            sourceLine,
            quoteLevel,
            semanticListMarkers: null,
            taskMarkerStart: -1,
            taskMarkerEnd: -1);

    internal static MarkdownContainerPrefixInfo Parse(
        string sourceLine,
        int quoteLevel,
        MarkdownSemanticSnapshot snapshot,
        int absoluteLineStart,
        int absoluteLineEnd)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var listMarkers = new List<SemanticListMarker>();
        var taskStart = -1;
        var taskEnd = -1;
        var lineIndex = FindLine(snapshot.LineStarts, absoluteLineStart);
        foreach (var span in snapshot.SpansForLine(lineIndex))
        {
            if (span.Start < absoluteLineStart || span.End > absoluteLineEnd)
            {
                continue;
            }

            if (span.Kind is MarkdownSemanticSpanKind.UnorderedListMarker or
                MarkdownSemanticSpanKind.OrderedListMarker)
            {
                listMarkers.Add(new SemanticListMarker(
                    span.Start - absoluteLineStart,
                    span.End - absoluteLineStart,
                    span.Kind == MarkdownSemanticSpanKind.UnorderedListMarker
                        ? MarkdownContainerPrefixKind.UnorderedList
                        : MarkdownContainerPrefixKind.OrderedList));
            }
            else if (taskStart < 0 && span.Kind == MarkdownSemanticSpanKind.TaskListMarker)
            {
                taskStart = span.Start - absoluteLineStart;
                taskEnd = span.End - absoluteLineStart;
            }
        }

        return ParseCore(sourceLine, quoteLevel, listMarkers, taskStart, taskEnd);
    }

    private static MarkdownContainerPrefixInfo ParseCore(
        string sourceLine,
        int quoteLevel,
        IReadOnlyList<SemanticListMarker>? semanticListMarkers,
        int taskMarkerStart,
        int taskMarkerEnd)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);
        var tokens = new List<MarkdownContainerPrefixToken>();
        var index = 0;
        var explicitQuoteLevels = 0;
        var semanticListIndex = 0;

        while (index < sourceLine.Length)
        {
            while (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
            {
                index++;
            }

            if (explicitQuoteLevels < quoteLevel &&
                index < sourceLine.Length &&
                sourceLine[index] == '>')
            {
                var quoteMarkerStart = index;
                index++;
                var quoteMarkerEnd = index;
                if (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
                {
                    index++;
                }

                tokens.Add(new MarkdownContainerPrefixToken(
                    MarkdownContainerPrefixKind.Quote,
                    quoteMarkerStart,
                    quoteMarkerEnd,
                    index));
                explicitQuoteLevels++;
                continue;
            }

            while (semanticListMarkers != null &&
                   semanticListIndex < semanticListMarkers.Count &&
                   semanticListMarkers[semanticListIndex].Start < index)
            {
                semanticListIndex++;
            }

            MarkdownContainerPrefixKind listKind;
            int listMarkerEnd;
            if (semanticListMarkers != null)
            {
                if (semanticListIndex >= semanticListMarkers.Count ||
                    semanticListMarkers[semanticListIndex].Start != index)
                {
                    break;
                }

                var marker = semanticListMarkers[semanticListIndex++];
                listKind = marker.Kind;
                listMarkerEnd = marker.End;
            }
            else if (!TryReadListMarker(sourceLine, index, out listKind, out listMarkerEnd))
            {
                break;
            }

            var listMarkerStart = index;
            index = listMarkerEnd;
            while (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
            {
                index++;
            }

            tokens.Add(new MarkdownContainerPrefixToken(
                listKind,
                listMarkerStart,
                listMarkerEnd,
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

            visualIndentEnd = taskMarkerEnd;
            while (visualIndentEnd < sourceLine.Length &&
                   sourceLine[visualIndentEnd] is ' ' or '\t')
            {
                visualIndentEnd++;
            }
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

    private static bool TryReadListMarker(
        string sourceLine,
        int start,
        out MarkdownContainerPrefixKind kind,
        out int markerEnd)
    {
        kind = default;
        markerEnd = start;
        if (start < 0 || start >= sourceLine.Length)
        {
            return false;
        }

        if (sourceLine[start] is '-' or '+' or '*')
        {
            kind = MarkdownContainerPrefixKind.UnorderedList;
            markerEnd = start + 1;
        }
        else if (char.IsAsciiDigit(sourceLine[start]))
        {
            var digits = 0;
            markerEnd = start;
            while (markerEnd < sourceLine.Length &&
                   digits < 9 &&
                   char.IsAsciiDigit(sourceLine[markerEnd]))
            {
                markerEnd++;
                digits++;
            }

            if (markerEnd < sourceLine.Length &&
                char.IsAsciiDigit(sourceLine[markerEnd]))
            {
                return false;
            }
            if (markerEnd >= sourceLine.Length ||
                sourceLine[markerEnd] is not ('.' or ')'))
            {
                return false;
            }

            markerEnd++;
            kind = MarkdownContainerPrefixKind.OrderedList;
        }
        else
        {
            return false;
        }

        return markerEnd >= sourceLine.Length ||
            sourceLine[markerEnd] is ' ' or '\t';
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
