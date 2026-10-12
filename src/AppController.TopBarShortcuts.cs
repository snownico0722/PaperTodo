namespace PaperTodo;

public sealed partial class AppController
{
    internal void SetTopBarShortcut(string paperType, string action, bool pinned)
    {
        if (!PaperTopBarShortcuts.IsAllowed(paperType, action) ||
            PaperTopBarShortcuts.Contains(State, paperType, action) == pinned) return;

        PaperTopBarShortcuts.Set(State, paperType, action, pinned);
        MarkDirty();
        RefreshTopBarShortcuts();
    }

    internal void MoveTopBarShortcut(string paperType, string action, int direction)
    {
        if (!PaperTopBarShortcuts.Move(State, paperType, action, direction)) return;
        MarkDirty();
        RefreshTopBarShortcuts();
    }

    private void RefreshTopBarShortcuts()
    {
        foreach (var window in _windows.Values)
            window.UpdateTopBarNewPaperButtons();
    }
}
