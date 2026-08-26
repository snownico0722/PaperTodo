namespace PaperTodo;

public sealed partial class PaperWindow
{
    public void RefreshLinkedPaperRows(string? paperId)
    {
        if (_paper.Type != PaperTypes.Todo ||
            _todoPanel == null ||
            string.IsNullOrWhiteSpace(paperId))
        {
            return;
        }

        var affectedItemIds = _paper.Items
            .Where(item => item.LinkedPaperIdsInternal.Any(id => string.Equals(id, paperId, StringComparison.Ordinal)))
            .Select(item => item.Id)
            .ToArray();
        if (affectedItemIds.Length == 0)
        {
            return;
        }

        ReconcileTodoRows(
            affectedItemIds,
            CurrentFocusedTodoItemId());
    }
}
