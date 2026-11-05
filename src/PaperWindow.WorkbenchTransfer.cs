using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

namespace PaperTodo;

internal sealed class WorkbenchTransferBoundary
{
    internal Guid Id { get; init; }
    internal object? NoteBefore { get; set; }
    internal object? NoteAfter { get; set; }
    internal List<PaperItem>? TodoUndo { get; set; }
    internal List<PaperItem>? TodoRedo { get; set; }
}

public sealed partial class PaperWindow
{
    private object _workbenchNoteHistoryPosition = new();
    private object? _workbenchNoteRedoPosition;
    private WorkbenchContentTransferPayload? _workbenchNoteSelection;
    private readonly Dictionary<List<PaperItem>, Guid> _workbenchTodoHistory = new();

    internal bool TryCreateWorkbenchContentTransfer(
        out WorkbenchContentTransferPayload payload, bool wholePaper = false)
    {
        payload = null!;
        if (_controller.IsDataReloading || _paper.WorkspaceMaterials != null) return false;
        if (_paper.Type == PaperTypes.Todo)
        {
            CommitFocusedTextIfNeeded();
            var ids = wholePaper ? OrderedItems().Where(TodoRules.HasMeaningfulContent).Select(i => i.Id).ToArray()
                : SelectedTodoItems().Select(i => i.Id).ToArray();
            if (ids.Length == 0 && !wholePaper && CurrentFocusedTodoItemId() is { } focused) ids = [focused];
            if (ids.Length == 0) return false;
            payload = new(_paper.Id, _controller.WorkspaceGeneration, _controller.WorkspaceBindingToken, ids, 0, 0, "");
            return true;
        }
        if (!IsCurrentBodyProviderMarkdown || _noteBox == null) return false;
        var content = _noteBox.PersistentText;
        var start = wholePaper ? 0 : _noteBox.SelectionStart;
        var length = wholePaper ? content.Length : _noteBox.SelectionLength;
        if (length == 0 && !wholePaper && _workbenchNoteSelection is { } cached &&
            cached.NoteStart >= 0 && cached.NoteStart <= content.Length - cached.NoteLength &&
            content.AsSpan(cached.NoteStart, cached.NoteLength).SequenceEqual(cached.ExpectedText))
        {
            payload = cached;
            return true;
        }
        if (length <= 0 || start < 0 || start > content.Length - length) return false;
        payload = new(_paper.Id, _controller.WorkspaceGeneration, _controller.WorkspaceBindingToken,
            [], start, length, content.Substring(start, length));
        return true;
    }

    private void AttachWorkbenchNoteTransfer(MarkdownTextBox box)
    {
        _workbenchNoteHistoryPosition = new();
        _workbenchNoteRedoPosition = null;
        _workbenchNoteSelection = null;
        box.TextArea.SelectionChanged += (_, _) =>
        {
            if (!ReferenceEquals(_noteBox, box)) return;
            if (box.SelectionLength > 0 && TryCreateWorkbenchContentTransfer(out var selection))
                _workbenchNoteSelection = selection;
            else if (box.IsKeyboardFocusWithin) _workbenchNoteSelection = null;
        };
        // A position marker is part of the SAME native document undo group. It carries no
        // text and never edits a document during playback. This lets two independent editors
        // identify a shared transaction boundary without replacing or clearing either stack.
        box.Document.Changing += (_, _) =>
        {
            if (!box.Document.UndoStack.AcceptChanges) return;
            var before = _workbenchNoteHistoryPosition;
            var after = new object();
            object? followingRedo = null;
            _workbenchNoteHistoryPosition = after;
            _workbenchNoteRedoPosition = null;
            box.Document.UndoStack.PushOptional(new WorkbenchNoteHistoryPosition(
                () =>
                {
                    followingRedo = _workbenchNoteRedoPosition;
                    _workbenchNoteHistoryPosition = before;
                    _workbenchNoteRedoPosition = after;
                },
                () =>
                {
                    _workbenchNoteHistoryPosition = after;
                    _workbenchNoteRedoPosition = followingRedo;
                }));
        };
        DataObject.AddCopyingHandler(box, (_, e) =>
        {
            if (e.IsDragDrop && IsWorkbenchHosted && TryCreateWorkbenchContentTransfer(out var payload))
                e.DataObject.SetData(WorkbenchContentTransferPayload.DataFormat, payload);
        });
        box.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (e.Command == ApplicationCommands.Undo)
                e.Handled = _controller.TryHandleWorkbenchContentHistory(_paper.Id, redo: false);
            else if (e.Command == ApplicationCommands.Redo)
                e.Handled = _controller.TryHandleWorkbenchContentHistory(_paper.Id, redo: true);
        }), true);
    }

    internal void PrepareWorkbenchContentTransfer()
    {
        if (_paper.Type == PaperTypes.Todo) CommitFocusedTextIfNeeded();
        else CommitPendingMarkdownContentForSave();
    }

    internal void ResetWorkbenchContentHistory()
    {
        if (IsClosed) return;
        _workbenchNoteHistoryPosition = new();
        _workbenchNoteRedoPosition = null;
        _workbenchNoteSelection = null;
        _noteBox?.Document.UndoStack.ClearAll();
        _undoStack.Clear();
        _redoStack.Clear();
        _workbenchTodoHistory.Clear();
        foreach (var editor in _todoEditors.Values)
        {
            if (!editor.IsUndoEnabled) continue;
            editor.SetCurrentValue(System.Windows.Controls.Primitives.TextBoxBase.IsUndoEnabledProperty, false);
            editor.SetCurrentValue(System.Windows.Controls.Primitives.TextBoxBase.IsUndoEnabledProperty, true);
        }
        // A later LostFocus must not recreate a snapshot spanning the retired boundary.
        _activeOriginalItemId = CurrentFocusedTodoItemId();
        _activeOriginalText = _activeOriginalItemId is { } id && _todoEditors.TryGetValue(id, out var focused)
            ? focused.Text : null;
    }

    internal WorkbenchTransferBoundary ApplyWorkbenchContentTransfer(
        Guid id, WorkbenchContentSnapshot before, WorkbenchContentSnapshot after)
    {
        var boundary = new WorkbenchTransferBoundary { Id = id };
        if (_paper.Type == PaperTypes.Todo)
        {
            boundary.TodoUndo = before.Items;
            _undoStack.Add(boundary.TodoUndo);
            while (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
            _redoStack.Clear();
            _workbenchTodoHistory[boundary.TodoUndo] = id;
            RestoreTodoHistorySnapshot(TodoRules.CloneAll(after.Items), null);
        }
        else if (_noteBox != null)
        {
            boundary.NoteBefore = _workbenchNoteHistoryPosition;
            var document = _noteBox.Document;
            document.UndoStack.StartUndoGroup(boundary);
            try { document.Replace(0, document.TextLength, after.Content); }
            finally { document.UndoStack.EndUndoGroup(); }
            boundary.NoteAfter = _workbenchNoteHistoryPosition;
            CommitPendingMarkdownContentForSave();
            _workbenchNoteSelection = null;
        }
        return boundary;
    }

    internal bool IsAtWorkbenchTransferBoundary(WorkbenchTransferBoundary boundary, bool redo)
    {
        if (IsClosed) return false;
        if (_paper.Type == PaperTypes.Note)
            return _noteBox != null && (redo ? _noteBox.CanRedo : _noteBox.CanUndo) &&
                ReferenceEquals(_workbenchNoteHistoryPosition, redo ? boundary.NoteBefore : boundary.NoteAfter) &&
                (!redo || ReferenceEquals(_workbenchNoteRedoPosition, boundary.NoteAfter));
        var stack = redo ? _redoStack : _undoStack;
        var marker = redo ? boundary.TodoRedo : boundary.TodoUndo;
        return stack.Count > 0 && ReferenceEquals(stack[^1], marker);
    }

    internal void ReplayWorkbenchContentTransfer(WorkbenchTransferBoundary boundary, bool redo,
        WorkbenchContentSnapshot before, WorkbenchContentSnapshot after)
    {
        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            if (redo) _noteBox.Document.UndoStack.Redo();
            else _noteBox.Document.UndoStack.Undo();
            CommitPendingMarkdownContentForSave();
            _workbenchNoteSelection = null;
            return;
        }
        var from = redo ? _redoStack : _undoStack;
        var to = redo ? _undoStack : _redoStack;
        from.RemoveAt(from.Count - 1);
        var destinationMarker = redo ? before.Items : after.Items;
        to.Add(destinationMarker);
        _workbenchTodoHistory[destinationMarker] = boundary.Id;
        if (redo) boundary.TodoUndo = destinationMarker;
        else boundary.TodoRedo = destinationMarker;
        RestoreTodoHistorySnapshot(TodoRules.CloneAll(redo ? after.Items : before.Items), null);
    }

    private bool TryHandleWorkbenchTodoHistory(bool redo)
    {
        var stack = redo ? _redoStack : _undoStack;
        return stack.Count > 0 && _workbenchTodoHistory.ContainsKey(stack[^1]) &&
            _controller.TryHandleWorkbenchContentHistory(_paper.Id, redo);
    }

    private bool TryStartWorkbenchTodoDrag(Point point)
    {
        if (!IsWorkbenchHosted || _todoDrag?.IsDragging != true ||
            new Rect(0, 0, _shell.ActualWidth, _shell.ActualHeight).Contains(point)) return false;
        var ids = _todoGroupDragItemIds.Count > 0 ? _todoGroupDragItemIds.ToArray() : [_todoDrag.ItemId];
        var payload = new WorkbenchContentTransferPayload(_paper.Id, _controller.WorkspaceGeneration,
            _controller.WorkspaceBindingToken, ids, 0, 0, "");
        EndTodoMouseDrag(commit: false);
        var data = new DataObject();
        data.SetData(WorkbenchContentTransferPayload.DataFormat, payload);
        DragDrop.DoDragDrop(_shell, data, DragDropEffects.Copy | DragDropEffects.Move);
        return true;
    }

    private sealed class WorkbenchNoteHistoryPosition(Action undo, Action redo) : IUndoableOperation
    {
        public void Undo() => undo();
        public void Redo() => redo();
    }
}
