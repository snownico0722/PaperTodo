using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperTodo;

public sealed partial class AppController
{
    internal readonly record struct MarkdownFindSource(string PaperId, string Text);

    internal IReadOnlyList<MarkdownFindSource> GetMarkdownFindSources()
    {
        var sources = new List<MarkdownFindSource>();
        foreach (var paper in State.Papers)
        {
            if (!IsMarkdownFindPaper(paper))
            {
                continue;
            }

            var text = paper.Content ?? string.Empty;
            if (_windows.TryGetValue(paper.Id, out var window) &&
                !window.IsClosed &&
                window.TryGetMarkdownFindText(out var liveText))
            {
                text = liveText;
            }

            sources.Add(new MarkdownFindSource(paper.Id, text));
        }
        return sources;
    }

    internal PaperWindow? OpenMarkdownFindTarget(string paperId)
    {
        var paper = State.Papers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, paperId, StringComparison.Ordinal));
        if (paper == null || !IsMarkdownFindPaper(paper))
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

    private static bool IsMarkdownFindPaper(PaperData paper) =>
        paper.Type == PaperTypes.Note &&
        (string.IsNullOrWhiteSpace(paper.BodyProviderId) ||
         string.Equals(
             paper.BodyProviderId,
             PaperBodyProviderIds.Markdown,
             StringComparison.Ordinal));
}
