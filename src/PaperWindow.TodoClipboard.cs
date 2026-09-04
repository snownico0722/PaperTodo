using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool TryCopySelectedTodosAsMarkdown()
    {
        if (_paper.Type != PaperTypes.Todo || _selectedTodoItemIds.Count == 0)
        {
            return false;
        }
        if (FocusManager.GetFocusedElement(this) is TodoTextBox { SelectionLength: > 0 })
        {
            return false;
        }

        // Reuse the selection owner's ordering. This is export only: do not mutate items,
        // reminder metadata, the undo stack, or the ordinary Ctrl+C plain-text contract.
        var selected = SelectedTodoItems();
        return selected.Count > 0 && ClipboardHelper.TrySetText(
            TodoClipboardFormatter.ToMarkdown(selected.Select(item => (item.Text, item.Done))));
    }
}
