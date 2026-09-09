using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly record struct GlobalFindMatch(
        string PaperId,
        PaperFindMatch Match);

    private readonly List<GlobalFindMatch> _globalFindMatches = [];
    private int _globalFindIndex = -1;

    internal bool TryGetBuiltInFindText(string? todoItemId, out string text)
    {
        if (_paper.Type == PaperTypes.Todo &&
            todoItemId != null &&
            _todoEditors.TryGetValue(todoItemId, out var editor))
        {
            text = editor.Text ?? string.Empty;
            return true;
        }
        if (_paper.Type == PaperTypes.Note &&
            todoItemId == null &&
            IsCurrentBodyProviderMarkdown &&
            _noteBox != null)
        {
            text = _noteBox.Text ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private void SynchronizeGlobalFindState(string query)
    {
        if (!CanUseBuiltInFind())
        {
            _globalFindMatches.Clear();
            _globalFindIndex = -1;
            return;
        }

        _globalFindMatches.Clear();
        _globalFindMatches.AddRange(ScanGlobalFindMatches(query));
        _globalFindIndex = ResolveCurrentGlobalFindIndex();
    }

    private List<GlobalFindMatch> ScanGlobalFindMatches(string query)
    {
        var matches = new List<GlobalFindMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }

        var localMatches = new List<PaperFindMatch>();
        foreach (var source in _controller.GetBuiltInFindSources())
        {
            localMatches.Clear();
            AddFindMatches(localMatches, source.TodoItemId, source.Text, query);
            foreach (var match in localMatches)
            {
                matches.Add(new GlobalFindMatch(source.PaperId, match));
            }
        }

        return matches;
    }

    private int ResolveCurrentGlobalFindIndex()
    {
        if (!TryGetCurrentFindMatch(out var local))
        {
            return -1;
        }

        return _globalFindMatches.FindIndex(match =>
            string.Equals(match.PaperId, _paper.Id, StringComparison.Ordinal) &&
            match.Match == local);
    }

    private void MoveFindMatch(int direction)
    {
        if (!CanUseBuiltInFind() ||
            _findInput == null ||
            string.IsNullOrEmpty(_findInput.Text))
        {
            return;
        }

        var query = _findInput.Text;
        SynchronizeGlobalFindState(query);

        if (_globalFindMatches.Count == 0)
        {
            _findMatches.Clear();
            _findMatchIndex = -1;
            ClearAppliedFindSelection();
            UpdateFindCount();
            return;
        }

        var targetIndex = _globalFindIndex < 0
            ? direction >= 0 ? 0 : _globalFindMatches.Count - 1
            : (_globalFindIndex + direction + _globalFindMatches.Count) %
              _globalFindMatches.Count;
        var target = _globalFindMatches[targetIndex];

        if (string.Equals(target.PaperId, _paper.Id, StringComparison.Ordinal))
        {
            _findMatches.Clear();
            _findMatches.AddRange(ScanFindMatches(query));
            _findMatchIndex = _findMatches.IndexOf(target.Match);
            _globalFindIndex = targetIndex;

            if (_findMatchIndex >= 0)
            {
                ApplyCurrentFindMatch();
            }
            else
            {
                ClearAppliedFindSelection();
            }
            UpdateFindCount();
            return;
        }

        var targetWindow = _controller.OpenBuiltInFindTarget(target.PaperId);
        if (targetWindow == null)
        {
            return;
        }

        // Keep the source search usable while the target finishes its queued show/layout work.
        // In particular, a first-show taskbar refresh can leave the target inactive. Transfer
        // focus only after that refresh, and close this popup only once the target accepts find.
        _findInput.Focus();
        _ = targetWindow.Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen ||
                !string.Equals(_findInput.Text, query, StringComparison.Ordinal) ||
                (!IsActive &&
                 _findHost?.IsKeyboardFocusWithin != true &&
                 !targetWindow.IsActive))
            {
                return;
            }

            if (!targetWindow.TryShowBuiltInFindAtGlobalMatch(query, target))
            {
                _findInput.Focus();
                ApplyCurrentFindMatch();
                return;
            }

            ClearAppliedFindSelection();
            HideBuiltInFind(restoreFocus: false);
            if (!IsActive)
            {
                ScheduleExperimentalAutoCollapse(blockedAtDeactivation: false);
            }
        }), DispatcherPriority.Background);
    }

    private bool TryShowBuiltInFindAtGlobalMatch(
        string query,
        GlobalFindMatch target)
    {
        if (IsClosed ||
            !IsVisible ||
            WindowState == WindowState.Minimized ||
            _paper.IsCollapsed ||
            !CanUseBuiltInFind() ||
            IsExperimentalPassive ||
            _controller.FullscreenAvoidanceWindowFor(this) != IntPtr.Zero)
        {
            return false;
        }

        EnsureBuiltInFindPopup();
        if (_findPopup == null || _findInput == null || !Activate())
        {
            return false;
        }

        SetBuiltInFindQuery(query, target.Match);
        UpdateBuiltInFindVisuals();
        _findPopup.IsOpen = true;
        SynchronizeBuiltInFindOwnerState(refreshMatches: false);
        if (!IsBuiltInFindOpen || !_findInput.Focus())
        {
            HideBuiltInFind(restoreFocus: false);
            return false;
        }
        _findInput.SelectAll();

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            ApplyCurrentFindMatch();
            SynchronizeGlobalFindState(query);
            UpdateFindCount();
            RepositionBuiltInFindPopup();
        }), DispatcherPriority.Background);
        return true;
    }

    private string BuiltInFindCountText(int localCurrent, int localTotal)
    {
        var globalCurrent = _globalFindIndex >= 0 &&
                            _globalFindIndex < _globalFindMatches.Count
            ? _globalFindIndex + 1
            : 0;
        return $"{localCurrent}/{localTotal} | {globalCurrent}/{_globalFindMatches.Count}";
    }
}
