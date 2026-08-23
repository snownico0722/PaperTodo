namespace PaperTodo;

public sealed partial class PaperWindow
{
    public void RefreshLinkedNoteRows(string? noteId)
    {
        if (_paper.Type != PaperTypes.Todo ||
            _todoPanel == null ||
            string.IsNullOrWhiteSpace(noteId))
        {
            return;
        }

        var affectedItems = _paper.Items
            .Where(item => string.Equals(
                item.LinkedNoteId,
                noteId,
                StringComparison.Ordinal))
            .ToList();
        if (affectedItems.Count == 0)
        {
            return;
        }

        var focusedItemId = CurrentFocusedTodoItemId();

        foreach (var item in affectedItems)
        {
            var oldRow = _todoRows.FirstOrDefault(row =>
                row.Tag is string itemId &&
                string.Equals(itemId, item.Id, StringComparison.Ordinal));
            if (oldRow == null)
            {
                continue;
            }

            var rowIndex = _todoRows.IndexOf(oldRow);
            var panelIndex = _todoPanel.Children.IndexOf(oldRow);
            if (rowIndex < 0 || panelIndex < 0)
            {
                continue;
            }

            if (ReferenceEquals(_linkedNoteDropRow, oldRow))
            {
                _linkedNoteDropRow = null;
            }
            if (ReferenceEquals(_activeDropRow, oldRow))
            {
                _activeDropRow = null;
            }

            _todoEditors.Remove(item.Id);
            _todoRows.RemoveAt(rowIndex);
            _todoPanel.Children.RemoveAt(panelIndex);

            var newRow = (System.Windows.Controls.Border)BuildTodoRow(item, isNewItem: false);
            _todoRows.Remove(newRow);
            _todoRows.Insert(rowIndex, newRow);
            _todoPanel.Children.Insert(panelIndex, newRow);
        }

        if (!string.IsNullOrWhiteSpace(focusedItemId))
        {
            FocusTodoItem(focusedItemId);
        }
    }
}
