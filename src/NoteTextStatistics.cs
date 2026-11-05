using System.Text;

namespace PaperTodo;

internal static class NoteTextStatistics
{
    // Count displayed non-whitespace Unicode characters, not Markdown delimiters,
    // link destinations, image URIs, or PaperTodo's image sizing metadata.
    internal static int Count(string text)
    {
        var source = text.Contains("](i:", StringComparison.Ordinal)
            ? WithoutImageDisplaySyntax(text)
            : text;
        var count = 0;
        foreach (var rune in MarkdownSemanticSnapshot.ToPlainText(source).EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune)) count++;
        return count;
    }

    private static string WithoutImageDisplaySyntax(string text)
    {
        // These are the editor's standalone image references. Reuse the semantic image
        // enumerator so identical text inside fenced code remains literal text.
        var references = MarkdownImageReferences.Enumerate(text).ToArray();
        if (references.Length == 0) return text;

        var result = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var reference in references)
        {
            result.Append(text, cursor, reference.LineStart - cursor);
            var line = text.AsSpan(reference.LineStart, reference.LineLength);
            var indent = 0;
            while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;
            result.Append(line[..indent]);

            var label = reference.Label;
            if (reference.DisplayOptions.LabelWidth != null ||
                reference.DisplayOptions.LabelScalePercent != null)
            {
                var separator = label.IndexOf('|');
                if (separator >= 0) label = label[..separator];
            }
            result.Append("![").Append(label).Append("](i:").Append(reference.ImageId).Append(')');
            cursor = reference.LineStart + reference.LineLength;
        }
        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }
}
