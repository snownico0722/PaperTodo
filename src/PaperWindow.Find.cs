using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly List<PaperFindMatch> _findMatches = [];
    private int _findMatchIndex = -1;
    private PaperFindMatch? _findAppliedMatch;
    private TodoTextBox? _findInactiveSelectionEditor;
    private bool _findInactiveSelectionOriginalValue;
    private bool _findSuppressInputTextChanged;
    private bool _findOwnerRefreshQueued;
    private bool _findBodyRefreshQueued;
    private double _findObservedTypographyScale = -1;
    private MarkdownTextBox? _findObservedNoteBox;
    private readonly List<TodoTextBox> _findObservedTodoEditors = [];
    private int _findObservedTodoRowsGeneration = -1;
    private EventHandler? _findNoteTextChangedHandler;
    private TextChangedEventHandler? _findTodoTextChangedHandler;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Built-in find is a fallback. Native/plugin content can consume Ctrl+F before this
        // bubbling Window handler and the host then leaves the gesture alone.
        if (e.Handled ||
            _advancedInteractionLocked ||
            _paper.IsCollapsed ||
            e.Key != Key.F ||
            Keyboard.Modifiers != ModifierKeys.Control ||
            !CanUseBuiltInFind())
        {
            return;
        }

        e.Handled = true;
        ShowBuiltInFind();
    }

    private bool CanUseBuiltInFind()
    {
        if (!_isShellBuilt)
        {
            return false;
        }

        return _paper.Type == PaperTypes.Todo ||
               (_paper.Type == PaperTypes.Note &&
                IsCurrentBodyProviderMarkdown &&
                _noteBox != null);
    }

    private void ShowBuiltInFind()
    {
        EnsureBuiltInFindPopup();
        if (_findPopup == null || _findInput == null)
        {
            return;
        }

        PaperFindMatch? preferredMatch = null;
        if (TryGetCurrentFindSeed(out var seed, out var seedMatch) &&
            seed.Length <= 200 &&
            seed.IndexOfAny(['\r', '\n']) < 0)
        {
            preferredMatch = seedMatch;
            SetBuiltInFindQuery(seed, preferredMatch);
        }
        else
        {
            RebuildFindMatches(preserveCurrent: true);
        }

        UpdateBuiltInFindVisuals();
        _findPopup.IsOpen = true;
        SynchronizeBuiltInFindOwnerState(refreshMatches: false);
        _findInput.Focus();
        _findInput.SelectAll();

        // Focusing the popup can make Markdown enter its normal preview state, which clears the
        // editor selection. Re-apply the selected match after that focus transition settles.
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            ApplyCurrentFindMatch();
            RepositionBuiltInFindPopup();
        }), DispatcherPriority.Background);
    }

    private bool TryGetCurrentFindSeed(
        out string seed,
        out PaperFindMatch match)
    {
        if (_paper.Type == PaperTypes.Note &&
            _noteBox is { IsKeyboardFocusWithin: true, SelectionLength: > 0 } note)
        {
            seed = note.SelectedText ?? string.Empty;
            match = new PaperFindMatch(null, note.SelectionStart, note.SelectionLength);
            return seed.Length > 0;
        }

        if (_paper.Type == PaperTypes.Todo &&
            Keyboard.FocusedElement is TodoTextBox { SelectionLength: > 0 } todo)
        {
            foreach (var pair in _todoEditors)
            {
                if (!ReferenceEquals(pair.Value, todo))
                {
                    continue;
                }

                seed = todo.SelectedText;
                match = new PaperFindMatch(pair.Key, todo.SelectionStart, todo.SelectionLength);
                return seed.Length > 0;
            }
        }

        seed = string.Empty;
        match = default;
        return false;
    }

    private void SetBuiltInFindQuery(
        string query,
        PaperFindMatch? preferredMatch = null)
    {
        if (_findInput == null)
        {
            return;
        }

        if (string.Equals(_findInput.Text, query, StringComparison.Ordinal))
        {
            RebuildFindMatches(
                preserveCurrent: preferredMatch == null,
                preferredMatch: preferredMatch);
            return;
        }

        _findSuppressInputTextChanged = true;
        try
        {
            _findInput.Text = query;
        }
        finally
        {
            _findSuppressInputTextChanged = false;
        }

        RebuildFindMatches(
            preserveCurrent: false,
            preferredMatch: preferredMatch);
    }

    private void OnBuiltInFindOwnerLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsBuiltInFindOpen || _findOwnerRefreshQueued)
        {
            return;
        }

        _findOwnerRefreshQueued = true;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            _findOwnerRefreshQueued = false;
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            SynchronizeBuiltInFindOwnerState(refreshMatches: true);
        }), DispatcherPriority.Background);
    }

    private void SynchronizeBuiltInFindOwnerState(bool refreshMatches)
    {
        if (!IsBuiltInFindOpen)
        {
            return;
        }

        if (!IsVisible ||
            WindowState == WindowState.Minimized ||
            _paper.IsCollapsed ||
            !CanUseBuiltInFind())
        {
            HideBuiltInFind(restoreFocus: false);
            return;
        }

        var visualsChanged = BuiltInFindVisualsNeedRefresh();
        if (visualsChanged)
        {
            UpdateBuiltInFindVisuals();
            RepositionBuiltInFindPopup();
        }

        var bodyChanged =
            (_paper.Type == PaperTypes.Note &&
             !ReferenceEquals(_findObservedNoteBox, _noteBox)) ||
            (_paper.Type == PaperTypes.Todo &&
             _findObservedTodoRowsGeneration != _todoRowsGeneration);
        if (!bodyChanged)
        {
            return;
        }

        HookBuiltInFindContentChanges();
        if (refreshMatches)
        {
            RebuildFindMatches(
                preserveCurrent: true,
                applySelection: !IsBuiltInFindBodyKeyboardFocusWithin());
        }
    }

    private void HookBuiltInFindContentChanges()
    {
        UnhookBuiltInFindContentChanges();

        _findNoteTextChangedHandler ??= (_, _) => QueueBuiltInFindBodyRefresh();
        _findTodoTextChangedHandler ??= (_, _) => QueueBuiltInFindBodyRefresh();

        if (_paper.Type == PaperTypes.Note && _noteBox != null)
        {
            _findObservedNoteBox = _noteBox;
            _findObservedNoteBox.TextChanged += _findNoteTextChangedHandler;
            _findObservedTodoRowsGeneration = -1;
            return;
        }

        _findObservedNoteBox = null;
        if (_paper.Type != PaperTypes.Todo)
        {
            _findObservedTodoRowsGeneration = -1;
            return;
        }

        _findObservedTodoRowsGeneration = _todoRowsGeneration;
        foreach (var editor in _todoEditors.Values.Distinct())
        {
            _findObservedTodoEditors.Add(editor);
            editor.TextChanged += _findTodoTextChangedHandler;
        }
    }

    private void UnhookBuiltInFindContentChanges()
    {
        if (_findObservedNoteBox != null && _findNoteTextChangedHandler != null)
        {
            _findObservedNoteBox.TextChanged -= _findNoteTextChangedHandler;
        }
        _findObservedNoteBox = null;

        if (_findTodoTextChangedHandler != null)
        {
            foreach (var editor in _findObservedTodoEditors)
            {
                editor.TextChanged -= _findTodoTextChangedHandler;
            }
        }
        _findObservedTodoEditors.Clear();
        _findObservedTodoRowsGeneration = -1;
    }

    private void QueueBuiltInFindBodyRefresh()
    {
        if (!IsBuiltInFindOpen || _findBodyRefreshQueued)
        {
            return;
        }

        _findBodyRefreshQueued = true;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            _findBodyRefreshQueued = false;
            if (!IsBuiltInFindOpen)
            {
                return;
            }

            RebuildFindMatches(
                preserveCurrent: true,
                applySelection: !IsBuiltInFindBodyKeyboardFocusWithin());
        }), DispatcherPriority.Background);
    }

    private bool IsBuiltInFindBodyKeyboardFocusWithin()
    {
        if (_paper.Type == PaperTypes.Note)
        {
            return _noteBox?.IsKeyboardFocusWithin == true;
        }

        return Keyboard.FocusedElement is TodoTextBox todo &&
               _todoEditors.Values.Any(editor => ReferenceEquals(editor, todo));
    }

    private void RebuildFindMatches(
        bool preserveCurrent,
        PaperFindMatch? preferredMatch = null,
        bool applySelection = true)
    {
        if (_findInput == null)
        {
            return;
        }

        var previous = preserveCurrent && TryGetCurrentFindMatch(out var current)
            ? current
            : (PaperFindMatch?)null;

        var query = _findInput.Text ?? string.Empty;
        SetFindMatches(_controller.GetBuiltInFindMatches(query));

        if (_findMatches.Count == 0)
        {
            _findMatchIndex = -1;
            ClearAppliedFindSelection();
            RefreshTodoFindCues();
            _globalFindIndex = -1;
            UpdateFindCount();
            return;
        }

        var preferredIndex = preferredMatch is PaperFindMatch preferred
            ? _findMatches.IndexOf(preferred)
            : -1;
        var previousIndex = previous is PaperFindMatch kept
            ? _findMatches.IndexOf(kept)
            : -1;
        _findMatchIndex = preferredIndex >= 0
            ? preferredIndex
            : previousIndex >= 0
                ? previousIndex
                : 0;

        if (applySelection)
        {
            ApplyCurrentFindMatch();
        }
        else
        {
            _findAppliedMatch = null;
            ReleaseTodoInactiveFindSelection();
        }
        RefreshTodoFindCues();
        _globalFindIndex = ResolveCurrentGlobalFindIndex();
        UpdateFindCount();
    }

    private void SetFindMatches(List<GlobalFindMatch> matches)
    {
        _globalFindMatches = matches;
        _findMatches.Clear();

        // The local counter only includes rows with a live editor. Global results
        // also contain persisted rows from windows that have not built their UI.
        foreach (var hit in _globalFindMatches)
        {
            if (hit.PaperId == _paper.Id && (hit.Match.TodoItemId is { } itemId
                    ? _todoEditors.ContainsKey(itemId) : _noteBox != null))
                _findMatches.Add(hit.Match);
        }
    }

    private bool TryGetCurrentFindMatch(out PaperFindMatch match)
    {
        if (_findMatchIndex >= 0 && _findMatchIndex < _findMatches.Count)
        {
            match = _findMatches[_findMatchIndex];
            return true;
        }

        match = default;
        return false;
    }

    private void ApplyCurrentFindMatch()
    {
        if (!TryGetCurrentFindMatch(out var match))
        {
            ClearAppliedFindSelection();
            return;
        }

        if (_findAppliedMatch is PaperFindMatch previous && previous != match)
        {
            ClearFindSelection(previous);
        }

        if (match.IsNote)
        {
            ReleaseTodoInactiveFindSelection();
            if (_noteBox == null ||
                match.Offset < 0 ||
                match.Offset + match.Length > _noteBox.Text.Length)
            {
                _findAppliedMatch = null;
                return;
            }

            _noteBox.Select(match.Offset, match.Length);
            if (_noteBox.Document.TextLength > 0)
            {
                var lineOffset = Math.Min(match.Offset, _noteBox.Document.TextLength - 1);
                _noteBox.ScrollToLine(_noteBox.Document.GetLineByOffset(lineOffset).LineNumber);
            }
            _findAppliedMatch = match;
            return;
        }

        if (match.TodoItemId == null ||
            !_todoEditors.TryGetValue(match.TodoItemId, out var editor) ||
            match.Offset < 0 ||
            match.Offset + match.Length > editor.Text.Length)
        {
            _findAppliedMatch = null;
            return;
        }

        if (IsTodoFindMatchHidden(match))
        {
            // Selecting text that is visually hidden can scroll the clipped editor;
            // cue the user without changing either folding state or search focus.
            ReleaseTodoInactiveFindSelection();
            _todoRows.FirstOrDefault(row =>
                string.Equals(row.Tag as string, match.TodoItemId, StringComparison.Ordinal))
                ?.BringIntoView();
            _findAppliedMatch = match;
            return;
        }

        EnableTodoInactiveFindSelection(editor);
        editor.Select(match.Offset, match.Length);
        _todoRows.FirstOrDefault(row =>
            string.Equals(row.Tag as string, match.TodoItemId, StringComparison.Ordinal))
            ?.BringIntoView();
        _findAppliedMatch = match;
    }

    private void ClearAppliedFindSelection()
    {
        if (_findAppliedMatch is PaperFindMatch applied)
        {
            ClearFindSelection(applied);
        }
        _findAppliedMatch = null;
    }

    private void ClearFindSelection(PaperFindMatch match)
    {
        if (match.IsNote)
        {
            if (_noteBox != null &&
                _noteBox.SelectionStart == match.Offset &&
                _noteBox.SelectionLength == match.Length)
            {
                _noteBox.SelectionLength = 0;
            }
            return;
        }

        if (match.TodoItemId != null &&
            _todoEditors.TryGetValue(match.TodoItemId, out var editor) &&
            editor.SelectionStart == match.Offset &&
            editor.SelectionLength == match.Length)
        {
            editor.SelectionLength = 0;
        }
        ReleaseTodoInactiveFindSelection();
    }

    private void EnableTodoInactiveFindSelection(TodoTextBox editor)
    {
        if (ReferenceEquals(_findInactiveSelectionEditor, editor))
        {
            return;
        }

        ReleaseTodoInactiveFindSelection();
        _findInactiveSelectionEditor = editor;
        _findInactiveSelectionOriginalValue = editor.IsInactiveSelectionHighlightEnabled;
        editor.IsInactiveSelectionHighlightEnabled = true;
    }

    private void ReleaseTodoInactiveFindSelection()
    {
        if (_findInactiveSelectionEditor == null)
        {
            return;
        }

        _findInactiveSelectionEditor.IsInactiveSelectionHighlightEnabled =
            _findInactiveSelectionOriginalValue;
        _findInactiveSelectionEditor = null;
    }

    private bool TryHandleBuiltInFindPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsBuiltInFindOpen ||
            e.Key != Key.Escape ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        HideBuiltInFind(restoreFocus: true);
        e.Handled = true;
        return true;
    }

    private void HideBuiltInFind(bool restoreFocus)
    {
        if (_findPopup == null || !_findPopup.IsOpen)
        {
            return;
        }

        var hasMatch = TryGetCurrentFindMatch(out var match);
        _findPopup.IsOpen = false;
        RefreshTodoFindCues();

        if (!restoreFocus ||
            !IsVisible ||
            _paper.IsCollapsed ||
            WindowState == WindowState.Minimized)
        {
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        if (hasMatch && match.IsNote)
        {
            if (_noteBox is { IsPreviewMode: false } note)
            {
                note.Focus();
            }
            else
            {
                Focus();
            }
            return;
        }

        if (hasMatch &&
            match.TodoItemId != null &&
            _todoEditors.TryGetValue(match.TodoItemId, out var editor) &&
            !_foldedTodoItemIds.Contains(match.TodoItemId) &&
            !IsTodoFindMatchHidden(match))
        {
            // Giving a folded editor focus would reveal its full text on search
            // close, even if the user never chose to expand it.
            editor.Focus();
            return;
        }

        Focus();
    }
}
