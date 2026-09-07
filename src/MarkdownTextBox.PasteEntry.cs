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
