namespace PaperTodo;

/// <summary>
/// The single source of truth for todo identity, placeholder and quick-launch semantics.
/// UI, persistence, commands and plugin events must all use these rules.
/// </summary>
internal static class TodoRules
{
    public static bool HasMeaningfulContent(PaperItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ||
        item.Done ||
        item.ReminderAt.HasValue ||
        item.ReminderTriggered ||
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);

    public static bool HasNonTextContent(PaperItem item) =>
        item.Done ||
        item.ReminderAt.HasValue ||
        item.ReminderTriggered ||
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);

    public static bool IsPlaceholder(PaperItem item) => !HasMeaningfulContent(item);

    public static PaperItem Clone(PaperItem item)
    {
        var clone = new PaperItem
        {
            Id = item.Id,
            Text = item.Text,
            Done = item.Done,
            Order = item.Order,
            ReminderAt = item.ReminderAt,
            ReminderTriggered = item.ReminderTriggered
        };
        clone.RestoreQuickLaunch(item.LinkedPaperId, item.LinkedPath, item.LinkedPathIsDirectory);
        return clone;
    }

    public static List<PaperItem> CloneAll(IEnumerable<PaperItem> items) =>
        items.Select(Clone).ToList();

    public static void NormalizeOrders(IList<PaperItem> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            items[index].Order = index;
        }
    }

    public static bool ApplyCompletedOrdering(
        List<PaperItem> items,
        bool enabled)
    {
        if (!enabled || items.Count < 2)
        {
            return false;
        }

        var reordered = items
            .Where(item => !item.Done)
            .Concat(items.Where(item => item.Done))
            .ToList();
        if (items.Select(item => item.Id)
            .SequenceEqual(reordered.Select(item => item.Id)))
        {
            return false;
        }

        items.Clear();
        items.AddRange(reordered);
        NormalizeOrders(items);
        return true;
    }

    public static bool ApplyDoneTransitionOrdering(
        List<PaperItem> items,
        IReadOnlyCollection<string> changedItemIds,
        bool done,
        bool enabled)
    {
        if (!enabled || changedItemIds.Count == 0)
        {
            return false;
        }

        var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);
        var before = items.OrderBy(item => item.Order).ToList();
        var moved = before
            .Where(item => changed.Contains(item.Id))
            .ToList();
        if (moved.Count == 0)
        {
            return false;
        }

        var remaining = before
            .Where(item => !changed.Contains(item.Id))
            .ToList();
        var insertIndex = done
            ? remaining.Count
            : remaining.FindIndex(item => item.Done);
        if (insertIndex < 0)
        {
            insertIndex = remaining.Count;
        }
        remaining.InsertRange(insertIndex, moved);

        if (before.Select(item => item.Id)
            .SequenceEqual(remaining.Select(item => item.Id)))
        {
            return false;
        }

        items.Clear();
        items.AddRange(remaining);
        NormalizeOrders(items);
        return true;
    }

    public static bool ApplyCompletionPolicy(
        List<PaperItem> items,
        IReadOnlyCollection<string> changedItemIds,
        bool done,
        bool autoClearCompleted,
        bool autoMoveCompletedToBottom)
    {
        if (changedItemIds.Count == 0)
        {
            return false;
        }

        if (done && autoClearCompleted)
        {
            var changed = changedItemIds.ToHashSet(StringComparer.Ordinal);
            items.RemoveAll(item => changed.Contains(item.Id) && item.Done);
            if (items.Count == 0)
            {
                items.Add(new PaperItem());
            }
            NormalizeOrders(items);
            return true;
        }

        return ApplyDoneTransitionOrdering(
            items,
            changedItemIds,
            done,
            autoMoveCompletedToBottom);
    }

}
