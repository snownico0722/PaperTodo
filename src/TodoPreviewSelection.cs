namespace PaperTodo;

internal sealed record TodoPreviewSelection<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Done,
    int MatchingCount)
{
    public int RemainingCount => Math.Max(0, MatchingCount - Items.Count);
}

/// <summary>
/// Selects a bounded, stable prefix without sorting or copying the entire paper. Filtering is
/// presentation-only: returned items are the original objects and the input is never modified.
/// </summary>
internal static class TodoPreviewSelection
{
    internal static TodoPreviewSelection<T> Capture<T>(
        IEnumerable<T> items,
        Func<T, bool> hasContent,
        Func<T, bool> isDone,
        Func<T, int> order,
        bool incompleteOnly,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(hasContent);
        ArgumentNullException.ThrowIfNull(isDone);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var selected = new List<T>(limit);
        var total = 0;
        var done = 0;
        var matching = 0;
        foreach (var item in items)
        {
            if (!hasContent(item)) continue;
            total++;
            var completed = isDone(item);
            if (completed) done++;
            if (incompleteOnly && completed) continue;
            matching++;
            if (limit == 0) continue;

            var insertion = selected.Count;
            var itemOrder = order(item);
            for (var index = 0; index < selected.Count; index++)
            {
                // Equal Order values retain source order, just like the full paper's stable sort.
                if (itemOrder < order(selected[index]))
                {
                    insertion = index;
                    break;
                }
            }
            if (insertion >= limit) continue;
            selected.Insert(insertion, item);
            if (selected.Count > limit) selected.RemoveAt(limit);
        }
        return new TodoPreviewSelection<T>(selected, total, done, matching);
    }
}
