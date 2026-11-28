using PaperTodo.Plugin;

namespace PaperTodo;

internal readonly record struct PaperBottomBarLayoutItem(PaperBottomBarSide Side, int Order, int Priority, double Width);
internal readonly record struct PaperBottomBarLayout(int[] Left, int[] Right, bool Omitted)
{
    internal static double ItemGap => InjectionParameters.BottomBarItemGap.Value;
    private static double GroupGap => InjectionParameters.BottomBarGroupGap.Value;

    // Input indices are in stable provider/item-id order. Output groups are ordered outside-in.
    internal static PaperBottomBarLayout Plan(IReadOnlyList<PaperBottomBarLayoutItem> items, double available, double ellipsis)
    {
        var order = Enumerable.Range(0, items.Count).OrderByDescending(i => items[i].Order).ToArray();
        var left = order.Where(i => items[i].Side == PaperBottomBarSide.Left).ToArray();
        var right = order.Where(i => items[i].Side == PaperBottomBarSide.Right).ToArray();

        double Width(int[] group) => group.Sum(i => items[i].Width) + Math.Max(0, group.Length - 1) * ItemGap;
        var gap = left.Length > 0 && right.Length > 0 ? GroupGap : 0;
        if (Width(left) + Width(right) + gap <= available) return new(left, right, false);
        var mergedWidth = Width(order);
        if (mergedWidth <= available) return new([], order, false);
        if (ellipsis > available) return new([], [], false);

        var removed = new HashSet<int>();
        var width = mergedWidth + ellipsis + ItemGap;
        foreach (var i in order.OrderBy(i => items[i].Priority).ThenByDescending(i => i))
        {
            removed.Add(i);
            width -= items[i].Width + ItemGap;
            if (width <= available) break;
        }

        return new([], order.Where(i => !removed.Contains(i)).ToArray(), true);
    }
}
