namespace PaperTodo;

/// <summary>
/// 一次局部增量解析实际用于 splice 的语义密封窗口：[OldStart,OldEnd) 是旧源上被替换的区间，
/// [NewStart,NewEnd) 是新源上替换成的区间（NewStart==OldStart）。窗口扩张稳定后无任何旧
/// snapshot 的 span/link 与窗口部分重叠；后缀平移量 = NewEnd-OldEnd = 新旧全文长度差。
/// </summary>
internal readonly record struct MarkdownSemanticIncrementalWindow(
    int OldStart,
    int OldEnd,
    int NewStart,
    int NewEnd);

internal sealed partial class MarkdownSemanticSnapshot
{
    private const int IncrementalWindowChars = 1024;

    /// <summary>同 <see cref="TryParseIncrementalLocal"/>，丢弃语义窗口（历史调用方用）。</summary>
    internal static bool TryParseIncremental(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        out MarkdownSemanticSnapshot snapshot) =>
        TryParseIncrementalLocal(oldSource, oldSnapshot, newSource, out snapshot, out _);

    /// <summary>
    /// Re-parses one local window around an edit. The 1K target may expand to complete semantics
    /// already known by the previous full snapshot. Fence-marker edits cheaply propagate the
    /// window until old/new fenced-code state converges. Other long-range Markdown constructs
    /// remain best-effort. Obvious global reference dependencies decline the local path so the
    /// caller can synchronously parse the full document. 成功时 out 语义窗口供折叠表做局部 rebase。
    /// </summary>
    internal static bool TryParseIncrementalLocal(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        out MarkdownSemanticSnapshot snapshot,
        out MarkdownSemanticIncrementalWindow window)
    {
        ArgumentNullException.ThrowIfNull(oldSource);
        ArgumentNullException.ThrowIfNull(oldSnapshot);
        ArgumentNullException.ThrowIfNull(newSource);

        snapshot = null!;
        window = default;

        if (ReferenceEquals(oldSource, newSource))
        {
            snapshot = oldSnapshot;
            return true;
        }

        if (MarkdownMathIncremental.ChangeMayAffectDelimiterState(
                oldSource,
                oldSnapshot,
                newSource))
        {
            return false;
        }

        FindContiguousDifference(
            oldSource,
            newSource,
            out var changedStart,
            out var oldChangedEnd,
            out var newChangedEnd);

        if (changedStart == oldSource.Length &&
            changedStart == newSource.Length)
        {
            snapshot = oldSnapshot;
            return true;
        }

        if (RangeContainsReferenceDependency(
                oldSource,
                changedStart,
                oldChangedEnd,
                oldSnapshot._hasReferenceDefinitions) ||
            RangeContainsReferenceDependency(
                newSource,
                changedStart,
                newChangedEnd,
                oldSnapshot._hasReferenceDefinitions))
        {
            return false;
        }

        var delta = newSource.Length - oldSource.Length;
        if (!TryBuildIncrementalWindow(
                oldSource,
                oldSnapshot,
                newSource,
                changedStart,
                oldChangedEnd,
                newChangedEnd,
                delta,
                out var oldStart,
                out var oldEnd,
                out var newStart,
                out var newEnd))
        {
            return false;
        }

        // A local parse has no access to distant reference definitions. If the old document has
        // definitions, do not replace any already-resolved link that happens to fall inside this
        // window with a locally unresolved result.
        if (oldSnapshot._hasReferenceDefinitions &&
            WindowOverlapsLink(oldSnapshot._links, oldStart, oldEnd))
        {
            return false;
        }

        var local = Parse(newSource[newStart..newEnd]);
        if (local._hasReferenceDefinitions)
        {
            // Creating or touching a definition can affect arbitrarily distant reference uses.
            return false;
        }

        if (oldStart == 0 &&
            oldEnd == oldSource.Length &&
            newStart == 0 &&
            newEnd == newSource.Length)
        {
            window = new MarkdownSemanticIncrementalWindow(oldStart, oldEnd, newStart, newEnd);
            snapshot = local;
            return true;
        }

        var spans = SpliceSpans(
            oldSnapshot._spans,
            local._spans,
            oldStart,
            oldEnd,
            newStart,
            delta);
        var links = SpliceLinks(
            oldSnapshot._links,
            local._links,
            oldStart,
            oldEnd,
            newStart,
            delta);

        var lineStarts = BuildLineStarts(newSource);
        var lines = new MarkdownSemanticLine[lineStarts.Length];
        foreach (var span in spans)
        {
            ApplySpanToLines(newSource, lineStarts, lines, span);
        }

        snapshot = new MarkdownSemanticSnapshot(
            lineStarts,
            lines,
            spans,
            links,
            BuildSpanLineIndex(newSource, lineStarts, spans),
            BuildLinkLineIndex(newSource, lineStarts, links),
            oldSnapshot._hasReferenceDefinitions);
        window = new MarkdownSemanticIncrementalWindow(oldStart, oldEnd, newStart, newEnd);
        return true;
    }

    private static void FindContiguousDifference(
        string oldSource,
        string newSource,
        out int start,
        out int oldEnd,
        out int newEnd)
    {
        var sharedLength = Math.Min(oldSource.Length, newSource.Length);
        start = 0;
        while (start < sharedLength && oldSource[start] == newSource[start])
        {
            start++;
        }

        oldEnd = oldSource.Length;
        newEnd = newSource.Length;
        while (oldEnd > start &&
               newEnd > start &&
               oldSource[oldEnd - 1] == newSource[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
    }

    private static bool TryBuildIncrementalWindow(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource,
        int changedStart,
        int oldChangedEnd,
        int newChangedEnd,
        int delta,
        out int oldStart,
        out int oldEnd,
        out int newStart,
        out int newEnd)
    {
        var leftBudget = IncrementalWindowChars / 2;
        var rightBudget = IncrementalWindowChars - leftBudget;
        oldStart = FindLineStart(oldSource, Math.Max(0, changedStart - leftBudget));
        oldEnd = FindLineEndExclusive(
            oldSource,
            Math.Min(oldSource.Length, Math.Max(oldChangedEnd, changedStart + rightBudget)));

        ExpandWindowToCompleteOldSemantics(
            oldSource,
            oldSnapshot,
            ref oldStart,
            ref oldEnd);

        if (oldStart > changedStart || oldEnd < oldChangedEnd)
        {
            newStart = 0;
            newEnd = 0;
            return false;
        }

        newStart = oldStart;
        newEnd = oldEnd == oldSource.Length
            ? newSource.Length
            : oldEnd + delta;
        if (newStart < 0 ||
            newStart > changedStart ||
            newEnd < newChangedEnd ||
            newEnd > newSource.Length)
        {
            return false;
        }

        if (ChangeMayAffectFenceState(
                oldSource,
                newSource,
                changedStart,
                oldChangedEnd,
                newChangedEnd))
        {
            ExpandWindowForFenceStateChange(
                oldSource,
                newSource,
                oldStart,
                newStart,
                ref oldEnd,
                ref newEnd);

            // Fence-state convergence can land inside another semantic container from the old
            // snapshot. Close the window again before splicing so a container whose start is
            // replaced is never preserved only in part.
            ExpandWindowToCompleteOldSemantics(
                oldSource,
                oldSnapshot,
                ref oldStart,
                ref oldEnd);
            newStart = oldStart;
            newEnd = oldEnd == oldSource.Length
                ? newSource.Length
                : oldEnd + delta;
            if (newStart < 0 ||
                newStart > changedStart ||
                newEnd < newChangedEnd ||
                newEnd > newSource.Length)
            {
                return false;
            }
        }

        return true;
    }

    private static void ExpandWindowToCompleteOldSemantics(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        ref int oldStart,
        ref int oldEnd)
    {
        while (true)
        {
            var previousStart = oldStart;
            var previousEnd = oldEnd;

            ExpandToOverlappingSemantics(oldSnapshot._spans, ref oldStart, ref oldEnd);
            ExpandToOverlappingSemantics(oldSnapshot._links, ref oldStart, ref oldEnd);
            oldStart = FindLineStart(oldSource, oldStart);
            oldEnd = AlignLineEndExclusive(oldSource, oldEnd);

            if (oldStart == previousStart && oldEnd == previousEnd)
            {
                return;
            }
        }
    }

    private static bool ChangeMayAffectFenceState(
        string oldSource,
        string newSource,
        int changedStart,
        int oldChangedEnd,
        int newChangedEnd) =>
        RangeContainsPotentialFenceLine(oldSource, changedStart, oldChangedEnd) ||
        RangeContainsPotentialFenceLine(newSource, changedStart, newChangedEnd);

    private static bool RangeContainsPotentialFenceLine(string source, int start, int end)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var normalizedStart = Math.Clamp(start, 0, source.Length);
        var normalizedEnd = Math.Clamp(end, normalizedStart, source.Length);
        var cursor = FindLineStart(source, Math.Max(0, normalizedStart - 1));
        var limit = FindLineEndExclusive(source, normalizedEnd);
        if (limit < source.Length)
        {
            limit = FindLineEndExclusive(source, limit);
        }

        while (cursor < limit)
        {
            var delimiter = FindLineDelimiterStart(source, cursor);
            if (MarkdownFencedCodeScanner.ClassifyLine(
                    source.AsSpan(cursor, delimiter - cursor),
                    default,
                    out _) == MarkdownFenceLineKind.Opening)
            {
                return true;
            }

            var next = FindLineEndExclusive(source, cursor);
            if (next <= cursor)
            {
                break;
            }
            cursor = next;
        }

        return false;
    }

    private static void ExpandWindowForFenceStateChange(
        string oldSource,
        string newSource,
        int oldStart,
        int newStart,
        ref int oldEnd,
        ref int newEnd)
    {
        var stateBeforeWindow = ScanFenceState(oldSource, 0, oldStart, default);
        var oldState = ScanFenceState(oldSource, oldStart, oldEnd, stateBeforeWindow);
        var newState = ScanFenceState(newSource, newStart, newEnd, stateBeforeWindow);

        // oldEnd/newEnd are corresponding line boundaries in the unchanged suffix. Once states
        // match there, identical remaining source keeps them matched. Otherwise walk that suffix
        // until they converge; if they never do, the window naturally reaches EOF.
        while (!oldState.Equals(newState) && newEnd < newSource.Length)
        {
            var delimiter = FindLineDelimiterStart(newSource, newEnd);
            var line = newSource.AsSpan(newEnd, delimiter - newEnd);
            _ = MarkdownFencedCodeScanner.ClassifyLine(line, oldState, out oldState);
            _ = MarkdownFencedCodeScanner.ClassifyLine(line, newState, out newState);

            var next = FindLineEndExclusive(newSource, newEnd);
            if (next <= newEnd)
            {
                oldEnd = oldSource.Length;
                newEnd = newSource.Length;
                break;
            }

            var advance = next - newEnd;
            oldEnd = Math.Min(oldSource.Length, oldEnd + advance);
            newEnd = next;
        }
    }

    private static MarkdownFencedCodeState ScanFenceState(
        string source,
        int start,
        int end,
        MarkdownFencedCodeState state)
    {
        var cursor = Math.Clamp(start, 0, source.Length);
        var limit = Math.Clamp(end, cursor, source.Length);
        while (cursor < limit)
        {
            var delimiter = FindLineDelimiterStart(source, cursor);
            var lineEnd = Math.Min(delimiter, limit);
            _ = MarkdownFencedCodeScanner.ClassifyLine(
                source.AsSpan(cursor, lineEnd - cursor),
                state,
                out state);

            var next = FindLineEndExclusive(source, cursor);
            if (next <= cursor)
            {
                break;
            }
            cursor = Math.Min(next, limit);
        }
        return state;
    }

    private static void ExpandToOverlappingSemantics(
        IReadOnlyList<MarkdownSemanticSpan> spans,
        ref int start,
        ref int end)
    {
        foreach (var span in spans)
        {
            if (span.End <= start)
            {
                continue;
            }
            if (span.Start >= end)
            {
                break;
            }

            start = Math.Min(start, span.Start);
            end = Math.Max(end, span.End);
        }
    }

    private static void ExpandToOverlappingSemantics(
        IReadOnlyList<MarkdownSemanticLink> links,
        ref int start,
        ref int end)
    {
        foreach (var link in links)
        {
            if (link.End <= start)
            {
                continue;
            }
            if (link.Start >= end)
            {
                break;
            }

            start = Math.Min(start, link.Start);
            end = Math.Max(end, link.End);
        }
    }

    private static bool WindowOverlapsLink(
        IReadOnlyList<MarkdownSemanticLink> links,
        int start,
        int end)
    {
        foreach (var link in links)
        {
            if (link.End <= start)
            {
                continue;
            }
            if (link.Start >= end)
            {
                break;
            }
            return true;
        }
        return false;
    }

    private static MarkdownSemanticSpan[] SpliceSpans(
        MarkdownSemanticSpan[] oldSpans,
        MarkdownSemanticSpan[] localSpans,
        int oldStart,
        int oldEnd,
        int newStart,
        int delta)
    {
        var prefixCount = LowerBoundSpanStart(oldSpans, oldStart);
        var suffixStart = LowerBoundSpanStart(oldSpans, oldEnd);
        var result = new MarkdownSemanticSpan[
            prefixCount + localSpans.Length + (oldSpans.Length - suffixStart)];

        if (prefixCount > 0)
        {
            Array.Copy(oldSpans, 0, result, 0, prefixCount);
        }

        var write = prefixCount;
        foreach (var span in localSpans)
        {
            result[write++] = ShiftSpan(span, newStart);
        }
        for (var index = suffixStart; index < oldSpans.Length; index++)
        {
            result[write++] = ShiftSpan(oldSpans[index], delta);
        }
        return result;
    }

    private static MarkdownSemanticLink[] SpliceLinks(
        MarkdownSemanticLink[] oldLinks,
        MarkdownSemanticLink[] localLinks,
        int oldStart,
        int oldEnd,
        int newStart,
        int delta)
    {
        var prefixCount = LowerBoundLinkStart(oldLinks, oldStart);
        var suffixStart = LowerBoundLinkStart(oldLinks, oldEnd);
        var result = new MarkdownSemanticLink[
            prefixCount + localLinks.Length + (oldLinks.Length - suffixStart)];

        if (prefixCount > 0)
        {
            Array.Copy(oldLinks, 0, result, 0, prefixCount);
        }

        var write = prefixCount;
        foreach (var link in localLinks)
        {
            result[write++] = ShiftLink(link, newStart);
        }
        for (var index = suffixStart; index < oldLinks.Length; index++)
        {
            result[write++] = ShiftLink(oldLinks[index], delta);
        }
        return result;
    }

    private static int LowerBoundSpanStart(MarkdownSemanticSpan[] spans, int start)
    {
        var low = 0;
        var high = spans.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (spans[middle].Start < start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static int LowerBoundLinkStart(MarkdownSemanticLink[] links, int start)
    {
        var low = 0;
        var high = links.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (links[middle].Start < start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    internal static MarkdownSemanticSpan ShiftSpan(MarkdownSemanticSpan span, int delta) =>
        delta == 0
            ? span
            : span with { Start = span.Start + delta };

    internal static MarkdownSemanticLink ShiftLink(MarkdownSemanticLink link, int delta)
    {
        if (delta == 0)
        {
            return link;
        }

        return link with
        {
            Start = link.Start + delta,
            LabelStart = ShiftOptionalOffset(link.LabelStart, delta),
            DestinationStart = ShiftOptionalOffset(link.DestinationStart, delta)
        };
    }

    private static int ShiftOptionalOffset(int offset, int delta) =>
        offset < 0 ? offset : offset + delta;

    private static bool RangeContainsReferenceDependency(
        string source,
        int start,
        int end,
        bool includeReferenceUses)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var normalizedStart = Math.Clamp(start, 0, source.Length);
        var normalizedEnd = Math.Clamp(end, normalizedStart, source.Length);
        var cursor = FindLineStart(source, normalizedStart);
        var limit = FindLineEndExclusive(source, normalizedEnd);
        while (cursor < limit)
        {
            var lineEnd = FindLineDelimiterStart(source, cursor);
            if (IsPotentialReferenceDefinition(source, cursor, lineEnd) ||
                (includeReferenceUses && LineContainsSquareBracket(source, cursor, lineEnd)))
            {
                return true;
            }

            var next = FindLineEndExclusive(source, cursor);
            if (next <= cursor)
            {
                break;
            }
            cursor = next;
        }

        return false;
    }

    private static bool IsPotentialReferenceDefinition(string source, int lineStart, int lineEnd)
    {
        var index = lineStart;
        var spaces = 0;
        while (index < lineEnd && source[index] == ' ' && spaces < 4)
        {
            index++;
            spaces++;
        }
        if (spaces > 3 || index >= lineEnd || source[index] != '[')
        {
            return false;
        }

        for (var cursor = index + 1; cursor + 1 < lineEnd; cursor++)
        {
            if (source[cursor] == ']' && source[cursor + 1] == ':')
            {
                return true;
            }
        }
        return false;
    }

    private static bool LineContainsSquareBracket(string source, int lineStart, int lineEnd)
    {
        var start = Math.Clamp(lineStart, 0, source.Length);
        var end = Math.Clamp(lineEnd, start, source.Length);
        for (var index = start; index < end; index++)
        {
            if (source[index] is '[' or ']')
            {
                return true;
            }
        }
        return false;
    }

    private static int FindLineStart(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        if (normalized == 0)
        {
            return 0;
        }

        for (var index = normalized - 1; index >= 0; index--)
        {
            if (source[index] == '\n')
            {
                return index + 1;
            }
            if (source[index] == '\r')
            {
                return index + 1 < source.Length && source[index + 1] == '\n'
                    ? index + 2
                    : index + 1;
            }
        }
        return 0;
    }

    private static int FindLineDelimiterStart(string source, int offset)
    {
        for (var index = Math.Clamp(offset, 0, source.Length); index < source.Length; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return index;
            }
        }
        return source.Length;
    }

    private static int FindLineEndExclusive(string source, int offset)
    {
        var delimiter = FindLineDelimiterStart(source, offset);
        if (delimiter >= source.Length)
        {
            return source.Length;
        }
        return source[delimiter] == '\r' && delimiter + 1 < source.Length && source[delimiter + 1] == '\n'
            ? delimiter + 2
            : delimiter + 1;
    }

    private static int AlignLineEndExclusive(string source, int offset)
    {
        var normalized = Math.Clamp(offset, 0, source.Length);
        if (normalized == source.Length || IsPhysicalLineStart(source, normalized))
        {
            return normalized;
        }
        return FindLineEndExclusive(source, normalized);
    }

    private static bool IsPhysicalLineStart(string source, int offset)
    {
        if (offset <= 0)
        {
            return true;
        }
        if (offset > source.Length)
        {
            return false;
        }
        if (source[offset - 1] == '\n')
        {
            return true;
        }
        return source[offset - 1] == '\r' &&
            (offset >= source.Length || source[offset] != '\n');
    }
}
