using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal static class MarkdownEditBehavior
{
    internal static MarkdownSemanticSnapshot ReadAfterEdit(string before, string after)
    {
        var document = new TextDocument(before);
        using var semantics = new MarkdownSemanticDocument(document);
        document.Text = after;
        return semantics.TryGetCurrent(out var result)
            ? result
            : throw new InvalidOperationException("Edited document did not publish current semantics.");
    }

    internal static void AssertEquivalent(MarkdownSemanticSnapshot expected, MarkdownSemanticSnapshot actual, string name)
    {
        if (expected.LineCount != actual.LineCount || !expected.Spans.SequenceEqual(actual.Spans) ||
            !expected.Links.SequenceEqual(actual.Links))
            throw new InvalidOperationException($"FAIL {name}: edited semantics differ.");
        for (var line = 0; line < expected.LineCount; line++)
            if (!expected.GetLine(line).Equals(actual.GetLine(line)) ||
                !expected.SpansForLine(line).SequenceEqual(actual.SpansForLine(line)) ||
                !expected.LinksForLine(line).SequenceEqual(actual.LinksForLine(line)))
                throw new InvalidOperationException($"FAIL {name}: line {line} differs.");
    }

    internal static string BuildDenseSource()
    {
        var builder = new System.Text.StringBuilder(100_000);
        var index = 0;
        while (builder.Length < 98_000)
        {
            builder.Append("# Heading ").Append(index).Append('\n');
            builder.Append("- [ ] item **bold** https://example.com/").Append(index).Append('\n');
            builder.Append("> quote `code` ~~gone~~\n");
            if ((index++ % 20) == 0)
            {
                builder.Append("```csharp\nvar x = 1;\n```\n");
            }
        }

        return builder.ToString();
    }
}
