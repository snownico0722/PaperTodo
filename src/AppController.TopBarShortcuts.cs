using System;
using System.Linq;
using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    // One global layout: insertion index is measured within the destination section
    // with the moving item excluded.
    internal bool MoveTopBarShortcut(string action, bool pinned, int destinationIndex)
    {
        if (FollowsMainWorkspaceSettings || IsExiting) return false;
        if (!PaperTopBarShortcuts.MoveTo(State, action, pinned, destinationIndex))
            return false;
        MarkDirty();
        RefreshTopBarShortcuts();
        RefreshSettingsRegions("general.topbar");
        return true;
    }

    internal void SetTopBarShortcutHidden(string action, bool hidden)
    {
        if (FollowsMainWorkspaceSettings || IsExiting || !PaperTopBarShortcuts.All.Contains(action)) return;
        var entries = State.HiddenTopBarShortcuts ??= [];
        if (hidden == entries.Contains(action)) return;
        if (hidden) entries.Add(action);
        else entries.Remove(action);
        MarkDirty();
        RefreshTopBarShortcuts();
    }

    internal void EditTopBarScript(Window? owner = null)
    {
        if (IsExiting) return;
        var state = State;
        var result = ScriptShortcutDialog.Edit(owner ?? _settingsWindow, state.TopBarScript,
            readOnly: FollowsMainWorkspaceSettings);
        // The dialog can pump workspace/exit messages. Never save its result into
        // a different workspace or into settings that became inherited meanwhile.
        if (result == null || IsExiting || !ReferenceEquals(state, State) || FollowsMainWorkspaceSettings ||
            string.Equals(result, state.TopBarScript, StringComparison.Ordinal)) return;
        state.TopBarScript = result;
        MarkDirty();
    }

    internal void RunTopBarScript(Window? owner)
    {
        if (IsExiting) return;
        if (string.IsNullOrWhiteSpace(State.TopBarScript))
        {
            EditTopBarScript(owner);
            return;
        }
        var state = State;
        ScriptExecutionService.TryParse(state.TopBarScript, allowDefaultEngine: true, out var spec);
        _ = ScriptExecutionService.RunAsync(spec, state, message =>
        {
            if (IsExiting || _workspaceClosingSurfaces || !ReferenceEquals(state, State)) return;
            MessageBox.Show(message, Strings.Get("ScriptCapsuleFailureTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void RefreshTopBarShortcuts()
    {
        foreach (var window in _windows.Values.ToArray())
            window.UpdateTopBarNewPaperButtons();
    }
}
