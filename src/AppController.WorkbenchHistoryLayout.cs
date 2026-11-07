namespace PaperTodo;

public sealed partial class AppController
{
    internal WorkbenchState CaptureWorkbenchHistoryLayout()
    {
        var layout = WorkbenchLayout.Copy();
        // A natively undone split keeps its paper only in the shared redo transaction.
        // Include its dormant placement in layout history, never in persisted live state.
        foreach (var transaction in _workbenchContentTransfers.Where(entry =>
                     IsCurrentWorkbenchTransfer(entry) && entry.CreatedTarget && !entry.Applied))
            if (transaction.CreatedCard is { } card)
                layout.Cards.TryAdd(transaction.Target.Id, card.Copy());
        return layout;
    }

    internal void RestoreWorkbenchHistoryLayout(WorkbenchState layout)
    {
        // Replaying an older move while a split is undone must also move its redo
        // placement; otherwise redoing the split resurrects the already undone move.
        foreach (var transaction in _workbenchContentTransfers.Where(entry =>
                     IsCurrentWorkbenchTransfer(entry) && entry.CreatedTarget && !entry.Applied))
            if (layout.Cards.TryGetValue(transaction.Target.Id, out var card))
                transaction.CreatedCard = card.Copy();
        StoreWorkbenchLayout(layout.Copy());
    }
}
