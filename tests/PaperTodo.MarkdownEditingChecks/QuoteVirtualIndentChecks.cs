using System.Runtime.CompilerServices;
using PaperTodo;

internal static class QuoteVirtualIndentChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        ExpectNoPrefix("> a", 1, "root explicit quote");
        ExpectNoPrefix("> > a", 2, "nested explicit quote");
        ExpectNoPrefix("- > a", 1, "unordered-list quote marker");
        ExpectNoPrefix("1. > a", 1, "ordered-list quote marker");
        ExpectNoPrefix("> - > a", 2, "alternating quote/list containers");

        ExpectPrefix("b", 1, 0, "> ", "root lazy continuation");
        ExpectPrefix("  b", 1, 2, "> ", "list-indented lazy continuation");
        ExpectPrefix("> lazy", 2, 2, "> ", "nested lazy continuation");
        ExpectPrefix("> -   lazy", 2, 6, "> ", "lazy quote after a list container");

        Console.WriteLine("PASS quote virtual indentation container scan");
    }

    private static void ExpectNoPrefix(string source, int level, string message)
    {
        if (MarkdownQuoteVirtualIndent.TryCreate(source, level, out var prefix))
        {
            throw new InvalidOperationException(
                $"FAIL quote virtual indentation: {message}: unexpected '{prefix.Text}' at {prefix.Offset}");
        }
    }

    private static void ExpectPrefix(
        string source,
        int level,
        int expectedOffset,
        string expectedText,
        string message)
    {
        if (!MarkdownQuoteVirtualIndent.TryCreate(source, level, out var prefix) ||
            prefix.Offset != expectedOffset ||
            !string.Equals(prefix.Text, expectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FAIL quote virtual indentation: {message}: expected '{expectedText}' at {expectedOffset}, actual '{prefix.Text}' at {prefix.Offset}");
        }
    }
}
