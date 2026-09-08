using System.Runtime.CompilerServices;
using PaperTodo;

internal static class ContainerPrefixChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("- > a", 0, expectedQuoteLevel: 1, expectedContentStart: 4, expectedQuoteStart: 2);
        Check("10. > a", 0, expectedQuoteLevel: 1, expectedContentStart: 6, expectedQuoteStart: 4);
        Check("10. > a\n    > b", 1, expectedQuoteLevel: 1, expectedContentStart: 6, expectedQuoteStart: 4);
        Check("> - > a", 0, expectedQuoteLevel: 2, expectedContentStart: 6, expectedQuoteStart: 0);
        Check("> - > - item", 0, expectedQuoteLevel: 2, expectedContentStart: 8, expectedQuoteStart: 0);
        Console.WriteLine("PASS unified container prefix offsets");
    }

    private static void Check(
        string source,
        int lineZero,
        int expectedQuoteLevel,
        int expectedContentStart,
        int expectedQuoteStart)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var semantic = snapshot.GetLine(lineZero);
        if (semantic.QuoteLevel != expectedQuoteLevel)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: line {lineZero} quote level {semantic.QuoteLevel}");
        }

        var lineStart = snapshot.LineStarts[lineZero];
        var lineEnd = lineZero + 1 < snapshot.LineStarts.Length
            ? snapshot.LineStarts[lineZero + 1]
            : source.Length;
        while (lineEnd > lineStart && source[lineEnd - 1] is '\r' or '\n')
        {
            lineEnd--;
        }
        var lineText = source[lineStart..lineEnd];
        var prefix = MarkdownContainerPrefix.Parse(
            lineText,
            snapshot,
            lineStart,
            lineEnd);
        if (prefix.ContentStart != expectedContentStart || prefix.MissingQuoteLevels != 0)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: '{lineText}' content {prefix.ContentStart}, missing {prefix.MissingQuoteLevels}");
        }

        var foundQuote = false;
        var firstQuoteStart = -1;
        foreach (var token in prefix.Tokens)
        {
            if (!token.IsQuote)
            {
                continue;
            }

            foundQuote = true;
            firstQuoteStart = token.MarkerStart;
            break;
        }
        if (!foundQuote || firstQuoteStart != expectedQuoteStart)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: '{lineText}' quote starts at {firstQuoteStart}");
        }
    }
}
