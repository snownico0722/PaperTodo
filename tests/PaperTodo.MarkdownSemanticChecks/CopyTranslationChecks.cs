using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static class CopyTranslationChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Equal(
            "bold and italic",
            MarkdownSemanticSnapshot.ToPlainText("**bold** and *italic*"),
            "Markdown emphasis is removed from plain-text copy");
        Equal(
            "label",
            MarkdownSemanticSnapshot.ToPlainText("[label](https://example.com)"),
            "Markdown link destination is removed from plain-text copy");

        var markdown = TodoClipboardFormatter.ToMarkdown(
        [
            ("buy milk", false),
            ("submitted", true)
        ]);
        Equal(
            "- [ ] buy milk" + Environment.NewLine + "- [x] submitted",
            markdown,
            "Todo completion state is preserved in Markdown copy");

        var escaped = TodoClipboardFormatter.ToMarkdown(
        [
            ("literal **stars** and [brackets]", false),
            ("first\r\n# heading-looking continuation", true)
        ]);
        Equal(
            "- [ ] literal \\*\\*stars\\*\\* and \\[brackets\\]" +
            Environment.NewLine +
            "- [x] first" + Environment.NewLine +
            "    \\# heading-looking continuation",
            escaped,
            "Todo text remains literal when translated to Markdown");

        Console.WriteLine("PASS copy translation checks");
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message}: expected [{expected}] actual [{actual}]");
        }
    }
}
