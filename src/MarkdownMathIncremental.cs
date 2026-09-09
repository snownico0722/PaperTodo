namespace PaperTodo;

/// <summary>
/// Detects edits that may create, remove or re-pair a math delimiter. Ordinary edits inside a
/// recognized formula may reuse the existing sealed incremental window: that window expands to
/// the complete old formula span before the local source is parsed again.
/// </summary>
internal static class MarkdownMathIncremental
{
    public static bool ChangeMayAffectDelimiterState(
        string oldSource,
        MarkdownSemanticSnapshot oldSnapshot,
        string newSource)
    {
        ArgumentNullException.ThrowIfNull(oldSource);
        ArgumentNullException.ThrowIfNull(oldSnapshot);
        ArgumentNullException.ThrowIfNull(newSource);

        FindDifference(oldSource, newSource, out var start, out var oldEnd, out var newEnd);
        if (start == oldSource.Length && start == newSource.Length)
        {
            return false;
        }

        // Adding/removing a delimiter token can change which distant delimiter it pairs with, so
        // a bounded local reparse is not sufficient even when the edit occurred inside a formula.
        if (DifferenceTouchesDelimiterToken(oldSource, start, oldEnd) ||
            DifferenceTouchesDelimiterToken(newSource, start, newEnd))
        {
            return true;
        }

        var delta = newSource.Length - oldSource.Length;
        foreach (var span in oldSnapshot.Spans)
        {
            if (span.Kind is not (
                    MarkdownSemanticSpanKind.InlineMath or
                    MarkdownSemanticSpanKind.BlockMath) ||
                span.MarkerLength <= 0)
            {
                continue;
            }

            var oldContentStart = span.Start + span.MarkerLength;
            var oldContentEnd = span.End - span.MarkerLength;
            if (start < oldContentStart || oldEnd > oldContentEnd)
            {
                continue;
            }

            var newContentEnd = oldContentEnd + delta;
            if (newContentEnd < oldContentStart || newContentEnd > newSource.Length)
            {
                return true;
            }

            // Turning the body into whitespace removes the formula and can expose dollar signs to
            // a different pairing farther away. Keep that case global; normal TeX edits stay local.
            if (!ContainsNonWhitespace(newSource, oldContentStart, newContentEnd))
            {
                return true;
            }

            if (span.Kind == MarkdownSemanticSpanKind.InlineMath)
            {
                // Inline formulas cannot cross a physical line. With dollar delimiters, inserting
                // a newline can also turn the old closing dollar into a new distant opener.
                if (RangeContainsLineBreak(oldSource, start, oldEnd) ||
                    RangeContainsLineBreak(newSource, start, newEnd))
                {
                    return true;
                }

                // Dollar validity depends on whitespace immediately inside both delimiters.
                if (span.MarkerLength == 1)
                {
                    if (oldContentStart >= oldContentEnd || oldContentStart >= newContentEnd)
                    {
                        return true;
                    }

                    if (char.IsWhiteSpace(oldSource[oldContentStart]) !=
                            char.IsWhiteSpace(newSource[oldContentStart]) ||
                        char.IsWhiteSpace(oldSource[oldContentEnd - 1]) !=
                            char.IsWhiteSpace(newSource[newContentEnd - 1]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Outside a known formula, changing text beside a literal delimiter can create or remove a
        // valid formula even though the delimiter character itself did not change (for example
        // changing "$x$2" to "$x$a").
        return HasNearbyDelimiter(oldSource, start, oldEnd) ||
            HasNearbyDelimiter(newSource, start, newEnd);
    }

    private static void FindDifference(
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

    private static bool DifferenceTouchesDelimiterToken(string source, int start, int end)
    {
        var from = Math.Clamp(start, 0, source.Length);
        var to = Math.Clamp(end, from, source.Length);
        for (var index = from; index < to; index++)
        {
            if (source[index] == '$')
            {
                return true;
            }
        }

        var contextStart = Math.Max(0, from - 1);
        var contextEnd = Math.Min(source.Length, to + 1);
        for (var index = contextStart; index + 1 < source.Length && index < contextEnd; index++)
        {
            if (source[index] != '\\' || source[index + 1] is not ('(' or ')' or '[' or ']'))
            {
                continue;
            }

            var tokenEnd = index + 2;
            var overlapsReplacement = to > from && index < to && from < tokenEnd;
            var crossesInsertion = to == from && index <= from && from <= tokenEnd;
            if (overlapsReplacement || crossesInsertion)
            {
                return true;
            }
        }

        // A delimiter can change meaning even when the delimiter character itself was untouched:
        // the parity of the contiguous backslash run immediately before it decides whether it is
        // escaped. An edit anywhere in that run can therefore re-pair with a distant delimiter.
        if (TouchesEscapeRunBeforeMathDelimiter(source, from) ||
            TouchesEscapeRunBeforeMathDelimiter(source, to))
        {
            return true;
        }

        for (var index = from; index < to; index++)
        {
            if (source[index] != '\\')
            {
                continue;
            }

            var runEnd = index + 1;
            while (runEnd < source.Length && source[runEnd] == '\\')
            {
                runEnd++;
            }

            if (runEnd < source.Length && IsMathDelimiterFollower(source[runEnd]))
            {
                return true;
            }

            index = runEnd - 1;
        }

        return false;
    }

    private static bool TouchesEscapeRunBeforeMathDelimiter(string source, int point)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var normalized = Math.Clamp(point, 0, source.Length);
        var runStart = normalized;
        while (runStart > 0 && source[runStart - 1] == '\\')
        {
            runStart--;
        }

        var runEnd = normalized;
        while (runEnd < source.Length && source[runEnd] == '\\')
        {
            runEnd++;
        }

        return runEnd > runStart &&
            runEnd < source.Length &&
            IsMathDelimiterFollower(source[runEnd]);
    }

    private static bool IsMathDelimiterFollower(char character) =>
        character is '$' or '(' or ')' or '[' or ']';

    private static bool HasNearbyDelimiter(string source, int start, int end)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var from = Math.Max(0, start - 2);
        var to = Math.Min(source.Length, end + 2);
        for (var index = from; index < to; index++)
        {
            if (source[index] == '$')
            {
                return true;
            }

            if (source[index] == '\\' && index + 1 < source.Length &&
                source[index + 1] is '(' or ')' or '[' or ']')
            {
                return true;
            }
        }

        return false;
    }

    private static bool RangeContainsLineBreak(string source, int start, int end)
    {
        var from = Math.Clamp(start, 0, source.Length);
        var to = Math.Clamp(end, from, source.Length);
        for (var index = from; index < to; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNonWhitespace(string source, int start, int end)
    {
        for (var index = Math.Max(0, start); index < Math.Min(source.Length, end); index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                return true;
            }
        }

        return false;
    }
}
