namespace PaperTodo;

/// <summary>
/// Stable 3.3 todo identity and quick-launch rules. A todo carrying a paper/path link is real
/// content even when its text is empty, so cleanup/backspace must not silently delete it.
/// </summary>
internal static class TodoRules
{
    public static bool HasMeaningfulContent(PaperItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ||
        item.Done ||
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);

    public static bool HasNonTextContent(PaperItem item) =>
        item.Done ||
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);

    public static bool IsPlaceholder(PaperItem item) => !HasMeaningfulContent(item);
    public static bool ApplyCompletedOrdering(List<PaperItem> items, bool enabled)
    {
        if (!enabled || items.Count < 2)
        {
            return false;
        }

        var reordered = items
            .OrderBy(item => item.Order)
            .Where(item => !item.Done)
            .Concat(items.OrderBy(item => item.Order).Where(item => item.Done))
            .ToList();
        if (items.Select(item => item.Id).SequenceEqual(reordered.Select(item => item.Id)))
        {
            return false;
        }

        items.Clear();
        items.AddRange(reordered);
        for (var index = 0; index < items.Count; index++)
        {
            items[index].Order = index;
        }
        return true;
    }

}
