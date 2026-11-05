using System;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _restoringExpandedEditorPosition;

    internal ExpandedEditorState? SavedExpandedEditorState => _paper.ExpandedEditor;

    internal void MarkExpandedEditorViewDirty()
    {
        if (IsExpandedEditorHosted && !_restoringExpandedEditorPosition &&
            !_controller.SuppressDataReloadEditorCommit)
            _controller.MarkDirty();
    }

    internal void CaptureExpandedEditorState(bool markDirty)
    {
        if (_expandedEditorHostWindow is not { IsLoaded: true } host ||
            _controller.IsExiting || _controller.SuppressDataReloadEditorCommit)
            return;

        var state = host.CaptureWindowState();
        var position = _paper.ExpandedEditor?.NotePosition;
        if (!_restoringExpandedEditorPosition && IsCurrentBodyProviderMarkdown && _noteBox is { } box)
        {
            position = new NoteEditorPosition
            {
                Caret = box.CaretOffset,
                SelectionStart = box.SelectionStart,
                SelectionLength = box.SelectionLength,
                HorizontalOffset = box.HorizontalOffset,
                VerticalOffset = box.VerticalOffset
            };
        }
        state = state with { SidebarOpen = _isExpandedEditorSidebarOpen, NotePosition = position };
        if (_paper.ExpandedEditor == state) return;
        _paper.ExpandedEditor = state;
        if (markDirty) _controller.MarkDirty();
    }

    internal void RestoreExpandedEditorPosition()
    {
        _restoringExpandedEditorPosition = false;
        if (!IsCurrentBodyProviderMarkdown || _noteBox is not { } box ||
            _paper.ExpandedEditor?.NotePosition is not { } position)
            return;

        // Apply after the borrowed shell has its editor-sized viewport, before normal input.
        // A queued restore must never run on a returned/reborrowed visual or replacement body.
        var lease = _expandedEditorVisualLease;
        _restoringExpandedEditorPosition = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(lease, _expandedEditorVisualLease) || !ReferenceEquals(box, _noteBox))
                return;
            try
            {
                box.UpdateLayout();
                var length = box.Document.TextLength;
                var start = Math.Clamp(position.SelectionStart, 0, length);
                box.Select(start, Math.Clamp(position.SelectionLength, 0, length - start));
                box.CaretOffset = Math.Clamp(position.Caret, 0, length);
                box.ScrollToHorizontalOffset(FiniteOffset(position.HorizontalOffset));
                box.ScrollToVerticalOffset(FiniteOffset(position.VerticalOffset));
            }
            finally { _restoringExpandedEditorPosition = false; }
        }));
    }

    private static double FiniteOffset(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
