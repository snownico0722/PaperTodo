namespace PaperTodo;

// The drop policy has already chosen these screen positions. This is only the
// conversion back to saved preferences, not another collision/drop solver.
internal readonly record struct EdgeQueueAnchor(
    string Key, double TopDip, double HeightDip, double PreferredTopDip);

internal static class EdgeCapsuleQueueAnchorPlan
{
    internal static IReadOnlyDictionary<string, double> Build(
        IReadOnlyList<EdgeQueueAnchor> queues, double gap)
    {
        var anchors = new Dictionary<string, double>(StringComparer.Ordinal);
        double? previousBottom = null;
        double? previousAnchor = null;
        foreach (var queue in queues.OrderBy(q => q.TopDip)
                     .ThenBy(q => q.Key, StringComparer.Ordinal))
        {
            var top = Math.Round(queue.TopDip, 1);
            // A gap needs an explicit anchor. A touching queue can keep its
            // preference: the ordinary downward-spacing pass recreates its top.
            var anchor = previousBottom == null ||
                top > Math.Round(previousBottom.Value + gap, 1) + .01
                    ? top : Math.Min(Math.Round(queue.PreferredTopDip, 1), top);
            // Saved anchors also determine queue order. Keep the chosen spatial
            // order when an explicit move crosses another queue's old preference.
            // One existing 0.1-DIP quantum avoids dependence on key tie-breaking.
            if (previousAnchor is { } previous)
                anchor = Math.Max(anchor, Math.Round(previous + .1, 1));
            anchors[queue.Key] = anchor;
            previousAnchor = anchor;
            previousBottom = top + queue.HeightDip;
        }
        return anchors;
    }
}

public sealed partial class AppController
{
    private Dictionary<string, double> BuildFreeQueueDropAnchors(
        IReadOnlyList<EdgeFreeQueueBox> boxes, EdgeFreeQueueDrop decision,
        string destination, int carriedCount, bool hasMaster, bool collapsed = false)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var queues = new List<EdgeQueueAnchor>();
        foreach (var box in boxes)
        {
            var target = decision.Kind == EdgeFreeQueueDropKind.Insert && box.Key == destination;
            var top = decision.Tops.TryGetValue(box.Key, out var moved) ? moved : box.TopDip;
            var saved = DeepCapsuleStartTopMarginForQueueKey(box.Key);
            // Upward movement cannot be reproduced by downward spacing. Apply
            // that displacement to the preference, not to an already displaced
            // screen top. Downstream passive displacement is never copied back.
            var preferred = saved + (target || top < box.TopDip ? top - box.TopDip : 0);
            var finalBox = target ? box with { MemberCount = box.MemberCount + carriedCount } : box;
            queues.Add(new(box.Key, top, finalBox.Height(pitch, DeepCapsuleGap), preferred));
        }
        if (decision.Kind == EdgeFreeQueueDropKind.Move)
        {
            var placed = new EdgeFreeQueueBox(destination, decision.TopDip,
                carriedCount, hasMaster, 0, collapsed);
            queues.Add(new(destination, decision.TopDip,
                placed.Height(pitch, DeepCapsuleGap), decision.TopDip));
        }

        var resolved = EdgeCapsuleQueueAnchorPlan.Build(queues, DeepCapsuleGap);
        // Complete geometry participates above, including queues that did not
        // move. Only changed preferences (and the explicit destination) persist.
        return resolved.Where(pair => pair.Key == destination ||
                Math.Abs(pair.Value - DeepCapsuleStartTopMarginForQueueKey(pair.Key)) > .01)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
