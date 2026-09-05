namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    internal bool TryCopySelectionAsPlainText()
    {
        var markdown = TextArea.Selection.GetText();
        if (string.IsNullOrEmpty(markdown))
        {
            return false;
        }

        return ClipboardHelper.TrySetText(
            MarkdownSemanticSnapshot.ToPlainText(markdown));
    }
}
