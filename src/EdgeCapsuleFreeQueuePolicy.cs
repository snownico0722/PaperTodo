namespace PaperTodo;

// All values are target-monitor-local DIP. Physical geometry remains in EdgeCapsuleGeometry.
internal readonly record struct EdgeFreeQueueBox(
    string Key, double TopDip, int MemberCount, bool HasMaster, double WidthDip)
{
    internal int Slots => MemberCount + (HasMaster ? 1 : 0);
    internal double Height(double pitch, double gap) => Math.Max(0, Slots * pitch - gap);
    internal double FirstMemberTop(double pitch) => TopDip + (HasMaster ? pitch : 0);
}

internal enum EdgeFreeQueueDropKind { Rejected, Move, Insert }

internal sealed record EdgeFreeQueueDrop(
    EdgeFreeQueueDropKind Kind, string TargetKey, int Index, double TopDip,
    IReadOnlyDictionary<string, double> Tops)
{
    internal bool Accepted => Kind != EdgeFreeQueueDropKind.Rejected;
    internal static EdgeFreeQueueDrop Rejected { get; } =
        new(EdgeFreeQueueDropKind.Rejected, "", 0, 0, new Dictionary<string, double>());
}

/// <summary>
/// Direct manipulation, not automatic free-space search. Independent queues stay at the
/// requested height. A prepend grows above existing members; an interior insertion moves
/// the suffix. Only neighbours actually intersected by that expansion need to move.
/// </summary>
internal static class EdgeCapsuleFreeQueuePolicy
{
    internal const double SnapDistance = 18;
    private const double Epsilon = 0.1;

    internal static bool ShouldDetachSingle(
        bool floating, double draggedTop, double firstTop, int count, double pitch) =>
        floating || draggedTop < firstTop - EdgeCapsuleLayout.CrossQueueDragUnlockDistance ||
        draggedTop > firstTop + Math.Max(0, count - 1) * pitch +
            EdgeCapsuleLayout.CrossQueueDragUnlockDistance;

    // Ctrl chooses membership, never the floating phase of the gesture.
    internal static bool ShouldFloat(double horizontalDistanceDip, bool enteredAnotherMonitor) =>
        enteredAnotherMonitor || horizontalDistanceDip >= EdgeCapsuleLayout.CrossQueueDragUnlockDistance;

    internal static (string Key, int Index)? FindInsertion(
        IReadOnlyList<EdgeFreeQueueBox> queues, double draggedTop, double visibleDraggedHeight,
        double pointerDistanceFromWall, double pitch, double gap)
    {
        (string Key, int Index)? best = null;
        var bestDistance = double.PositiveInfinity;
        var bestCentre = double.PositiveInfinity;
        foreach (var queue in queues)
        {
            if (queue.Slots == 0 || pointerDistanceFromWall > queue.WidthDip + SnapDistance) continue;
            var top = queue.MemberCount == 0 ? queue.TopDip : queue.FirstMemberTop(pitch);
            var bottom = queue.TopDip + queue.Height(pitch, gap);
            var draggedBottom = draggedTop + visibleDraggedHeight;
            var distance = Math.Max(0, Math.Max(top - draggedBottom, draggedTop - bottom));
            if (distance > SnapDistance) continue;
            var centre = Math.Abs((draggedTop + draggedBottom) / 2 - (top + bottom) / 2);
            if (distance > bestDistance || distance == bestDistance &&
                (centre > bestCentre || centre == bestCentre && best.HasValue &&
                 string.CompareOrdinal(queue.Key, best.Value.Key) >= 0)) continue;
            var index = draggedTop < top && draggedBottom <= top + pitch / 2
                ? 0 : Math.Clamp((int)Math.Floor((draggedTop - top) / pitch + 0.5), 0, queue.MemberCount);
            best = (queue.Key, index);
            bestDistance = distance;
            bestCentre = centre;
        }
        return best;
    }

    internal static EdgeFreeQueueDrop Place(
        IReadOnlyList<EdgeFreeQueueBox> obstacles, double requestedTop,
        int slots, double areaHeight, double pitch, double gap)
    {
        if (!double.IsFinite(requestedTop) || slots < 1) return EdgeFreeQueueDrop.Rejected;
        var height = slots * pitch - gap;
        // D-006 retains overlong queues without paging. Clamp their head, not every member.
        var top = Math.Clamp(requestedTop, EdgeCapsuleLayout.TopMargin,
            Math.Max(EdgeCapsuleLayout.TopMargin, areaHeight - EdgeCapsuleLayout.TopMargin - height));
        foreach (var box in obstacles)
        {
            if (box.Slots == 0) continue;
            if (top < box.TopDip + box.Height(pitch, gap) + gap - Epsilon &&
                top + height + gap > box.TopDip + Epsilon) return EdgeFreeQueueDrop.Rejected;
        }
        return new(EdgeFreeQueueDropKind.Move, "", 0, top, new Dictionary<string, double>());
    }

    internal static EdgeFreeQueueDrop Insert(
        IReadOnlyList<EdgeFreeQueueBox> queues, string targetKey, int index,
        int carriedCount, double areaHeight, double pitch, double gap, bool prependAbove = true)
    {
        var targetIndex = -1;
        for (var i = 0; i < queues.Count; i++)
            if (queues[i].Key == targetKey) { targetIndex = i; break; }
        if (targetIndex < 0 || carriedCount < 1) return EdgeFreeQueueDrop.Rejected;
        var target = queues[targetIndex];
        index = Math.Clamp(index, 0, target.MemberCount);
        var top = target.TopDip;
        if (prependAbove && index == 0 && target.MemberCount > 0) top -= carriedCount * pitch;
        var height = (target.Slots + carriedCount) * pitch - gap;
        var min = EdgeCapsuleLayout.TopMargin;
        var max = areaHeight - min;
        if (top < min - Epsilon || top + height > max + Epsilon) return EdgeFreeQueueDrop.Rejected;

        var tops = new Dictionary<string, double>(StringComparer.Ordinal) { [targetKey] = top };
        var above = queues.Where(q => q.Key != targetKey && q.Slots > 0 &&
                (q.TopDip < target.TopDip || q.TopDip == target.TopDip && string.CompareOrdinal(q.Key, targetKey) < 0))
            .OrderByDescending(q => q.TopDip).ThenByDescending(q => q.Key, StringComparer.Ordinal);
        var below = queues.Where(q => q.Key != targetKey && q.Slots > 0 &&
                (q.TopDip > target.TopDip || q.TopDip == target.TopDip && string.CompareOrdinal(q.Key, targetKey) > 0))
            .OrderBy(q => q.TopDip).ThenBy(q => q.Key, StringComparer.Ordinal);
        var edge = top - gap;
        foreach (var box in above)
        {
            var moved = Math.Min(box.TopDip, edge - box.Height(pitch, gap));
            if (moved < box.TopDip - Epsilon)
            {
                if (moved < min - Epsilon) return EdgeFreeQueueDrop.Rejected;
                tops[box.Key] = moved;
            }
            edge = moved - gap;
        }
        edge = top + height + gap;
        foreach (var box in below)
        {
            var moved = Math.Max(box.TopDip, edge);
            if (moved > box.TopDip + Epsilon)
            {
                if (moved + box.Height(pitch, gap) > max + Epsilon) return EdgeFreeQueueDrop.Rejected;
                tops[box.Key] = moved;
            }
            edge = moved + box.Height(pitch, gap) + gap;
        }
        return new(EdgeFreeQueueDropKind.Insert, targetKey, index, top, tops);
    }
}
