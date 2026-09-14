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
            RecordExternalTodoMutationAsUndoStep(before.Items);
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
}
