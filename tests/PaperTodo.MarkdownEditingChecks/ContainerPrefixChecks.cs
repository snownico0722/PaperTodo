using System.Runtime.CompilerServices;
using PaperTodo;

internal static class ContainerPrefixChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Check("- > a", expectedQuoteLevel: 1, expectedContentStart: 4, expectedQuoteStart: 2);
        Check("10. > a", expectedQuoteLevel: 1, expectedContentStart: 6, expectedQuoteStart: 4);
        Check("    > b", expectedQuoteLevel: 1, expectedContentStart: 6, expectedQuoteStart: 4);
        Check("> - > a", expectedQuoteLevel: 2, expectedContentStart: 6, expectedQuoteStart: 0);
        Console.WriteLine("PASS unified container prefix offsets");
    }

    private static void Check(
        string source,
        int expectedQuoteLevel,
        int expectedContentStart,
        int expectedQuoteStart)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(source);
        var semantic = snapshot.GetLine(0);
        if (semantic.QuoteLevel != expectedQuoteLevel)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: '{source}' quote level {semantic.QuoteLevel}");
        }

        var prefix = MarkdownContainerPrefix.Parse(
            source,
            semantic.QuoteLevel,
            snapshot,
            0,
            source.Length);
        if (prefix.ContentStart != expectedContentStart || prefix.MissingQuoteLevels != 0)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: '{source}' content {prefix.ContentStart}, missing {prefix.MissingQuoteLevels}");
        }

        var firstQuote = prefix.Tokens.FirstOrDefault(token => token.IsQuote);
        if (!firstQuote.IsQuote || firstQuote.MarkerStart != expectedQuoteStart)
        {
            throw new InvalidOperationException(
                $"FAIL container prefix: '{source}' quote starts at {firstQuote.MarkerStart}");
        }
    }
}
