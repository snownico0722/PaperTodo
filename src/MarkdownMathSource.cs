namespace PaperTodo;

internal static class MarkdownMathSource
{
    internal static bool TryExtract(
        string source,
        MarkdownSemanticSpan span,
        out string formula,
        out bool display)
    {
        formula = string.Empty;
        display = span.Kind == MarkdownSemanticSpanKind.BlockMath;
        if (span.Kind is not (
                MarkdownSemanticSpanKind.InlineMath or
                MarkdownSemanticSpanKind.BlockMath) ||
            span.Length <= 0 ||
            span.Start < 0 ||
            span.End > source.Length)
        {
            return false;
        }

        var raw = source.AsSpan(span.Start, span.Length);
        if (TryDelimiter(raw, span, out var open, out var close))
        {
            if (display && open.SequenceEqual("$$") && raw.IndexOfAny('\r', '\n') >= 0)
            {
                return TryExtractDollarBlock(raw, out formula);
            }

            if (raw.Length <= open.Length + close.Length ||
                !raw.StartsWith(open, StringComparison.Ordinal) ||
                !raw.EndsWith(close, StringComparison.Ordinal))
            {
                return false;
            }

            formula = NormalizeFormula(raw[open.Length..^close.Length]);
            return formula.Length > 0;
        }

        return false;
    }

    private static bool TryDelimiter(
        ReadOnlySpan<char> raw,
        MarkdownSemanticSpan span,
        out ReadOnlySpan<char> open,
        out ReadOnlySpan<char> close)
    {
        if (span.MarkerLength == 1 && raw.StartsWith("$", StringComparison.Ordinal))
        {
            open = "$";
            close = "$";
            return true;
        }

        if (span.MarkerLength == 2 && raw.StartsWith("$$", StringComparison.Ordinal))
        {
            open = "$$";
            close = "$$";
            return true;
        }

        if (span.MarkerLength == 2 && raw.StartsWith("\\(", StringComparison.Ordinal))
        {
            open = "\\(";
            close = "\\)";
            return true;
        }

        if (span.MarkerLength == 2 && raw.StartsWith("\\[", StringComparison.Ordinal))
        {
            open = "\\[";
            close = "\\]";
            return true;
        }

        open = default;
        close = default;
        return false;
    }

    private static bool TryExtractDollarBlock(
        ReadOnlySpan<char> raw,
        out string formula)
    {
        formula = string.Empty;
        var firstLineEnd = IndexOfLineEnd(raw, 0);
        if (firstLineEnd < 0 || !raw[..firstLineEnd].Trim().SequenceEqual("$$"))
        {
            return false;
        }

        var contentStart = SkipLineBreak(raw, firstLineEnd);
        var closingLineStart = FindLastLineStart(raw);
        if (closingLineStart <= contentStart ||
            !raw[closingLineStart..].Trim().SequenceEqual("$$"))
        {
            return false;
        }

        formula = NormalizeFormula(raw[contentStart..closingLineStart]);
        return formula.Length > 0;
    }

    private static int IndexOfLineEnd(ReadOnlySpan<char> source, int start)
    {
        for (var index = Math.Max(0, start); index < source.Length; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                return index;
            }
        }
        return -1;
    }

    private static int SkipLineBreak(ReadOnlySpan<char> source, int index)
    {
        if (index < source.Length && source[index] == '\r')
        {
            index++;
        }
        if (index < source.Length && source[index] == '\n')
        {
            index++;
        }
        return index;
    }

    private static int FindLastLineStart(ReadOnlySpan<char> source)
    {
        var end = source.Length;
        while (end > 0 && source[end - 1] is ' ' or '\t' or '\r' or '\n')
        {
            end--;
        }
        if (end <= 0)
        {
            return 0;
        }

        for (var index = end - 1; index >= 0; index--)
        {
            if (source[index] is '\r' or '\n')
            {
                return index + 1;
            }
        }
        return 0;
    }

    private static string NormalizeFormula(ReadOnlySpan<char> source)
    {
        var value = source.Trim().ToString();
        if (value.Length == 0 || value.IndexOf('\0') >= 0)
        {
            return string.Empty;
        }

        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }
}
