using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly Dictionary<string, (Border Button, System.Windows.Shapes.Path Icon)>
        _todoFoldFindControls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _findHiddenTodoItemIds = new(StringComparer.Ordinal);
    private bool _findHiddenCompletedTodoMatch;

    private bool IsTodoFindMatchBehindFold(PaperFindMatch match)
    {
        if (match.TodoItemId == null ||
            !_foldedTodoItemIds.Contains(match.TodoItemId) ||
            !_todoEditors.TryGetValue(match.TodoItemId, out var editor) ||
            editor.MaxLines != TodoFoldVisibleLines ||
            editor.IsKeyboardFocusWithin ||
            match.Length <= 0 || editor.Text.Length == 0)
            return false;

        var last = Math.Clamp(match.Offset + match.Length - 1, 0, editor.Text.Length - 1);
        var displayLine = editor.GetLineIndexFromCharacterIndex(last);
        // Clipped lines can be unrealized, but must never be treated as visible.
        return displayLine < 0 || displayLine >= TodoFoldVisibleLines;
    }

    private bool IsTodoFindMatchHidden(PaperFindMatch match) =>
        match.TodoItemId != null &&
        (IsCompletedTodoItemHidden(match.TodoItemId) ||
         IsTodoFindMatchBehindFold(match));

    private void RefreshTodoFindCues()
    {
        _findHiddenTodoItemIds.Clear();
        _findHiddenCompletedTodoMatch = false;
        if (IsBuiltInFindOpen && _paper.Type == PaperTypes.Todo)
        {
            foreach (var match in _findMatches)
            {
                if (match.TodoItemId == null) continue;
                if (IsCompletedTodoItemHidden(match.TodoItemId))
                    _findHiddenCompletedTodoMatch = true;
                else if (IsTodoFindMatchBehindFold(match))
                    _findHiddenTodoItemIds.Add(match.TodoItemId);
            }
        }

        foreach (var row in _todoRows)
        {
            if (!ReferenceEquals(row, _activeDropRow) &&
                !ReferenceEquals(row, _linkedPaperDropRow))
                UpdateTodoRowBackground(row);
        }
        foreach (var itemId in _todoFoldFindControls.Keys)
            UpdateTodoFoldFindCue(itemId);
        UpdateCompletedTodoSectionVisuals();
    }

    private void UpdateTodoFoldFindCue(string itemId)
    {
        if (!_todoFoldFindControls.TryGetValue(itemId, out var controls))
            return;

        var highlighted = _findHiddenTodoItemIds.Contains(itemId) &&
                          controls.Button.Visibility == Visibility.Visible;
        controls.Button.Background = highlighted
            ? Theme.Tint((byte)(Theme.IsDark ? 96 : 78)) : Brushes.Transparent;
        controls.Icon.Stroke = highlighted ? TextBrush : WeakTextBrush;
        controls.Icon.Opacity = highlighted ? 1.0 : 0.58;
    }
}
