using System.Diagnostics;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void InvalidateEdgeCapsulePreviewContent() =>
        _edgeCapsulePreviewInvalidationSource.Invalidate();

    private string CurrentMarkdownTextForEdgeCapsulePreview()
    {
        if (_paper.Type != PaperTypes.Note || !IsCurrentBodyProviderMarkdown)
        {
            return string.Empty;
        }

        return _noteBox?.PersistentText ?? _paper.Content ?? string.Empty;
    }

    private bool SetTodoDoneFromEdgeCapsulePreview(
        string itemId,
        bool done)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null)
        {
            return false;
        }
        if (item.Done == done)
        {
            return true;
        }

        var focusedId = CurrentFocusedTodoItemId();
        PushUndoSnapshot();
        item.Done = done;
        InvalidateEdgeCapsulePreviewContent();

        if (done && (item.ReminderAt.HasValue || item.ReminderTriggered))
        {
            item.ReminderAt = null;
            item.ReminderTriggered = false;
            _controller.NotifyTodoReminderChanged(saveImmediately: false);
        }
        else if (!done && item.ReminderAt.HasValue)
        {
            _controller.NotifyTodoReminderCollectionChanged();
        }

        _controller.MarkDirty();
        if (done && _controller.State.AutoClearCompletedTodos)
        {
            RemoveItem(item, pushUndo: false);
            return true;
        }

        MoveTodoItemsAfterDoneChange([item], done);
        ReconcileTodoRows(
            new[] { item.Id },
            focusItemId: focusedId);
        return true;
    }

    private bool OpenTodoLinkedTargetFromEdgeCapsulePreview(string itemId)
    {
        if (_paper.Type != PaperTypes.Todo)
        {
            return false;
        }

        var item = _paper.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item == null)
        {
            return false;
        }

        if (_controller.State.EnableTodoPaperLinks &&
            _controller.TryGetLinkedPaperTitle(item.LinkedPaperId, out _))
        {
            if (!_controller.ShouldRunLinkedScriptCapsule(item.LinkedPaperId) ||
                !_controller.RunLinkedScriptCapsule(item.LinkedPaperId))
            {
                _controller.OpenLinkedPaper(item.LinkedPaperId, this);
            }
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            OpenTodoLinkedPath(item);
            return true;
        }

        return false;
    }

    private string CurrentPluginStatusForEdgeCapsulePreview()
    {
        if (_pluginCapsulePresentation != null &&
            !IsCurrentBodyProviderMarkdown &&
            (!_bodyFailed || HasWebPaperRuntimePresentationOwner))
        {
            return CapsulePresentationFallbackText(
                _pluginCapsulePresentation);
        }

        var status = (_paper.BodyCapsuleText ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status))
        {
            return status;
        }

        var capsuleTitle = _controller.PaperCapsuleTitle(_paper);
        return string.Equals(
                capsuleTitle,
                _controller.PaperTitleText(_paper),
                StringComparison.Ordinal)
            ? string.Empty
            : capsuleTitle;
    }

    private static void OpenExternalFromEdgeCapsulePreview(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // Preview links are best-effort and must not close the card or the paper.
        }
    }
}
