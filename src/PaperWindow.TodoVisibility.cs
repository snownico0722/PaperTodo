namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Owned by the corresponding live row. Full rebuild clears the map; a rebuilt or removed
    // row replaces/removes its callback. No parallel model or persisted visibility state.
    private readonly Dictionary<string, Action> _todoLinkedPresentationRefreshers = new(StringComparer.Ordinal);

    public void RefreshTodoRowsForPaperVisibility()
    {
        if (_paper.Type != PaperTypes.Todo || _todoPanel == null ||
            !_controller.State.EnableTodoNoteLinks) return;
        // Merely change the linked button's appearance. Keep all editors, selection, native
        // undo history and row order; hidden/deferred bodies must not be built for this refresh.
        foreach (var refresh in _todoLinkedPresentationRefreshers.Values) refresh();
    }
}
