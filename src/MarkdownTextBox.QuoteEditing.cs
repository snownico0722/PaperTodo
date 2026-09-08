using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    /// <summary>
    /// Full 编辑态在引用内容行按 Enter 时按语义层级续前缀；空引用行返回 false，交默认换行结束引用。
    /// </summary>
    private bool TryContinueQuoteOnEnter(DocumentLine line, string text)
    {
        if (!RenderModeIsFull || !TryGetCurrentSemanticSnapshot(out var snapshot))
        {
            return false;
        }

        var level = snapshot.GetLine(Math.Max(0, line.LineNumber - 1)).QuoteLevel;
        if (level <= 0)
        {
            return false;
        }

        var caret = Math.Clamp(CaretOffset, 0, Document!.TextLength);
        var indexInLine = Math.Clamp(caret - line.Offset, 0, text.Length);
        var contentStart = QuoteContentStart(text);
        if (indexInLine < contentStart)
        {
            // 光标还停在 marker 前缀里：不主动续行，走默认换行。
            return false;
        }

        if (IsQuoteLineEmpty(text, contentStart))
        {
            // 空引用行 Enter → 默认换行产生空行，引用到此结束。
            return false;
        }

        var prefix = MarkdownQuoteMarkers.RepeatMarkerPrefix(level);
        var insertion = NewLineTextFor(line) + prefix;
        if (MaxLength > 0 && Text.Length + insertion.Length > MaxLength)
        {
            return false;
        }

        Document.BeginUpdate();
        try
        {
            Document.Insert(caret, insertion);
            CaretOffset = caret + insertion.Length;
            Select(CaretOffset, 0);
        }
        finally
        {
            Document.EndUpdate();
        }

        return true;
    }

    /// <summary>行首显式引用 marker 组之后的正文起点。</summary>
    private static int QuoteContentStart(string text)
    {
        var markers = MarkdownQuoteMarkers.EnumerateMarkers(text, 0, text.Length);
        return markers.Count == 0 ? 0 : markers[^1].End;
    }

    private static bool IsQuoteLineEmpty(string text, int contentStart)
    {
        for (var index = contentStart; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
