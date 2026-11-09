using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

internal readonly record struct PaperFindMatch(string? TodoItemId, int Offset, int Length)
{
    public bool IsNote => TodoItemId == null;
}

internal readonly record struct GlobalFindMatch(
    string PaperId,
    PaperFindMatch Match);

public sealed partial class AppController
{
    internal readonly record struct PaperFindSource(string PaperId, string? TodoItemId, string Text);

    private IReadOnlyList<PaperFindSource> _lastFindSources = [];
    private string? _lastFindQuery;
    private List<GlobalFindMatch> _lastFindMatches = [];

    internal List<GlobalFindMatch> GetBuiltInFindMatches(string query)
    {
        if (query.Length == 0) return [];
        var sources = GetBuiltInFindSources();
        // A save revision misses uncommitted text in other open editors. Compare the
        // ordered live inputs before reusing offsets, including on cross-paper navigation.
        if (query == _lastFindQuery && sources.SequenceEqual(_lastFindSources))
            return _lastFindMatches;

        var matches = new List<GlobalFindMatch>();
        foreach (var source in sources)
        {
            AddFindMatches(matches, source, query);
        }
        _lastFindQuery = query;
        _lastFindSources = sources;
        _lastFindMatches = matches;
        return matches;
    }

    private static void AddFindMatches(
        List<GlobalFindMatch> destination,
        PaperFindSource source,
        string query)
    {
        var text = source.Text;
        var searchFrom = 0;
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

            destination.Add(new GlobalFindMatch(source.PaperId, new PaperFindMatch(source.TodoItemId, offset, query.Length)));
            searchFrom = offset + query.Length;
        }
    }

    internal void ReleaseBuiltInFindResultsIfClosed()
    {
        if (_windows.Values.Any(window => window.IsBuiltInFindOpen)) return;
        _lastFindSources = [];
        _lastFindQuery = null;
        _lastFindMatches = [];
    }

    private IReadOnlyList<PaperFindSource> GetBuiltInFindSources()
    {
        var sources = new List<PaperFindSource>();
        foreach (var paper in State.Papers)
        {
            if (!IsBuiltInFindPaper(paper))
            {
                continue;
            }

            if (paper.Type == PaperTypes.Todo)
            {
                foreach (var item in paper.Items.OrderBy(item => item.Order))
                {
                    sources.Add(CreateBuiltInFindSource(paper, item.Id, item.Text));
                }
            }
            else
            {
                sources.Add(CreateBuiltInFindSource(paper, null, paper.Content));
            }
        }
        return sources;
    }

    private PaperFindSource CreateBuiltInFindSource(PaperData paper, string? todoItemId, string? text)
    {
        if (_windows.TryGetValue(paper.Id, out var window) &&
            !window.IsClosed &&
            window.TryGetBuiltInFindText(todoItemId, out var liveText))
        {
            text = liveText;
        }
        return new PaperFindSource(paper.Id, todoItemId, text ?? string.Empty);
    }

    internal PaperWindow? OpenBuiltInFindTarget(string paperId)
    {
        var paper = State.Papers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, paperId, StringComparison.Ordinal));
        if (paper == null || !IsBuiltInFindPaper(paper))
        {
            return null;
        }

        // Search navigation is an explicit open. Reuse the same programmatic expansion path
        // as other paper retrieval flows so ordinary and deep capsules hand off correctly,
        // but do not move the target beside the source paper or toggle it on repeated opens.
        if (_windows.TryGetValue(paper.Id, out var window) && !window.IsClosed)
        {
            window.RestoreExperimentalTetherPresentationForExplicitShow();
            paper.IsVisible = true;
            RescuePaperIfOffScreen(paper, State.Papers.IndexOf(paper));
            window.CancelPendingVisibilityTransitions();

            if (paper.IsCollapsed)
            {
                window.ExpandForProgrammaticOpen();
            }
            else if (!window.HasVisibleSurface)
            {
                RestoreExistingPaperWindowSurface(paper, window);
            }

            ForceWindowToFront(window);
            RefreshTrayMenu();
            MarkDirty();
            return window;
        }

        SetPaperCollapsedRuntime(
            paper,
            collapsed: false,
            animate: false,
            saveGeometry: false);
        ShowPaper(paper);

        if (!_windows.TryGetValue(paper.Id, out window) || window.IsClosed)
        {
            return null;
        }

        ForceWindowToFront(window);
        return window;
    }

    private static bool IsBuiltInFindPaper(PaperData paper) =>
        paper.Type == PaperTypes.Todo ||
        (paper.Type == PaperTypes.Note &&
         PaperWindow.NormalizeBodyProviderId(paper.BodyProviderId) == PaperBodyProviderIds.Markdown);
}
