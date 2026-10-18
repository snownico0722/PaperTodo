using System.Diagnostics;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    // Two 60-Hz frames reject incidental pass-through without adding the visibly sluggish 50-ms
    // floor to every A-to-B switch. The negative-only predictor can still extend or veto motion.
    private const double EdgeCapsulePreviewTransferResidenceMilliseconds = 32;
    private const double EdgeCapsulePreviewPointerToleranceDip = 2;
    private const double EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds = 24;
    private const double EdgeCapsulePreviewActivationIntentTrackingMilliseconds = 16;
    private const double EdgeCapsulePreviewLayoutSuppressionMilliseconds =
        EdgeCapsuleLayout.SlotMoveMilliseconds + 50;
    private const int EdgeCapsulePreviewCursorReadRetryCount = 1;

    private readonly EdgeCapsuleHoverIntentPredictor
        _edgeCapsulePreviewIntentPredictor = new();
    private EdgeCapsulePreviewLayoutSession? _edgeCapsulePreviewSession;

    /// <summary>测试用：真正重建预览布局 session 的次数。</summary>
    internal int PreviewSessionRebuildCount { get; private set; }
    private string? _edgeCapsulePreviewOutgoingPaperId;
    private string? _edgeCapsulePreviewQueuedTransferPaperId;
    private string? _edgeCapsulePreviewQueuedCloseOwnerPaperId;
    private EdgeCapsulePreviewCloseReason _edgeCapsulePreviewQueuedCloseReason;
    private EdgeCapsulePreviewActivationIntent?
        _edgeCapsulePreviewActivationIntent;
    private EdgeCapsulePreviewCorridorExitIntent?
        _edgeCapsulePreviewCorridorExitIntent;
    private DispatcherTimer? _edgeCapsulePreviewCorridorIntentTimer;
    private DispatcherTimer? _edgeCapsulePreviewActivationIntentTimer;
    private EdgeCapsulePreviewPointerAnchor?
        _edgeCapsulePreviewLayoutSuppressionAnchor;
    private int _edgeCapsulePreviewTransferGeneration;
    private int _edgeCapsulePreviewCloseGeneration;
    private int _edgeCapsulePreviewPointerResolutionVersion;
    private EdgeCapsulePreviewLayoutSession?
        _edgeCapsulePreviewLastResolvedSession;
    private DeviceScreenPoint? _edgeCapsulePreviewLastResolvedPointer;
    private int _edgeCapsulePreviewLastResolvedVersion = -1;

    private readonly record struct EdgeCapsulePreviewActivationIntent(
        string TargetPaperId,
        string? ExpectedOwnerPaperId,
        DeviceScreenPoint StableAnchor,
        long CandidateSinceTimestamp,
        long StableSinceTimestamp);

    private readonly record struct EdgeCapsulePreviewPointerAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY,
        string QueueKey,
        long CreatedAtTimestamp);

    private readonly record struct EdgeCapsulePreviewStableAnchor(
        DeviceScreenPoint Point,
        double DpiScaleX,
        double DpiScaleY);

    private readonly record struct EdgeCapsulePreviewCorridorExitIntent(
        string OwnerPaperId,
        long CorridorSinceTimestamp,
        long? NoTargetIntentSinceTimestamp,
        bool PausedForPointerCapture);

    private readonly record struct EdgeCapsulePreviewPointerResolution(
        PaperWindow? Target,
        bool OwnerContains,
        bool TransferRectangleContains);

    private EdgeCapsuleQueuePlan BuildCurrentEdgeCapsuleQueuePlan()
    {
        var papers = DeepCapsulePapersInOrder();
        return EdgeCapsuleQueueCoordinator.Build(
            papers.Select(paper =>
                new EdgeCapsuleQueueMember(paper, QueueKey(paper))),
            State.UseCapsuleCollapseAll);
    }

    private EdgeCapsuleQueuePlan ApplyEdgeCapsulePreviewLayout(
        EdgeCapsuleQueuePlan basePlan)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return basePlan;
        }

        if (!basePlan.Placements.ContainsKey(session.OwnerPaperId) ||
            IsCapsuleCollapseAllActiveForQueue(session.QueueKey) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var ownerSize = owner.CurrentEdgeCapsulePreviewSize;
        if (!owner.IsEdgeCapsulePreviewOpen ||
            !ownerSize.HasValue ||
            ownerSize.Value != session.Size ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentQueueKey = QueueKey(owner.EdgeCapsulePreviewPaper);
        var currentQueue = basePlan.Queues.FirstOrDefault(queue =>
            string.Equals(
                queue.Key,
                currentQueueKey,
                StringComparison.Ordinal));
        if (currentQueue == null)
        {
            ResetEdgeCapsulePreviewWithoutArrange();
            return basePlan;
        }

        var currentIds = currentQueue.Papers
            .Select(paper => paper.Id)
            .ToArray();
        var edgeQueues = BuildEdgeCapsuleAvoidanceQueues(basePlan, session, currentQueueKey);
        var currentEdgeIds = edgeQueues.SelectMany(queue => queue.PaperIds).ToArray();

        // EdgePaperIds 只影响边级避让的输入，不应触发 session 重建：重建会以 previous:null
        // 重算，丢掉同队列 owner 转移的连续性。只有队列身份/成员变化才需要新 session。
        var queueKeyChanged = !string.Equals(
            session.QueueKey,
            currentQueueKey,
            StringComparison.Ordinal);
        var queueMembersChanged = !session.QueuePaperIds.SequenceEqual(
            currentIds,
            StringComparer.Ordinal);
        if (queueKeyChanged || queueMembersChanged)
        {
            PreviewSessionRebuildCount++;
            session = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
                basePlan,
                null,
                currentQueueKey,
                session.OwnerPaperId,
                session.Size,
                PaperLayoutDefaults.CapsuleHeight,
                DeepCapsuleGap);
            if (session == null)
            {
                ResetEdgeCapsulePreviewWithoutArrange();
                return basePlan;
            }
        }

        // 只存基础 session（owner 自身 offsets）+ 最新边级成员集合；边级避让是每帧从 rest
        // margin 重新计算的派生量，写回会让旧邻居 offset 在下一次 Arrange 残留。
        // 仅在记录实际变化时替换引用，保持指针解析的 ReferenceEquals 缓存有效。
        var edgePaperIdsChanged = !session.EdgePaperIds.SequenceEqual(
            currentEdgeIds,
            StringComparer.Ordinal);
        if (queueKeyChanged || queueMembersChanged || edgePaperIdsChanged)
        {
            session = session with { EdgePaperIds = currentEdgeIds };
            _edgeCapsulePreviewSession = session;
        }

        var appliedSession = ApplyEdgeCapsuleAvoidance(
            basePlan,
            session,
            edgeQueues,
            currentEdgeIds);
        return EdgeCapsulePreviewLayoutCoordinator.Apply(basePlan, appliedSession);
    }

    /// <summary>
    /// Every queue resting on the owner's monitor+edge, ascending by slot-0 top, with each queue's
    /// member ids in their own order.
    ///
    /// Free queues let one edge hold several independent stacks (D-058), so a mini-view on one of
    /// them would otherwise be overlapped by the others. Only the controller can read the persisted
    /// per-queue start margins, so this descriptor is assembled here and handed to the pure policy.
    /// </summary>
    private List<EdgeCapsuleAvoidanceQueue> BuildEdgeCapsuleAvoidanceQueues(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession session,
        string ownerQueueKey)
    {
        var result = new List<EdgeCapsuleAvoidanceQueue>();
        var ownerPaper = session.OwnerPaperId;
        if (!basePlan.Placements.ContainsKey(ownerPaper) ||
            !EdgeCapsuleQueueIds.TryGetEdge(ownerQueueKey, out _, out _))
        {
            return result;
        }

        var ownerLiveEdgeKey = LiveEdgeOfQueueKey(ownerQueueKey);
        foreach (var queue in basePlan.Queues)
        {
            // 边级避让按物理 live edge 分组：断连队列回退到主屏后必须与主屏同侧队列
            // 一起避让。队列身份仍使用持久 key，二者不能混用。
            if (!string.Equals(
                    LiveEdgeOfQueueKey(queue.Key),
                    ownerLiveEdgeKey,
                    StringComparison.Ordinal) ||
                !EdgeCapsuleQueueIds.TryGetEdge(queue.Key, out var monitorDeviceName, out _) ||
                !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                    monitorDeviceName,
                    out var geometry))
            {
                continue;
            }

            var papers = queue.Papers;
            if (papers.Count == 0)
            {
                continue;
            }

            var slotCount = papers.Count;
            // margin 必须从队列身份 key 派生：free 关闭时 key 是裸 edge key，
            // 纸片上的实例 id 已不参与运行期身份，继续读它会得到不同的起始高度。
            result.Add(new EdgeCapsuleAvoidanceQueue(
                queue.Key,
                papers.Select(p => p.Id).ToArray(),
                EdgeCapsuleLayout.TopForIndex(
                    0,
                    DeepCapsuleStartTopMarginForQueueKey(queue.Key),
                    geometry.LocalWorkAreaDip,
                    slotCount,
                    DeepCapsuleGap)));
        }

        result.Sort(static (left, right) => left.RestTopDip.CompareTo(right.RestTopDip));
        return result;
    }

    /// <summary>
    /// Merge the edge-wide avoidance into the session so the single-queue policy above and the
    /// cross-queue policy below travel through the same
    /// <see cref="EdgeCapsulePreviewLayoutCoordinator.Apply"/> path.
    /// </summary>
    private EdgeCapsulePreviewLayoutSession ApplyEdgeCapsuleAvoidance(
        EdgeCapsuleQueuePlan basePlan,
        EdgeCapsulePreviewLayoutSession session,
        IReadOnlyList<EdgeCapsuleAvoidanceQueue> edgeQueues,
        IReadOnlyList<string> edgePaperIds)
    {
        if (edgeQueues.Count == 0)
        {
            return session;
        }

        if (!basePlan.Placements.TryGetValue(session.OwnerPaperId, out var ownerPlacement))
        {
            return session with { EdgePaperIds = edgePaperIds };
        }

        var ownerQueue = edgeQueues.FirstOrDefault(queue => queue.PaperIds.Contains(session.OwnerPaperId));
        if (ownerQueue.QueueKey == null)
        {
            return session with { EdgePaperIds = edgePaperIds };
        }

        // The card occupies the owner's compact slot and grows downward from it — but the card's
        // bottom edge is NOT the anchor. The single-queue policy also pushes the owner's own lower
        // members, and they can settle below the card: hovering A3 in [A1..A4] leaves A4 sitting at
        // cardTop + cardHeight + gap. Anchoring on the card alone would push the next queue to
        // exactly A4's new top, so the two overlap. Take the lowest bottom the owner's queue
        // reaches, card included.
        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        // owner 自己可能带着上一帧 session 留下的位移（向下换卡时 keep 分支继承 currentTops），
        // 卡片就画在那个位置上。漏掉它会让避让按静止槽位计算——矮卡片恰好被下面的成员循环补偿、
        // 高卡片则少算整个 offset，导致下一条队列被少推、与展开卡片重叠。
        var ownerOffset = session.TopOffsetsDip.GetValueOrDefault(session.OwnerPaperId);
        var ownerCardTop = ownerQueue.RestTopDip
            + (ownerPlacement.VisualIndex * slotHeight)
            + ownerOffset;
        var cardHeight = Math.Max(session.Size.HeightDip, PaperLayoutDefaults.CapsuleHeight);
        var ownerBottom = ownerCardTop + cardHeight;
        for (var index = 0; index < ownerQueue.PaperIds.Count; index++)
        {
            var memberId = ownerQueue.PaperIds[index];
            var memberRestTop = ownerQueue.RestTopDip + (index * slotHeight);
            var memberOffset = session.TopOffsetsDip.TryGetValue(memberId, out var value) ? value : 0;
            ownerBottom = Math.Max(
                ownerBottom,
                memberRestTop + memberOffset + PaperLayoutDefaults.CapsuleHeight);
        }
        var extra = EdgeCapsulePreviewLayoutCoordinator.BuildEdgeAvoidanceOffsets(
            edgeQueues,
            session.QueueKey,
            ownerBottom,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (extra.Count == 0)
        {
            return session with { EdgePaperIds = edgePaperIds };
        }

        var merged = new Dictionary<string, double>(session.TopOffsetsDip, StringComparer.Ordinal);
        foreach (var (paperId, offset) in extra)
        {
            merged[paperId] = offset;
        }


        return session with { TopOffsetsDip = merged, EdgePaperIds = edgePaperIds };
    }

    private void ResetEdgeCapsulePreviewWithoutArrange()
    {
        var session = _edgeCapsulePreviewSession;
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        var closeGeneration = ++_edgeCapsulePreviewCloseGeneration;
        ReleaseTrackedOutgoingEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            closeGeneration != _edgeCapsulePreviewCloseGeneration ||
            _edgeCapsulePreviewSession != null)
        {
            return;
        }
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            owner.SetEdgeCapsulePreviewClosed(animate: false);
        }
    }

    internal void NotifyEdgeCapsulePointerOverChanged(
        PaperWindow window,
        bool pointerOver)
    {
        if (IsExiting)
        {
            return;
        }

        if (!pointerOver)
        {
            CancelEdgeCapsulePreviewActivationIntent(
                window.EdgeCapsulePreviewPaperId);
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            return;
        }

        var session = _edgeCapsulePreviewSession;
        if (session != null &&
            string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer) ||
            !window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer);
        if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
        {
            // A WPF enter caused only by the moving queue has no activation authority. Real
            // screen-space pointer motion clears this suppression before the intent gate starts.
            return;
        }

        AdvanceEdgeCapsulePreviewActivationIntent(
            session,
            window,
            pointer);
    }

    internal void NotifyEdgeCapsulePreviewPointerSample(
        PaperWindow window,
        DeviceScreenPoint? pointer)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (!pointer.HasValue)
            {
                return;
            }

            ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
                pointer.Value);
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(window))
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
                return;
            }

            // This also recovers an initial enter whose first dispatcher turn became stale. A real
            // first hit has no dwell; the queued callback still revalidates the physical target.
            if (window.IsEdgeCapsulePointerOver &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer.Value))
            {
                AdvanceEdgeCapsulePreviewActivationIntent(
                    null,
                    window,
                    pointer.Value);
            }
            else
            {
                CancelEdgeCapsulePreviewActivationIntent(
                    window.EdgeCapsulePreviewPaperId);
            }
            return;
        }

        if (!string.Equals(
                session.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!window.CanEnterEdgeCapsulePreview)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
            return;
        }
        if (!pointer.HasValue)
        {
            if (window.EdgeCapsulePreviewPointerCaptureActive)
            {
                HoldEdgeCapsulePreviewForPointerCapture(window, session);
            }
            else
            {
                TrackEdgeCapsulePreviewUnavailablePointer(window, session);
            }
            return;
        }

        ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(pointer.Value);
        if (window.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(window, session);
            return;
        }
        ResumeEdgeCapsulePreviewAfterPointerCapture();

        if (CanReuseEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value))
        {
            return;
        }

        ObserveEdgeCapsulePreviewPointer(window, pointer.Value);

        var resolution = ResolveEdgeCapsulePreviewPointer(
            session,
            pointer.Value);
        if (resolution.OwnerContains)
        {
            CancelEdgeCapsulePreviewActivationIntent();
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        if (resolution.Target != null)
        {
            CancelQueuedEdgeCapsulePreviewClose();
            ResetEdgeCapsulePreviewCorridorExitIntent();
            if (IsEdgeCapsulePreviewLayoutSuppressedFor(
                    resolution.Target))
            {
                CancelEdgeCapsulePreviewActivationIntent();
                RememberEdgeCapsulePreviewPointerResolution(
                    session,
                    pointer.Value);
                return;
            }

            AdvanceEdgeCapsulePreviewActivationIntent(
                session,
                resolution.Target,
                pointer.Value);
            return;
        }

        CancelEdgeCapsulePreviewActivationIntent();
        var exitPolicy = EdgeCapsulePreviewExitPolicy.Resolve(
            resolution.TransferRectangleContains,
            State.ExperimentalEdgeCapsuleHoverIntent);
        if (exitPolicy != EdgeCapsulePreviewExitPolicyDecision.ImmediateClose)
        {
            var queuedCloseForOwner = string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal);
            if (queuedCloseForOwner &&
                _edgeCapsulePreviewQueuedCloseReason ==
                    EdgeCapsulePreviewCloseReason.OutsideTransferRectangle)
            {
                // Once the physical pointer has crossed the hard edge, returning only to empty
                // space cannot revoke that event. A real owner/target (handled above) still can.
                RememberEdgeCapsulePreviewPointerResolution(
                    session,
                    pointer.Value);
                return;
            }

            var decision = AdvanceEdgeCapsulePreviewCorridorExitIntent(
                window,
                session,
                pointer.Value);
            if (queuedCloseForOwner)
            {
                // A completed fixed wait is final. In predictive mode, only newly confirmed motion
                // toward a real capsule may revoke a queued no-target close; another blank/no-intent
                // sample must not restart the sensitivity clock.
                if (EdgeCapsulePreviewExitPolicy.EmptyRegionCanCancelQueuedClose(
                        _edgeCapsulePreviewQueuedCloseReason,
                        State.ExperimentalEdgeCapsuleHoverIntent,
                        decision == EdgeCapsuleCorridorExitDecision.KeepAlive))
                {
                    CancelQueuedEdgeCapsulePreviewClose();
                }
                else
                {
                    ResetEdgeCapsulePreviewCorridorExitIntent();
                }
            }
            RememberEdgeCapsulePreviewPointerResolution(
                session,
                pointer.Value);
            return;
        }

        // The transfer rectangle's outer edge is absolute. Prediction only owns its interior.
        ResetEdgeCapsulePreviewCorridorExitIntent();
        RememberEdgeCapsulePreviewPointerResolution(
            session,
            pointer.Value);
        QueueEdgeCapsulePreviewClose(window, session.OwnerPaperId);
    }

    internal bool CloseEdgeCapsulePreviewForDrag(PaperWindow draggedWindow)
    {
        var session = _edgeCapsulePreviewSession;
        if (session == null)
        {
            return false;
        }

        var draggedWindowWasOwner = string.Equals(
            session.OwnerPaperId,
            draggedWindow.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);
        CloseEdgeCapsulePreview(animate: true, arrange: true);
        return draggedWindowWasOwner;
    }

    internal void CloseEdgeCapsulePreviewForClose(PaperWindow window)
    {
        if (IsEdgeCapsulePreviewOwner(window))
        {
            CloseEdgeCapsulePreview(animate: false, arrange: false);
        }
    }

    internal void CloseEdgeCapsulePreviewForBodySessionReset(PaperWindow window)
    {
        if (IsEdgeCapsulePreviewOwner(window))
        {
            // A protocol 1.8 preview can own controls from the body session that is about to be
            // replaced. End the controller session and restore queue placement before that tree is
            // disposed, otherwise the edge host can retain a dead native/Web mini view.
            CloseEdgeCapsulePreview(animate: false, arrange: true);
        }

        // Also repair a locally staged request if the controller session was already removed (for
        // example during shutdown). Apply the compact state synchronously enough to precede the
        // next render, then sever the final visual-tree reference while the session is still alive.
        window.ResetEdgeCapsulePreviewForBodySessionChange();
    }

    internal bool IsEdgeCapsulePreviewOwner(PaperWindow window) =>
        _edgeCapsulePreviewSession is { } session &&
        string.Equals(
            session.OwnerPaperId,
            window.EdgeCapsulePreviewPaperId,
            StringComparison.Ordinal);

    private void QueueEdgeCapsulePreviewClose(
        PaperWindow window,
        string ownerPaperId,
        EdgeCapsulePreviewCloseReason reason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle)
    {
        if (string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewQueuedCloseReason =
                EdgeCapsulePreviewExitPolicy.StrongerCloseReason(
                    _edgeCapsulePreviewQueuedCloseReason,
                    reason);
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = ownerPaperId;
        _edgeCapsulePreviewQueuedCloseReason = reason;
        var generation = ++_edgeCapsulePreviewCloseGeneration;
        ProcessQueuedEdgeCapsulePreviewClose(
            window,
            ownerPaperId,
            generation,
            EdgeCapsulePreviewCursorReadRetryCount);
    }

    private void ProcessQueuedEdgeCapsulePreviewClose(
        PaperWindow window,
        string ownerPaperId,
        int generation,
        int cursorReadRetriesRemaining)
    {
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _edgeCapsulePreviewCloseGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (IsExiting ||
                    _edgeCapsulePreviewSession is not { } session ||
                    !string.Equals(
                        session.OwnerPaperId,
                        ownerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(ownerPaperId, out var owner) ||
                    !ReferenceEquals(owner, window))
                {
                    ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                    return;
                }
                if (owner.CanEnterEdgeCapsulePreview &&
                    owner.EdgeCapsulePreviewPointerCaptureActive)
                {
                    ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                    HoldEdgeCapsulePreviewForPointerCapture(owner, session);
                    return;
                }
                if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    // A transient cursor-read failure must not silently consume the only close
                    // requested by this input turn. Retry once; a confirmed hard boundary or
                    // elapsed no-target deadline remains close authority if reading still fails.
                    ForgetEdgeCapsulePreviewPointerResolution();
                    if (cursorReadRetriesRemaining > 0)
                    {
                        ProcessQueuedEdgeCapsulePreviewClose(
                            window,
                            ownerPaperId,
                            generation,
                            cursorReadRetriesRemaining - 1);
                    }
                    else
                    {
                        var fallbackReason =
                            _edgeCapsulePreviewQueuedCloseReason;
                        ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);
                        CloseEdgeCapsulePreview(animate: true, arrange: true);
                    }
                    return;
                }

                var queuedReason = _edgeCapsulePreviewQueuedCloseReason;
                ConsumeQueuedEdgeCapsulePreviewClose(ownerPaperId);

                var resolution = ResolveEdgeCapsulePreviewPointer(
                    session,
                    pointer);
                if (owner.CanEnterEdgeCapsulePreview &&
                    resolution.OwnerContains)
                {
                    return;
                }
                if (owner.CanEnterEdgeCapsulePreview &&
                    resolution.Target != null)
                {
                    // A real target discovered during close revalidation is transfer authority,
                    // not merely a reason to swallow the close. Route it through the same owner
                    // arbiter so the normal 32 ms target-residence rule still applies.
                    ForgetEdgeCapsulePreviewPointerResolution();
                    NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
                    return;
                }
                CloseEdgeCapsulePreview(animate: true, arrange: true);
            }),
            DispatcherPriority.Input);
    }

    private void ConsumeQueuedEdgeCapsulePreviewClose(string ownerPaperId)
    {
        if (!string.Equals(
                _edgeCapsulePreviewQueuedCloseOwnerPaperId,
                ownerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
    }

    private void CancelQueuedEdgeCapsulePreviewClose()
    {
        if (_edgeCapsulePreviewQueuedCloseOwnerPaperId == null)
        {
            return;
        }

        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        _edgeCapsulePreviewCloseGeneration++;
    }

    private void QueueEdgeCapsulePreviewTransfer(
        PaperWindow window,
        string? expectedOwnerPaperId = null,
        EdgeCapsulePreviewStableAnchor? stableAnchor = null)
    {
        var paperId = window.EdgeCapsulePreviewPaperId;
        if (!AllowsEdgeCapsuleQueueProxyOwnership(
                QueueKey(window.EdgeCapsulePreviewPaper)))
        {
            CancelEdgeCapsulePreviewActivationIntent();
            return;
        }
        if (string.Equals(
                _edgeCapsulePreviewQueuedTransferPaperId,
                paperId,
                StringComparison.Ordinal))
        {
            return;
        }

        _edgeCapsulePreviewQueuedTransferPaperId = paperId;
        var generation = ++_edgeCapsulePreviewTransferGeneration;
        ProcessQueuedEdgeCapsulePreviewTransfer(
            window,
            paperId,
            expectedOwnerPaperId,
            stableAnchor,
            generation,
            EdgeCapsulePreviewCursorReadRetryCount);
    }

    private void ProcessQueuedEdgeCapsulePreviewTransfer(
        PaperWindow window,
        string paperId,
        string? expectedOwnerPaperId,
        EdgeCapsulePreviewStableAnchor? stableAnchor,
        int generation,
        int cursorReadRetriesRemaining)
    {
        window.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (IsExiting ||
                    generation != _edgeCapsulePreviewTransferGeneration ||
                    !string.Equals(
                        _edgeCapsulePreviewQueuedTransferPaperId,
                        paperId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (!_windows.TryGetValue(paperId, out var current) ||
                    !ReferenceEquals(current, window))
                {
                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    return;
                }
                if (!AllowsEdgeCapsuleQueueProxyOwnership(
                        QueueKey(current.EdgeCapsulePreviewPaper)))
                {
                    CancelEdgeCapsulePreviewActivationIntent();
                    return;
                }
                if (!current.CanEnterEdgeCapsulePreview)
                {
                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    return;
                }
                if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
                {
                    if (cursorReadRetriesRemaining > 0)
                    {
                        ProcessQueuedEdgeCapsulePreviewTransfer(
                            window,
                            paperId,
                            expectedOwnerPaperId,
                            stableAnchor,
                            generation,
                            cursorReadRetriesRemaining - 1);
                        return;
                    }

                    _edgeCapsulePreviewQueuedTransferPaperId = null;
                    return;
                }
                _edgeCapsulePreviewQueuedTransferPaperId = null;
                if (!current.IsEdgeCapsuleInteractiveAt(pointer))
                {
                    return;
                }
                if (stableAnchor.HasValue &&
                    EdgeCapsulePreviewPointerMovedBeyondTolerance(
                        stableAnchor.Value.Point,
                        pointer,
                        stableAnchor.Value.DpiScaleX,
                        stableAnchor.Value.DpiScaleY))
                {
                    return;
                }

                var session = _edgeCapsulePreviewSession;
                if (expectedOwnerPaperId == null)
                {
                    // An initial-profile request is valid only for the first preview in a session.
                    // If another request already opened one, its owner sampler starts a transfer.
                    if (session != null)
                    {
                        return;
                    }
                }
                else if (session == null ||
                    !string.Equals(
                        session.OwnerPaperId,
                        expectedOwnerPaperId,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(
                        expectedOwnerPaperId,
                        out var owner))
                {
                    return;
                }
                else if (owner.EdgeCapsulePreviewPointerCaptureActive)
                {
                    HoldEdgeCapsulePreviewForPointerCapture(owner, session);
                    return;
                }

                OpenOrTransferEdgeCapsulePreview(current, pointer);
            }),
            // Opening after a real hit must be committed before the next composition pass. The
            // callback still runs outside the current routed event and keeps every stale-work guard.
            DispatcherPriority.Send);
    }

    private void OpenOrTransferEdgeCapsulePreview(
        PaperWindow window,
        DeviceScreenPoint pointer)
    {
        if (IsExiting)
        {
            return;
        }
        if (!window.CanEnterEdgeCapsulePreview)
        {
            return;
        }
        if (!window.IsEdgeCapsuleInteractiveAt(pointer))
        {
            return;
        }

        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        _edgeCapsulePreviewCloseGeneration++;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        var previous = _edgeCapsulePreviewSession;
        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                window.EdgeCapsulePreviewPaperId,
                StringComparison.Ordinal))
        {
            // A transfer may begin before the owner from the transfer before it has completed its
            // 200 ms retraction. Keep at most the current owner plus one outgoing preview tree.
            ReleaseTrackedOutgoingEdgeCapsulePreview();
        }
        var request = window.PrepareEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            !EdgeCapsulePreviewSessionsHaveSameOwner(
                previous,
                _edgeCapsulePreviewSession))
        {
            return;
        }
        // Same-generation layout refreshes may replace the immutable record while preserving the
        // owner. Treat that refreshed record as the source session for the rest of this CAS.
        previous = _edgeCapsulePreviewSession;
        if (request == null)
        {
            return;
        }

        var basePlan = BuildCurrentEdgeCapsuleQueuePlan();
        var queueKey = QueueKey(window.EdgeCapsulePreviewPaper);
        var next = EdgeCapsulePreviewLayoutCoordinator.OpenOrTransfer(
            basePlan,
            previous,
            queueKey,
            window.EdgeCapsulePreviewPaperId,
            request.Size,
            PaperLayoutDefaults.CapsuleHeight,
            DeepCapsuleGap);
        if (next == null)
        {
            return;
        }
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            !EdgeCapsulePreviewSessionsHaveSameOwner(
                previous,
                _edgeCapsulePreviewSession))
        {
            return;
        }
        previous = _edgeCapsulePreviewSession;

        // Commit the controller owner before the target view is mounted. StagePreviewContent and
        // WPF layout are allowed to re-enter input/layout code; every re-entrant observer must see
        // the same owner that the target model is about to expose. If opening is rejected, roll
        // the controller session back before returning.
        _edgeCapsulePreviewSession = next;
        if (!window.SetEdgeCapsulePreviewOpen(
                request,
                animate: true))
        {
            if (transferGeneration == _edgeCapsulePreviewTransferGeneration &&
                string.Equals(
                    _edgeCapsulePreviewSession?.OwnerPaperId,
                    next.OwnerPaperId,
                    StringComparison.Ordinal))
            {
                _edgeCapsulePreviewSession = previous;
                // Stage/Prepare may have re-entered layout while the temporary owner was visible.
                // Restore the source plan immediately so queued placement work cannot paint the
                // rejected target generation.
                ArrangeDeepCapsules(animate: false);
            }
            return;
        }
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } currentSession ||
            !string.Equals(
                currentSession.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }
        // A re-entrant layout refresh may rebuild the immutable session record without superseding
        // this transfer. Continue with that same-generation, same-owner plan.
        next = currentSession;

        if (previous != null &&
            !string.Equals(
                previous.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            if (_windows.TryGetValue(previous.OwnerPaperId, out var oldOwner))
            {
                oldOwner.SetEdgeCapsulePreviewClosed(animate: true);
                if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                    _edgeCapsulePreviewSession is not { } sessionAfterClose ||
                    !string.Equals(
                        sessionAfterClose.OwnerPaperId,
                        next.OwnerPaperId,
                        StringComparison.Ordinal))
                {
                    return;
                }
                if (!oldOwner.IsEdgeCapsulePreviewOpen)
                {
                    _edgeCapsulePreviewOutgoingPaperId = previous.OwnerPaperId;
                }
            }
        }

        EnforceEdgeCapsulePreviewContentLimit(transferGeneration);
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            _edgeCapsulePreviewSession is not { } committedSession ||
            !string.Equals(
                committedSession.OwnerPaperId,
                next.OwnerPaperId,
                StringComparison.Ordinal))
        {
            return;
        }
        next = committedSession;

        ResetEdgeCapsulePreviewCorridorExitIntent();
        RecordEdgeCapsulePreviewTransferPointer(window, next.QueueKey);
        ArrangeDeepCapsules(animate: true);
    }

    private static bool EdgeCapsulePreviewSessionsHaveSameOwner(
        EdgeCapsulePreviewLayoutSession? first,
        EdgeCapsulePreviewLayoutSession? second) =>
        first == null
            ? second == null
            : second != null && string.Equals(
                first.OwnerPaperId,
                second.OwnerPaperId,
                StringComparison.Ordinal);

    private void EnforceEdgeCapsulePreviewContentLimit(int transferGeneration)
    {
        foreach (var window in _windows.Values.ToArray())
        {
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration)
            {
                return;
            }

            var currentPaperId = _edgeCapsulePreviewSession?.OwnerPaperId;
            var outgoingPaperId = _edgeCapsulePreviewOutgoingPaperId;
            var paperId = window.EdgeCapsulePreviewPaperId;
            if (string.Equals(paperId, currentPaperId, StringComparison.Ordinal) ||
                string.Equals(paperId, outgoingPaperId, StringComparison.Ordinal) ||
                (!window.IsEdgeCapsulePreviewOpen &&
                 !window.HasEdgeCapsulePreviewContent))
            {
                continue;
            }

            // Re-entrant provider/layout work can supersede an outer A→B transfer with C before A
            // is recorded as outgoing. Repair that stale third owner immediately; the invariant is
            // current plus one outgoing mounted tree, regardless of callback nesting.
            if (window.IsEdgeCapsulePreviewOpen)
            {
                window.SetEdgeCapsulePreviewClosed(animate: false);
            }
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                string.Equals(
                    paperId,
                    _edgeCapsulePreviewSession?.OwnerPaperId,
                    StringComparison.Ordinal) ||
                string.Equals(
                    paperId,
                    _edgeCapsulePreviewOutgoingPaperId,
                    StringComparison.Ordinal))
            {
                return;
            }
            window.ClearEdgeCapsulePreviewContent();
        }
    }

    private void CloseEdgeCapsulePreview(bool animate, bool arrange)
    {
        var transferGeneration = ++_edgeCapsulePreviewTransferGeneration;
        var closeGeneration = ++_edgeCapsulePreviewCloseGeneration;
        var session = _edgeCapsulePreviewSession;
        _edgeCapsulePreviewSession = null;
        _edgeCapsulePreviewQueuedTransferPaperId = null;
        _edgeCapsulePreviewQueuedCloseOwnerPaperId = null;
        _edgeCapsulePreviewQueuedCloseReason =
            EdgeCapsulePreviewCloseReason.OutsideTransferRectangle;
        ResetEdgeCapsulePreviewActivationIntent();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        ReleaseTrackedOutgoingEdgeCapsulePreview();
        if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
            closeGeneration != _edgeCapsulePreviewCloseGeneration ||
            _edgeCapsulePreviewSession != null)
        {
            return;
        }
        PaperWindow? owner = null;
        if (session != null &&
            _windows.TryGetValue(session.OwnerPaperId, out var currentOwner))
        {
            owner = currentOwner;
            currentOwner.SetEdgeCapsulePreviewClosed(animate);
            if (transferGeneration != _edgeCapsulePreviewTransferGeneration ||
                closeGeneration != _edgeCapsulePreviewCloseGeneration ||
                _edgeCapsulePreviewSession != null)
            {
                return;
            }
            if (!currentOwner.IsEdgeCapsulePreviewOpen)
            {
                _edgeCapsulePreviewOutgoingPaperId = session.OwnerPaperId;
            }
        }

        if (arrange && session != null && owner != null)
        {
            // Compacting the source queue must not manufacture a new hover under a stationary
            // pointer. The queue key scopes this suppression so a capsule already reached in a
            // different queue can still become an initial candidate immediately.
            RecordEdgeCapsulePreviewTransferPointer(owner, session.QueueKey);
        }
        else
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
        }

        if (arrange)
        {
            ArrangeDeepCapsules(animate);
        }
    }

    private void ReleaseTrackedOutgoingEdgeCapsulePreview()
    {
        var outgoingPaperId = _edgeCapsulePreviewOutgoingPaperId;
        _edgeCapsulePreviewOutgoingPaperId = null;
        if (outgoingPaperId == null ||
            string.Equals(
                outgoingPaperId,
                _edgeCapsulePreviewSession?.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(outgoingPaperId, out var outgoing))
        {
            return;
        }

        // The outgoing model was closed when it stopped owning the session. Releasing its old
        // preview tree must not snap that window ahead of the still-animated queue generation;
        // the compact shell can finish the existing transition without retaining heavy content.
        outgoing.ClearEdgeCapsulePreviewContent();
    }

    private void RecordEdgeCapsulePreviewTransferPointer(
        PaperWindow target,
        string queueKey)
    {
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (target.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
        }
        else if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                pointer,
                out var monitor))
        {
            dpiScaleX = monitor.DpiScaleX;
            dpiScaleY = monitor.DpiScaleY;
        }
        else
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            _edgeCapsulePreviewIntentPredictor.Reset();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _edgeCapsulePreviewLayoutSuppressionAnchor =
            new EdgeCapsulePreviewPointerAnchor(
                pointer,
                dpiScaleX,
                dpiScaleY,
                queueKey,
                now);
        _edgeCapsulePreviewIntentPredictor.Reset(
            pointer,
            now,
            dpiScaleX,
            dpiScaleY);
    }

    private void ClearEdgeCapsulePreviewLayoutSuppressionWhenPointerMoves(
        DeviceScreenPoint pointer)
    {
        if (!_edgeCapsulePreviewLayoutSuppressionAnchor.HasValue)
        {
            return;
        }

        var anchor = _edgeCapsulePreviewLayoutSuppressionAnchor.Value;
        if (IsEdgeCapsulePreviewLayoutSuppressionExpired(anchor) ||
            EdgeCapsulePreviewPointerMovedBeyondTolerance(
                anchor.Point,
                pointer,
                anchor.DpiScaleX,
                anchor.DpiScaleY))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            ForgetEdgeCapsulePreviewPointerResolution();
        }
    }

    private bool IsEdgeCapsulePreviewLayoutSuppressedFor(
        PaperWindow target)
    {
        if (_edgeCapsulePreviewLayoutSuppressionAnchor is not { } anchor)
        {
            return false;
        }
        if (IsEdgeCapsulePreviewLayoutSuppressionExpired(anchor))
        {
            _edgeCapsulePreviewLayoutSuppressionAnchor = null;
            ForgetEdgeCapsulePreviewPointerResolution();
            return false;
        }

        return
            string.Equals(
                anchor.QueueKey,
                QueueKey(target.EdgeCapsulePreviewPaper),
                StringComparison.Ordinal);
    }

    private static bool IsEdgeCapsulePreviewLayoutSuppressionExpired(
        EdgeCapsulePreviewPointerAnchor anchor) =>
        Stopwatch.GetElapsedTime(
            anchor.CreatedAtTimestamp,
            Stopwatch.GetTimestamp()).TotalMilliseconds >=
        EdgeCapsulePreviewLayoutSuppressionMilliseconds;

    private void AdvanceEdgeCapsulePreviewActivationIntent(
        EdgeCapsulePreviewLayoutSession? session,
        PaperWindow target,
        DeviceScreenPoint pointer)
    {
        var targetPaperId = target.EdgeCapsulePreviewPaperId;
        if (!target.TryGetEdgeCapsuleInteractiveGeometry(
                out var targetGeometry))
        {
            CancelEdgeCapsulePreviewActivationIntent(targetPaperId);
            return;
        }

        var expectedOwnerPaperId = session?.OwnerPaperId;
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        if (session == null)
        {
            if (predictiveIntentEnabled)
            {
                _edgeCapsulePreviewIntentPredictor.Observe(
                    pointer,
                    now,
                    targetGeometry.DpiScaleX,
                    targetGeometry.DpiScaleY);
            }
            ResetEdgeCapsulePreviewActivationIntent();
            QueueEdgeCapsulePreviewTransfer(target);
            return;
        }

        var intent = _edgeCapsulePreviewActivationIntent;
        if (!intent.HasValue ||
            !string.Equals(
                intent.Value.TargetPaperId,
                targetPaperId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.Value.ExpectedOwnerPaperId,
                expectedOwnerPaperId,
                StringComparison.Ordinal))
        {
            _edgeCapsulePreviewActivationIntent =
                new EdgeCapsulePreviewActivationIntent(
                    targetPaperId,
                    expectedOwnerPaperId,
                    pointer,
                    now,
                    now);
            ScheduleEdgeCapsulePreviewActivationIntentCheck(
                target,
                EdgeCapsulePreviewTransferResidenceMilliseconds);
            return;
        }

        var currentIntent = intent.Value;
        if (EdgeCapsulePreviewPointerMovedBeyondTolerance(
                currentIntent.StableAnchor,
                pointer,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY))
        {
            currentIntent = currentIntent with
            {
                StableAnchor = pointer,
                StableSinceTimestamp = now
            };
        }

        _edgeCapsulePreviewActivationIntent = currentIntent;
        var candidateElapsed = Stopwatch.GetElapsedTime(
            currentIntent.CandidateSinceTimestamp,
            now).TotalMilliseconds;
        var stableElapsed = Stopwatch.GetElapsedTime(
            currentIntent.StableSinceTimestamp,
            now).TotalMilliseconds;
        if (candidateElapsed <
            EdgeCapsulePreviewTransferResidenceMilliseconds)
        {
            ScheduleEdgeCapsulePreviewActivationIntentCheck(
                target,
                EdgeCapsulePreviewTransferResidenceMilliseconds -
                candidateElapsed);
            return;
        }

        if (predictiveIntentEnabled)
        {
            var decision =
                _edgeCapsulePreviewIntentPredictor.Evaluate(
                    EdgeCapsuleHoverIntentMode.Transfer,
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                    targetGeometry.Bounds,
                    pointer,
                    candidateElapsed,
                    stableElapsed);
            if (decision !=
                EdgeCapsuleHoverIntentDecision.NoExtraDelay)
            {
                ScheduleEdgeCapsulePreviewActivationIntentCheck(
                    target,
                    EdgeCapsulePreviewActivationIntentTrackingMilliseconds);
                return;
            }
        }

        ResetEdgeCapsulePreviewActivationIntent();
        QueueEdgeCapsulePreviewTransfer(
            target,
            expectedOwnerPaperId,
            new EdgeCapsulePreviewStableAnchor(
                currentIntent.StableAnchor,
                targetGeometry.DpiScaleX,
                targetGeometry.DpiScaleY));
    }

    internal void InvalidateEdgeCapsulePreviewPointerResolution()
    {
        unchecked
        {
            _edgeCapsulePreviewPointerResolutionVersion++;
        }
        ForgetEdgeCapsulePreviewPointerResolution();
    }

    private bool CanReuseEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer) =>
        // Reuse only an unchanged, settled result between real input, intent deadlines and
        // presentation invalidations. Pending intent always runs the full physical queue resolver.
        _edgeCapsulePreviewActivationIntent == null &&
        _edgeCapsulePreviewQueuedTransferPaperId == null &&
        ReferenceEquals(
            _edgeCapsulePreviewLastResolvedSession,
            session) &&
        _edgeCapsulePreviewLastResolvedPointer == pointer &&
        _edgeCapsulePreviewLastResolvedVersion ==
            _edgeCapsulePreviewPointerResolutionVersion;

    private void RememberEdgeCapsulePreviewPointerResolution(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        _edgeCapsulePreviewLastResolvedSession = session;
        _edgeCapsulePreviewLastResolvedPointer = pointer;
        _edgeCapsulePreviewLastResolvedVersion =
            _edgeCapsulePreviewPointerResolutionVersion;
    }

    private void ForgetEdgeCapsulePreviewPointerResolution()
    {
        _edgeCapsulePreviewLastResolvedSession = null;
        _edgeCapsulePreviewLastResolvedPointer = null;
        _edgeCapsulePreviewLastResolvedVersion = -1;
    }

    private void ObserveEdgeCapsulePreviewPointer(
        PaperWindow owner,
        DeviceScreenPoint pointer)
    {
        if (!State.ExperimentalEdgeCapsuleHoverIntent)
        {
            return;
        }

        double dpiScaleX;
        double dpiScaleY;
        if (WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                pointer,
                out var monitor))
        {
            dpiScaleX = monitor.DpiScaleX;
            dpiScaleY = monitor.DpiScaleY;
        }
        else if (owner.TryGetEdgeCapsuleAppliedGeometry(out var geometry))
        {
            dpiScaleX = geometry.DpiScaleX;
            dpiScaleY = geometry.DpiScaleY;
        }
        else
        {
            return;
        }

        _edgeCapsulePreviewIntentPredictor.Observe(
            pointer,
            Stopwatch.GetTimestamp(),
            dpiScaleX,
            dpiScaleY);
    }

    private EdgeCapsuleCorridorExitDecision AdvanceEdgeCapsulePreviewCorridorExitIntent(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        var now = Stopwatch.GetTimestamp();
        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var intent = _edgeCapsulePreviewCorridorExitIntent;
        var current = !intent.HasValue ||
            !string.Equals(
                intent.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal)
            ? new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false)
            : intent.Value;

        var corridorElapsed = Stopwatch.GetElapsedTime(
            current.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var noTargetIntentElapsed = current.NoTargetIntentSinceTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(
                current.NoTargetIntentSinceTimestamp.Value,
                now).TotalMilliseconds
            : 0;

        EdgeCapsuleCorridorExitDecision decision;
        if (!predictiveIntentEnabled)
        {
            decision = corridorElapsed >=
                EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds
                ? EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent
                : EdgeCapsuleCorridorExitDecision.KeepAlive;
        }
        else
        {
            Span<DeviceScreenRect> keepAliveBounds =
                session.QueuePaperIds.Count <= 32
                    ? stackalloc DeviceScreenRect[session.QueuePaperIds.Count]
                    : new DeviceScreenRect[session.QueuePaperIds.Count];
            var keepAliveCount = 0;
            foreach (var paperId in session.QueuePaperIds)
            {
                if (!_windows.TryGetValue(paperId, out var candidate) ||
                    !candidate.CanEnterEdgeCapsulePreview ||
                    !candidate.TryGetEdgeCapsuleInteractiveGeometry(
                        out var geometry))
                {
                    continue;
                }

                keepAliveBounds[keepAliveCount++] = geometry.Bounds;
            }

            decision = _edgeCapsulePreviewIntentPredictor
                .EvaluateCorridorExit(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity,
                    keepAliveBounds.Slice(0, keepAliveCount),
                    pointer,
                    noTargetIntentElapsed);
        }
        switch (decision)
        {
            case EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent:
                current = current with
                {
                    NoTargetIntentSinceTimestamp =
                        current.NoTargetIntentSinceTimestamp ?? now
                };
                break;
            case EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent:
                QueueEdgeCapsulePreviewClose(
                    owner,
                    session.OwnerPaperId,
                    EdgeCapsulePreviewCloseReason.NoTargetIntent);
                ResetEdgeCapsulePreviewCorridorExitIntent();
                return decision;
            default:
                current = current with
                {
                    NoTargetIntentSinceTimestamp = null
                };
                break;
        }

        _edgeCapsulePreviewCorridorExitIntent = current;
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(owner, now);
        return decision;
    }

    private void ScheduleEdgeCapsulePreviewCorridorIntentCheck(
        PaperWindow owner,
        long now,
        bool retryAfterCursorReadFailure = false)
    {
        if (_edgeCapsulePreviewCorridorExitIntent is not { } intent)
        {
            return;
        }

        var predictiveIntentEnabled =
            State.ExperimentalEdgeCapsuleHoverIntent;
        var closeMilliseconds = predictiveIntentEnabled
            ? _edgeCapsulePreviewIntentPredictor
                .CorridorNoTargetIntentCloseMilliseconds(
                    State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
            : EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds;
        var corridorElapsed = Stopwatch.GetElapsedTime(
            intent.CorridorSinceTimestamp,
            now).TotalMilliseconds;
        var closeElapsed = predictiveIntentEnabled
            ? intent.NoTargetIntentSinceTimestamp.HasValue
                ? Stopwatch.GetElapsedTime(
                    intent.NoTargetIntentSinceTimestamp.Value,
                    now).TotalMilliseconds
                : 0
            : corridorElapsed;
        var remaining = Math.Max(1, closeMilliseconds - closeElapsed);
        // Empty corridor pixels are intentionally HTTRANSPARENT, so they do not keep producing WPF
        // mouse moves. Keep one owner-local sampler active only while the pointer is in that empty
        // rectangle, so crossing its hard boundary is observed promptly in either settings mode.
        var nextCheck = retryAfterCursorReadFailure
            ? EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds
            : Math.Min(
                EdgeCapsulePreviewCorridorTrackingIntervalMilliseconds,
                remaining);
        if (_edgeCapsulePreviewCorridorIntentTimer == null)
        {
            _edgeCapsulePreviewCorridorIntentTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                owner.Dispatcher);
            _edgeCapsulePreviewCorridorIntentTimer.Tick +=
                OnEdgeCapsulePreviewCorridorIntentTimerTick;
        }

        _edgeCapsulePreviewCorridorIntentTimer.Stop();
        _edgeCapsulePreviewCorridorIntentTimer.Interval =
            TimeSpan.FromMilliseconds(nextCheck);
        _edgeCapsulePreviewCorridorIntentTimer.Start();
    }

    private void OnEdgeCapsulePreviewCorridorIntentTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewCorridorExitIntent is not { } intent ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                intent.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner))
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            return;
        }

        if (!owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewCorridorExitIntent();
            QueueEdgeCapsulePreviewClose(owner, session.OwnerPaperId);
            return;
        }
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(owner, session);
            return;
        }
        if (intent.PausedForPointerCapture)
        {
            intent = new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                Stopwatch.GetTimestamp(),
                null,
                false);
            _edgeCapsulePreviewCorridorExitIntent = intent;
            _edgeCapsulePreviewIntentPredictor.Reset();
        }
        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // Empty pixels are HTTRANSPARENT, so losing this one sample must not silently discard
            // the only active boundary/deadline watcher. Without a fresh trajectory, prediction no
            // longer has positive keep-alive evidence; start/continue the normal no-target clock.
            var now = Stopwatch.GetTimestamp();
            if (!intent.PausedForPointerCapture)
            {
                var predictiveIntentEnabled =
                    State.ExperimentalEdgeCapsuleHoverIntent;
                var current = intent;
                if (predictiveIntentEnabled &&
                    !current.NoTargetIntentSinceTimestamp.HasValue)
                {
                    current = current with
                    {
                        NoTargetIntentSinceTimestamp = now
                    };
                    _edgeCapsulePreviewCorridorExitIntent = current;
                }

                var closeMilliseconds = predictiveIntentEnabled
                    ? _edgeCapsulePreviewIntentPredictor
                        .CorridorNoTargetIntentCloseMilliseconds(
                            State.ExperimentalEdgeCapsuleHoverIntentSensitivity)
                    : EdgeCapsulePreviewExitPolicy.FixedWaitMilliseconds;
                var closeSince = predictiveIntentEnabled
                    ? current.NoTargetIntentSinceTimestamp ?? now
                    : current.CorridorSinceTimestamp;
                if (Stopwatch.GetElapsedTime(
                        closeSince,
                        now).TotalMilliseconds >= closeMilliseconds)
                {
                    ResetEdgeCapsulePreviewCorridorExitIntent();
                    QueueEdgeCapsulePreviewClose(
                        owner,
                        session.OwnerPaperId,
                        EdgeCapsulePreviewCloseReason.NoTargetIntent);
                    return;
                }
            }

            // Keep probing so a recovered cursor read can still recognize a real owner/target or a
            // hard-boundary crossing before the current deadline expires.
            ScheduleEdgeCapsulePreviewCorridorIntentCheck(
                owner,
                now,
                retryAfterCursorReadFailure: true);
            return;
        }

        // Re-enter the one owner arbiter. This is important when the timer itself discovers a real
        // target: that hit must start the normal 32 ms transfer rather than merely cancel closing.
        ForgetEdgeCapsulePreviewPointerResolution();
        NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
    }

    private void HoldEdgeCapsulePreviewForPointerCapture(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session)
    {
        var now = Stopwatch.GetTimestamp();
        ForgetEdgeCapsulePreviewPointerResolution();
        CancelEdgeCapsulePreviewActivationIntent();
        CancelQueuedEdgeCapsulePreviewClose();
        _edgeCapsulePreviewIntentPredictor.Reset();
        _edgeCapsulePreviewCorridorExitIntent =
            new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                true);
        // Capture pauses both close clocks, but the existing empty-region watcher keeps polling.
        // Once capture ends it re-enters the owner arbiter and starts a fresh blank/outside decision.
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
    }

    private void TrackEdgeCapsulePreviewUnavailablePointer(
        PaperWindow owner,
        EdgeCapsulePreviewLayoutSession session)
    {
        var now = Stopwatch.GetTimestamp();
        ForgetEdgeCapsulePreviewPointerResolution();
        CancelEdgeCapsulePreviewActivationIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
        var intent = _edgeCapsulePreviewCorridorExitIntent;
        var current = !intent.HasValue ||
            intent.Value.PausedForPointerCapture ||
            !string.Equals(
                intent.Value.OwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal)
            ? new EdgeCapsulePreviewCorridorExitIntent(
                session.OwnerPaperId,
                now,
                null,
                false)
            : intent.Value;
        if (State.ExperimentalEdgeCapsuleHoverIntent &&
            !current.NoTargetIntentSinceTimestamp.HasValue)
        {
            current = current with
            {
                NoTargetIntentSinceTimestamp = now
            };
        }
        _edgeCapsulePreviewCorridorExitIntent = current;
        ScheduleEdgeCapsulePreviewCorridorIntentCheck(
            owner,
            now,
            retryAfterCursorReadFailure: true);
    }

    private void ResumeEdgeCapsulePreviewAfterPointerCapture()
    {
        if (_edgeCapsulePreviewCorridorExitIntent is not
            { PausedForPointerCapture: true })
        {
            return;
        }

        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewIntentPredictor.Reset();
    }

    private void ResetEdgeCapsulePreviewCorridorExitIntent()
    {
        _edgeCapsulePreviewCorridorExitIntent = null;
        _edgeCapsulePreviewCorridorIntentTimer?.Stop();
    }

    private void CancelEdgeCapsulePreviewActivationIntent(
        string? targetPaperId = null)
    {
        var intent = _edgeCapsulePreviewActivationIntent;
        if (intent.HasValue &&
            (targetPaperId == null ||
             string.Equals(
                 intent.Value.TargetPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            ResetEdgeCapsulePreviewActivationIntent();
        }

        if (_edgeCapsulePreviewQueuedTransferPaperId != null &&
            (targetPaperId == null ||
             string.Equals(
                 _edgeCapsulePreviewQueuedTransferPaperId,
                 targetPaperId,
                 StringComparison.Ordinal)))
        {
            _edgeCapsulePreviewQueuedTransferPaperId = null;
            _edgeCapsulePreviewTransferGeneration++;
        }
    }

    private void ResetEdgeCapsulePreviewActivationIntent()
    {
        _edgeCapsulePreviewActivationIntent = null;
        _edgeCapsulePreviewActivationIntentTimer?.Stop();
    }

    private void ScheduleEdgeCapsulePreviewActivationIntentCheck(
        PaperWindow target,
        double delayMilliseconds)
    {
        if (_edgeCapsulePreviewActivationIntentTimer == null)
        {
            _edgeCapsulePreviewActivationIntentTimer = new DispatcherTimer(
                DispatcherPriority.Input,
                target.Dispatcher);
            _edgeCapsulePreviewActivationIntentTimer.Tick +=
                OnEdgeCapsulePreviewActivationIntentTimerTick;
        }

        _edgeCapsulePreviewActivationIntentTimer.Stop();
        _edgeCapsulePreviewActivationIntentTimer.Interval =
            TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds));
        _edgeCapsulePreviewActivationIntentTimer.Start();
    }

    private void OnEdgeCapsulePreviewActivationIntentTimerTick(
        object? sender,
        EventArgs e)
    {
        _edgeCapsulePreviewActivationIntentTimer?.Stop();
        if (IsExiting ||
            _edgeCapsulePreviewActivationIntent is not { } intent ||
            _edgeCapsulePreviewSession is not { } session ||
            !string.Equals(
                intent.ExpectedOwnerPaperId,
                session.OwnerPaperId,
                StringComparison.Ordinal) ||
            !_windows.TryGetValue(session.OwnerPaperId, out var owner) ||
            !owner.CanEnterEdgeCapsulePreview)
        {
            ResetEdgeCapsulePreviewActivationIntent();
            return;
        }
        if (owner.EdgeCapsulePreviewPointerCaptureActive)
        {
            HoldEdgeCapsulePreviewForPointerCapture(owner, session);
            return;
        }

        if (!WindowNative.TryGetCursorScreenPosition(out var pointer))
        {
            // Do not leave a second retry loop alive after the target sample disappears. The shared
            // pointer-recovery watcher will retry, recognize a real target again with a fresh 32 ms
            // residence period, or close through the normal blank-region deadline.
            TrackEdgeCapsulePreviewUnavailablePointer(owner, session);
            return;
        }

        // Reuse the owner path so the deadline still performs the complete physical queue hit test,
        // stable-anchor validation and predictor veto before it can enqueue a transfer.
        ForgetEdgeCapsulePreviewPointerResolution();
        NotifyEdgeCapsulePreviewPointerSample(owner, pointer);
    }

    private EdgeCapsulePreviewPointerResolution ResolveEdgeCapsulePreviewPointer(
        EdgeCapsulePreviewLayoutSession session,
        DeviceScreenPoint pointer)
    {
        PaperWindow? target = null;
        var ownerContains = false;
        Span<EdgeCapsulePreviewCorridorNode> corridorNodes =
            session.QueuePaperIds.Count <= 32
                ? stackalloc EdgeCapsulePreviewCorridorNode[
                    session.QueuePaperIds.Count]
                : new EdgeCapsulePreviewCorridorNode[
                    session.QueuePaperIds.Count];
        var corridorCount = 0;
        var previousCorridorNodeValid = false;

        foreach (var paperId in session.QueuePaperIds)
        {
            if (!_windows.TryGetValue(paperId, out var window))
            {
                previousCorridorNodeValid = false;
                continue;
            }

            if (window.CanEnterEdgeCapsulePreview &&
                window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry))
            {
                corridorNodes[corridorCount++] =
                    new EdgeCapsulePreviewCorridorNode(
                        geometry.Bounds,
                        previousCorridorNodeValid);
                previousCorridorNodeValid = true;
            }
            else
            {
                previousCorridorNodeValid = false;
            }

            var isOwner = string.Equals(
                paperId,
                session.OwnerPaperId,
                StringComparison.Ordinal);
            if (isOwner)
            {
                ownerContains = window.IsEdgeCapsuleInteractiveAt(pointer);
            }
            else if (target == null &&
                window.CanEnterEdgeCapsulePreview &&
                window.IsEdgeCapsuleInteractiveAt(pointer))
            {
                target = window;
            }
        }

        if (target == null)
        {
            // A physical hit in another monitor/edge queue is still a transfer from the current
            // session. Let the old owner arbitrate it so the target keeps the same 32 ms residence
            // contract instead of closing and reopening as a new first card.
            foreach (var paper in State.Papers)
            {
                if (string.Equals(
                        QueueKey(paper),
                        session.QueueKey,
                        StringComparison.Ordinal) ||
                    !_windows.TryGetValue(paper.Id, out var candidate) ||
                    !candidate.CanEnterEdgeCapsulePreview ||
                    !candidate.IsEdgeCapsuleInteractiveAt(pointer))
                {
                    continue;
                }

                target = candidate;
                break;
            }
        }

        if (corridorCount == 0)
        {
            return new EdgeCapsulePreviewPointerResolution(
                target,
                ownerContains,
                false);
        }

        var corridorContains = EdgeCapsulePreviewCorridor.Contains(
            corridorNodes[..corridorCount],
            pointer);
        return new EdgeCapsulePreviewPointerResolution(
            target,
            ownerContains,
            corridorContains);
    }

    private static bool EdgeCapsulePreviewPointerMovedBeyondTolerance(
        DeviceScreenPoint anchor,
        DeviceScreenPoint pointer,
        double dpiScaleX,
        double dpiScaleY)
    {
        var deltaX = (pointer.X - anchor.X) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleX);
        var deltaY = (pointer.Y - anchor.Y) /
            NormalizeEdgeCapsulePreviewDpiScale(dpiScaleY);
        var tolerance = EdgeCapsulePreviewPointerToleranceDip;
        return deltaX * deltaX + deltaY * deltaY >
            tolerance * tolerance;
    }

    private static double NormalizeEdgeCapsulePreviewDpiScale(double scale) =>
        double.IsFinite(scale) ? Math.Max(1, scale) : 1;

    private void RefreshEdgeCapsuleHoverIntentRuntime()
    {
        CancelEdgeCapsulePreviewActivationIntent();
        CancelQueuedEdgeCapsulePreviewClose();
        ResetEdgeCapsulePreviewCorridorExitIntent();
        _edgeCapsulePreviewLayoutSuppressionAnchor = null;
        _edgeCapsulePreviewIntentPredictor.Reset();
        foreach (var window in _windows.Values)
        {
            window.RefreshEdgeCapsuleHoverIntentSettings();
        }
    }
}
