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
        item.LinkedPaperIds is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);

    public static bool HasNonTextContent(PaperItem item) =>
        item.Done ||
        item.ReminderAt.HasValue ||
        item.ReminderTriggered ||
        item.LinkedPaperIds is { Count: > 0 } ||
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
        clone.RestoreQuickLaunch(
            item.LinkedPaperIds,
            item.LinkedPath,
            item.LinkedPathIsDirectory);
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
}
