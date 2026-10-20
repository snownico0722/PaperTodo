namespace PaperTodo;

public sealed partial class AppController
{
    // One global layout: insertion index is measured within the destination section
    // with the moving item excluded.
    internal bool MoveTopBarShortcut(string action, bool pinned, int destinationIndex)
    {
        if (!PaperTopBarShortcuts.MoveTo(State, action, pinned, destinationIndex))
            return false;
        MarkDirty();
        RefreshTopBarShortcuts();
        RefreshSettingsRegions("general.topbar");
        return true;
    }

    private void RefreshTopBarShortcuts()
    {
        foreach (var window in _windows.Values.ToArray())
            window.UpdateTopBarNewPaperButtons();
    }
}
