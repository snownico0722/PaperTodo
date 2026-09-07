namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    /// <summary>
    /// Keeps explicit paste entry points (notably the note context menu) aligned with Ctrl+V.
    /// AvalonEdit disables its text paste command when the clipboard contains only an image, so
    /// try PaperTodo's image path first in that case and otherwise preserve the native text path.
    /// </summary>
    public new void Paste()
    {
        if (!IsReadOnly &&
            !ClipboardHasText() &&
            TryInsertImageFromClipboard())
        {
            return;
        }

        base.Paste();
    }
}
