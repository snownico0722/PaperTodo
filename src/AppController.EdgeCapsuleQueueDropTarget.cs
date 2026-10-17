using System.IO;

namespace PaperTodo;

/// <summary>
/// Lifecycle for the outline that marks which free queue a drag would join.
///
/// The outline spans the target queue's whole slot range including the gap opened for the incoming
/// capsule, so one glance answers both "which queue" and "where in it".
/// </summary>
public sealed partial class AppController
{
    private EdgeCapsuleQueueDropTargetOutline? _edgeCapsuleQueueDropTargetOutline;

    internal void ShowEdgeCapsuleQueueDropTargetOutline(
        string queueKey,
        string draggedPaperId,
        int carriedCount = 1,
        int targetIndex = 0)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (!TryGetEdgeCapsuleQueueDropTargetBounds(
                queueKey,
                draggedPaperId,
                Math.Max(1, carriedCount),
                targetIndex,
                out var layout,
                out var monitorDeviceName))
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (_edgeCapsuleQueueDropTargetOutline is null)
        {
            _edgeCapsuleQueueDropTargetOutline = new EdgeCapsuleQueueDropTargetOutline();
        }

        _edgeCapsuleQueueDropTargetOutline.ShowAt(
            layout,
            queueKey,
            FullscreenAvoidanceWindowForQueue(monitorDeviceName),
            animate: State.EnableAnimations);
    }

    internal void EndEdgeCapsuleQueueDropTargetOutline()
    {
        var outline = _edgeCapsuleQueueDropTargetOutline;
        _edgeCapsuleQueueDropTargetOutline = null;
        outline?.CloseForOwner();
    }

    /// <summary>
    /// 目标队列插入后的整条队列矩形，以及第 <paramref name="targetIndex"/> 个槽位对应的
    /// 幽灵占位框矩形。两者都是设备像素；DPI 一并返回，供描边窗口换算成窗口内 DIP。
    ///
    /// 整条队列矩形包含为 incoming capsule 打开的空隙；占位框只覆盖真正要插入的那几个槽，
    /// 这样用户既知道“是哪条队列”，也知道“插在第几格”。
    /// </summary>
    private bool TryGetEdgeCapsuleQueueDropTargetBounds(
        string queueKey,
        string draggedPaperId,
        int carriedCount,
        int targetIndex,
        out EdgeCapsuleQueueDropTargetLayout layout,
        out string monitorDeviceName)
    {
        layout = default;
        monitorDeviceName = "";
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        if (separator < 0)
        {
            return false;
        }

        monitorDeviceName = edgeKey[..separator];
        var side = edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left
            : EdgeCapsuleEdge.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitorDeviceName, out var geometry))
        {
            return false;
        }

        var members = FreeQueueMembersExcluding(queueKey, draggedPaperId);
        if (members.Count == 0)
        {
            return false;
        }

        var area = geometry.LocalWorkAreaDip;
        var slotCount = members.Count + carriedCount;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(monitorDeviceName, side, FirstQueueIdOf(members)),
            area,
            slotCount,
            DeepCapsuleGap);

        var topDip = EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap);
        var heightDip = Math.Max(
            slotCount * slotHeight - DeepCapsuleGap,
            PaperLayoutDefaults.CapsuleHeight);
        var widthDevice = (int)Math.Round(geometry.DpiScaleX * PaperLayoutDefaults.CapsuleWidth);

        // Wall-anchored horizontally, exactly like a docked capsule, so the outline reads as
        // "this queue" rather than a floating rectangle.
        var leftDevice = side == EdgeCapsuleEdge.Left
            ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - widthDevice;

        // MonitorGeometry only converts device -> local, so invert it explicitly.
        double DeviceY(double dip) => geometry.WorkArea.Top + dip * geometry.DpiScaleY;

        var queueBounds = new DeviceScreenRect(
            leftDevice,
            (int)Math.Round(DeviceY(topDip)),
            leftDevice + widthDevice,
            (int)Math.Round(DeviceY(topDip + heightDip)));

        // 队尾插入时 index == members.Count，占位框底边正好与整条队列底边重合。
        var clampedIndex = Math.Clamp(targetIndex, 0, members.Count);
        var placeholderTopDip = EdgeCapsuleQueueDropTargetGeometry.PlaceholderTopDip(
            topDip, clampedIndex, slotHeight);
        var placeholderHeightDip = EdgeCapsuleQueueDropTargetGeometry.PlaceholderHeightDip(
            carriedCount, slotHeight, DeepCapsuleGap, PaperLayoutDefaults.CapsuleHeight);
        var placeholderBounds = new DeviceScreenRect(
            leftDevice,
            (int)Math.Round(DeviceY(placeholderTopDip)),
            leftDevice + widthDevice,
            (int)Math.Round(DeviceY(placeholderTopDip + placeholderHeightDip)));

        layout = new EdgeCapsuleQueueDropTargetLayout(
            queueBounds,
            placeholderBounds,
            geometry.DpiScaleX,
            geometry.DpiScaleY,
            clampedIndex,
            carriedCount);
        return true;
    }

    // 拖拽插入时被联动推开的队列：key -> 是否为源队列。
    // 源队列恢复时必须重新下发“撤走 carried 后”的收拢布局，不能按真实成员恢复。
    private readonly Dictionary<string, bool> _deepCapsuleDragAvoidanceQueues = new(StringComparer.Ordinal);
    private string _deepCapsuleDragAvoidanceTargetKey = "";
    private string _deepCapsuleDragAvoidanceSourceQueueKey = "";
    private readonly HashSet<string> _deepCapsuleDragAvoidanceCarriedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 磁吸插入时，把目标队列同一边下方的其它队列一起推开，复用 hover mini-view 的边级避让。
    ///
    /// 目标队列自身的成员让位由 PreviewDeepCapsuleQueueDrag 的绝对 placement 负责；这里只处理
    /// 因为目标队列变高而需要下移的其它队列。offset 以相对位移写入 TopOffsetDip，和 mini-view
    /// 避让使用同一套语义。
    /// </summary>
    private void PreviewDeepCapsuleQueueDragAvoidance(
        IReadOnlyList<PaperData> carried,
        string targetQueueKey)
    {
        if (carried.Count == 0 || string.IsNullOrEmpty(targetQueueKey))
        {
            return;
        }

        var sourceQueueKey = QueueKey(carried[0]);
        if (!string.Equals(
                _deepCapsuleDragAvoidanceTargetKey,
                targetQueueKey,
                StringComparison.Ordinal))
        {
            // 目标切换：先撤掉上一目标留下的联动位移。
            RestoreDeepCapsuleQueueDragAvoidance();
        }

        _deepCapsuleDragAvoidanceTargetKey = targetQueueKey;
        _deepCapsuleDragAvoidanceSourceQueueKey = sourceQueueKey;
        _deepCapsuleDragAvoidanceCarriedIds.Clear();
        foreach (var paper in carried)
        {
            _deepCapsuleDragAvoidanceCarriedIds.Add(paper.Id);
        }

        var targetEdgeKey = EdgeCapsuleQueueIds.StripInstance(targetQueueKey);
        var separator = targetEdgeKey.LastIndexOf('|');
        if (separator < 0 ||
            !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                targetEdgeKey[..Math.Max(separator, 0)],
                out var targetGeometry))
        {
            return;
        }

        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, "");
        if (targetMembers.Count == 0)
        {
            return;
        }

        var area = targetGeometry.LocalWorkAreaDip;
        var side = targetEdgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left
            : EdgeCapsuleEdge.Right;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var slotCount = targetMembers.Count + carried.Count;
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(
                targetEdgeKey[..Math.Max(separator, 0)],
                side,
                FirstQueueIdOf(targetMembers)),
            area,
            slotCount,
            DeepCapsuleGap);
        var targetTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap);
        var targetBottom = targetTop + slotCount * slotHeight - DeepCapsuleGap;

        var queues = BuildDeepCapsuleDragAvoidanceQueues(
            targetQueueKey, targetEdgeKey, targetTop);
        if (queues.Count == 0)
        {
            return;
        }

        var offsets = EdgeCapsulePreviewLayoutCoordinator.BuildEdgeAvoidanceOffsets(
            queues,
            targetQueueKey,
            targetBottom,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (offsets.Count == 0)
        {
            return;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        EdgeCapsuleQueuePlan? vacatedSourcePlan = null;
        if (!string.IsNullOrEmpty(sourceQueueKey))
        {
            var remaining = new List<PaperData>();
            foreach (var paper in DeepCapsulePapersInOrder())
            {
                if (!_deepCapsuleDragAvoidanceCarriedIds.Contains(paper.Id) &&
                    QueueKey(paper) == sourceQueueKey)
                {
                    remaining.Add(paper);
                }
            }

            if (remaining.Count > 0)
            {
                vacatedSourcePlan = EdgeCapsuleQueueCoordinator.Build(
                    remaining.Select(paper => new EdgeCapsuleQueueMember(paper, sourceQueueKey)),
                    State.UseCapsuleCollapseAll);
            }
        }

        var queueKeysByPaper = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            queueKeysByPaper[paper.Id] = QueueKey(paper);
        }

        foreach (var (paperId, offset) in offsets)
        {
            EdgeCapsulePlacement placement;
            var queueKey = queueKeysByPaper.GetValueOrDefault(paperId, "");
            if (vacatedSourcePlan is { } sourcePlan &&
                sourcePlan.Placements.TryGetValue(paperId, out var sourcePlacement))
            {
                // 源队列成员用“撤走 carried 后”的收拢槽位，再叠加联动位移。
                placement = sourcePlacement;
                queueKey = sourceQueueKey;
            }
            else if (basePlan.Placements.TryGetValue(paperId, out var basePlacement))
            {
                placement = basePlacement;
            }
            else
            {
                continue;
            }

            if (!_windows.TryGetValue(paperId, out var window))
            {
                continue;
            }

            window.PreviewDeepCapsulePlacement(placement with { TopOffsetDip = offset });
            if (queueKey.Length > 0)
            {
                _deepCapsuleDragAvoidanceQueues[queueKey] =
                    string.Equals(queueKey, sourceQueueKey, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// 目标队列同 monitor+side 上的所有队列，按 rest top 升序；carried 成员从源队列中剔除，
    /// 因此源队列的成员列表和 slotCount 反映“撤走 carried 后”的收拢状态。
    /// </summary>
    private List<EdgeCapsuleAvoidanceQueue> BuildDeepCapsuleDragAvoidanceQueues(
        string targetQueueKey,
        string targetEdgeKey,
        double targetRestTopDip)
    {
        var result = new List<EdgeCapsuleAvoidanceQueue>();
        var members = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (_deepCapsuleDragAvoidanceCarriedIds.Contains(paper.Id))
            {
                continue;
            }

            var key = QueueKey(paper);
            if (!string.Equals(
                    EdgeCapsuleQueueIds.StripInstance(key),
                    targetEdgeKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!members.TryGetValue(key, out var list))
            {
                list = new List<PaperData>();
                members[key] = list;
                order.Add(key);
            }

            list.Add(paper);
        }

        foreach (var key in order)
        {
            var papers = members[key];
            if (papers.Count == 0)
            {
                continue;
            }

            var edgeKey = EdgeCapsuleQueueIds.StripInstance(key);
            var separator = edgeKey.LastIndexOf('|');
            if (separator < 0)
            {
                continue;
            }

            var side = edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
                ? EdgeCapsuleEdge.Left
                : EdgeCapsuleEdge.Right;
            if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    edgeKey[..Math.Max(separator, 0)],
                    out var geometry))
            {
                continue;
            }

            var slotCount = papers.Count;
            var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
                DeepCapsuleStartTopMarginForQueue(
                    edgeKey[..Math.Max(separator, 0)],
                    side,
                    FirstQueueIdOf(papers)),
                geometry.LocalWorkAreaDip,
                slotCount,
                DeepCapsuleGap);
            var restTop = EdgeCapsuleLayout.TopForIndex(
                0,
                startTop,
                geometry.LocalWorkAreaDip,
                slotCount,
                DeepCapsuleGap);
            result.Add(new EdgeCapsuleAvoidanceQueue(
                key,
                papers.Select(paper => paper.Id).ToArray(),
                restTop));
        }

        // 目标队列用插入后的 top 参与排序，让 owner 在列表中的位置反映新布局。
        for (var index = 0; index < result.Count; index++)
        {
            if (string.Equals(result[index].QueueKey, targetQueueKey, StringComparison.Ordinal))
            {
                result[index] = result[index] with { RestTopDip = targetRestTopDip };
            }
        }

        result.Sort(static (left, right) => left.RestTopDip.CompareTo(right.RestTopDip));
        return result;
    }

    /// <summary>撤掉拖拽插入造成的同边联动位移；源队列恢复为收拢后的布局。</summary>
    private void RestoreDeepCapsuleQueueDragAvoidance()
    {
        if (_deepCapsuleDragAvoidanceQueues.Count > 0)
        {
            foreach (var (queueKey, isSource) in _deepCapsuleDragAvoidanceQueues)
            {
                if (isSource)
                {
                    var remaining = new List<PaperData>();
                    foreach (var paper in DeepCapsulePapersInOrder())
                    {
                        if (!_deepCapsuleDragAvoidanceCarriedIds.Contains(paper.Id) &&
                            QueueKey(paper) == queueKey)
                        {
                            remaining.Add(paper);
                        }
                    }

                    if (remaining.Count > 0)
                    {
                        PreviewEdgeCapsuleQueueMembers(
                            queueKey, remaining, _deepCapsuleDragAvoidanceCarriedIds);
                    }
                }
                else
                {
                    var members = FreeQueueMembersExcluding(queueKey, "");
                    if (members.Count > 0)
                    {
                        PreviewEdgeCapsuleQueueMembers(
                            queueKey,
                            members,
                            new HashSet<string>(StringComparer.Ordinal));
                    }
                }
            }
        }

        ClearDeepCapsuleQueueDragAvoidanceState();
    }

    /// <summary>手势结束时只清状态，不做恢复；最终 ArrangeDeepCapsules 会兜底真实布局。</summary>
    private void ClearDeepCapsuleQueueDragAvoidanceState()
    {
        _deepCapsuleDragAvoidanceQueues.Clear();
        _deepCapsuleDragAvoidanceTargetKey = "";
        _deepCapsuleDragAvoidanceSourceQueueKey = "";
        _deepCapsuleDragAvoidanceCarriedIds.Clear();
    }
}
