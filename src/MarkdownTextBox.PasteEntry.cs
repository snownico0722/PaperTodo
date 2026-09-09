using System.Windows;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    // AvalonEdit can consume preview drag events before ordinary instance handlers. Keep this
    // class-level interception for preview-mode text drops, but route all policy/validation through
    // the same helpers used by the PaperWindow host instead of maintaining a second implementation.
    private static readonly bool PreviewTextDropHandlersRegistered =
        RegisterPreviewTextDropHandlers();

    private static bool RegisterPreviewTextDropHandlers()
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

        e.Effects = ResolveTextDropEffect(e);
        e.Handled = e.Effects != DragDropEffects.None;
    }

    private static void OnPreviewTextDrop(object sender, DragEventArgs e)
    {
        if (sender is not MarkdownTextBox editor ||
            !editor.IsPreviewMode ||
            editor.CanInsertImagesFromDataObject(e.Data) ||
            !HasTextDropData(e.Data) ||
            editor.ValidateTextDrop(e.Data))
        {
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    internal static bool HasTextDropData(IDataObject data)
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

    internal static DragDropEffects ResolveTextDropEffect(DragEventArgs e)
    {
        var controlPressed =
            (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;
        return (e.AllowedEffects & DragDropEffects.Move) != 0 && !controlPressed
            ? DragDropEffects.Move
            : (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
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
