namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void RecordExternalTodoMutationAsUndoStep(
        IReadOnlyList<PaperItem> before)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var editing = before.FirstOrDefault(item => item.Id == _activeOriginalItemId);
        if (editing != null &&
            _activeOriginalText != null &&
            editing.Text != _activeOriginalText)
        {
            // A focused human edit has not reached the paper-level history yet. Record it
            // before the external snapshot so undo order stays human edit -> external write.
            var original = TodoRules.CloneAll(before);
            original.First(item => item.Id == editing.Id).Text = _activeOriginalText;
            _undoStack.Add(original);
        }

        _undoStack.Add(TodoRules.CloneAll(before));
        while (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
        _activeOriginalItemId = null;
        _activeOriginalText = null;
    }

    internal void PruneDeletedLinkedPaperFromTodoHistory(string deletedPaperId)
    {
        if (_paper.Type != PaperTypes.Todo || string.IsNullOrWhiteSpace(deletedPaperId))
        {
            return;
        }

        PruneDeletedLinkedPaper(_undoStack, deletedPaperId);
        PruneDeletedLinkedPaper(_redoStack, deletedPaperId);
    }

    private static void PruneDeletedLinkedPaper(
        IEnumerable<List<PaperItem>> history,
        string deletedPaperId)
    {
        foreach (var snapshot in history)
        {
            foreach (var item in snapshot)
            {
                if (string.Equals(
                        item.LinkedPaperId,
                        deletedPaperId,
                        StringComparison.Ordinal))
                {
                    item.ClearQuickLaunch();
                }
            }
        }
    }

    internal void RefreshTodoRowsForExternalMutation()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        // A post-commit UI retry can run after another human edit. Settle that edit before
        // rebuilding rows, but do not record the already-committed external mutation again.
        CommitFocusedTextIfNeeded();
        _activeOriginalItemId = null;
        _activeOriginalText = null;

        RefreshTodoRowsForExternalChange();
    }
}
