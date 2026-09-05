namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void RefreshTodoRowsForExternalMutation()
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        // Undo/redo stores whole-paper snapshots. Once MCP or a plugin mutates the paper,
        // those snapshots are no longer safe to replay because they can overwrite the
        // externally committed state. Also suppress the focused editor's LostFocus handler
        // from recreating a stale undo snapshot while the rows are rebuilt.
        _undoStack.Clear();
        _redoStack.Clear();
        _activeOriginalItemId = null;
        _activeOriginalText = null;

        RefreshTodoRowsForExternalChange();
    }
}
