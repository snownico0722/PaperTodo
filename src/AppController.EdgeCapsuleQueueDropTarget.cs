using System.IO;

namespace PaperTodo;

/// <summary>
/// Lifecycle for the lightweight overlay that marks which free queue a drag would join.
///
/// Two markers answer the two questions: a thin rail on the queue's inner edge identifies the
/// queue, and a short insertion line identifies the slot. The preview uses the full inserted
/// slots and the same layout plan as the commit, so other queues avoid exactly once.
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
        int targetIndex = 0,
        EdgeCapsuleQueueDropLayoutPlan? plan = null)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (plan != null)
        {
            _edgeCapsuleQueueDropLayoutPlan = plan;
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
        _edgeCapsuleQueueDropLayoutPlan = null;
        outline?.CloseForOwner();
    }

    /// <summary>
    /// 目标队列的预览矩形，以及插入位置的两条指示线矩形。全部是设备像素；DPI 一并返回，
    /// 供描边窗口换算成窗口内 DIP。
    ///
    /// 可行时使用与提交完全相同的计划（已按完整槽位展开并求解邻队避让）；不可行时使用
    /// 目标队列的真实布局绘制警示线，不展示一个实际不会发生的重叠结果。
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
        var side = isLeftQueue ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                monitorDeviceName,
                out var geometry))
        {
            return false;
        }

        var members = FreeQueueMembersExcluding(queueKey, draggedPaperId);
        if (members.Count == 0)
        {
            return false;
        }

        var area = geometry.LocalWorkAreaDip;
        var clampedIndex = Math.Clamp(targetIndex, 0, members.Count);
        var requestedCarriedCount = Math.Max(1, carriedCount);

        var plan = _edgeCapsuleQueueDropLayoutPlan;
        var hasPlan = plan != null &&
                      string.Equals(plan.TargetQueueKey, queueKey, StringComparison.Ordinal) &&
                      plan.TargetIndex == clampedIndex &&
                      plan.CarriedCount == requestedCarriedCount;

        double targetTopDip;
        double targetHeightDip;
        double lineCenterDip;
        bool feasible;
        if (hasPlan)
        {
            targetTopDip = plan!.TargetTopDip;
            targetHeightDip = plan.TargetHeightDip;
            lineCenterDip = plan.InsertionLineCenterYDip;
            feasible = plan.Feasible;
        }
        else
        {
            // 预览计划尚未生成时的安全回退：按真实队列布局绘制，不做插入展开。
            var gap = DeepCapsuleGap;
            var slotHeight = EdgeCapsuleLayout.SlotHeight(gap);
            var margin = DeepCapsuleStartTopMarginForQueue(
                monitorDeviceName,
                EdgeForQueueSide(side),
                FirstQueueIdOf(members));
            targetTopDip = EdgeCapsuleLayout.TopForIndex(
                0,
                margin,
                area,
                members.Count,
                gap);
            targetHeightDip = members.Count * slotHeight - gap;
            lineCenterDip = EdgeCapsuleQueueDropTargetGeometry.InsertionLineCenterYDip(
                targetTopDip,
                clampedIndex,
                slotHeight,
                gap);
            feasible = true;
        }

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
            (int)Math.Round(DeviceY(targetTopDip)),
            leftDevice + widthDevice,
            (int)Math.Round(DeviceY(targetTopDip + targetHeightDip)));

        // 贴边胶囊窗口只在靠屏幕内侧保留 chrome，可见 body 的起点/宽度据此换算。
        var bodyWidthDip = Math.Max(
            0,
            visibleWidthDip - EdgeCapsuleLayout.WindowChromeMargin);
        var bodyLeftDip = isLeftQueue ? 0 : EdgeCapsuleLayout.WindowChromeMargin;

        var lineWidthDip = EdgeCapsuleQueueDropTargetGeometry.InsertionLineWidthDip(bodyWidthDip);
        var lineLeftDip = bodyLeftDip + (bodyWidthDip - lineWidthDip) / 2.0;
        var lineTopDip = lineCenterDip -
            EdgeCapsuleQueueDropTargetGeometry.InsertionLineThicknessDip / 2.0;

        var railLeftDip = EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineXDip(
            visibleWidthDip,
            isLeftQueue);
        var railTopDip = targetTopDip +
            EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineInsetDip;
        var railHeightDip = Math.Max(
            1,
            targetHeightDip - EdgeCapsuleQueueDropTargetGeometry.QueueEdgeLineInsetDip * 2);

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
            requestedCarriedCount,
            feasible);
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
    private readonly HashSet<string> _deepCapsuleDragAvoidanceCarriedIds = new(StringComparer.Ordinal);

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
        _deepCapsuleDragAvoidanceCarriedIds.Clear();
    }
}
