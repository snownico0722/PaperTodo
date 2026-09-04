namespace PaperTodo;

/// <summary>一段需要在 Full 档从布局中“塌缩”的源码区间（控制符不参与排版、其余内容重排）。</summary>
internal readonly record struct MarkdownCollapseRun(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// 计算 Full（WYSIWYG 块级编辑态）下应当真塌缩的源码区间——纯逻辑、无 WPF 依赖，可被
/// MarkdownSemanticChecks 直接链接测试。
///
/// 仅收窄覆盖“隐藏后不需要留白/缩进”的控制符：ATX 标题标记（开/闭 #）、行内成对分隔符
/// （** / * / ~~ / 反引号）、行内链接语法（[label](url) 的非 label 部分）、HTML 标签、转义
/// 反斜杠。列表/任务标记、引用 &gt;、围栏行、setext、分隔线等需要保留视觉格子/行高的不在此列。
///
/// 已被光标“显灵”（活动块）的区间不塌缩，返回给渲染层以源码形态呈现。
/// </summary>
internal static class MarkdownSemanticCollapseLayout
{
    /// <summary>按文档绝对偏移排序、互不重叠（已合并）的塌缩区间。</summary>
    public static IReadOnlyList<MarkdownCollapseRun> ComputeCollapsedRuns(
        MarkdownSemanticSnapshot snapshot,
        string? source,
        MarkdownCaretReveal caret)
    {
        source ??= string.Empty;
        if (snapshot.Spans.Count == 0 && snapshot.Links.Count == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        var lineStarts = BuildLineStarts(source);
        if (lineStarts.Length == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        var runs = new List<MarkdownCollapseRun>();
        foreach (var span in snapshot.Spans)
        {
            switch (span.Kind)
            {
                case MarkdownSemanticSpanKind.Heading:
                    CollectAtxHeading(span, source, lineStarts, caret, runs);
                    break;

                case MarkdownSemanticSpanKind.Emphasis:
                case MarkdownSemanticSpanKind.Strong:
                case MarkdownSemanticSpanKind.Strikethrough:
                case MarkdownSemanticSpanKind.InlineCode:
                    CollectInlineDelimiters(span, source, lineStarts, caret, runs);
                    break;

                case MarkdownSemanticSpanKind.HtmlMarker:
                case MarkdownSemanticSpanKind.EscapeMarker:
                    CollectCell(span, source, lineStarts, caret, runs);
                    break;
            }
        }

        foreach (var link in snapshot.Links)
        {
            if (link.IsAuto)
            {
                continue;
            }

            if (MarkdownSemanticReveal.RevealRange(caret, link.Start, link.End))
            {
                continue;
            }

            AddIfSingleLine(lineStarts, source.Length, link.Start, link.LabelStart, runs);
            AddIfSingleLine(lineStarts, source.Length, link.LabelEnd, link.End, runs);
        }

        if (runs.Count == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        runs.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<MarkdownCollapseRun>(runs.Count);
        foreach (var run in runs)
        {
            if (merged.Count == 0)
            {
                merged.Add(run);
                continue;
            }

            var previous = merged[^1];
            if (run.Start <= previous.End)
            {
                merged[^1] = new MarkdownCollapseRun(previous.Start, Math.Max(previous.End, run.End));
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }

    /// <summary>ATX 标题：开标记 `#…` + 后随空格、以及行尾闭合 `#…`（若有）。</summary>
    private static void CollectAtxHeading(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts,
        MarkdownCaretReveal caret,
        List<MarkdownCollapseRun> runs)
    {
        if (span.Length <= 0)
        {
            return;
        }

        var line = FindLine(lineStarts, span.Start);
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : source.Length;

        // 开标记：从 '#' 起到其后空白结束，作为内容起点。
        var contentStart = span.Start;
        while (contentStart < source.Length && source[contentStart] == '#')
        {
            contentStart++;
        }
        while (contentStart < source.Length && (source[contentStart] == ' ' || source[contentStart] == '\t'))
        {
            contentStart++;
        }
        if (contentStart <= span.Start)
        {
            return;
        }

        // 闭合标记：标题内容之后的尾部 `#…`（被前导空白分隔，且位于行尾）。
        // ATX 标题是单行叶块，按整行扫描（Markdig 的 span.End 可能不含闭合标记）。
        var contentEnd = lineEnd;
        while (contentEnd > contentStart && char.IsWhiteSpace(source[contentEnd - 1]))
        {
            contentEnd--;
        }
        var closingStart = contentEnd;
        while (closingStart > contentStart && source[closingStart - 1] == '#')
        {
            closingStart--;
        }
        var hasClosing = closingStart < contentEnd &&
            closingStart > contentStart &&
            char.IsWhiteSpace(source[closingStart - 1]);

        var revealed = MarkdownSemanticReveal.RevealMarker(
            caret,
            line,
            span.Start,
            contentStart - span.Start,
            MarkdownSemanticSpanKind.Heading);
        if (!revealed)
        {
            runs.Add(new MarkdownCollapseRun(span.Start, contentStart));
            if (hasClosing)
            {
                runs.Add(new MarkdownCollapseRun(closingStart, contentEnd));
            }
        }
    }

    /// <summary>行内成对分隔符：前/后两段（**、*、~~、反引号）。</summary>
    private static void CollectInlineDelimiters(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts,
        MarkdownCaretReveal caret,
        List<MarkdownCollapseRun> runs)
    {
        var line = FindLine(lineStarts, span.Start);
        var revealed = MarkdownSemanticReveal.RevealMarker(
            caret,
            line,
            span.Start,
            span.Length,
            span.Kind,
            span.Start,
            span.End);
        if (revealed)
        {
            return;
        }

        var markerLength = Math.Clamp(span.MarkerLength, 1, Math.Max(1, span.Length / 2));
        AddIfSingleLine(lineStarts, source.Length, span.Start, span.Start + markerLength, runs);
        AddIfSingleLine(lineStarts, source.Length, span.End - markerLength, span.End, runs);
    }

    /// <summary>整段塌缩的单格标记（HTML 标签、转义反斜杠）。</summary>
    private static void CollectCell(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts,
        MarkdownCaretReveal caret,
        List<MarkdownCollapseRun> runs)
    {
        var line = FindLine(lineStarts, span.Start);
        var revealed = MarkdownSemanticReveal.RevealMarker(
            caret,
            line,
            span.Start,
            span.Length,
            span.Kind);
        if (!revealed)
        {
            runs.Add(new MarkdownCollapseRun(span.Start, span.End));
        }
    }

    private static void AddIfSingleLine(
        int[] lineStarts,
        int sourceLength,
        int start,
        int end,
        List<MarkdownCollapseRun> runs)
    {
        if (end <= start)
        {
            return;
        }

        if (FindLine(lineStarts, start) != FindLine(lineStarts, Math.Min(end - 1, sourceLength - 1)))
        {
            return;
        }

        runs.Add(new MarkdownCollapseRun(start, end));
    }

    private static int FindLine(int[] lineStarts, int offset)
    {
        var normalized = Math.Clamp(offset, 0, lineStarts.Length == 0 ? 0 : lineStarts[^1]);
        var index = Array.BinarySearch(lineStarts, normalized);
        if (index >= 0)
        {
            return index;
        }

        return Math.Max(0, ~index - 1);
    }

    private static int[] BuildLineStarts(string source)
    {
        var starts = new List<int>(Math.Max(1, source.Length / 32)) { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
                starts.Add(index + 1);
            }
            else if (source[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }
}
