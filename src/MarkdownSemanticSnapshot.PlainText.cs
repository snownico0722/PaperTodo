namespace PaperTodo;

internal sealed partial class MarkdownSemanticSnapshot
{
    internal static string ToPlainText(string? markdown)
    {
        var source = markdown ?? string.Empty;
        if (source.Length == 0)
        {
            return string.Empty;
        }

        // Use the exact same Markdig pipeline as PaperTodo's semantic renderer. Shift-copy is a
        // presentation conversion, not a second Markdown parser with subtly different rules.
        var text = Markdig.Markdown.ToPlainText(source, Pipeline);
        return RemoveRendererTerminalLineBreak(text);
    }

    private static string RemoveRendererTerminalLineBreak(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }
        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            return text[..^1];
        }
        return text;
    }
}
