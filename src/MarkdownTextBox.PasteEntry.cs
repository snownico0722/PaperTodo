using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private static readonly bool PreviewTextDropEffectHandlerRegistered =
        RegisterPreviewTextDropEffectHandler();

    private static bool RegisterPreviewTextDropEffectHandler()
    {
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            DragDrop.PreviewDragOverEvent,
            new DragEventHandler(OnPreviewTextDragOver),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            DragDrop.PreviewDropEvent,
            new DragEventHandler(OnPreviewTextDrop),
            handledEventsToo: true);
        return true;
    }

    private static void OnPreviewTextDragOver(object sender, DragEventArgs e)
    {
        if (sender is not MarkdownTextBox editor ||
            !editor.IsPreviewMode ||
            editor.CanInsertImagesFromDataObject(e.Data) ||
            !HasTextDropData(e.Data))
        {
            return;
        }

        var controlPressed =
            (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 && !controlPressed
            ? DragDropEffects.Move
            : (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = e.Effects != DragDropEffects.None;
    }

    private static void OnPreviewTextDrop(object sender, DragEventArgs e)
    {
        if (sender is not MarkdownTextBox editor ||
            !editor.IsPreviewMode ||
            editor.CanInsertImagesFromDataObject(e.Data) ||
            !HasTextDropData(e.Data))
        {
            return;
        }

        string? text;
        try
        {
            text = e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? e.Data.GetData(DataFormats.UnicodeText) as string
                : e.Data.GetDataPresent(DataFormats.Text)
                    ? e.Data.GetData(DataFormats.Text) as string
                    : null;
        }
        catch
        {
            editor.PasteRejected?.Invoke();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Preview mode is intentionally read-only until PaperWindow places the caret and enters
        // edit mode. Validate the payload here, before that state change, so the native Drop path
        // cannot bypass the same length/line-length guard used by normal paste.
        if (string.IsNullOrEmpty(text) ||
            editor.TryBuildSafePasteText(text, selectedLength: 0, out _))
        {
            return;
        }

        editor.PasteRejected?.Invoke();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static bool HasTextDropData(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.UnicodeText) ||
                data.GetDataPresent(DataFormats.Text);
        }
        catch
        {
            return false;
        }
    }

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
