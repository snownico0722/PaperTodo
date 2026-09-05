using System.Text;

namespace PaperTodo;

internal static class TodoClipboardFormatter
{
    private const string AlwaysEscapedMarkdownCharacters = "\\`*_{}[]<>~|&";

    internal static string ToMarkdown(IEnumerable<(string Text, bool Done)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var result = new StringBuilder();
        foreach (var (text, done) in items)
        {
            if (result.Length > 0)
            {
                result.AppendLine();
            }

            result.Append(done ? "- [x] " : "- [ ] ");
            var lines = (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    result.AppendLine();
                    result.Append("    ");
                }

                AppendEscapedLine(result, lines[index]);
            }
        }

        return result.ToString();
    }

    private static void AppendEscapedLine(StringBuilder result, string line)
    {
        var firstContent = 0;
        while (firstContent < line.Length && char.IsWhiteSpace(line[firstContent]))
        {
            firstContent++;
        }

        var orderedDelimiter = OrderedListDelimiterIndex(line, firstContent);
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            var escapeBlockMarker =
                index == firstContent &&
                character is '#' or '>' or '+' or '-';
            var escapeOrderedDelimiter = index == orderedDelimiter;
            if (AlwaysEscapedMarkdownCharacters.Contains(character) ||
                escapeBlockMarker ||
                escapeOrderedDelimiter)
            {
                result.Append('\\');
            }
            result.Append(character);
        }
    }

    private static int OrderedListDelimiterIndex(string line, int firstContent)
    {
        if (firstContent >= line.Length || !char.IsDigit(line[firstContent]))
        {
            return -1;
        }

        var index = firstContent;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        if (index >= line.Length || line[index] is not ('.' or ')'))
        {
            return -1;
        }

        var after = index + 1;
        return after >= line.Length || char.IsWhiteSpace(line[after])
            ? index
            : -1;
    }
}
