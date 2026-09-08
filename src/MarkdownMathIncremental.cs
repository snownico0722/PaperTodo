namespace PaperTodo;

/// <summary>
/// Detects edits that may create, remove or reclassify a math delimiter. Formula-body edits can
/// still use the existing sealed incremental window because a previously recognized math span
/// expands that window to the complete expression.
/// </summary>
internal static class MarkdownMathIncremental
{
    public static bool ChangeMayAffectDelimiterState(string oldSource, string newSource)
    {
        ArgumentNullException.ThrowIfNull(oldSource);
        ArgumentNullException.ThrowIfNull(newSource);

        var sharedLength = Math.Min(oldSource.Length, newSource.Length);
        var start = 0;
        while (start < sharedLength && oldSource[start] == newSource[start])
        {
            start++;
        }

        var oldEnd = oldSource.Length;
        var newEnd = newSource.Length;
        while (oldEnd > start &&
               newEnd > start &&
               oldSource[oldEnd - 1] == newSource[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        if (start == oldSource.Length && start == newSource.Length)
        {
            return false;
        }

        return RangeMayContainDelimiterContext(oldSource, start, oldEnd) ||
            RangeMayContainDelimiterContext(newSource, start, newEnd);
    }

    private static bool RangeMayContainDelimiterContext(string source, int start, int end)
    {
        if (source.Length == 0)
        {
            return false;
        }

        // Three characters on either side cover $$ and the escaped two-character delimiters,
        // plus the adjacent whitespace/digit rules used by inline dollars.
        var from = Math.Max(0, start - 3);
        var to = Math.Min(source.Length, end + 3);
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
}
