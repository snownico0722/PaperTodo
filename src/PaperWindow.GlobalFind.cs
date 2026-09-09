using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly record struct GlobalNoteFindMatch(
        string PaperId,
        int Offset,
        int Length);

    private readonly List<GlobalNoteFindMatch> _globalNoteFindMatches = [];
    private int _globalNoteFindIndex = -1;

    private bool UsesGlobalNoteFind =>
        _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown;

    internal bool TryGetMarkdownFindText(out string text)
    {
        if (!UsesGlobalNoteFind || _noteBox == null)
        {
            text = string.Empty;
            return false;
        }

        text = _noteBox.Text ?? string.Empty;
        return true;
    }

    private void SynchronizeGlobalNoteFindState(string query)
    {
        if (!UsesGlobalNoteFind)
        {
            _globalNoteFindMatches.Clear();
            _globalNoteFindIndex = -1;
            return;
        }

        _globalNoteFindMatches.Clear();
        _globalNoteFindMatches.AddRange(ScanGlobalNoteFindMatches(query));
        _globalNoteFindIndex = ResolveCurrentGlobalNoteFindIndex();
    }

    private List<GlobalNoteFindMatch> ScanGlobalNoteFindMatches(string query)
    {
        var matches = new List<GlobalNoteFindMatch>();
        if (string.IsNullOrEmpty(query))
        {
            return matches;
        }

        foreach (var source in _controller.GetMarkdownFindSources())
        {
            var searchFrom = 0;
            var text = source.Text ?? string.Empty;
            while (searchFrom <= text.Length - query.Length)
            {
                var offset = text.IndexOf(
                    query,
                    searchFrom,
                    StringComparison.OrdinalIgnoreCase);
                if (offset < 0)
                {
                    break;
                }

                matches.Add(new GlobalNoteFindMatch(
                    source.PaperId,
                    offset,
                    query.Length));
                searchFrom = offset + query.Length;
            }
        }

        return matches;
    }

    private int ResolveCurrentGlobalNoteFindIndex()
    {
        if (!TryGetCurrentFindMatch(out var local) || !local.IsNote)
        {
            return -1;
        }

        return _globalNoteFindMatches.FindIndex(match =>
            string.Equals(match.PaperId, _paper.Id, StringComparison.Ordinal) &&
            match.Offset == local.Offset &&
            match.Length == local.Length);
    }

    private bool TryMoveGlobalNoteFindMatch(int direction)
    {
        if (!UsesGlobalNoteFind ||
            _findInput == null ||
            string.IsNullOrEmpty(_findInput.Text))
        {
            return false;
        }

        var query = _findInput.Text;
        _globalNoteFindMatches.Clear();
        _globalNoteFindMatches.AddRange(ScanGlobalNoteFindMatches(query));
        _globalNoteFindIndex = ResolveCurrentGlobalNoteFindIndex();

        if (_globalNoteFindMatches.Count == 0)
        {
            _findMatches.Clear();
            _findMatchIndex = -1;
            ClearAppliedFindSelection();
            UpdateFindCount();
            return true;
        }

        var targetIndex = _globalNoteFindIndex < 0
            ? direction >= 0 ? 0 : _globalNoteFindMatches.Count - 1
            : (_globalNoteFindIndex + direction + _globalNoteFindMatches.Count) %
              _globalNoteFindMatches.Count;
        var target = _globalNoteFindMatches[targetIndex];

        if (string.Equals(target.PaperId, _paper.Id, StringComparison.Ordinal))
        {
            _findMatches.Clear();
            _findMatches.AddRange(ScanFindMatches(query));
            _findMatchIndex = _findMatches.FindIndex(match =>
                match.IsNote &&
                match.Offset == target.Offset &&
                match.Length == target.Length);
            _globalNoteFindIndex = targetIndex;

            if (_findMatchIndex >= 0)
            {
                ApplyCurrentFindMatch();
            }
            else
            {
                ClearAppliedFindSelection();
            }
            UpdateFindCount();
            return true;
        }

        ClearAppliedFindSelection();
        HideBuiltInFind(restoreFocus: false);
        var targetWindow = _controller.OpenMarkdownFindTarget(target.PaperId);
        if (targetWindow == null)
        {
            return true;
        }

        targetWindow.ShowBuiltInFindAtGlobalMatch(query, target);
        if (!IsActive)
        {
            ScheduleExperimentalAutoCollapse(blockedAtDeactivation: false);
        }
        return true;
    }

    private void ShowBuiltInFindAtGlobalMatch(
        string query,
        GlobalNoteFindMatch target)
    {
        EnsureBuiltInFindPopup();
        if (_findPopup == null || _findInput == null)
        {
            return;
        }

        SetBuiltInFindQuery(
            query,
            new PaperFindMatch(null, target.Offset, target.Length));
        UpdateBuiltInFindVisuals();
        _findPopup.IsOpen = true;
        SynchronizeBuiltInFindOwnerState(refreshMatches: false);
        _findInput.Focus();
        _findInput.SelectAll();

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBuiltInFindOpen)
            {
                return;
            }
            ApplyCurrentFindMatch();
            SynchronizeGlobalNoteFindState(query);
            UpdateFindCount();
            RepositionBuiltInFindPopup();
        }), DispatcherPriority.Background);
    }

    private string BuiltInFindCountText(int localCurrent, int localTotal)
    {
        if (!UsesGlobalNoteFind)
        {
            return $"{localCurrent} / {localTotal}";
        }

        var globalCurrent = _globalNoteFindIndex >= 0 &&
                            _globalNoteFindIndex < _globalNoteFindMatches.Count
            ? _globalNoteFindIndex + 1
            : 0;
        return $"{localCurrent}/{localTotal} | {globalCurrent}/{_globalNoteFindMatches.Count}";
    }

    private bool HasBuiltInFindNavigationTarget() =>
        UsesGlobalNoteFind
            ? _globalNoteFindMatches.Count > 0
            : _findMatches.Count > 0;
}
