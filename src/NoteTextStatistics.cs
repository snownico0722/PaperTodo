using System.Text;

namespace PaperTodo;

internal static class NoteTextStatistics
{
    // Count displayed non-whitespace Unicode characters (including punctuation), not Markdown
    // delimiters, link destinations or image URIs. Use the existing renderer's plain-text path.
    internal static int Count(string text)
    {
        var count = 0;
        foreach (var rune in MarkdownSemanticSnapshot.ToPlainText(text).EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune)) count++;
        return count;
    }
}
