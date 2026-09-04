using System.Text;

namespace PaperTodo;

internal static class TodoClipboardFormatter
{
    // Todo text is plain text. Escape Markdown punctuation rather than interpreting a user's
    // literal brackets, paths, links or formatting as markup in the receiving application.
    private const string MarkdownPunctuation = "\\`*_{}[]<>()#+-.!|~&=";

    internal static string ToMarkdown(IEnumerable<(string Text, bool Done)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new StringBuilder();
        foreach (var (text, done) in items)
        {
            if (result.Length > 0) result.AppendLine();
            result.Append(done ? "- [x] " : "- [ ] ");
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) result.AppendLine().Append("    ");
                foreach (var character in lines[index])
                {
                    if (MarkdownPunctuation.Contains(character)) result.Append('\\');
                    result.Append(character);
                }
            }
        }
        return result.ToString();
    }
}
