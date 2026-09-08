using System.Runtime.CompilerServices;
using PaperTodo;

internal static class QuoteVirtualIndentChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        ExpectNoMissingQuote("> a", 0, "root explicit quote");
        ExpectNoMissingQuote("> > a", 0, "nested explicit quote");
        ExpectNoMissingQuote("- > a", 0, "unordered-list quote marker");
        ExpectNoMissingQuote("1. > a", 0, "ordered-list quote marker");
        ExpectNoMissingQuote("> - > a", 0, "alternating quote/list containers");

        // 四空格不是独立语法猜测，而是 10. 列表项真实上下文中的 content indent。
        ExpectNoMissingQuote("10. > a\n    > b", 1, "four-space ordered-list continuation quote");
        ExpectNoMissingQuote("10. > > a\n    > > b", 1, "four-space nested quote continuation");

        ExpectMissingQuote("> a\nb", 1, 0, "> ", "root lazy continuation");
        ExpectMissingQuote("- > a\n  b", 1, 2, "> ", "list-indented lazy continuation");
        ExpectMissingQuote("> > a\n> lazy", 1, 2, "> ", "nested lazy continuation");
        ExpectMissingQuote("> - > a\n>   lazy", 1, 4, "> ", "lazy quote after list container");

        CheckSemanticContainerOrder();
        CheckTaskOwner();
        CheckContinuationIndentComesFromContext();
        CheckIndentedLookalikeUsesMarkdigMeaning();
        Console.WriteLine("PASS Markdig-backed quote/list container mapping");
    }

    private static void CheckSemanticContainerOrder()
    {
        const string source = "- > - item";
        var prefix = ParseLine(source, 0);
        Expect(prefix.Tokens.Count == 3, "three alternating physical containers");
        Expect(prefix.Tokens[0].Kind == MarkdownContainerPrefixKind.UnorderedList, "outer list first");
        Expect(prefix.Tokens[1].Kind == MarkdownContainerPrefixKind.Quote, "quote second");
        Expect(prefix.Tokens[2].Kind == MarkdownContainerPrefixKind.UnorderedList, "inner list last");
    }

    private static void CheckTaskOwner()
    {
        const string source = "- > - [ ] item";
        var prefix = ParseLine(source, 0);
        Expect(prefix.TaskOwnerTokenIndex == 2, "task belongs to innermost list");
        Expect(prefix.VisualIndentEnd > prefix.ContentStart, "task marker participates in wrap indent");
    }

    private static void CheckContinuationIndentComesFromContext()
    {
        const string source = "10. > a\n    > b";
        var prefix = ParseLine(source, 1);
        Expect(prefix.Tokens.Count == 1 && prefix.Tokens[0].IsQuote,
            "continuation row has only its physical quote token");
        Expect(prefix.Tokens[0].MarkerStart == 4,
            "Markdig list context keeps the physical quote after four-space content indent");
        Expect(prefix.ContentStart == 6,
            "content starts after the real quote marker, not after a synthetic three-space rule");
    }

    private static void CheckIndentedLookalikeUsesMarkdigMeaning()
    {
        var prefix = ParseLine("    > b", 0);
        Expect(prefix.QuoteLevel == 0,
            "four-space indented standalone row is code, not a quote guessed from its characters");
        Expect(prefix.Tokens.Count == 0,
            "no quote token is fabricated when Markdig did not parse a quote container");
    }

    private static void ExpectNoMissingQuote(string markdown, int lineZero, string message)
    {
        var prefix = ParseLine(markdown, lineZero);
        if (prefix.MissingQuoteLevels != 0)
        {
            throw new InvalidOperationException(
                $"FAIL container mapping: {message}: unexpected {prefix.MissingQuoteLevels} missing quote level(s) at {prefix.ContentStart}");
        }
    }

    private static void ExpectMissingQuote(
        string markdown,
        int lineZero,
        int expectedOffset,
        string expectedText,
        string message)
    {
        var prefix = ParseLine(markdown, lineZero);
        var actualText = MarkdownQuoteMarkers.RepeatMarkerPrefix(prefix.MissingQuoteLevels);
        if (prefix.MissingQuoteLevels <= 0 ||
            prefix.ContentStart != expectedOffset ||
            !string.Equals(actualText, expectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL container mapping: {message}: expected '{expectedText}' at {expectedOffset}, actual '{actualText}' at {prefix.ContentStart}");
        }
    }

    private static MarkdownContainerPrefixInfo ParseLine(string markdown, int lineZero)
    {
        var snapshot = MarkdownSemanticSnapshot.Parse(markdown);
        if (lineZero < 0 || lineZero >= snapshot.LineStarts.Length)
        {
            throw new InvalidOperationException($"FAIL container mapping: line {lineZero} out of range");
        }

        var start = snapshot.LineStarts[lineZero];
        var end = lineZero + 1 < snapshot.LineStarts.Length
            ? snapshot.LineStarts[lineZero + 1]
            : markdown.Length;
        while (end > start && markdown[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return MarkdownContainerPrefix.Parse(
            markdown[start..end],
            snapshot,
            start,
            end);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL container mapping: {message}");
        }
    }
}
