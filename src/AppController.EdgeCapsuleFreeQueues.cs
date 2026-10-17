using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// 一次拖拽的落点锚点。
///
/// Pointer 用于解析显示器、选择贴墙侧和计算水平距离；CapsuleCenterYDevice 是被拖胶囊的
/// 真实中心设备 Y。浮空阶段胶囊窗口以光标为中心，两者 Y 相同；贴边阶段胶囊顶边跟随光标，
/// 中心还要补上抓取偏移与半个胶囊高度。
/// </summary>
internal readonly record struct EdgeCapsuleDropAnchor(
    DeviceScreenPoint Pointer,
    double CapsuleCenterYDevice);

/// <summary>
/// Free edge-capsule queues (D-058).
///
/// The default arrangement lets one edge hold exactly one queue, whose height comes from a single
/// remembered start margin. Free mode keeps that arrangement intact but lets several queues share
/// an edge: a capsule dropped far from every queue starts a new one, a capsule dropped near one
/// joins it, and dragging a member away takes it out on its own.
///
/// All geometry decisions live in <see cref="EdgeCapsuleQueueMagnet"/>, which is a pure function.
/// This file only collects live state into magnet inputs and applies the resulting plan.
/// </summary>
public sealed partial class AppController
{
    internal bool EdgeCapsuleFreeQueuesEnabled =>
        State.UseCapsuleFreeQueues && State.UseCapsuleMode && State.UseDeepCapsuleMode;

    /// <summary>
    /// Queue instance key of a paper, for callers outside the controller that need to tell a
    /// same-queue reorder apart from a cross-queue merge.
    /// </summary>
    internal static string QueueKeyForPaper(PaperData paper) => QueueKey(paper);

    // The queue this drag is currently snapped to. Fed back into the magnet as its held queue so
    // the Schmitt trigger's dead band keeps a queue attached while the pointer jitters near the
    // exit threshold, instead of releasing and re-acquiring every few frames.
    private string _deepCapsuleDragHeldQueueKey = "";

    // Candidates depend only on the gesture's carried set and target monitor/side. Rebuilding them
    // on every 8 ms evaluation would re-measure every capsule title; cache them for the gesture.
    private string _deepCapsuleMagnetCandidateMonitor = "";
    private string _deepCapsuleMagnetCandidateSide = "";
    private bool _deepCapsuleMagnetCandidateExcludeSource;
    private readonly List<string> _deepCapsuleMagnetCandidateCarriedIds = new();
    private List<EdgeCapsuleQueueMagnetCandidate>? _deepCapsuleMagnetCandidateCache;

    /// <summary>测试用：真正重建磁吸候选集合的次数。</summary>
    internal int MagnetCandidateBuildCount { get; private set; }

    /// <summary>
    /// Geometry of the monitor a queue lives on, falling back to the primary monitor when the
    /// remembered device has since been unplugged. Null when no monitor can be resolved at all.
    /// </summary>
    private static MonitorGeometry? MonitorGeometryForQueue(string? monitorDeviceName)
    {
        if (WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName),
                out var geometry))
        {
            return geometry;
        }

        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out geometry)
            ? geometry
            : null;
    }

    /// <summary>
    /// Split a capsule out of its current queue into a brand new one resting at
    /// <paramref name="startTopDip"/>. The new queue keeps its own instance id and remembered
    /// height, so it survives a restart independently of the queue it came from.
    /// </summary>
    private void SplitCapsuleIntoQueue(
        PaperData paper,
        string monitorDeviceName,
        string side,
        double startTopDip,
        string queueId)
    {
        var normalizedMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName);
        var normalizedSide = DeepCapsuleSides.Normalize(side);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(normalizedMonitor, out var geometry))
        {
            return;
        }

        paper.CapsuleMonitorDeviceName = normalizedMonitor;
        paper.CapsuleSide = normalizedSide;
        paper.CapsuleQueueId = EdgeCapsuleQueueIds.Normalize(queueId);

        // Clamp at commit time only: while dragging, the capsule tracks the pointer 1:1 so the
        // drop position never feels like it lags behind the cursor.
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            startTopDip,
            geometry.LocalWorkAreaDip,
            1,
            DeepCapsuleGap);
        State.DeepCapsuleQueueStartTopMargins[QueueKey(normalizedMonitor, normalizedSide, paper.CapsuleQueueId)] = startTop;

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>
    /// Queue snapshot the magnet reasons about: which queue each visible docked capsule belongs to,
    /// and where each queue's slot 0 sits on the target monitor.
    ///
    /// <paramref name="carried"/> is everything the gesture is taking away. A whole-queue transfer
    /// carries its entire source queue, so that queue must be dropped from the candidates — it is
    /// the thing being moved, not somewhere to merge into. A single capsule keeps its source queue
    /// as a candidate, because reordering within your own queue is a valid outcome.
    /// </summary>
    private List<EdgeCapsuleQueueMagnetCandidate> BuildEdgeCapsuleQueueMagnetCandidates(
        IReadOnlyList<PaperData> carried,
        MonitorGeometry targetGeometry,
        string targetMonitor,
        string targetSide,
        bool excludeEntireSourceQueue,
        out string sourceQueueKey)
    {
        sourceQueueKey = carried.Count == 0 ? "" : QueueKey(carried[0]);
        if (_deepCapsuleMagnetCandidateCache is { } cached &&
            _deepCapsuleMagnetCandidateExcludeSource == excludeEntireSourceQueue &&
            string.Equals(_deepCapsuleMagnetCandidateMonitor, targetMonitor, StringComparison.Ordinal) &&
            string.Equals(_deepCapsuleMagnetCandidateSide, targetSide, StringComparison.Ordinal) &&
            _deepCapsuleMagnetCandidateCarriedIds.Count == carried.Count &&
            CarriedIdsMatch(carried))
        {
            return cached;
        }

        var result = new List<EdgeCapsuleQueueMagnetCandidate>();
        var carriedIds = new HashSet<string>(carried.Select(p => p.Id), StringComparer.Ordinal);
        var members = new Dictionary<string, List<PaperData>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (carriedIds.Contains(paper.Id) ||
                !string.Equals(WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(paper.CapsuleMonitorDeviceName),
                    targetMonitor, StringComparison.Ordinal) ||
                DeepCapsuleSides.Normalize(paper.CapsuleSide) != targetSide)
            {
                continue;
            }

            var key = QueueKey(paper);
            if (!members.TryGetValue(key, out var list))
            {
                // Carrying the whole source queue: it must not offer itself as a merge target.
                if (excludeEntireSourceQueue && key == sourceQueueKey)
                {
                    continue;
                }

                list = new List<PaperData>();
                members[key] = list;
                order.Add(key);
            }

            list.Add(paper);
        }

        var area = targetGeometry.LocalWorkAreaDip;
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        foreach (var key in order)
        {
            var papers = members[key];
            // Free mode has no master pill, so slot 0 belongs to a real capsule.
            var slotCount = papers.Count;
            var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
                DeepCapsuleStartTopMarginForQueue(targetMonitor,
                    targetSide == DeepCapsuleSides.Left ? EdgeCapsuleEdge.Left : EdgeCapsuleEdge.Right,
                    FirstQueueIdOf(papers)),
                area,
                slotCount,
                DeepCapsuleGap);

            // The magnet measures to the queue's wall-anchored rectangle, so the band must be the
            // width this queue actually occupies: its widest resting capsule. LocalWorkAreaDip's X
            // origin is the work area's left edge, matching EdgeCapsuleGeometry's `wall` for a left
            // queue and `wall - width` for a right one.
            var bandWidth = 0.0;
            foreach (var member in papers)
            {
                if (_windows.TryGetValue(member.Id, out var memberWindow))
                {
                    bandWidth = Math.Max(bandWidth, memberWindow.DeepCapsuleRestingVisibleWidth);
                }
            }

            var bandLeft = targetSide == DeepCapsuleSides.Left
                ? 0
                : Math.Max(0, area.Width - bandWidth);
            result.Add(new EdgeCapsuleQueueMagnetCandidate(
                key,
                slotCount,
                EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap),
                slotHeight,
                -1,
                bandLeft,
                bandWidth));
        }

        _deepCapsuleMagnetCandidateMonitor = targetMonitor;
        _deepCapsuleMagnetCandidateSide = targetSide;
        _deepCapsuleMagnetCandidateExcludeSource = excludeEntireSourceQueue;
        _deepCapsuleMagnetCandidateCarriedIds.Clear();
        foreach (var paper in carried)
        {
            _deepCapsuleMagnetCandidateCarriedIds.Add(paper.Id);
        }

        _deepCapsuleMagnetCandidateCache = result;
        MagnetCandidateBuildCount++;
        return result;
    }

    private bool CarriedIdsMatch(IReadOnlyList<PaperData> carried)
    {
        for (var index = 0; index < carried.Count; index++)
        {
            if (!string.Equals(
                    _deepCapsuleMagnetCandidateCarriedIds[index],
                    carried[index].Id,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ClearDeepCapsuleQueueMagnetCandidates()
    {
        _deepCapsuleMagnetCandidateCache = null;
        _deepCapsuleMagnetCandidateMonitor = "";
        _deepCapsuleMagnetCandidateSide = "";
        _deepCapsuleMagnetCandidateExcludeSource = false;
        _deepCapsuleMagnetCandidateCarriedIds.Clear();
    }

    private static string FirstQueueIdOf(List<PaperData> papers) =>
        papers.Count == 0 ? "" : EdgeCapsuleQueueIds.Normalize(papers[0].CapsuleQueueId);

    /// <summary>
    /// <summary>
    /// Commit a free-queue drag: decide with the magnet, then apply the resulting Reorder / Merge /
    /// NewQueue outcome. Falls back to the legacy arrangement when free mode is off.
    /// </summary>
    internal void CommitDeepCapsuleQueueDrop(PaperData paper, EdgeCapsuleDropAnchor anchor)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            return;
        }

        if (!TryResolveQueueDropGeometry(paper, anchor, out var drop))
        {
            // No monitor to resolve against: leave the arrangement untouched rather than guess.
            ArrangeDeepCapsules(animate: true);
            return;
        }

        // The commit must not inherit a drag-time hold: the pointer is already released, so the
        // Schmitt trigger's exit threshold would no longer reflect anything meaningful.
        _deepCapsuleDragHeldQueueKey = "";
        var plan = EvaluateDeepCapsuleQueueDrop(paper, drop);

        switch (plan.Outcome)
        {
            case EdgeCapsuleQueueMagnetOutcome.None:
                ArrangeDeepCapsules(animate: true);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Reorder:
                ReorderDeepCapsuleInFreeQueue(paper, plan.TargetQueueKey, drop.Geometry, anchor);
                return;

            case EdgeCapsuleQueueMagnetOutcome.Merge:
                MergeCapsuleIntoFreeQueue(paper, plan.TargetQueueKey, drop.Geometry, anchor);
                return;

            default:
                SplitCapsuleIntoQueue(
                    paper,
                    drop.MonitorDeviceName,
                    drop.Side,
                    plan.NewQueueStartTopDip,
                    EdgeCapsuleQueueIds.New());
                return;
        }
    }

    /// <summary>
    /// Live magnet evaluation for the drag in progress. Feeds the previous frame's target back in
    /// as the held queue so the Schmitt trigger's dead band can suppress flicker, then records the
    /// new target for the next frame.
    ///
    /// This is what turns free-queue dragging from "decide on release" into "decide continuously".
    /// </summary>
    internal EdgeCapsuleQueueMagnetPlan EvaluateDeepCapsuleQueueDrag(
        IReadOnlyList<PaperData> carried,
        EdgeCapsuleDropAnchor anchor,
        bool excludeEntireSourceQueue = false)
    {
        if (carried.Count == 0 ||
            !EdgeCapsuleFreeQueuesEnabled ||
            !TryResolveQueueDropGeometry(carried[0], anchor, out var drop))
        {
            return EdgeCapsuleQueueMagnet.None;
        }

        var plan = EdgeCapsuleQueueMagnet.Evaluate(
            BuildQueueMagnetInput(carried, drop, excludeEntireSourceQueue, _deepCapsuleDragHeldQueueKey));
        _deepCapsuleDragHeldQueueKey = plan.Outcome is EdgeCapsuleQueueMagnetOutcome.Merge
            or EdgeCapsuleQueueMagnetOutcome.Reorder
            ? plan.TargetQueueKey
            : "";
        return plan;
    }

    /// <summary>
    /// Tear down everything the drag-time feedback built: the held-queue state and the gate.
    /// Called on every gesture end so a dropped outline can never outlive the drag that created it,
    /// and so the next drag starts from a clean slate rather than inheriting a stale hold.
    /// </summary>
    internal void EndDeepCapsuleQueueMagnetPreview()
    {
        _deepCapsuleDragHeldQueueKey = "";
        ClearDeepCapsuleQueueDragAvoidanceState();
        ClearDeepCapsuleQueueMagnetCandidates();
        EndEdgeCapsuleQueueDropTargetOutline();
    }

    /// <summary>Drop the drag-time hold so the next gesture starts from a clean slate.</summary>
    internal void ForgetDeepCapsuleQueueDrag()
    {
        _deepCapsuleDragHeldQueueKey = "";
        ClearDeepCapsuleQueueMagnetCandidates();
    }

    private EdgeCapsuleQueueMagnetPlan EvaluateDeepCapsuleQueueDrop(
        PaperData paper,
        QueueDropGeometry drop) =>
        EdgeCapsuleQueueMagnet.Evaluate(BuildQueueMagnetInput([paper], drop, false, ""));

    private EdgeCapsuleQueueMagnetInput BuildQueueMagnetInput(
        IReadOnlyList<PaperData> carried,
        QueueDropGeometry drop,
        bool excludeEntireSourceQueue,
        string heldQueueKey)
    {
        var candidates = BuildEdgeCapsuleQueueMagnetCandidates(
            carried, drop.Geometry, drop.MonitorDeviceName, drop.Side, excludeEntireSourceQueue, out var sourceQueueKey);

        // 新队列提交时会按实际放下的槽位数夹回工作区：单胶囊是 1，Ctrl 整队搬移是整队成员数。
        // 磁吸必须用同一上界校验，否则它验证的位置与实际提交的位置不是同一个。
        var area = drop.Geometry.LocalWorkAreaDip;
        return new EdgeCapsuleQueueMagnetInput(
            drop.CenterTopDip,
            sourceQueueKey,
            candidates,
            EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap),
            PaperLayoutDefaults.CapsuleHeight,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            heldQueueKey,
            drop.CenterLeftDip,
            EdgeCapsuleLayout.TopMargin,
            EdgeCapsuleLayout.MaxStartTopMarginForCount(
                Math.Max(1, carried.Count), area, DeepCapsuleGap),
            Math.Max(1, carried.Count));
    }

    /// <summary>
    /// 指针位置解析出的目标显示器、贴墙侧与胶囊中心（本地 DIP）。
    /// </summary>
    private readonly record struct QueueDropGeometry(
        MonitorGeometry Geometry,
        string MonitorDeviceName,
        string Side,
        double CenterTopDip,
        double CenterLeftDip);

    private bool TryResolveQueueDropGeometry(
        PaperData paper,
        EdgeCapsuleDropAnchor anchor,
        out QueueDropGeometry drop)
    {
        drop = default;
        var pointer = anchor.Pointer;
        MonitorGeometry? resolved = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            pointer,
            out var atPoint)
            ? atPoint
            : MonitorGeometryForQueue(paper.CapsuleMonitorDeviceName);
        if (!resolved.HasValue)
        {
            return false;
        }

        var geometry = resolved.GetValueOrDefault();
        drop = new QueueDropGeometry(
            geometry,
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName),
            // 取目标显示器更近的物理墙。
            pointer.X < geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
                ? DeepCapsuleSides.Left
                : DeepCapsuleSides.Right,
            // 胶囊中心已由调用方按拖拽阶段换算好，这里不能再加半个胶囊高度。
            geometry.DeviceYToLocalDip(anchor.CapsuleCenterYDevice),
            // 浮空时胶囊以光标为中心；贴边时光标位于胶囊内，仍在同一贴墙条带附近。
            geometry.DeviceXToLocalDip(pointer.X));
        return true;
    }

    /// <summary>
    /// 被拖胶囊中心落在设备 Y <paramref name="draggedCenterDeviceY"/> 时，它在
    /// <paramref name="queueKey"/> 中会占据的槽位下标。
    ///
    /// 预览与提交共用这一函数，因此用户看到的空隙就是松手后得到的空隙。距离按「中心减半个
    /// 胶囊高度」换算成 top，抓取点位于胶囊顶部、中心还是底部都不会改变插入位。
    /// 坐标转换在目标队列自己的显示器上完成，跨屏/混合 DPI 时也能得到正确槽位。
    /// 取整规则沿用自由队列之前的插入规则（最近槽位，数值向上取整）。
    /// </summary>
    internal int DeepCapsuleQueueInsertIndexFor(
        string queueKey,
        string draggedPaperId,
        double draggedCenterDeviceY,
        int carriedCount = 1)
    {
        var members = FreeQueueMembersExcluding(queueKey, draggedPaperId);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        var side = separator >= 0 && edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left
            : EdgeCapsuleEdge.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(edgeKey[..Math.Max(separator, 0)], out var geometry))
        {
            return 0;
        }

        var area = geometry.LocalWorkAreaDip;
        var dropTopDip = geometry.DeviceYToLocalDip(draggedCenterDeviceY)
                         - PaperLayoutDefaults.CapsuleHeight / 2.0;
        var realCount = members.Count + Math.Max(1, carriedCount);
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(
                edgeKey[..Math.Max(separator, 0)],
                side,
                FirstQueueIdOf(members)),
            area,
            realCount,
            DeepCapsuleGap);
        var firstTop = EdgeCapsuleLayout.TopForIndex(0, startTop, area, realCount, DeepCapsuleGap);
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        return Math.Clamp((int)Math.Floor((dropTopDip - firstTop) / slotHeight + 0.5), 0, members.Count);
    }

    /// <summary>Insert the dragged capsule into an existing queue at the slot nearest the drop.</summary>
    private void MergeCapsuleIntoFreeQueue(
        PaperData paper,
        string targetQueueKey,
        MonitorGeometry targetGeometry,
        EdgeCapsuleDropAnchor anchor)
    {
        var targetMembers = FreeQueueMembersExcluding(targetQueueKey, paper.Id);
        var queueId = FirstQueueIdOf(targetMembers);
        var targetSide = DeepCapsuleSides.Normalize(
            EdgeCapsuleQueueIds.StripInstance(targetQueueKey)[(EdgeCapsuleQueueIds.StripInstance(targetQueueKey).LastIndexOf('|') + 1)..]);
        var insertAt = DeepCapsuleQueueInsertIndexFor(
            targetQueueKey, paper.Id, anchor.CapsuleCenterYDevice);

        paper.CapsuleMonitorDeviceName =
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(targetGeometry.DeviceName);
        paper.CapsuleSide = targetSide;
        paper.CapsuleQueueId = queueId;
        InsertPaperBesideQueue(paper, targetMembers, insertAt);

        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        SaveNow();
    }

    /// <summary>Move the dragged capsule to a new slot inside the queue it is already in.</summary>
    private void ReorderDeepCapsuleInFreeQueue(
        PaperData paper,
        string queueKey,
        MonitorGeometry targetGeometry,
        EdgeCapsuleDropAnchor anchor)
    {
        var members = FreeQueueMembersExcluding(queueKey, paper.Id);
        var queueId = FirstQueueIdOf(members);
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var targetSide = DeepCapsuleSides.Normalize(edgeKey[(edgeKey.LastIndexOf('|') + 1)..]);

        paper.CapsuleMonitorDeviceName =
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(targetGeometry.DeviceName);
        paper.CapsuleSide = targetSide;
        paper.CapsuleQueueId = queueId;

        var insertAt = DeepCapsuleQueueInsertIndexFor(
            queueKey, paper.Id, anchor.CapsuleCenterYDevice);

        InsertPaperBesideQueue(paper, members, insertAt);
        ArrangeDeepCapsules(animate: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    /// <summary>Diagnostics only: how many members a queue would have besides the dragged capsule.</summary>
    internal int FreeQueueMemberCount(string queueKey, string excludedPaperId) =>
        FreeQueueMembersExcluding(queueKey, excludedPaperId).Count;

    private List<PaperData> FreeQueueMembersExcluding(string queueKey, string excludedPaperId)
    {
        var members = new List<PaperData>();
        foreach (var paper in DeepCapsulePapersInOrder())
        {
            if (paper.Id != excludedPaperId && QueueKey(paper) == queueKey)
            {
                members.Add(paper);
            }
        }

        return members;
    }

    /// <summary>Single-capsule form of <see cref="InsertPaperBesideQueues"/>.</summary>
    private void InsertPaperBesideQueue(PaperData paper, List<PaperData> targetMembers, int insertAt)
        => InsertPaperBesideQueues([paper], targetMembers, insertAt);

    /// <summary>
    /// Rebuild <see cref="AppState.Papers"/> so <paramref name="papers"/> land together at
    /// <paramref name="insertAt"/> within the run of papers that already hold the target queue.
    /// Every unrelated paper keeps its position, matching the legacy reorder rule.
    ///
    /// A whole-queue transfer inserts its members as one block, so the queue's internal order is
    /// preserved exactly as it was before the drag.
    /// </summary>
    private void InsertPaperBesideQueues(
        IReadOnlyList<PaperData> papers,
        List<PaperData> targetMembers,
        int insertAt)
    {
        if (papers.Count == 0)
        {
            return;
        }

        var movingIds = new HashSet<string>(papers.Select(p => p.Id), StringComparer.Ordinal);
        var rebuilt = new List<PaperData>(State.Papers.Count);
        var anchorPaper = insertAt < targetMembers.Count ? targetMembers[insertAt] : null;
        foreach (var existing in State.Papers)
        {
            if (movingIds.Contains(existing.Id))
            {
                continue;
            }

            if (anchorPaper != null && existing.Id == anchorPaper.Id)
            {
                rebuilt.AddRange(papers);
            }

            rebuilt.Add(existing);
        }

        if (anchorPaper == null)
        {
            if (targetMembers.Count > 0)
            {
                var lastId = targetMembers[^1].Id;
                var index = rebuilt.FindIndex(p => p.Id == lastId);
                rebuilt.InsertRange(index + 1, papers);
            }
            else
            {
                rebuilt.AddRange(papers);
            }
        }

        State.Papers = rebuilt;
    }
}
