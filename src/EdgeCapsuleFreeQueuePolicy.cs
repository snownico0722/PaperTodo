namespace PaperTodo;

// All values are target-monitor-local DIP. Physical geometry remains in EdgeCapsuleGeometry.
internal readonly record struct EdgeFreeQueueBox(
    string Key, double TopDip, int MemberCount, bool HasMaster, double WidthDip, bool Collapsed = false)
{
    internal int Slots => MemberCount + (HasMaster ? 1 : 0);
    internal double Height(double pitch, double gap) => Collapsed && HasMaster
        ? PaperLayoutDefaults.CapsuleHeight : Math.Max(0, Slots * pitch - gap);
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
/// Direct manipulation, not automatic free-space search. Independent queues use the
/// adjacent gap before displacing neighbours. Every insertion moves the suffix down, including the first slot;
/// only insufficient space below lifts the merged queue. Intersected neighbours give way.
/// </summary>
// Gesture-local: a snap is a short-lived attachment to an exact slot, never a
// continuously varying drag resistance. Moving back into the source rearms it.
internal sealed class EdgeQueueExitSnapState
{
    internal int HeldDirection; // -1 first slot/up, +1 last slot/down, 0 not held.
    internal bool Released;
    internal double LastPointerTop = double.NaN;
}

internal static class EdgeCapsuleFreeQueuePolicy
{
    internal const double SnapDistance = 18;
    internal const double ReleaseDistance = 30; // A held target must be pulled further away to release.
    internal const double ExitSnapDistance = 18; // Pointer distance needed to break an outer-slot snap.

    // An actively dragged block must not be positioned outside the visible work area
    // when it fits. Oversized queues keep their first slot visible; adding members is
    // never rejected simply because a full queue cannot physically fit on screen.
    internal static double ClampVisibleTop(double requestedTop, double areaHeight, double visibleHeight)
    {
        if (!double.IsFinite(requestedTop) || !double.IsFinite(areaHeight) || areaHeight <= 0)
            return requestedTop;
        var maxTop = Math.Max(0, areaHeight - Math.Max(PaperLayoutDefaults.CapsuleHeight, visibleHeight));
        return Math.Clamp(requestedTop, 0, maxTop);
    }
    private const double TakeoverAdvantage = 6;
    private const double Epsilon = 0.1;

    internal static bool ShouldDetachSingle(
        bool floating, double draggedTop, double firstTop, int count, double pitch) =>
        floating || draggedTop < firstTop - EdgeCapsuleLayout.CrossQueueDragUnlockDistance ||
        draggedTop > firstTop + Math.Max(0, count - 1) * pitch +
            EdgeCapsuleLayout.CrossQueueDragUnlockDistance;

    // Ctrl chooses membership, never the floating phase of the gesture.
    internal static bool ShouldFloat(double horizontalDistanceDip, bool enteredAnotherMonitor) =>
        enteredAnotherMonitor || horizontalDistanceDip >= EdgeCapsuleLayout.CrossQueueDragUnlockDistance;

    internal static double GhostSlotTop(EdgeFreeQueueDrop drop, bool targetHasMaster, double pitch,
        bool targetCollapsed = false) =>
        drop.Kind == EdgeFreeQueueDropKind.Insert && !targetCollapsed
            ? drop.TopDip + (targetHasMaster ? pitch : 0) + drop.Index * pitch
            : drop.TopDip;

    // Only crossing beyond the first or last ORIGINAL member slot can attach
    // the dragged capsule to that exact boundary. While attached it does not
    // ease/lag behind the pointer. Once pulled more than 18 DIP, it releases to
    // full pointer motion. Coming back inward never attracts; entering the queue
    // rearms the next outward snap, even after a previous detach.
    internal static double SnapLeavingSource(double pointerTop, double first, double last,
        EdgeQueueExitSnapState state)
    {
        var direction = pointerTop < first ? -1 : pointerTop > last ? 1 : 0;
        var previous = state.LastPointerTop;
        state.LastPointerTop = pointerTop;

        if (direction == 0)
        {
            state.HeldDirection = 0;
            state.Released = false;
            return pointerTop;
        }
        if (state.Released) return pointerTop;

        // Moving towards the source while outside is a drag-IN, not a drag-OUT.
        // It must follow the pointer without a second capture or sticky reversal.
        if (state.HeldDirection == direction && double.IsFinite(previous) &&
            (pointerTop - previous) * direction < 0)
        {
            state.HeldDirection = 0;
            state.Released = true;
            return pointerTop;
        }

        var edgeTop = direction < 0 ? first : last;
        if (Math.Abs(pointerTop - edgeTop) > ExitSnapDistance)
        {
            state.HeldDirection = 0;
            state.Released = true;
            return pointerTop;
        }

        state.HeldDirection = direction;
        return edgeTop;
    }

    internal static (string Key, int Index)? FindInsertion(
        IReadOnlyList<EdgeFreeQueueBox> queues, double draggedTop, double visibleDraggedHeight,
        double pointerDistanceFromWall, double pitch, double gap, string? heldKey = null,
        double? pointerTopDip = null, bool floating = false)
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
            // Floating drags choose the nearer wall, but may hover far from it.
            // Horizontal distance cannot veto an otherwise valid target slot.
            if (queue.Slots == 0 || !floating && pointerDistanceFromWall > queue.WidthDip + range) continue;
            // Collapsed queues expose only their visible master. Never hit-test the
            // invisible member slots, even if the carried queue overlaps their old span.
            var masterOnly = queue.Collapsed && queue.HasMaster;
            var top = masterOnly ? queue.TopDip :
                queue.MemberCount == 0 ? queue.TopDip : queue.FirstMemberTop(pitch);
            var bottom = queue.TopDip + queue.Height(pitch, gap);
            var draggedBottom = draggedTop + visibleDraggedHeight;
            var distance = masterOnly
                ? pointerTopDip is { } pointerTop && double.IsFinite(pointerTop)
                    ? Math.Max(0, Math.Max(top - pointerTop, pointerTop - bottom))
                    : double.PositiveInfinity
                : Math.Max(0, Math.Max(top - draggedBottom, draggedTop - bottom));
            if (distance > range) continue;
            // The hidden members have no visible insertion slots. Merging at the
            // master appends to the existing queue without moving its saved head.
            var index = masterOnly ? queue.MemberCount :
                draggedTop < top && draggedBottom <= top + pitch / 2
                    ? 0 : Math.Clamp((int)Math.Floor((draggedTop - top) / pitch + 0.5), 0, queue.MemberCount);
            if (isHeld)
            {
                held = (queue.Key, index);
                heldDistance = distance;
                continue;
            }
            // A carried block may overlap several queues. The pointer indicates which
            // visible queue the user is aiming at; the block bounds only establish
            // reachability. Otherwise a multi-paper drag can pick the queue BELOW
            // the pointer and never preview the intended neighbour's slot.
            var probeTop = pointerTopDip is { } y && double.IsFinite(y)
                ? y : (draggedTop + draggedBottom) / 2;
            var centre = Math.Abs(probeTop - (top + bottom) / 2);
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
        int slots, double areaHeight, double pitch, double gap, bool stableNeighbours = true)
    {
        if (!double.IsFinite(requestedTop) || slots < 1) return EdgeFreeQueueDrop.Rejected;
        // Block a user-controlled position from leaving the display. This does not
        // search for a free slot or refuse a drop when the queue is longer than the screen.
        var height = slots * pitch - gap;
        var top = ClampVisibleTop(requestedTop, areaHeight, height);
        var pivot = top;
        if (stableNeighbours)
        {
            // Use the resting gap selected by the drag, never last frame's displaced
            // tops. The landing stack fits above the next queue before moving it;
            // a short gap pushes down only by its deficit, once for this gap.
            // Below a queue, move the ghost past its tail instead of pushing it up.
            var floor = 0d;
            var ceiling = double.PositiveInfinity;
            foreach (var box in obstacles.Where(q => q.Slots > 0))
            {
                if (box.TopDip < pivot)
                    floor = Math.Max(floor, box.TopDip + box.Height(pitch, gap) + gap);
                else
                    ceiling = Math.Min(ceiling, box.TopDip - height - gap);
            }
            top = ClampVisibleTop(Math.Max(floor, Math.Min(top, ceiling)), areaHeight, height);
        }
        var moved = ShiftNeighbours(obstacles, "", pivot, top, height, pitch, gap);
        return new(EdgeFreeQueueDropKind.Move, "", 0, top, moved);
    }

    internal static EdgeFreeQueueDrop Insert(
        IReadOnlyList<EdgeFreeQueueBox> queues, string targetKey, int index,
        int carriedCount, double areaHeight, double pitch, double gap)
    {
        var targetIndex = -1;
        for (var i = 0; i < queues.Count; i++)
            if (queues[i].Key == targetKey) { targetIndex = i; break; }
        if (targetIndex < 0 || carriedCount < 1) return EdgeFreeQueueDrop.Rejected;
        var target = queues[targetIndex];
        index = target.Collapsed && target.HasMaster
            ? target.MemberCount : Math.Clamp(index, 0, target.MemberCount);
        var top = target.TopDip;
        // Slot zero follows the same downward-first rule as every interior slot.
        // Keep the head unless the expanded queue would cross the bottom boundary.
        // Keep the merged block in view when it can fit. If the combined queue is
        // taller than the work area, preserve the merge and pin its first slot in view.
        var visibleHeight = target.Collapsed && target.HasMaster
            ? PaperLayoutDefaults.CapsuleHeight : (target.Slots + carriedCount) * pitch - gap;
        top = ClampVisibleTop(top, areaHeight, visibleHeight);
        var tops = ShiftNeighbours(queues, targetKey, target.TopDip, top,
            visibleHeight, pitch, gap);
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

