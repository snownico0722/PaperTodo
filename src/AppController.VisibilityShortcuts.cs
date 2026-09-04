using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

public sealed partial class AppController
{
    private HashSet<string>? _visibilityShortcutVisibleLinkedPaperIds;
    private bool _executingVisibilityShortcutCommand;

    private void ExecuteGlobalShortcutCommand(GlobalShortcutDefinition definition)
    {
        var commandKind = definition.StartupCommandKind;
        if (commandKind is not (
                StartupCommandKind.Show or
                StartupCommandKind.Hide or
                StartupCommandKind.Toggle))
        {
            ExecuteStartupCommand(new StartupCommand(commandKind));
            return;
        }

        ExecuteVisibilityShortcut(commandKind);
    }

    private void ExecuteVisibilityShortcut(StartupCommandKind commandKind)
    {
        var effectiveKind = commandKind;
        if (commandKind == StartupCommandKind.Toggle)
        {
            effectiveKind = State.Papers.Any(IsPaperShown)
                ? StartupCommandKind.Hide
                : StartupCommandKind.Show;
        }

        _executingVisibilityShortcutCommand = true;
        try
        {
            switch (effectiveKind)
            {
                case StartupCommandKind.Hide:
                    CaptureVisibilityShortcutRestoreSnapshot();
                    HideAllPapers();
                    break;
                case StartupCommandKind.Show:
                    ShowAllPapersForVisibilityShortcut();
                    break;
            }
        }
        finally
        {
            _executingVisibilityShortcutCommand = false;
            if (effectiveKind == StartupCommandKind.Show)
            {
                ClearVisibilityShortcutRestoreSnapshot();
            }
        }
    }

    private bool IsLinkedPaperProtectedFromVisibilityShortcutRestore(PaperData paper)
    {
        return State.EnableTodoPaperLinks &&
            State.HideLinkedPapersFromCapsules &&
            IsPaperLinkedToAnyTodo(paper);
    }

    private void CaptureVisibilityShortcutRestoreSnapshot()
    {
        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts)
        {
            ClearVisibilityShortcutRestoreSnapshot();
            return;
        }

        _visibilityShortcutVisibleLinkedPaperIds = VisibilityShortcutSnapshot.Capture(
            _visibilityShortcutVisibleLinkedPaperIds,
            State.Papers.Where(paper =>
                    IsLinkedPaperProtectedFromVisibilityShortcutRestore(paper) &&
                    IsPaperShown(paper))
                .Select(paper => paper.Id));
    }

    private void ShowAllPapersForVisibilityShortcut()
    {
        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts ||
            !State.EnableTodoPaperLinks ||
            !State.HideLinkedPapersFromCapsules ||
            _visibilityShortcutVisibleLinkedPaperIds == null)
        {
            ShowAllPapers();
            return;
        }

        // A shortcut hide records only the linked papers that were actually visible. Papers that
        // were already hidden stay hidden, while ordinary papers retain Show All's existing semantics.
        var linkedPapersToRestore = _visibilityShortcutVisibleLinkedPaperIds;
        var papersToShow = State.Papers
            .Where(paper =>
                !IsLinkedPaperProtectedFromVisibilityShortcutRestore(paper) ||
                linkedPapersToRestore.Contains(paper.Id))
            .ToList();

        ShowPapersBatch(papersToShow);
    }

    private void InvalidateVisibilityShortcutSnapshotForExternalCommand()
    {
        if (!_executingVisibilityShortcutCommand)
        {
            ClearVisibilityShortcutRestoreSnapshot();
        }
    }

    private void ClearVisibilityShortcutRestoreSnapshot()
    {
        _visibilityShortcutVisibleLinkedPaperIds = null;
    }

    private void TogglePreserveLinkedPaperHiddenStateInVisibilityShortcuts()
    {
        State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts =
            !State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts;
        if (!State.PreserveLinkedPaperHiddenStateInVisibilityShortcuts)
        {
            ClearVisibilityShortcutRestoreSnapshot();
        }
        MarkDirty();
    }

}
