using System.IO;

namespace PaperTodo;

/// <summary>
/// Lifecycle for the lightweight overlay that marks which free queue a drag would join.
///
/// Two markers answer the two questions: a thin rail on the queue's inner edge identifies the
/// queue, and a short insertion line identifies the slot. The preview queue only grows by a small
/// gap below the insertion point; the full slot is opened when the capsule is committed.
/// </summary>
public sealed partial class AppController
{
    private EdgeCapsuleQueueDropTargetOutline? _edgeCapsuleQueueDropTargetOutline;

    // 高频评估（8ms 级）时的早退缓存：同一目标槽位不重复计算布局/移动窗口。
    private string _edgeCapsuleQueueDropTargetOutlineQueueKey = "";
    private string _edgeCapsuleQueueDropTargetOutlinePaperId = "";
    private int _edgeCapsuleQueueDropTargetOutlineCarriedCount;
    private int _edgeCapsuleQueueDropTargetOutlineIndex = -1;

    /// <summary>测试用：真正重新计算描边布局的次数。</summary>
    internal int DropTargetLayoutUpdateCount { get; private set; }

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

        var requestedCarriedCount = Math.Max(1, carriedCount);
        if (_edgeCapsuleQueueDropTargetOutline is { IsClosed: false, IsVisible: true } &&
            string.Equals(
                _edgeCapsuleQueueDropTargetOutlineQueueKey,
                queueKey,
                StringComparison.Ordinal) &&
            string.Equals(
                _edgeCapsuleQueueDropTargetOutlinePaperId,
                draggedPaperId,
                StringComparison.Ordinal) &&
            _edgeCapsuleQueueDropTargetOutlineCarriedCount == requestedCarriedCount &&
            _edgeCapsuleQueueDropTargetOutlineIndex == targetIndex)
        {
            return;
        }

        if (!TryGetEdgeCapsuleQueueDropTargetBounds(
                queueKey,
                draggedPaperId,
                requestedCarriedCount,
                targetIndex,
                out var layout,
                out var monitorDeviceName))
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (_edgeCapsuleQueueDropTargetOutline is null ||
            _edgeCapsuleQueueDropTargetOutline.IsClosed)
        {
            _edgeCapsuleQueueDropTargetOutline = new EdgeCapsuleQueueDropTargetOutline();
        }

        _edgeCapsuleQueueDropTargetOutline.ShowAt(
            layout,
            queueKey,
            FullscreenAvoidanceWindowForQueue(monitorDeviceName),
            animate: State.EnableAnimations);
        DropTargetLayoutUpdateCount++;
        _edgeCapsuleQueueDropTargetOutlineQueueKey = queueKey;
        _edgeCapsuleQueueDropTargetOutlinePaperId = draggedPaperId;
        _edgeCapsuleQueueDropTargetOutlineCarriedCount = requestedCarriedCount;
        _edgeCapsuleQueueDropTargetOutlineIndex = targetIndex;
    }

    internal void EndEdgeCapsuleQueueDropTargetOutline()
    {
        var outline = _edgeCapsuleQueueDropTargetOutline;
        _edgeCapsuleQueueDropTargetOutline = null;
        _edgeCapsuleQueueDropTargetOutlineQueueKey = "";
        _edgeCapsuleQueueDropTargetOutlinePaperId = "";
        _edgeCapsuleQueueDropTargetOutlineCarriedCount = 0;
        _edgeCapsuleQueueDropTargetOutlineIndex = -1;
        outline?.CloseForOwner();
    }

    /// <summary>
    /// 目标队列的预览矩形，以及插入位置的两条指示线矩形。全部是设备像素；DPI 一并返回，
    /// 供描边窗口换算成窗口内 DIP。
    ///
    /// 队列矩形只按真实成员高度加一个额外插入间隙，不再为 incoming capsule 打开完整槽位：
    /// 拖拽期间只需要告诉用户“插在哪”，松手落位时才展开完整槽位。
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
        var isLeftQueue = edgeKey[(separator + 1)..] == DeepCapsuleSides.Left;
        var side = isLeftQueue ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right;
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
        // 起始高度仍按最终提交槽数归一化，避免长队列靠近底部时预览与落位整队跳动。
        var finalSlotCount = members.Count + carriedCount;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(monitorDeviceName, side, FirstQueueIdOf(members)),
            area,
            finalSlotCount,
            DeepCapsuleGap);

        var topDip = EdgeCapsuleLayout.TopForIndex(
            0,
            startTop,
            area,
            finalSlotCount,
            DeepCapsuleGap);
        var heightDip = EdgeCapsuleQueueDropTargetGeometry.PreviewQueueHeightDip(
            members.Count,
            slotHeight,
            DeepCapsuleGap,
            PaperLayoutDefaults.CapsuleHeight,
            carriedCount);

        // 指示线跟随目标队列的真实可见宽度，而不是固定 92 DIP；短标题胶囊也能对齐。
        var visibleWidthDip = MeasureEdgeCapsuleQueueVisibleWidthDip(members, area.Width);
        var widthDevice = Math.Max(1, (int)Math.Round(geometry.DpiScaleX * visibleWidthDip));

        // Wall-anchored horizontally, exactly like a docked capsule, so the markers read as
        // "this queue" rather than a floating overlay.
        var leftDevice = isLeftQueue
            ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - widthDevice;

        // MonitorGeometry only converts device -> local, so invert it explicitly.
        double DeviceY(double dip) => geometry.WorkArea.Top + dip * geometry.DpiScaleY;

        var queueBounds = new DeviceScreenRect(
            leftDevice,
            (int)Math.Round(DeviceY(topDip)),
            leftDevice + widthDevice,
            (int)Math.Round(DeviceY(topDip + heightDip)));

        var clampedIndex = Math.Clamp(targetIndex, 0, members.Count);
        // 贴边胶囊窗口只在靠屏幕内侧保留 chrome，可见 body 的起点/宽度据此换算。
        var bodyWidthDip = Math.Max(
            0,
            visibleWidthDip - EdgeCapsuleLayout.WindowChromeMargin);
        var bodyLeftDip = isLeftQueue ? 0 : EdgeCapsuleLayout.WindowChromeMargin;

        var lineWidthDip = EdgeCapsuleQueueDropTargetGeometry.InsertionLineWidthDip(bodyWidthDip);
        var lineLeftDip = bodyLeftDip + (bodyWidthDip - lineWidthDip) / 2.0;
        var lineTopDip = EdgeCapsuleQueueDropTargetGeometry.InsertionLineCenterYDip(
                topDip,
                clampedIndex,
                members.Count,
                slotHeight,
                PaperLayoutDefaults.CapsuleHeight,
                carriedCount) -
            EdgeCapsuleQueueDropTargetGeometry.InsertionLineThicknessDip / 2.0;

        var railLeftDip = EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineXDip(
            visibleWidthDip,
            isLeftQueue);
        var railTopDip = topDip + EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineInsetDip;
        var railHeightDip = Math.Max(
            1,
            heightDip - EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineInsetDip * 2);

        var lineBottomDip = lineTopDip +
            EdgeCapsuleQueueDropTargetGeometry.InsertionLineThicknessDip;
        var insertionLineBounds = new DeviceScreenRect(
            leftDevice + (int)Math.Round(lineLeftDip * geometry.DpiScaleX),
            (int)Math.Round(DeviceY(lineTopDip)),
            leftDevice + (int)Math.Round((lineLeftDip + lineWidthDip) * geometry.DpiScaleX),
            (int)Math.Round(DeviceY(lineBottomDip)));
        var queueEdgeLineBounds = new DeviceScreenRect(
            leftDevice + (int)Math.Round(railLeftDip * geometry.DpiScaleX),
            (int)Math.Round(DeviceY(railTopDip)),
            leftDevice + (int)Math.Round(
                (railLeftDip + EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineWidthDip) *
                geometry.DpiScaleX),
            (int)Math.Round(DeviceY(railTopDip + railHeightDip)));

        layout = new EdgeCapsuleQueueDropTargetLayout(
            queueBounds,
            queueEdgeLineBounds,
            insertionLineBounds,
            geometry.DpiScaleX,
            geometry.DpiScaleY,
            clampedIndex,
            carriedCount);
        return true;
    }

    /// <summary>
    /// 目标队列的可见胶囊宽度（DIP，含靠屏幕内侧的 chrome margin）。取成员窗口实际测量宽度
    /// 的最大值；测不到时回退标准胶囊宽度，并按工作区宽度收窄，避免指示线被屏幕边缘裁切。
    /// </summary>
    private double MeasureEdgeCapsuleQueueVisibleWidthDip(
        IReadOnlyList<PaperData> members,
        double workAreaWidthDip)
    {
        var width = 0.0;
        foreach (var member in members)
        {
            if (!_windows.TryGetValue(member.Id, out var window))
            {
                continue;
            }

            var candidate = window.DeepCapsuleRestingVisibleWidth;
            if (double.IsFinite(candidate) && candidate > 0)
            {
                width = Math.Max(width, candidate);
            }
        }

        if (width <= 0)
        {
            width = PaperLayoutDefaults.CapsuleWidth;
        }

        return double.IsFinite(workAreaWidthDip) && workAreaWidthDip > 0
            ? Math.Min(width, workAreaWidthDip)
            : width;
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
        // 预览只让出一个小间隙；邻队避让必须按预览底边计算，否则会被多推一个完整槽位。
        var targetBottom = targetTop
            + targetMembers.Count * slotHeight
            - DeepCapsuleGap
            + EdgeCapsuleQueueDropTargetGeometry.ExtraInsertionGapDip(carried.Count);

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
