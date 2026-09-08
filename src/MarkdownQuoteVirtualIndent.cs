namespace PaperTodo;

/// <summary>
/// A presentation-only quote prefix inserted into an AvalonEdit visual line. Offset is relative to
/// the physical source line; Text never enters the TextDocument or undo stack.
/// </summary>
internal readonly record struct MarkdownQuoteVirtualPrefix(int Offset, string Text);

internal static class MarkdownQuoteVirtualIndent
{
    /// <summary>
    /// Builds only the quote marker groups missing from a physical source line. Markdig's semantic
    /// QuoteLevel remains the authority; this helper merely compares it with explicit source markers.
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

        var markers = MarkdownQuoteMarkers.EnumerateMarkers(
            sourceLine,
            0,
            sourceLine.Length);
        var missingLevels = quoteLevel - markers.Count;
        if (missingLevels <= 0)
        {
            return false;
        }

        var offset = markers.Count > 0 ? markers[^1].End : 0;
        if (markers.Count == 0)
        {
            // CommonMark permits up to three spaces before a block marker. Preserve those physical
            // source spaces and insert the presentation-only marker at the same logical location.
            while (offset < sourceLine.Length &&
                   offset < 3 &&
                   sourceLine[offset] == ' ')
            {
                offset++;
            }
        }

        prefix = new MarkdownQuoteVirtualPrefix(
            offset,
            MarkdownQuoteMarkers.RepeatMarkerPrefix(missingLevels));
        return true;
    }
}
