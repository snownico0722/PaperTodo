namespace PaperTodo;

public sealed partial class AppController
{
    private void ActivateEdgeCapsuleShortcut(string preferredSide, int ordinal)
    {
        if (HasDeepCapsuleReorderDragInProgress() ||
            ordinal is < 1 or > 9 ||
            !State.UseCapsuleMode ||
            !State.UseDeepCapsuleMode)
        {
            return;
        }

        var papers = DeepCapsulePapersInOrder();
        if (papers.Count == 0)
        {
            return;
        }

        var normalizedPreferredSide = DeepCapsuleSides.Normalize(preferredSide);
        var oppositeSide = normalizedPreferredSide == DeepCapsuleSides.Left
            ? DeepCapsuleSides.Right
            : DeepCapsuleSides.Left;
        var queuePlan = BuildEdgeQueuePlan(
            papers.Select(paper => new EdgeCapsuleQueueMember(paper, QueueKey(paper))),
            State.UseCapsuleCollapseAll);
        var restingDeltas = BuildEffectiveQueueRestingDeltas();
        // 自由队列的 key 带 #instance；按物理边汇总，才能把同一侧的全部队列纳入序号。
        var queuesByLiveEdge = queuePlan.Queues
            .Where(queue => queue.Papers.Count > 0)
            .Select(queue => (
                LiveEdge: LiveEdgeOfQueueKey(queue.Key),
                Queue: EdgeShortcutQueue(queue, restingDeltas)))
            .Where(entry => entry.LiveEdge.Length > 0 && entry.Queue.HasValue)
            .GroupBy(entry => entry.LiveEdge, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Queue!.Value).ToList(),
                StringComparer.Ordinal);

        foreach (var monitorName in EdgeShortcutMonitorSearchOrder())
        {
            foreach (var side in new[] { normalizedPreferredSide, oppositeSide })
            {
                if (!queuesByLiveEdge.TryGetValue(EdgeKey(monitorName, side), out var edgeQueues))
                {
                    continue;
                }

                var target = EdgeCapsuleShortcutResolver.Resolve(edgeQueues, ordinal);
                if (target == null)
                {
                    continue;
                }

                if (_windows.TryGetValue(target.Id, out var window))
                {
                    window.ActivateFromEdgeShortcut();
                }
                return;
            }
        }
    }

    /// <summary>
    /// 把队列转换成快捷键解析器的输入；几何不可解析的队列直接排除。
    /// </summary>
    private EdgeCapsuleShortcutQueue? EdgeShortcutQueue(
        EdgeCapsuleQueue queue,
        IReadOnlyDictionary<string, double> restingDeltas)
    {
        if (!EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitor, out _) ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitor, out var geometry))
        {
            return null;
        }

        // Use the same base geometry as the actual queue. Legacy fit-to-screen
        // normalization would lift a folded free queue by its hidden full height.
        var margin = DeepCapsuleStartTopMarginForQueueKey(queue.Key);
        var top = EdgeCapsuleFreeQueuesEnabled
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(0, margin, geometry.LocalWorkAreaDip, DeepCapsuleGap)
            : EdgeCapsuleLayout.TopForIndex(0, margin, geometry.LocalWorkAreaDip,
                queue.Papers.Count + (queue.HasMaster ? 1 : 0), DeepCapsuleGap);
        return new EdgeCapsuleShortcutQueue(
            queue.Key, top + restingDeltas.GetValueOrDefault(queue.Key), queue.Papers);
    }

    private static IReadOnlyList<string> EdgeShortcutMonitorSearchOrder()
    {
        var currentMonitor = "";
        if (WindowNative.TryGetCursorScreenPosition(out var cursor) &&
            WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(cursor) is { } cursorMonitor)
        {
            currentMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(cursorMonitor.DeviceName);
        }

        var connectedMonitors = WindowWorkAreaHelper.ConnectedMonitorGeometries();
        var candidateNames = connectedMonitors
            .Select(monitor => WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitor.DeviceName))
            .Append(currentMonitor)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(currentMonitor, out var currentGeometry))
        {
            return candidateNames;
        }

        var currentCenterX = (currentGeometry.WorkArea.Left + currentGeometry.WorkArea.Right) / 2.0;
        var currentCenterY = (currentGeometry.WorkArea.Top + currentGeometry.WorkArea.Bottom) / 2.0;
        var candidates = connectedMonitors
            .Select(geometry => (
                Name: WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName),
                Geometry: geometry))
            .Where(candidate => !string.Equals(candidate.Name, currentMonitor, StringComparison.Ordinal))
            .ToList();

        static double CenterX(MonitorGeometry geometry) =>
            (geometry.WorkArea.Left + geometry.WorkArea.Right) / 2.0;
        static double CenterY(MonitorGeometry geometry) =>
            (geometry.WorkArea.Top + geometry.WorkArea.Bottom) / 2.0;

        var left = candidates
            .Where(candidate => CenterX(candidate.Geometry) < currentCenterX - 0.5)
            .OrderBy(candidate => currentCenterX - CenterX(candidate.Geometry))
            .ThenBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();
        var right = candidates
            .Where(candidate => CenterX(candidate.Geometry) > currentCenterX + 0.5)
            .OrderBy(candidate => CenterX(candidate.Geometry) - currentCenterX)
            .ThenBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();
        var vertical = candidates
            .Where(candidate => Math.Abs(CenterX(candidate.Geometry) - currentCenterX) <= 0.5)
            .OrderBy(candidate => Math.Abs(currentCenterY - CenterY(candidate.Geometry)))
            .ToList();

        var ordered = new List<string>(candidateNames.Count) { currentMonitor };
        var horizontalDepth = Math.Max(left.Count, right.Count);
        for (var index = 0; index < horizontalDepth; index++)
        {
            if (index < left.Count)
            {
                ordered.Add(left[index].Name);
            }
            if (index < right.Count)
            {
                ordered.Add(right[index].Name);
            }
        }
        ordered.AddRange(vertical.Select(candidate => candidate.Name));
        return ordered;
    }
}

/// <summary>
/// 一条位于某个物理边上的队列，以及它在静止状态下的可见起始位置。
/// </summary>
internal readonly record struct EdgeCapsuleShortcutQueue(
    string QueueKey,
    double StartTopDip,
    IReadOnlyList<PaperData> Papers);

/// <summary>
/// 数字快捷键的序号解析。纯函数：调用方先把队列过滤到同一物理边，
/// 这里按可见顺序跨队列连续计数；主胶囊与队列内的 master slot 不占序号。
/// </summary>
internal static class EdgeCapsuleShortcutResolver
{
    /// <summary>
    /// 解析第 <paramref name="ordinal"/> 个（1 起）真实胶囊。
    /// 等 top 时按 QueueKey 稳定排序，与静止避让的约定一致。
    /// </summary>
    internal static PaperData? Resolve(
        IReadOnlyList<EdgeCapsuleShortcutQueue> queues,
        int ordinal)
    {
        if (ordinal is < 1 or > 9)
        {
            return null;
        }

        var remaining = ordinal - 1;
        foreach (var queue in queues
                     .Where(queue => queue.Papers.Count > 0)
                     .OrderBy(queue => queue.StartTopDip)
                     .ThenBy(queue => queue.QueueKey, StringComparer.Ordinal))
        {
            if (remaining < queue.Papers.Count)
            {
                return queue.Papers[remaining];
            }

            remaining -= queue.Papers.Count;
        }

        return null;
    }
}
