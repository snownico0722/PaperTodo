using System.Windows;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record WorkspaceCapsuleOverride(
    Guid OwnerId,
    PaperCapsulePresentation Presentation,
    string PlainText,
    Func<PaperCapsuleViewContext, FrameworkElement?>? CreateView);

public sealed partial class AppController
{
    // Volatile chrome contributions indexed by target. The last publisher owns the override;
    // releasing a different Runtime must not erase it or change the target's own presentation.
    private Dictionary<string, WorkspaceCapsuleOverride>? _workspaceCapsuleOverrides;

    internal WorkspaceCapsuleOverride? GetWorkspaceCapsuleOverride(string paperId) =>
        _workspaceCapsuleOverrides?.GetValueOrDefault(paperId);

    internal void SetWorkspaceCapsuleOverride(
        Guid ownerId,
        string paperId,
        PaperCapsulePresentation? presentation,
        Func<PaperCapsuleViewContext, FrameworkElement?>? createView = null)
    {
        if (!State.Papers.Any(paper => string.Equals(paper.Id, paperId, StringComparison.Ordinal)))
        {
            throw new PaperTodoPluginException("paper_not_found", "The requested Paper does not exist.");
        }

        var previous = GetWorkspaceCapsuleOverride(paperId);
        if (presentation == null)
        {
            if (previous?.OwnerId == ownerId)
            {
                _workspaceCapsuleOverrides!.Remove(paperId);
                RefreshWorkspaceCapsuleOverride(paperId, previous, null);
            }
            return;
        }

        var current = new WorkspaceCapsuleOverride(
            ownerId, presentation, PaperWindow.CapsulePresentationFallbackText(presentation), createView);
        (_workspaceCapsuleOverrides ??= new(StringComparer.Ordinal))[paperId] = current;
        RefreshWorkspaceCapsuleOverride(paperId, previous, current);
    }

    internal void ClearWorkspaceCapsuleOverrides(Guid ownerId)
    {
        if (_workspaceCapsuleOverrides == null) return;

        foreach (var paperId in _workspaceCapsuleOverrides
                     .Where(item => item.Value.OwnerId == ownerId)
                     .Select(item => item.Key)
                     .ToArray())
        {
            var previous = GetWorkspaceCapsuleOverride(paperId);
            if (previous?.OwnerId == ownerId)
            {
                _workspaceCapsuleOverrides.Remove(paperId);
                RefreshWorkspaceCapsuleOverride(paperId, previous, null);
            }
        }
    }

    private void RefreshWorkspaceCapsuleOverride(
        string paperId,
        WorkspaceCapsuleOverride? previous,
        WorkspaceCapsuleOverride? current)
    {
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.RefreshWorkspaceCapsulePresentation(previous, current);
        }
    }
}
