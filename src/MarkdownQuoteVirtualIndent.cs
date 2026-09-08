namespace PaperTodo;

/// <summary>
/// A presentation-only quote prefix inserted into an AvalonEdit visual line. Offset is relative to
/// the physical source line; Text never enters the TextDocument or undo stack.
/// </summary>
internal readonly record struct MarkdownQuoteVirtualPrefix(int Offset, string Text);

internal static class MarkdownQuoteVirtualIndent
{
    /// <summary>
    /// Builds only quote marker groups missing from a physical source line. Markdig's semantic
    /// QuoteLevel remains authoritative; this scanner reads explicit quote/list container markers
    /// only to locate the matching presentation indent and never changes the source.
    /// </summary>
    internal static bool TryCreate(
        string sourceLine,
        int quoteLevel,
        out MarkdownQuoteVirtualPrefix prefix)
    {
        ArgumentNullException.ThrowIfNull(sourceLine);
        prefix = default;
        if (quoteLevel <= 0)
        {
            return false;
        }

        ReadContainerPrefix(
            sourceLine,
            quoteLevel,
            out var explicitQuoteLevels,
            out var insertionOffset);
        var missingLevels = quoteLevel - explicitQuoteLevels;
        if (missingLevels <= 0)
        {
            return false;
        }

        prefix = new MarkdownQuoteVirtualPrefix(
            insertionOffset,
            MarkdownQuoteMarkers.RepeatMarkerPrefix(missingLevels));
        return true;
    }

    private static void ReadContainerPrefix(
        string sourceLine,
        int quoteLevel,
        out int explicitQuoteLevels,
        out int insertionOffset)
    {
        explicitQuoteLevels = 0;
        insertionOffset = 0;
        var index = 0;
        while (index < sourceLine.Length && explicitQuoteLevels < quoteLevel)
        {
            var spaces = 0;
            while (index < sourceLine.Length &&
                   spaces < 3 &&
                   sourceLine[index] == ' ')
            {
                index++;
                spaces++;
            }
            insertionOffset = index;

            if (index < sourceLine.Length && sourceLine[index] == '>')
            {
                explicitQuoteLevels++;
                index++;
                if (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
                {
                    index++;
                }
                insertionOffset = index;
                continue;
            }

            if (TryConsumeListMarker(sourceLine, ref index))
            {
                insertionOffset = index;
                continue;
            }

            break;
        }
    }

    private static bool TryConsumeListMarker(string sourceLine, ref int index)
    {
        var markerStart = index;
        if (markerStart >= sourceLine.Length)
        {
            return false;
        }

        var markerEnd = markerStart;
        if (sourceLine[markerStart] is '-' or '+' or '*')
        {
            markerEnd++;
        }
        else if (char.IsAsciiDigit(sourceLine[markerStart]))
        {
            var digits = 0;
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
        }
        else
        {
            return false;
        }

        if (markerEnd < sourceLine.Length &&
            sourceLine[markerEnd] is not (' ' or '\t'))
        {
            return false;
        }

        index = markerEnd;
        while (index < sourceLine.Length && sourceLine[index] is ' ' or '\t')
        {
            index++;
        }
        return true;
    }
}
