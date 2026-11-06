namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal ExpandedEditorState? SavedExpandedEditorState => _paper.ExpandedEditor;

    internal void MarkExpandedEditorViewDirty()
    {
        if (IsExpandedEditorHosted && !_controller.SuppressDataReloadEditorCommit)
            _controller.MarkDirty();
    }

    internal void CaptureExpandedEditorState(bool markDirty)
    {
        if (_expandedEditorHostWindow is not { IsLoaded: true } host ||
            !_controller.IsRunning || _controller.SuppressDataReloadEditorCommit)
            return;

        // Only window geometry and sidebar visibility are persisted. The live editor
        // already owns its caret/selection; never replay stale document offsets.
        var state = host.CaptureWindowState();
        if (_paper.ExpandedEditor == state) return;
        _paper.ExpandedEditor = state;
        if (markDirty) _controller.MarkDirty();
    }
}
