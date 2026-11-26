using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal void ApplyReloadedPaper(PaperData before)
    {
        if (_paper.Type == PaperTypes.Note && before.Content != _paper.Content)
            RefreshNoteForExternalChange();
        if (_paper.Type == PaperTypes.Todo && !StateReloadModels.Equivalent(before.Items, _paper.Items))
        {
            RecordExternalTodoMutationAsUndoStep(TodoRules.CloneAll(before.Items));
            RefreshTodoRowsForExternalChange();
        }
        if (before.Title != _paper.Title || before.BodyHeaderText != _paper.BodyHeaderText ||
            before.BodyCapsuleText != _paper.BodyCapsuleText) RefreshPaperTitle();
        if (before.TextZoom != _paper.TextZoom) UpdateTextZoom();
        if (before.AlwaysOnTop != _paper.AlwaysOnTop) RefreshEffectiveTopmost();
        if (before.IsCollapsed != _paper.IsCollapsed)
        {
            var collapsed = _paper.IsCollapsed;
            var expandingFromDeepCapsuleEdge = !collapsed && HasDeepCapsuleSlotPlacement;
            _paper.IsCollapsed = before.IsCollapsed;
            if (expandingFromDeepCapsuleEdge) ShowMainWindowForDeepCapsuleActivation();
            SetCollapsedState(collapsed, animate: false, saveGeometry: false,
                alignExpandedToDockedEdge: expandingFromDeepCapsuleEdge);
        }
        if (before.CapsuleSide != _paper.CapsuleSide ||
            before.CapsuleMonitorDeviceName != _paper.CapsuleMonitorDeviceName)
            UpdateDeepCapsuleMode();
        if ((before.X != _paper.X || before.Y != _paper.Y ||
             before.Width != _paper.Width || before.Height != _paper.Height) &&
            !(_paper.IsCollapsed && _controller.State.UseDeepCapsuleMode && _controller.State.UseCapsuleMode))
        {
            MoveWindowWithoutGeometrySave(() =>
            {
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                Left = _paper.X;
                Top = _paper.Y;
                Width = _paper.IsCollapsed && _controller.State.UseCapsuleMode ? DesiredCapsuleWindowWidth : _paper.Width;
                Height = _paper.IsCollapsed && _controller.State.UseCapsuleMode ? PaperLayoutDefaults.CapsuleHeight : _paper.Height;
            });
        }
    }

    // Takes ownership of a detached snapshot of the rows before the change.
    internal void RecordExternalTodoMutationAsUndoStep(
        List<PaperItem> snapshot)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return;
        }

        var editing = snapshot.FirstOrDefault(item => item.Id == _activeOriginalItemId);
        if (editing != null &&
            _activeOriginalText != null &&
            editing.Text != _activeOriginalText)
        {
            // A focused human edit has not reached the paper-level history yet. Record it
            // before the external snapshot so undo order stays human edit -> external write.
            var original = TodoRules.CloneAll(snapshot);
            original.First(item => item.Id == editing.Id).Text = _activeOriginalText;
            _undoStack.Add(original);
        }

        _undoStack.Add(snapshot);
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

        // Settle any focused human edit before rebuilding rows, without recording the already
        // committed external mutation a second time.
        CommitFocusedTextIfNeeded();
        _activeOriginalItemId = null;
        _activeOriginalText = null;

        RefreshTodoRowsForExternalChange();
    }
}
