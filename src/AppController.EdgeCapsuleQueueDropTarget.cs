using System.Globalization;
using System.IO;
using System.Text;

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
    private int _edgeCapsuleQueueDropTargetOutlinePlanVersion = -1;

    // 每次重新生成拖放布局计划时递增。早退键必须包含它，否则同槽位的外部输入变化会
    // 复用旧的可行性/几何。
    private int _edgeCapsuleQueueDropLayoutVersion;

    /// <summary>测试用：真正重新计算描边布局的次数。</summary>
    internal int DropTargetLayoutUpdateCount { get; private set; }

    // 拖拽期间目标队列的成员、标题和字体不会变化，插入槽变化只影响指示线的纵向位置。
    // 指示线宽度此前每次 index 变化都会对每个成员重新读取 resting width（多次文本测量），
    // 是大队列热路径上的固定浪费；这里只在手势内按队列缓存宽度，结束拖拽即清空。
    private readonly Dictionary<string, (string Signature, double Width)>
        _edgeCapsuleQueueDropTargetWidthCache = new(StringComparer.Ordinal);

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
            _edgeCapsuleQueueDropLayoutVersion++;
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
            _edgeCapsuleQueueDropTargetOutlineIndex == targetIndex &&
            _edgeCapsuleQueueDropTargetOutlinePlanVersion == _edgeCapsuleQueueDropLayoutVersion)
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

        if (!_edgeCapsuleQueueDropTargetOutline.ShowAt(
            layout,
            queueKey,
            FullscreenAvoidanceWindowForQueue(monitorDeviceName),
            animate: State.EnableAnimations))
        {
            // FQ-13: ShowAt 返回 false 表示下发失败（宿主已关闭、设备矩形失败或布局为空），
            // 此时 markers / 颜色 / z-order 都未被应用，写早退缓存会让下一帧目标不再重试。
            // 直接 return 保留所有早退键与计数，下一反馈周期自然重进 ShowAt。
            return;
        }
        DropTargetLayoutUpdateCount++;
        _edgeCapsuleQueueDropTargetOutlineQueueKey = queueKey;
        _edgeCapsuleQueueDropTargetOutlinePaperId = draggedPaperId;
        _edgeCapsuleQueueDropTargetOutlineCarriedCount = requestedCarriedCount;
        _edgeCapsuleQueueDropTargetOutlineIndex = targetIndex;
        _edgeCapsuleQueueDropTargetOutlinePlanVersion = _edgeCapsuleQueueDropLayoutVersion;
    }

    internal void EndEdgeCapsuleQueueDropTargetOutline()
    {
        var outline = _edgeCapsuleQueueDropTargetOutline;
        _edgeCapsuleQueueDropTargetOutline = null;
        _edgeCapsuleQueueDropTargetOutlineQueueKey = "";
        _edgeCapsuleQueueDropTargetOutlinePaperId = "";
        _edgeCapsuleQueueDropTargetOutlineCarriedCount = 0;
        _edgeCapsuleQueueDropTargetOutlineIndex = -1;
        _edgeCapsuleQueueDropTargetOutlinePlanVersion = -1;
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
            var margin = DeepCapsuleStartTopMarginForQueueKey(queueKey);
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
        var visibleWidthDip = MeasureEdgeCapsuleQueueVisibleWidthDipCached(
            queueKey, members, area.Width);
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
    /// 带手势级缓存的可见宽度查询。插入槽变化不改变队列成员与标题，同一拖动内无需重复测量。
    /// </summary>
    private double MeasureEdgeCapsuleQueueVisibleWidthDipCached(
        string queueKey,
        IReadOnlyList<PaperData> members,
        double workAreaWidthDip)
    {
        var signature = MeasureEdgeCapsuleQueueVisibleWidthSignature(members, workAreaWidthDip);
        if (_edgeCapsuleQueueDropTargetWidthCache.TryGetValue(queueKey, out var cached) &&
            string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            return cached.Width;
        }

        var width = MeasureEdgeCapsuleQueueVisibleWidthDip(members, workAreaWidthDip);
        _edgeCapsuleQueueDropTargetWidthCache[queueKey] = (signature, width);
        return width;
    }

    /// <summary>
    /// 影响目标队列可见宽度的输入签名。标题、折叠状态或排版设置变化时必须重测，
    /// 因此这些值都进入签名；同一手势内仅插入槽变化时签名保持不变。
    /// </summary>
    private string MeasureEdgeCapsuleQueueVisibleWidthSignature(
        IReadOnlyList<PaperData> members,
        double workAreaWidthDip)
    {
        var builder = new StringBuilder();
        builder.Append(State.CapsuleTextSize).Append('\u001f')
            .Append(State.CapsuleTextBold).Append('\u001f')
            .Append(State.Zoom.ToString("R", CultureInfo.InvariantCulture)).Append('\u001f')
            .Append(State.UiFontPreset).Append(':')
            .Append(State.UiLanguage).Append(':')
            .Append(State.TextRenderingProfile).Append('\u001f')
            .Append(State.DeepCapsuleTitleMeasureCharacterLimit).Append('\u001f')
            .Append(State.ExperimentalEdgeCapsuleHoverPreview).Append('\u001f')
            .Append(workAreaWidthDip.ToString("R", CultureInfo.InvariantCulture));
        foreach (var member in members)
        {
            builder.Append('\u001e')
                .Append(member.Id).Append(':')
                .Append(member.IsCollapsed ? 'c' : 'e').Append(':')
                .Append(PaperCapsuleTitle(member));
            // 插件 PreferredWidth / 自动宽度模板变化由窗口版本号覆盖，不重新测量文字宽度。
            builder.Append(_windows.TryGetValue(member.Id, out var window)
                ? ":w" + window.CapsuleWidthRevision
                : ":nw");
        }

        return builder.ToString();
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
    internal void RestoreDeepCapsuleQueueDragAvoidance()
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
