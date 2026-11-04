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
    internal const double ReleaseDistance = 30; // A held target must be pulled further away to release.
    internal const double DetachResistance = 12; // Leaving the source is stickier than joining another.
    internal const double JoinNudge = 6;
    private const double TakeoverAdvantage = 6;
    private const double Epsilon = 0.1;

    internal static bool ShouldDetachSingle(
        bool floating, double draggedTop, double firstTop, int count, double pitch) =>
        floating || draggedTop < firstTop - EdgeCapsuleLayout.CrossQueueDragUnlockDistance - DetachResistance ||
        draggedTop > firstTop + Math.Max(0, count - 1) * pitch +
            EdgeCapsuleLayout.CrossQueueDragUnlockDistance + DetachResistance;

    // Ctrl chooses membership, never the floating phase of the gesture.
    internal static bool ShouldFloat(double horizontalDistanceDip, bool enteredAnotherMonitor, bool freeQueues = false) =>
        enteredAnotherMonitor || horizontalDistanceDip >= EdgeCapsuleLayout.CrossQueueDragUnlockDistance +
            (freeQueues ? DetachResistance : 0);

    internal static double NudgeToward(double actualTop, double targetTop, double maximumDip) =>
        actualTop + Math.Clamp(targetTop - actualTop, -maximumDip, maximumDip);

    // Source resistance vanishes smoothly at the detachment threshold: no jump when the
    // capsule becomes independent, and no resistance while sorting inside the source span.
    internal static double ResistLeavingSource(double top, double sourceFirst, double sourceLast)
    {
        var displacement = top < sourceFirst ? top - sourceFirst :
            top > sourceLast ? top - sourceLast : 0;
        var distance = Math.Abs(displacement);
        var threshold = EdgeCapsuleLayout.CrossQueueDragUnlockDistance + DetachResistance;
        if (distance <= 0 || distance >= threshold) return top;
        var resistance = Math.Min(10, distance) * (1 - distance / threshold);
        return top - Math.Sign(displacement) * resistance;
    }

    internal static (string Key, int Index)? FindInsertion(
        IReadOnlyList<EdgeFreeQueueBox> queues, double draggedTop, double visibleDraggedHeight,
        double pointerDistanceFromWall, double pitch, double gap, string? heldKey = null)
    {
        (string Key, int Index)? best = null;
        (string Key, int Index)? held = null;
        var heldDistance = double.PositiveInfinity;
        var bestDistance = double.PositiveInfinity;
        var bestCentre = double.PositiveInfinity;
        foreach (var queue in queues)
        {
            var isHeld = queue.Key == heldKey;
            var range = isHeld ? ReleaseDistance : SnapDistance;
            if (queue.Slots == 0 || pointerDistanceFromWall > queue.WidthDip + range) continue;
            var top = queue.MemberCount == 0 ? queue.TopDip : queue.FirstMemberTop(pitch);
            var bottom = queue.TopDip + queue.Height(pitch, gap);
            var draggedBottom = draggedTop + visibleDraggedHeight;
            var distance = Math.Max(0, Math.Max(top - draggedBottom, draggedTop - bottom));
            if (distance > range) continue;
            var index = draggedTop < top && draggedBottom <= top + pitch / 2
                ? 0 : Math.Clamp((int)Math.Floor((draggedTop - top) / pitch + 0.5), 0, queue.MemberCount);
            if (isHeld)
            {
                held = (queue.Key, index);
                heldDistance = distance;
                continue;
            }
            var centre = Math.Abs((draggedTop + draggedBottom) / 2 - (top + bottom) / 2);
            if (distance > bestDistance || distance == bestDistance &&
                (centre > bestCentre || centre == bestCentre && best.HasValue &&
                 string.CompareOrdinal(queue.Key, best.Value.Key) >= 0)) continue;
            best = (queue.Key, index);
            bestDistance = distance;
            bestCentre = centre;
        }
        return held.HasValue && (!best.HasValue || heldDistance <= bestDistance + TakeoverAdvantage)
            ? held : best;
    }

    internal static EdgeFreeQueueDrop Place(
        IReadOnlyList<EdgeFreeQueueBox> obstacles, double requestedTop,
        int slots, double areaHeight, double pitch, double gap)
    {
        if (!double.IsFinite(requestedTop) || slots < 1) return EdgeFreeQueueDrop.Rejected;
        // Never search for a different destination or reject because the monitor is full.
        // Keep the exact requested anchor and push only neighbours intersecting it.
        var moved = ShiftNeighbours(obstacles, "", requestedTop, requestedTop,
            slots * pitch - gap, pitch, gap);
        return new(EdgeFreeQueueDropKind.Move, "", 0, requestedTop, moved);
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
        // Prepending extends above the target. Appending/interior insertion leaves its head in
        // place. Overflow is permitted; a full edge must never veto the user's chosen slot.
        var tops = ShiftNeighbours(queues, targetKey, target.TopDip, top,
            (target.Slots + carriedCount) * pitch - gap, pitch, gap);
        tops[targetKey] = top;
        return new(EdgeFreeQueueDropKind.Insert, targetKey, index, top, tops);
    }

    private static Dictionary<string, double> ShiftNeighbours(
        IReadOnlyList<EdgeFreeQueueBox> queues, string targetKey, double pivot,
        double top, double height, double pitch, double gap)
    {
        var tops = new Dictionary<string, double>(StringComparer.Ordinal);
        var above = queues.Where(q => q.Key != targetKey && q.Slots > 0 &&
                (q.TopDip < pivot || q.TopDip == pivot && string.CompareOrdinal(q.Key, targetKey) < 0))
            .OrderByDescending(q => q.TopDip).ThenByDescending(q => q.Key, StringComparer.Ordinal);
        var below = queues.Where(q => q.Key != targetKey && q.Slots > 0 &&
                (q.TopDip > pivot || q.TopDip == pivot && string.CompareOrdinal(q.Key, targetKey) >= 0))
            .OrderBy(q => q.TopDip).ThenBy(q => q.Key, StringComparer.Ordinal);

        var edge = top - gap;
        foreach (var box in above)
        {
            var moved = Math.Min(box.TopDip, edge - box.Height(pitch, gap));
            if (moved < box.TopDip - Epsilon) tops[box.Key] = moved;
            edge = moved - gap;
        }
        edge = top + height + gap;
        foreach (var box in below)
        {
            var moved = Math.Max(box.TopDip, edge);
            if (moved > box.TopDip + Epsilon) tops[box.Key] = moved;
            edge = moved + box.Height(pitch, gap) + gap;
        }
        return tops;
    }
}
