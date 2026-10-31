using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// 自由队列拖拽反馈的分支。跨队列开槽、整队保持、贴边同队列重排、源队列收拢四选一。
/// </summary>
internal enum EdgeCapsuleFreeQueuePreviewAction
{
    KeepTransfer,
    SameQueueReorder,
    VacateSource,
    CrossQueue
}

public sealed partial class PaperWindow
{
    private readonly record struct PreviewOriginReorderBaseline(
        int OriginalIndex,
        DeviceScreenPoint PointerDownScreenPosition,
        double DpiScaleY);

    private PreviewOriginReorderBaseline? _previewOriginReorderBaseline;

    // Raw WM_LBUTTONDOWN wParam captured by the host hook; see ConsumeQueueDragModifier.
    private int _edgeCapsulePointerDownKeyState;

    // Latched at pointer-down, cleared when the gesture ends. Whole-queue drags are a queue-level
    // transaction, so the flag rides the gesture rather than being re-read mid-drag.
    private bool _deepCapsuleQueueTransferRequested;

    // Set when a Ctrl drag has frozen its source queue; null once committed or rolled back.
    private EdgeCapsuleQueueTransferSnapshot? _deepCapsuleQueueTransferSnapshot;

    internal bool AllowsDeepCapsuleQueueProxyOwnership =>
        EdgeCapsuleQueueProxyPolicy.AllowsQueueProxyOwnership(
            CurrentEdgeCapsuleVisualAuthority);

    private void OnEdgeCapsulePointerPressed(DeviceScreenPoint screenPosition)
    {
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.input phase=pointer-down " +
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"pointer={screenPosition.X:F0},{screenPosition.Y:F0}");
        _deepCapsuleQueueTransferRequested =
            ConsumeQueueDragModifier(_controller.EdgeCapsuleFreeQueuesEnabled);
        if (BeginEdgeCapsulePointerInteraction(screenPosition))
        {
            // 新 PendingClick 已建立，上一手势可能残留的等待/撤销状态一律作废。
            ResetDeepCapsulePointerGestureSuppression();
        }
        QueueDeepCapsuleFloatingDragInfrastructurePrewarm();
    }

    /// <summary>
    /// Ctrl 整队意图的三个来源：宿主收到 WM_LBUTTONDOWN 时的原生 MK_CONTROL、
    /// WPF 键盘状态，以及不依赖窗口焦点/激活状态的 OS 异步键状态。任一成立即视为整队。
    ///
    /// 展开纸片持有焦点时 host 不是键盘焦点窗口，WPF 状态可能退化为 None；原生 wParam 在
    /// 代理延迟转发时又可能比实时键状态更可靠，所以三路保留 OR。
    /// </summary>
    internal static bool IsQueueDragModifier(
        int nativeKeyState,
        ModifierKeys modifiers,
        bool osControlDown) =>
        (nativeKeyState & MkControl) != 0 ||
        (modifiers & ModifierKeys.Control) == ModifierKeys.Control ||
        osControlDown;

    /// <summary>
    /// Ctrl 整队意图只属于自由队列：功能关闭时 Ctrl 必须完全不影响原有的沿边重排。
    /// </summary>
    internal static bool ShouldBeginQueueTransfer(
        bool freeQueuesEnabled,
        int nativeKeyState,
        ModifierKeys modifiers,
        bool osControlDown) =>
        freeQueuesEnabled &&
        IsQueueDragModifier(nativeKeyState, modifiers, osControlDown);

    /// <summary>
    /// 浮空的两个独立理由：真正脱墙，或自由队列生效时的 Ctrl 整队意图。
    /// 自由队列关闭时不能把整个条件都短路，否则 legacy 跨屏拖动无法浮空。
    /// </summary>
    internal static bool ShouldStartFloatingReorder(
        bool freeQueuesEnabled,
        bool transferRequested,
        bool unlockRequested) =>
        unlockRequested || (freeQueuesEnabled && transferRequested);

    /// <summary>
    /// Whether Ctrl was held when the press landed. Read exactly once, at pointer-down.
    /// </summary>
    private bool ConsumeQueueDragModifier(bool freeQueuesEnabled)
    {
        var nativeKeyState = _edgeCapsulePointerDownKeyState;
        // 只消费一次；无论功能是否启用都清位，capture-lost 等窄路径不会留下陈旧位。
        _edgeCapsulePointerDownKeyState = 0;
        return ShouldBeginQueueTransfer(
            freeQueuesEnabled,
            nativeKeyState,
            Keyboard.Modifiers,
            WindowNative.IsControlKeyDown);
    }

    private bool OnEdgeCapsulePointerMoved(
        DeviceScreenPoint currentScreenPosition,
        bool leftButtonPressed)
    {
        if ((IsDeepCapsuleReordering || IsDeepCapsuleSlotPendingClick) && !leftButtonPressed)
        {
            if (IsDeepCapsuleReordering)
            {
                EndDeepCapsuleReorderDrag(commit: false);
                ClearCapsuleInteractionKeyboardFocus();
            }
            else
            {
                FinishEdgeCapsulePointerInteraction();
            }
            _edgeCapsuleHost?.ReleaseContentPointer();
            return true;
        }

        if (IsDeepCapsuleReordering)
        {
            UpdateDeepCapsuleReorderDrag(currentScreenPosition);
            return true;
        }
        // FQ-09: 兜底——之前 StartDeepCapsuleReorderDrag 因 lease 占用排队了一次重试。
        // lease 异常长时间不释放（handoff 反复重启 / HWND 出问题）会让我们永远等下去：
        // 检测超时并退化到明确反馈（用户取消 + restoreLayout）而不是继续无限循环。
        if (HasDeepCapsulePendingReorderDrag &&
            Environment.TickCount64 - _deepCapsulePendingReorderDragRequestTimestamp
                > DeepCapsulePendingReorderRetryTimeoutMilliseconds)
        {
            ResetDeepCapsulePointerGestureSuppression();
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            _edgeCapsuleHost?.ReleaseContentPointer();
            return true;
        }
        if (!IsDeepCapsuleSlotPendingClick)
        {
            return false;
        }
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            _edgeCapsuleHost?.ReleaseContentPointer();
            return true;
        }

        var canReorder = CanReorderDeepCapsuleSlot();
        var movedEnough = WindowWorkAreaHelper.ExceedsDragThreshold(
            session.PointerDownScreenPosition,
            currentScreenPosition,
            additionalDistanceDip: canReorder ? DeepCapsuleReorderDragExtraThreshold : 0);
        if (canReorder)
        {
            if (movedEnough)
            {
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"drag.input phase=reorder-threshold " +
                    $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
                    $"pointer={currentScreenPosition.X:F0},{currentScreenPosition.Y:F0}");
                StartDeepCapsuleReorderDrag(currentScreenPosition);
                return true;
            }
            return false;
        }

        if (movedEnough)
        {
            FinishEdgeCapsulePointerInteraction();
            _edgeCapsuleHost?.ReleaseContentPointer();
        }
        return false;
    }

    private bool OnEdgeCapsulePointerReleased(DeviceScreenPoint _)
    {
        if (IsDeepCapsuleReordering)
        {
            EndDeepCapsuleReorderDrag(commit: true);
            ClearCapsuleInteractionKeyboardFocus();
            return true;
        }
        if (_deepCapsulePointerClickRevoked || HasDeepCapsulePendingReorderDrag)
        {
            // 阈值已越过但没有可提交的拖拽：结束手势，绝不退化成点击（否则会展开或执行脚本）。
            // 返回 true 交给 EdgeCapsuleHost.CompleteContentPointer 释放 content 捕获。
            FinishEdgeCapsulePointerInteraction();
            ClearCapsuleInteractionKeyboardFocus();
            return true;
        }
        if (!IsDeepCapsuleSlotPendingClick)
        {
            return false;
        }

        FinishEdgeCapsulePointerInteraction();
        try
        {
            PrepareEdgeCapsulePreviewForActivation();
            // Preview is a presentation state, not a second activation semantic. Background
            // clicks therefore inherit the standard capsule action, including script execution
            // and the optional "collapse expanded capsule on click" behavior.
            ActivateFromDeepCapsuleSlot();
        }
        finally
        {
            ClearCapsuleInteractionKeyboardFocus();
        }
        return true;
    }

    private EdgeCapsuleCaptureAction OnEdgeCapsuleCaptureLost(EdgeCapsuleCaptureLoss captureLoss)
    {
        var wasPendingClick = IsDeepCapsuleSlotPendingClick;
        var action = _edgeCapsule.HandleCaptureLost(captureLoss);
        if (action == EdgeCapsuleCaptureAction.CancelDrag)
        {
            EndDeepCapsuleReorderDrag(commit: false);
            ClearCapsuleInteractionKeyboardFocus();
        }
        else if (wasPendingClick && action == EdgeCapsuleCaptureAction.None)
        {
            // LoseCapture 已把 reducer 从 PendingClick 送回 Idle；窗口侧的等待/撤销字段
            // 不经过 FinishEdgeCapsulePointerInteraction，必须在这里显式收口。
            ResetDeepCapsulePointerGestureSuppression();
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Pointer);
        }
        return action;
    }

    private void OnEdgeCapsuleCloseInvoked()
    {
        _controller.CloseEdgeCapsulePreviewForClose(this);
        _controller.HidePaper(_paper);
        ClearCapsuleInteractionKeyboardFocus();
    }

    private EdgeCapsuleDragWindowOptions CreateDeepCapsuleFloatingDragHostOptions(
        EdgeCapsuleFloatingShape shape)
    {
        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        return new EdgeCapsuleDragWindowOptions
        {
#if DEBUG
            DiagnosticId = EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id),
#endif
            Shape = shape,
            WindowChromeMargin = WindowChromeMargin,
            OutlineMargin = outlineMargin,
            OutlineThickness = DeepCapsuleSlotOutlineThickness,
            OutlineOverlap = DeepCapsuleSlotOutlineOverlap,
            LeftPadding = CapsuleLeftPadding,
            IconGap = CapsuleIconGap,
            RightPadding = CapsuleRightPadding,
            Icon = CapsuleIconText(),
            Label = _controller.PaperCapsuleTitle(_paper),
            IconFontSize = CapsuleIconFontSizeForCurrentPaper(),
            LabelFontSize = CapsuleLabelFontSize,
            LabelFontWeight = CapsuleLabelFontWeight,
            UiFontFamily = CapsuleLabelFontFamily,
            SymbolFontFamily = AppTypography.SymbolFontFamily,
            Language = AppTypography.Language,
            PaperBrush = PaperBrush,
            PaperBorderBrush = PaperBorderBrush,
            IconBrush = BrightWeakTextBrush,
            LabelBrush = WeakTextBrush,
            OutlineBrush = Theme.CapsuleFocusBorderBrush,
            Topmost = _edgeCapsuleHost?.IsTopmost == true
        };
    }

    internal bool TryCreateMasterQueueFloatingDragHostOptions(
        string icon,
        string label,
        out EdgeCapsuleDragWindowOptions options)
    {
        options = null!;
        if (!HasDeepCapsuleSlotPlacement)
        {
            return false;
        }

        var layout = CaptureEdgeCapsuleLayoutSnapshot();
        if (!layout.IsUsable)
        {
            return false;
        }

        var shape = EdgeCapsuleTargetPlanner.CreateFloatingShape(
            layout,
            outlineVisible: true) with
        {
            // A queue master carries only its arrow/count while detached. Keep the same ordinary
            // FloatingFree shell but use the normal compact drag width instead of inheriting the
            // sampled paper's title-dependent resting width.
            WindowWidthDip = PaperLayoutDefaults.CapsuleWidth
        };
        options = CreateDeepCapsuleFloatingDragHostOptions(shape) with
        {
            Icon = icon,
            Label = label
        };
        return true;
    }

    private void QueueDeepCapsuleFloatingDragInfrastructurePrewarm(
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Background,
        bool requireActiveInteraction = true)
    {
        // This is a one-time process infrastructure warm-up, not a prediction that the current
        // paper will be dragged. Once the shared HWND/tree has been shown and parked, every later
        // paper binds its properties synchronously at Rent without another Show/Hide warm cycle.
        if (!EdgeCapsuleDragWindow.NeedsInfrastructurePrewarm)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (!EdgeCapsuleDragWindow.NeedsInfrastructurePrewarm)
                {
                    return;
                }

                var interactionActive =
                    IsDeepCapsuleSlotPendingClick ||
                    IsDeepCapsuleReordering;
                var idleEligible =
                    HasDeepCapsuleSlotPlacement &&
                    _paper.IsVisible &&
                    !IsDeepCapsuleRetractedIntoMaster &&
                    !IsDeepCapsuleSlotRetracting;
                if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                    (requireActiveInteraction
                        ? !interactionActive
                        : !idleEligible))
                {
                    return;
                }

                var layout = CaptureEdgeCapsuleLayoutSnapshot();
                if (!layout.IsUsable)
                {
                    return;
                }
                var shape = EdgeCapsuleTargetPlanner.CreateFloatingShape(
                    layout,
                    _edgeCapsule.State.Visual == EdgeCapsuleVisualState.Active);
                if (!shape.Visible ||
                    shape.Kind != EdgeCapsuleSurfaceKind.FloatingFree ||
                    !double.IsFinite(shape.WindowWidthDip) ||
                    !double.IsFinite(shape.WindowHeightDip) ||
                    shape.WindowWidthDip <= 0 ||
                    shape.WindowHeightDip <= 0)
                {
                    return;
                }
                _ = EdgeCapsuleDragWindow.TryPrewarmInfrastructure(
                    CreateDeepCapsuleFloatingDragHostOptions(shape));
            }),
            priority);
    }

    private EdgeCapsuleDragWindow CreateDeepCapsuleFloatingDragHost(
        DeviceScreenPoint pointer,
        EdgeCapsuleFloatingShape shape)
    {
        CloseDeepCapsuleFloatingDragHost();

        var host = EdgeCapsuleDragWindow.Rent(
            CreateDeepCapsuleFloatingDragHostOptions(shape));
        host.UnexpectedlyClosed += OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
        host.LocationChanged += OnDeepCapsuleFloatingDragHostLocationChanged;

        _deepCapsuleFloatingDragHost = host;
        _deepCapsuleFloatingFullscreenAvoidanceWindow = IntPtr.Zero;
        try
        {
            if (_capsuleDragBackgroundCapture != null)
                host.BeginDragBackground();
            // No entrance morph on pull-out: the HWND must be ready for caption drag immediately.
            // A 90ms scale-in here only delays ownership hand-off and reads as sticky detach.
            host.ShowWithEntrance(
                pointer,
                animate: false,
                DeepCapsuleCrossQueueDragScaleFrom,
                DeepCapsuleCrossQueueDragMorphMilliseconds);
            if (_capsuleDragBackgroundSnapshot is { } dragBackground)
                host.UseDragBackground(dragBackground);
            RefreshDeepCapsuleSlotTopmost();
            return host;
        }
        catch
        {
            _deepCapsuleFloatingDragHost = null;
            _deepCapsuleFloatingFullscreenAvoidanceWindow = IntPtr.Zero;
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            host.LocationChanged -= OnDeepCapsuleFloatingDragHostLocationChanged;
            try
            {
                host.ReturnToPool();
            }
            catch
            {
                // Preserve the original Show failure; the interaction caller owns rollback and
                // the pool discards an unusable host on its next validation.
            }
            throw;
        }
    }

    private void CloseDeepCapsuleFloatingDragHost()
    {
        _edgeCapsule.ClearPresentationSettleNotification();
        var host = _deepCapsuleFloatingDragHost;
        _deepCapsuleFloatingDragHost = null;
        if (host != null)
        {
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            host.LocationChanged -= OnDeepCapsuleFloatingDragHostLocationChanged;
            _deepCapsuleFloatingFullscreenAvoidanceWindow = IntPtr.Zero;
            host.ReturnToPool();
        }
    }

    private void OnDeepCapsuleFloatingDragHostLocationChanged(object? sender, EventArgs e)
    {
        if (sender is not EdgeCapsuleDragWindow host ||
            !ReferenceEquals(host, _deepCapsuleFloatingDragHost))
        {
            return;
        }

        // 原生移动循环期间 WM_TIMER 会被高频移动消息饿死；浮动窗口的位置变化与光标同步
        // 到达，用它驱动同一采样入口，跨侧快速拖拽才能继续逐槽更新预览。
        SampleEdgeCapsuleNativeDragFeedback(forceEvaluation: false);

        if (_deepCapsuleFloatingFullscreenAvoidanceWindow !=
            _controller.FullscreenAvoidanceWindowFor(host))
        {
            RefreshDeepCapsuleSlotTopmost();
        }
    }

    private void OnDeepCapsuleFloatingDragHostUnexpectedlyClosed(object? sender, EventArgs e)
    {
        if (sender is not EdgeCapsuleDragWindow host ||
            !ReferenceEquals(host, _deepCapsuleFloatingDragHost))
        {
            return;
        }

        host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
        host.LocationChanged -= OnDeepCapsuleFloatingDragHostLocationChanged;
        _deepCapsuleFloatingDragHost = null;
        _deepCapsuleFloatingFullscreenAvoidanceWindow = IntPtr.Zero;
        _edgeCapsule.ClearPresentationSettleNotification();
        if (IsDeepCapsuleDockingHandoff)
        {
            // The visual cover disappeared unexpectedly. Reveal the already-committed destination
            // through the normal Presenter path and let the topology settle pass verify it.
            FinishEdgeCapsulePointerInteraction();
            FlushEdgeCapsulePresentation(
                EdgeCapsuleTransitionReason.FloatingTransfer,
                EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
            _controller.CompleteDeepCapsuleReorderDrag();
            _controller.ScheduleDisplayMetricsRefresh();
            return;
        }
        // FloatingTransfer 阶段宿主已创建、但手势尚未进入 FloatingReordering；此时异常关闭
        // 同样必须取消，否则 arrange gate、隐藏成员和鼠标光标都会永久卡住。
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private void StartDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        // 本方法只会在越过拖拽阈值的路径上调用：从这一刻起本次手势不再是点击。
        _deepCapsulePointerClickRevoked = true;
        _previewOriginReorderBaseline = null;
        if (EdgeCapsuleDragWindow.HasActiveLease)
        {
            // FQ-09: 上一条手势还在 docking handoff（flight + reveal，160–240ms 起步，可被 restart 拉长）。
            // 不能调 CancelDeepCapsuleReorderDrag()：它会走 finally 把 PendingClick 也清掉，把整个手势
            // 静默吞成一次意外单击。改为记录起点，租约释放后下一次指针移动由 OnEdgeCapsulePointerMoved
            // 重新调用本方法自然重进；同时在 OnEdgeCapsulePointerMoved 入口检查超时做兜底。
            // 等待期间点击资格已撤销，但手势保留，租约释放后仍需能续拖。
            // 只有首次进入等待才记录起点与时间戳；后续移动不得重置 750ms 兜底时钟。
            _deepCapsulePointerClickRevoked = true;
            if (!HasDeepCapsulePendingReorderDrag)
            {
                _deepCapsulePendingReorderDragStart = currentScreenPos;
                _deepCapsulePendingReorderDragRequestTimestamp = Environment.TickCount64;
            }
            return;
        }
        // lease 已释放或从未占用，把 pending 意图清掉以免后续误判超时。
        ClearDeepCapsulePendingReorderDrag();
        if (_edgeCapsule.NativeBatchRetryPending)
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        if (!CanReorderDeepCapsuleSlot() ||
            _edgeCapsuleHost == null ||
            !_edgeCapsuleHost.TryGetAppliedPresentation(out var appliedFrame) ||
            !appliedFrame.Visible ||
            appliedFrame.Bounds.IsEmpty)
        {
            return;
        }

        var collapsedPreview =
            _controller.CloseEdgeCapsulePreviewForDrag(this);
        if (_edgeCapsule.NativeBatchRetryPending)
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var appliedBounds = appliedFrame.Bounds;
        var startMonitorDeviceName = WindowWorkAreaHelper
            .MonitorAtDeviceScreenPoint(session.PointerDownScreenPosition)?.DeviceName ?? "";
        var compactHeightDevice = Math.Max(
            1,
            (int)Math.Round(
                PaperLayoutDefaults.CapsuleHeight * Math.Max(
                    1,
                    appliedFrame.DpiScaleY),
                MidpointRounding.AwayFromZero));
        var pointerOffsetY = collapsedPreview
            // The compact frame is staged in the pending visual transaction, so AppliedPresentation
            // can still describe the tall preview until commit. Use the compact protocol target.
            ? compactHeightDevice / 2.0
            : currentScreenPos.Y - appliedBounds.Top;
        var topDip = DeepCapsuleMonitorGeometry().DeviceYToLocalDip(appliedBounds.Top);
        var previewOriginBaseline = collapsedPreview
            ? new PreviewOriginReorderBaseline(
                _edgeCapsule.Placement.Index,
                session.PointerDownScreenPosition,
                Math.Max(1, DeepCapsuleMonitorGeometry().DpiScaleY))
            : (PreviewOriginReorderBaseline?)null;
        if (!BeginEdgeCapsuleDockedReorder(
                currentScreenPos,
                startMonitorDeviceName,
                pointerOffsetY,
                topDip))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        _previewOriginReorderBaseline = previewOriginBaseline;
        // reducer 已进入 DockedReordering，松手走拖拽提交分支。
        _deepCapsulePointerClickRevoked = false;

        _controller.BeginDeepCapsuleReorderDrag(_paper);
        BeginCapsuleDragBackground(_edgeCapsuleHost.Handle);
        CloseDeepCapsuleFloatingDragHost();
        // The shared Drag HWND/tree was prewarmed once at idle, with pointer-down as the only
        // fallback. Reorder no longer queues paper-specific work on the interaction path.
        _edgeCapsuleHost.BringToFrontNoActivate();

        Mouse.OverrideCursor = Cursors.SizeAll;
        UpdateDeepCapsuleReorderDrag(currentScreenPos);
    }

    private void UpdateDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        if (_edgeCapsuleHost == null)
        {
            return;
        }

        if (IsDeepCapsuleDockedReordering &&
            ShouldStartFloatingReorder(
                _controller.EdgeCapsuleFreeQueuesEnabled,
                _deepCapsuleQueueTransferRequested,
                ShouldUnlockDeepCapsuleCrossQueueDrag(currentScreenPos)))
        {
            BeginDeepCapsuleFloatingReorder(currentScreenPos);
            return;
        }

        if (IsDeepCapsuleFloatingReordering)
        {
            UpdateEdgeCapsuleDragPointer(currentScreenPos);
            // The docked phase is over the moment the capsule leaves the wall, but a free-queue
            // merge must keep reporting for the rest of the gesture. Crossing to the other edge
            // is the single most common merge — and it happens entirely in this phase, so skipping
            // it here is what left cross-edge drags with no feedback at all.
            if (_controller.EdgeCapsuleFreeQueuesEnabled)
            {
                PreviewDeepCapsuleQueueMagnetForCurrentPosition(floatingPhase: true);
            }

            return;
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var targetDeviceTop = currentScreenPos.Y - session.DockedPointerOffsetY;
        if (!MoveEdgeCapsuleDockedReorder(
                currentScreenPos,
                geometry.DeviceYToLocalDip(targetDeviceTop)))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Drag);
        if (_controller.EdgeCapsuleFreeQueuesEnabled)
        {
            PreviewDeepCapsuleQueueMagnetForCurrentPosition();
            return;
        }

        PreviewDeepCapsuleReorderForCurrentPosition();
    }

    // Drag-time feedback stays close to pointer-move frequency so the insertion preview tracks the
    // pointer like the legacy same-queue reorder does. Candidate building is cached per gesture, so
    // this only bounds the cheap distance evaluation.
    private const double DeepCapsuleQueuePreviewIntervalMilliseconds = 8;

    /// <summary>
    /// 预览节流判定：pointer move 受间隔约束；原生拖拽定时器由 timer 自身节流，直接放行。
    /// </summary>
    internal static bool IsDeepCapsuleQueuePreviewDue(
        double elapsedMilliseconds,
        double intervalMilliseconds,
        bool forceEvaluation) =>
        forceEvaluation || elapsedMilliseconds >= intervalMilliseconds;

    private DispatcherTimer? _deepCapsuleNativeDragFeedbackTimer;
    private DeviceScreenPoint? _deepCapsuleNativeDragFeedbackCursor;
    private bool _deepCapsuleNativeDragFeedbackSampling;

    // Dedup key for the source-queue close-up. Deliberately separate from the cross-queue
    // (queueKey, index) sentinel: the two describe different states, and sharing one key made each
    // clear the other, so a merge would cancel the close-up and the close-up would cancel a merge.
    private string _deepCapsuleVacatedQueueKey = "";
    private long _deepCapsuleQueuePreviewTimestamp;
    private string _deepCapsuleQueuePreviewKey = "";
    private int _deepCapsuleQueuePreviewIndex = -1;

    // 贴边同队列重排的预览只改本队内部顺序；提交时必须走纯重排，不能触发整边求解器。
    // 浮空拖回源队列同样会把 preview key 钉成源队列，因此不能只靠 key 区分。
    private bool _deepCapsuleSameQueueReorderPreviewed;

    // 候选缓存版本。目标 key/index 不变但候选输入变化时，必须重新生成拖放计划，
    // 否则预览/描边会停留在旧几何，而提交会重新求解。
    private int _deepCapsuleQueuePlanInputVersion = -1;

    // FQ-09: docking handoff 期间到达的拖拽请求不能被吞掉。浮动 HWND 只有一个租约，
    // 但用户已经按下的按钮不能变成一次意外单击：在 lease 占用期间记录意图与时间戳，
    // 租约释放后下一次指针移动自然重进 StartDeepCapsuleReorderDrag（OnEdgeCapsulePointerMoved
    // 会重复调用本方法直到 lease 空闲）。超时兜底：超过 750ms 仍未释放则视为死锁，按用户
    // 取消处理并清字段。
    private DeviceScreenPoint? _deepCapsulePendingReorderDragStart;
    private long _deepCapsulePendingReorderDragRequestTimestamp;
    private const int DeepCapsulePendingReorderRetryTimeoutMilliseconds = 750;

    // 越过拖拽阈值后即撤销本次手势的点击资格。即使因共享 lease 占用或宿主未就绪
    // 没能真正起拖、reducer 仍是 PendingClick，松手也只能取消，不能退化成点击/脚本执行。
    private bool _deepCapsulePointerClickRevoked;

    /// <summary>当前是否有 pending 拖拽请求正在等待 handoff lease 释放。</summary>
    private bool HasDeepCapsulePendingReorderDrag =>
        _deepCapsulePendingReorderDragStart.HasValue;

    /// <summary>清理 pending 重试意图。在任何取消 / 完成 / 关闭窗口路径上统一调用。</summary>
    private void ClearDeepCapsulePendingReorderDrag()
    {
        _deepCapsulePendingReorderDragStart = null;
        _deepCapsulePendingReorderDragRequestTimestamp = 0;
    }

    /// <summary>手势真正结束时调用：清等待意图，同时恢复点击资格判定。</summary>
    private void ResetDeepCapsulePointerGestureSuppression()
    {
        ClearDeepCapsulePendingReorderDrag();
        _deepCapsulePointerClickRevoked = false;
    }

    /// <summary>
    /// Sample the cursor on a timer while the native caption loop owns the drag.
    ///
    /// Cross-edge and cross-monitor drags spend their entire gesture inside that loop, so without
    /// this the most common free-queue merge gets no feedback until release. The timer runs on the
    /// dispatcher, which the modal loop keeps pumping; its interval
    /// (<see cref="DeepCapsuleQueuePreviewIntervalMilliseconds"/>, currently 8 ms) is the throttle,
    /// and each tick forces an evaluation instead of racing the Stopwatch gate.
    /// </summary>
    private void StartEdgeCapsuleNativeDragFeedback()
    {
        if (!_controller.EdgeCapsuleFreeQueuesEnabled)
        {
            return;
        }

        _deepCapsuleNativeDragFeedbackTimer ??= new DispatcherTimer(
            DispatcherPriority.Input,
            Dispatcher)
        {
            // The timer itself is the throttle for the native caption loop. Ticks force an
            // evaluation instead of racing the Stopwatch gate at the same interval.
            Interval = TimeSpan.FromMilliseconds(DeepCapsuleQueuePreviewIntervalMilliseconds)
        };
        _deepCapsuleNativeDragFeedbackCursor = null;
        _deepCapsuleNativeDragFeedbackTimer.Tick -= OnEdgeCapsuleNativeDragFeedbackTick;
        _deepCapsuleNativeDragFeedbackTimer.Tick += OnEdgeCapsuleNativeDragFeedbackTick;
        _deepCapsuleNativeDragFeedbackTimer.Start();
    }

    private void StopEdgeCapsuleNativeDragFeedback()
    {
        _deepCapsuleNativeDragFeedbackTimer?.Stop();
        _deepCapsuleNativeDragFeedbackCursor = null;
    }

    private void OnEdgeCapsuleNativeDragFeedbackTick(object? sender, EventArgs e) =>
        SampleEdgeCapsuleNativeDragFeedback(forceEvaluation: true);

    /// <summary>
    /// 浮空阶段的光标采样。timer 是兜底；Win32 原生 caption 循环会连续投递移动消息并
    /// 饿死 WM_TIMER，因此浮动窗口的 LocationChanged 也走同一入口驱动磁吸预览。
    /// </summary>
    private void SampleEdgeCapsuleNativeDragFeedback(bool forceEvaluation)
    {
        // LocationChanged 可能在按钮动画/SetWindowPos 泵消息时重入 timer 回调。
        if (_deepCapsuleNativeDragFeedbackSampling ||
            !IsDeepCapsuleFloatingReordering ||
            !WindowNative.TryGetCursorScreenPosition(out var cursor))
        {
            return;
        }

        // A stationary pointer cannot change the target slot; skip the evaluation entirely.
        if (_deepCapsuleNativeDragFeedbackCursor == cursor)
        {
            return;
        }

        if (!forceEvaluation)
        {
            // LocationChanged can arrive faster than the preview interval. If the gate is not
            // due yet, leave the cursor unrecorded so the next event or timer retries it.
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsedMilliseconds = _deepCapsuleQueuePreviewTimestamp == 0
                ? double.PositiveInfinity
                : (now - _deepCapsuleQueuePreviewTimestamp) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
            if (!IsDeepCapsuleQueuePreviewDue(
                    elapsedMilliseconds,
                    DeepCapsuleQueuePreviewIntervalMilliseconds,
                    forceEvaluation: false))
            {
                return;
            }
        }

        _deepCapsuleNativeDragFeedbackSampling = true;
        try
        {
            _deepCapsuleNativeDragFeedbackCursor = cursor;
            UpdateEdgeCapsuleDragPointer(cursor);
            // The outer sample already applied the interval gate; force the inner gate so the
            // timestamp and cursor stay consistent.
            PreviewDeepCapsuleQueueMagnetForCurrentPosition(
                floatingPhase: true,
                forceEvaluation: true);
        }
        finally
        {
            _deepCapsuleNativeDragFeedbackSampling = false;
        }
    }

    /// <summary>Clear the drag-time feedback gate, outline and held queue at the end of a gesture.</summary>
    private void ResetDeepCapsuleQueueMagnetPreview()
    {
        StopEdgeCapsuleNativeDragFeedback();
        ResetDeepCapsuleQueueMagnetPreviewState();
        // FQ-12: 把调用方纸片 id 传给 controller，由 controller 判定是否 owner。
        // 无关窗口同样走到这里，但只有 owner 的清理请求会真正命中。
        _controller.EndDeepCapsuleQueueMagnetPreview(_paper.Id);
    }

    /// <summary>
    /// Forget the last previewed target without touching the controller's held-queue state, so the
    /// next frame's Schmitt trigger still knows what this drag is attached to.
    /// </summary>
    private void ResetDeepCapsuleQueueMagnetPreviewState()
    {
        _deepCapsuleQueuePreviewKey = "";
        _deepCapsuleQueuePreviewIndex = -1;
        _deepCapsuleVacatedQueueKey = "";
        _deepCapsuleQueuePreviewTimestamp = 0;
        _deepCapsuleSameQueueReorderPreviewed = false;
        _deepCapsuleQueuePlanInputVersion = -1;
    }

    /// <summary>
    /// 把上一帧实际开过槽的目标队列放回真实槽位，并清空目标去重键。
    ///
    /// 只处理跨队列预览记录的目标；源队列由现有的补位逻辑负责。绝不在这里恢复源队列，
    /// 否则会把被拖胶囊重新算回源队列，撑开刚刚收拢的空隙。
    /// </summary>
    private void RestoreDeepCapsuleQueuePreviewTarget()
    {
        var targetKey = _deepCapsuleQueuePreviewKey;
        if (targetKey.Length == 0)
        {
            return;
        }

        _deepCapsuleQueuePreviewKey = "";
        _deepCapsuleQueuePreviewIndex = -1;
        _deepCapsuleSameQueueReorderPreviewed = false;

        if (!string.Equals(
                targetKey,
                _controller.QueueKeyForPaper(_paper),
                StringComparison.Ordinal))
        {
            _controller.RestoreDeepCapsuleQueuePreview(targetKey);
        }
        else
        {
            // 目标是源队列时不能恢复源队列成员（会把空槽重新撑开），但求解器联动推开的
            // 同边邻居必须恢复，否则离开预览后会留下瞬态位移。
            _controller.RestoreDeepCapsuleQueueDragAvoidance();
        }
    }

    /// <summary>
    /// Close up the queue the dragged capsule came from, once per queue.
    ///
    /// Re-running it every feedback tick (currently 8 ms) would restart the 200 ms transition on
    /// every member, which reads as a stutter rather than a settle.
    /// </summary>
    private void PreviewDeepCapsuleQueueVacatedSourceOnce()
    {
        var sourceQueueKey = _controller.QueueKeyForPaper(_paper);
        if (_deepCapsuleVacatedQueueKey == sourceQueueKey)
        {
            return;
        }

        _deepCapsuleVacatedQueueKey = sourceQueueKey;
        _controller.PreviewDeepCapsuleQueueVacatedSource(_paper);
    }

    /// <summary>
    /// 拖拽当前的真实落点锚点。
    ///
    /// 浮空阶段拖拽窗口以光标为中心，鼠标点就是胶囊中心；贴边阶段胶囊顶边跟随光标，
    /// 中心 = 光标 Y - 抓取偏移 + 半个胶囊高度（设备像素）。
    /// </summary>
    private EdgeCapsuleDropAnchor DeepCapsuleDropAnchorFor(
        DeviceScreenPoint pointer,
        in EdgeCapsuleDragSession session,
        bool floatingPhase)
    {
        if (floatingPhase)
        {
            return new EdgeCapsuleDropAnchor(pointer, pointer.Y);
        }

        var scaleY = DeepCapsuleMonitorGeometry().DpiScaleY;
        var centerY = pointer.Y - session.DockedPointerOffsetY
                      + PaperLayoutDefaults.CapsuleHeight * scaleY / 2.0;
        return new EdgeCapsuleDropAnchor(pointer, centerY);
    }

    /// <summary>
    /// 自由队列拖拽反馈的分支选择（纯函数）。贴边的 NewQueue/None 也必须收拢源队列，
    /// 否则会显示一个提交时不会发生的同队列重排。浮空阶段在 Merge 或拖回源队列时仍会
    /// 开槽；只有 NewQueue/None（且不是整队转移）才仅收拢源队列、不预览槽位。
    /// </summary>
    internal static EdgeCapsuleFreeQueuePreviewAction ResolveEdgeCapsuleFreeQueuePreviewAction(
        EdgeCapsuleQueueMagnetOutcome outcome,
        bool hasTargetPreview,
        bool floatingPhase,
        bool hasTransfer)
    {
        if (hasTargetPreview)
        {
            return EdgeCapsuleFreeQueuePreviewAction.CrossQueue;
        }
        if (hasTransfer)
        {
            return EdgeCapsuleFreeQueuePreviewAction.KeepTransfer;
        }

        return !floatingPhase && outcome == EdgeCapsuleQueueMagnetOutcome.Reorder
            ? EdgeCapsuleFreeQueuePreviewAction.SameQueueReorder
            : EdgeCapsuleFreeQueuePreviewAction.VacateSource;
    }

    /// <summary>
    /// Live magnet feedback for a free-queue drag: decide what the drop would do right now, then
    /// show it — the target queue opens a slot, and the outline says which queue that is.
    ///
    /// <paramref name="floatingPhase"/> marks the phase where the capsule has left the wall. There
    /// is no docked slot index to track any more, but the source queue must still close the gap it
    /// vacated — otherwise it keeps a permanent hole and the drag reads as a queue coming apart.
    /// </summary>
    private void PreviewDeepCapsuleQueueMagnetForCurrentPosition(
        bool floatingPhase = false,
        bool forceEvaluation = false)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMilliseconds = _deepCapsuleQueuePreviewTimestamp == 0
            ? double.PositiveInfinity
            : (now - _deepCapsuleQueuePreviewTimestamp) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
        if (!IsDeepCapsuleQueuePreviewDue(
                elapsedMilliseconds,
                DeepCapsuleQueuePreviewIntervalMilliseconds,
                forceEvaluation))
        {
            // Inside the gate: keep showing the previous frame's plan rather than clearing it, so
            // the preview never blinks between updates.
            return;
        }

        _deepCapsuleQueuePreviewTimestamp = now;

        // A Ctrl drag carries the whole queue, so the magnet has to reason about every member as
        // one unit: N slots open in the target, and the source queue is not itself a merge target.
        var transfer = _deepCapsuleQueueTransferSnapshot;
        var carried = transfer != null
            ? _controller.EdgeCapsuleQueueTransferMembers(transfer)
            : [_paper];
        if (carried.Count == 0)
        {
            _deepCapsuleSameQueueReorderPreviewed = false;
            return;
        }

        // Ctrl 整队搬移始终处于浮空阶段；其余情况由调用点告诉我们是贴边还是浮空。
        var anchor = DeepCapsuleDropAnchorFor(
            session.LastScreenPosition,
            session,
            floatingPhase || transfer != null);
        var plan = _controller.EvaluateDeepCapsuleQueueDrag(
            carried,
            anchor,
            excludeEntireSourceQueue: transfer != null);
        var sourceQueueKey = _controller.QueueKeyForPaper(_paper);

        // 跨队列 Merge 需要描边与邻队避让。浮空阶段拖回源队列时磁吸会返回 Reorder；
        // 此时胶囊已经脱离队列，旧的后排预览不适用，也必须按插入计划重新给源队列开槽。
        var targetIsSourceQueue = string.Equals(
            plan.TargetQueueKey,
            sourceQueueKey,
            StringComparison.Ordinal);
        var crossQueue = plan.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
                         !targetIsSourceQueue;
        var sameQueueFloatingPreview = floatingPhase &&
            transfer == null &&
            plan.Outcome == EdgeCapsuleQueueMagnetOutcome.Reorder;
        var previewAction = ResolveEdgeCapsuleFreeQueuePreviewAction(
            plan.Outcome,
            crossQueue || sameQueueFloatingPreview,
            floatingPhase,
            transfer != null);

        // 本帧默认不是纯重排；只有 SameQueueReorder 分支成功执行后才置 true。
        _deepCapsuleSameQueueReorderPreviewed = false;

        if (sameQueueFloatingPreview)
        {
            // 源队列现在以插入槽形态显示，不再处于已收拢状态；离开该预览时必须允许
            // VacateSource 再收拢一次，否则会留下一个空洞。
            _deepCapsuleVacatedQueueKey = "";
        }

        if (previewAction != EdgeCapsuleFreeQueuePreviewAction.CrossQueue)
        {
            _controller.EndEdgeCapsuleQueueDropTargetOutline();
            // 真正离开吸附区：把上一帧被撑开的目标队列放回真实槽位，空隙随之合拢。
            RestoreDeepCapsuleQueuePreviewTarget();
            switch (previewAction)
            {
                case EdgeCapsuleFreeQueuePreviewAction.KeepTransfer:
                    // The queue being carried is already lifted off the wall and hidden; there is
                    // no gap left to close, so re-running the close-up would only churn members.
                    ClearDeepCapsulePreviewIndex(session);
                    ResetDeepCapsuleQueueMagnetPreviewState();
                    break;

                case EdgeCapsuleFreeQueuePreviewAction.SameQueueReorder:
                    // 只清 vacated 去重键：允许之后切换到 NewQueue 时重新收拢一次，
                    // 但不要重置预览节流/key。索引与提交共用同一取整规则。
                    _deepCapsuleVacatedQueueKey = "";
                    PreviewDeepCapsuleFreeQueueReorderAt(anchor, sourceQueueKey);
                    break;

                case EdgeCapsuleFreeQueuePreviewAction.VacateSource:
                    // floating / NewQueue / None：源队列按提交语义收拢。这里不能 Reset，
                    // 否则每个 tick 都会重启 200ms 过渡并造成抖动。
                    ClearDeepCapsulePreviewIndex(session);
                    PreviewDeepCapsuleQueueVacatedSourceOnce();
                    break;
            }

            return;
        }

        // 离开同队列重排预览后（跨队列开槽会覆盖同伴 placement），必须清掉 reducer 的
        // PreviewIndex，否则回到相同索引时 helper 会误判“已预览”而跳过重新下发。
        ClearDeepCapsulePreviewIndex(session);

        var index = _controller.DeepCapsuleQueueInsertIndexFor(
            plan.TargetQueueKey,
            _paper.Id,
            anchor.CapsuleCenterYDevice,
            carried.Count);

        var targetChanged = !string.Equals(
            plan.TargetQueueKey,
            _deepCapsuleQueuePreviewKey,
            StringComparison.Ordinal);

        // A→B：先把 A 合拢，再让 B 应用新计划，避免两条队列同时保持被撑开的状态。
        if (targetChanged)
        {
            RestoreDeepCapsuleQueuePreviewTarget();
        }

        var inputVersion = _controller.MagnetCandidateBuildCount;
        var sameSlot = !targetChanged &&
            index == _deepCapsuleQueuePreviewIndex &&
            inputVersion == _deepCapsuleQueuePlanInputVersion;
        if (!sameSlot)
        {
            var feasible = _controller.PreviewDeepCapsuleQueueDrag(
                carried,
                plan.TargetQueueKey,
                index,
                allowSourceQueueTarget: sameQueueFloatingPreview);
            _deepCapsuleQueuePlanInputVersion = inputVersion;
            if (!feasible)
            {
                // 计划不可行：恢复上一帧的目标/邻队展开，源队列保持收拢；
                // 描边会读取计划并画警示色插入线。
                if (!targetChanged)
                {
                    RestoreDeepCapsuleQueuePreviewTarget();
                }

                PreviewDeepCapsuleQueueVacatedSourceOnce();
            }

            _deepCapsuleQueuePreviewKey = plan.TargetQueueKey;
            _deepCapsuleQueuePreviewIndex = index;
        }

        // 先 Preview（生成/保存计划），再 Show（读取同一份计划画描边）。
        _controller.ShowEdgeCapsuleQueueDropTargetOutline(
            plan.TargetQueueKey, _paper.Id, carried.Count, index);
    }

    /// <summary>
    /// 重置 reducer 的预览索引去重键。离开同队列重排预览时必须调用，否则同伴 placement
    /// 已被收拢/跨队列预览覆盖后，回到同一索引会被误判为“已预览”。
    /// </summary>
    private void ClearDeepCapsulePreviewIndex(EdgeCapsuleDragSession session)
    {
        if (session.PreviewIndex != -1)
        {
            _ = UpdateEdgeCapsulePreviewIndex(-1);
        }
    }

    /// <summary>
    /// 自由队列贴边同队列重排预览：索引与提交共用 DeepCapsuleQueueInsertIndexFor，
    /// 避免旧的方向性 floor/ceil 在 0.5 槽边界与提交的四舍五入给出不同槽位。
    /// 同时钉扎 preview key/index，保证松手提交落在预览显示的队列与槽位。
    /// </summary>
    private void PreviewDeepCapsuleFreeQueueReorderAt(
        EdgeCapsuleDropAnchor anchor,
        string sourceQueueKey)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

        var index = _controller.DeepCapsuleQueueInsertIndexFor(
            sourceQueueKey,
            _paper.Id,
            anchor.CapsuleCenterYDevice);
        if (index != session.PreviewIndex)
        {
            if (!UpdateEdgeCapsulePreviewIndex(index))
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }

            _controller.PreviewDeepCapsuleReorder(_paper, index);
        }

        // 每帧都要重设：RestoreDeepCapsuleQueuePreviewTarget 会在本分支入口清空这两个字段。
        _deepCapsuleQueuePreviewKey = sourceQueueKey;
        _deepCapsuleQueuePreviewIndex = index;
        _deepCapsuleSameQueueReorderPreviewed = true;
    }

    private void PreviewDeepCapsuleReorderForCurrentPosition()
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var dropIndex = DeepCapsuleDropIndexForCurrentPosition();
        if (dropIndex == session.PreviewIndex)
        {
            return;
        }

        if (!UpdateEdgeCapsulePreviewIndex(dropIndex))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        _controller.PreviewDeepCapsuleReorder(_paper, dropIndex);
    }

    private void BeginDeepCapsuleFloatingReorder(DeviceScreenPoint currentScreenPos)
    {
        if (_edgeCapsuleHost == null || !IsDeepCapsuleDockedReordering)
        {
            return;
        }

        // Ctrl was latched at pointer-down: freeze the whole queue before anything moves, so its
        // layout cannot reflow while the floating capsule is off the wall.
        if (_deepCapsuleQueueTransferRequested)
        {
            if (!_controller.TryBeginEdgeCapsuleQueueTransfer(_paper, out var transfer))
            {
                // Ctrl 本身不能成为浮空理由：事务建立失败时清掉意图，只有真正脱墙才继续。
                // 不要 abandon：失败也可能是因为另一个手势的 transfer 正在占用。
                _deepCapsuleQueueTransferRequested = false;
                _deepCapsuleQueueTransferSnapshot = null;
                if (!ShouldUnlockDeepCapsuleCrossQueueDrag(currentScreenPos))
                {
                    return; // 保持 DockedReordering，下一帧继续沿边重排。
                }
            }
            else
            {
                _deepCapsuleQueueTransferSnapshot = transfer;
            }
        }
        if (_edgeCapsule.NativeBatchRetryPending)
        {
            // A failed queue-wide HWND commit still owns the docked generation. Do not derive a
            // floating drag shape from the intentionally hidden/uncommitted host frame.
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

#if DEBUG
        var diagnosticId = EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id);
        var transferStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
        var stageStartedAt = transferStartedAt;
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.transfer phase=unlock-begin paper={diagnosticId} " +
            $"pointer={currentScreenPos.X:F0},{currentScreenPos.Y:F0}");
#endif
        var edgeHost = _edgeCapsuleHost;
        if (!BeginEdgeCapsuleFloatingTransfer(currentScreenPos))
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=unlock-failed paper={diagnosticId} " +
                "stage=begin-floating-transfer");
#endif
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
#if DEBUG
        var transferStateMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
#endif
        try
        {
#if DEBUG
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
#if DEBUG
            var flushMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var floatingHost = CreateDeepCapsuleFloatingDragHost(
                currentScreenPos,
                _edgeCapsule.FloatingShape);
#if DEBUG
            var createShowMs = EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            if (!BeginEdgeCapsuleFloatingReorder())
            {
#if DEBUG
                EdgeCapsulePerformanceDiagnostics.Trace(
                    $"drag.transfer phase=unlock-failed paper={diagnosticId} " +
                    "stage=begin-floating-reorder");
#endif
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }

            // 宿主可能在创建/切换状态的同步调用里被非 owner 关闭；事件处理器会取消手势，
            // 这里再校验一次，避免继续在已失效的宿主上运行原生拖拽。
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleFloatingReordering)
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }

            // 已离开墙面，贴边同队列重排预览不再代表当前落点。
            _deepCapsuleSameQueueReorderPreviewed = false;
            WindowNative.BringToFrontNoActivate(floatingHost);
            // Only now that the floating capsule is on screen may the rest of the queue be hidden;
            // detaching first would leave a frame with nothing drawn at all.
            if (_deepCapsuleQueueTransferSnapshot != null)
            {
                _controller.HideEdgeCapsuleQueueTransferMembers(_paper.Id);
            }

            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;
#if DEBUG
            var stateAndZOrderMs =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=host-ready paper={diagnosticId} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(transferStartedAt):F3} " +
                $"transferStateMs={transferStateMs:F3} flushMs={flushMs:F3} " +
                $"createShowMs={createShowMs:F3} " +
                $"stateAndZOrderMs={stateAndZOrderMs:F3}");
            var nativeDragStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            // Shared preparation checks this owner again after Render, then Windows owns the
            // movement until button release. The reducer retains only its transaction/slot state.
            //
            // "Windows owns the movement" also means no WPF pointer event reaches us for the rest
            // of the gesture, so drag-time feedback has to sample the cursor itself. The modal
            // loop still pumps messages, so a dispatcher timer runs inside it.
            StartEdgeCapsuleNativeDragFeedback();
            var nativeDragOutcome = floatingHost.PrepareAndRunNativeDrag(
                currentScreenPos,
                () => ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) &&
                    IsDeepCapsuleFloatingReordering,
                edgeHost.TransferContentPointerToNativeDrag);
            StopEdgeCapsuleNativeDragFeedback();
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=native-return paper={diagnosticId} " +
                $"result={nativeDragOutcome.Result} " +
                $"preparedNativeCallMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(nativeDragStartedAt):F3} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(transferStartedAt):F3} " +
                $"drop={nativeDragOutcome.DropPosition.X:F0},{nativeDragOutcome.DropPosition.Y:F0}");
#endif
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleFloatingReordering)
            {
                return;
            }

            if (nativeDragOutcome.Result != EdgeCapsuleNativeDragResult.Completed)
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                ClearCapsuleInteractionKeyboardFocus();
                return;
            }

            CommitDeepCapsuleFloatingReorderAtCursor(nativeDragOutcome.DropPosition);
            // A fast release can complete before the native loop releases WPF capture.
            edgeHost.ReleaseContentPointer();
        }
        catch (Exception ex)
        {
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=exception paper={diagnosticId} " +
                $"type={ex.GetType().Name} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(transferStartedAt):F3}");
#else
            _ = ex;
#endif
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
        }
    }

    private void CommitDeepCapsuleFloatingReorderAtCursor(DeviceScreenPoint fallbackPosition)
    {
        var dropPosition = fallbackPosition;
        if (WindowNative.TryGetCursorScreenPosition(out var livePosition))
        {
            dropPosition = livePosition;
        }

        if (!UpdateEdgeCapsuleDragPointer(dropPosition))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

        // 原生循环返回时 StopEdgeCapsuleNativeDragFeedback() 已清空缓存光标；用最终物理位置
        // 强制重算一次预览，避免提交沿用更早的目标/槽位。此时 timer 已停止，不会重入。
        if (_controller.EdgeCapsuleFreeQueuesEnabled)
        {
            PreviewDeepCapsuleQueueMagnetForCurrentPosition(
                floatingPhase: true,
                forceEvaluation: true);
        }

        EndDeepCapsuleReorderDrag(commit: true);
        ClearCapsuleInteractionKeyboardFocus();
    }

    /// <summary>
    /// 只有水平位移达到解锁距离，或指针进入另一显示器，才从贴边重排切到浮空拖拽。
    /// 贴边重排本身就是纵向移动，不能把纵向位移当成“离开墙面”；跨显示器则必须浮空才能
    /// 改写目标边。
    /// </summary>
    private bool ShouldUnlockDeepCapsuleCrossQueueDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            return false;
        }
        var scaleX = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
            session.PointerDownScreenPosition,
            out var startGeometry)
                ? startGeometry.DpiScaleX
                : 1.0;
        var horizontalDelta = Math.Abs(
            currentScreenPos.X - session.PointerDownScreenPosition.X);
        if (horizontalDelta >= DeepCapsuleCrossQueueDragUnlockDistance * scaleX)
        {
            return true;
        }

        return HasDeepCapsuleDragEnteredAnotherMonitor(currentScreenPos);
    }
    private bool HasDeepCapsuleDragEnteredAnotherMonitor(DeviceScreenPoint currentScreenPos)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            return false;
        }
        if (string.IsNullOrEmpty(session.StartMonitorDeviceName))
        {
            return false;
        }

        var currentMonitor = WindowWorkAreaHelper.MonitorAtDeviceScreenPoint(currentScreenPos);
        return currentMonitor.HasValue &&
            !string.IsNullOrEmpty(currentMonitor.Value.DeviceName) &&
            !string.Equals(
                currentMonitor.Value.DeviceName,
                session.StartMonitorDeviceName,
                StringComparison.Ordinal);
    }

    private void EndDeepCapsuleReorderDrag(bool commit)
    {
        if (!IsDeepCapsuleReordering)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var wasFloatingDrag = IsDeepCapsuleFloatingReordering;
        // 快照在本方法内一次性消费：free 分支提交、legacy 分支回滚、未提交/异常路径由
        // finally 回滚。拖拽期间设置 API/插件可能关闭自由队列，因此不能假设提交一定走 free 分支。
        var transfer = _deepCapsuleQueueTransferSnapshot;
        _deepCapsuleQueueTransferSnapshot = null;
        var transferConsumed = false;
        var shouldAnimateFloatingDrop = false;
        try
        {
            Mouse.OverrideCursor = null;

            if (commit)
            {
                // Free queues decide by distance rather than by slot index, so the drop goes
                // through the magnet even for a drag that never left the wall.
                if (_controller.EdgeCapsuleFreeQueuesEnabled)
                {
                    // A floating drop returns through the same docking hand-off transaction as
                    // an ordinary cross-edge one, so this must be latched before the branch
                    // returns: otherwise finally withdraws the floating HWND outright and the
                    // capsule teleports off the wall. A drag that never left the wall has no
                    // floating host, so the condition is naturally false there.
                    shouldAnimateFloatingDrop =
                        _controller.State.EnableAnimations &&
                        _deepCapsuleFloatingDragHost != null;

                    // 落点锚点必须在清空 transfer 快照之前算好：整队搬移一定处于浮空阶段。
                    var anchor = DeepCapsuleDropAnchorFor(
                        session.LastScreenPosition,
                        session,
                        wasFloatingDrag || transfer != null);

                    if (transfer is { } activeTransfer)
                    {
                        _controller.CommitEdgeCapsuleQueueTransfer(
                            activeTransfer,
                            anchor,
                            _deepCapsuleQueuePreviewKey,
                            _deepCapsuleQueuePreviewIndex);
                        // 只有提交正常返回后才算消费；异常时交给 finally 回滚。
                        transferConsumed = true;
                    }
                    else
                    {
                        _controller.CommitDeepCapsuleQueueDrop(
                            _paper,
                            anchor,
                            _deepCapsuleQueuePreviewKey,
                            _deepCapsuleQueuePreviewIndex,
                            !wasFloatingDrag && _deepCapsuleSameQueueReorderPreviewed);
                    }

                    return;
                }

                // 拖拽中自由队列被关闭：先回滚整队事务，再走旧路径，避免 source key 泄漏。
                if (transfer is { } legacyTransfer)
                {
                    _controller.AbandonEdgeCapsuleQueueTransfer(legacyTransfer);
                    transferConsumed = true;
                }

                if (!wasFloatingDrag)
                {
                    _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                    return;
                }

                // Resolve the (monitor, edge) queue under the drop point. If it differs from this
                // paper's current queue, reassign it (cross-edge / cross-monitor move). Otherwise it's
                // a plain vertical reorder within the same queue.
                var targetGeometry = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                    session.LastScreenPosition,
                    out var resolvedGeometry)
                        ? resolvedGeometry
                        : DeepCapsuleMonitorGeometry();
                var targetMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(
                    targetGeometry.DeviceName);

                // Choose the nearer physical wall of the target monitor.
                var targetSide = session.LastScreenPosition.X <
                    targetGeometry.WorkArea.Left + targetGeometry.WorkArea.Width / 2.0
                    ? DeepCapsuleSides.Left
                    : DeepCapsuleSides.Right;

                var queueChanged = targetSide != _paper.CapsuleSide ||
                    !string.Equals(targetMonitor, _paper.CapsuleMonitorDeviceName, StringComparison.Ordinal);

                if (queueChanged)
                {
                    _controller.MoveCapsuleToQueue(
                        _paper,
                        targetMonitor,
                        targetSide,
                        session.LastScreenPosition);
                }
                else
                {
                    _controller.ReorderDeepCapsule(_paper, DeepCapsuleDropIndexForCurrentPosition());
                }
                shouldAnimateFloatingDrop =
                    _controller.State.EnableAnimations &&
                    _deepCapsuleFloatingDragHost != null;
                return;
            }

            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            // Keep the gesture alive through the queue mutation so arrange calls are coalesced. End
            // it before completing the controller gate, allowing the destination placement to be
            // measured and committed while the floating HWND is still visible.
            _previewOriginReorderBaseline = null;
            ResetDeepCapsuleQueueMagnetPreview();
            if (!transferConsumed && transfer is { } cancelled)
            {
                if (commit)
                {
                    // 提交异常真正逃逸时只剩起拖快照：这里只能恢复身份并释放冻结；
                    // 顺序与 margin 的完整回滚由 Commit 内部 catch 保证。
                    _controller.RollbackEdgeCapsuleQueueTransfer(cancelled, null);
                }
                else
                {
                    // 普通取消：业务数据未被修改，只释放冻结与预览。
                    _controller.AbandonEdgeCapsuleQueueTransfer(cancelled);
                }
            }

            FinishEdgeCapsulePointerInteraction();
            _controller.CompleteDeepCapsuleReorderDrag();
            var handoffStarted = shouldAnimateFloatingDrop &&
                BeginEdgeCapsuleDockingHandoff();
            _controller.RefreshFloatingSurfaceZOrder();
            if (handoffStarted)
            {
                BeginDeepCapsuleFloatingDockingHandoff();
            }
            else
            {
                if (wasFloatingDrag || _deepCapsuleFloatingDragHost != null)
                {
                    CompleteDeepCapsuleFloatingDragDrop();
                }
                FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
            }
            EndCapsuleDragBackground();
        }
    }

    private void CancelDeepCapsuleReorderDrag(bool restoreLayout = false)
    {
        // 任何取消/异常路径都必须先释放整队搬移事务。源队列 key 残留在 controller 会让
        // 后续每次 Arrange 都 detach 整条队列，并永久拒绝新的整队拖拽。
        // 先清空字段再回滚，保证本方法被 Arrange 重入时不会重复执行。
        if (_deepCapsuleQueueTransferSnapshot is { } cancelledTransfer)
        {
            _deepCapsuleQueueTransferSnapshot = null;
            _controller.AbandonEdgeCapsuleQueueTransfer(cancelledTransfer);
        }

        var wasReordering = IsDeepCapsuleReordering;
        var wasDockingHandoff = IsDeepCapsuleDockingHandoff;
        var wasDockingReveal = IsDeepCapsuleDockingReveal;
        if (!wasReordering &&
            !wasDockingHandoff &&
            !IsDeepCapsuleSlotPendingClick &&
            _deepCapsuleFloatingDragHost == null)
        {
            _previewOriginReorderBaseline = null;
            // 早退时不经过下面的 finally，拖拽期磁吸状态与手势抑制字段必须在这里一并清理。
            ResetDeepCapsuleQueueMagnetPreview();
            ResetDeepCapsulePointerGestureSuppression();
            return;
        }

        try
        {
            CloseDeepCapsuleFloatingDragHost();
            Mouse.OverrideCursor = null;
            if (wasReordering && restoreLayout &&
                _windowLifecycle == PaperWindowLifecycleState.Alive)
            {
                _controller.ArrangeDeepCapsules(animate: true);
            }
        }
        finally
        {
            _previewOriginReorderBaseline = null;
            // 取消/交接失败时不能让目标队列的开槽和描边活到下一次手势。
            RestoreDeepCapsuleQueuePreviewTarget();
            ResetDeepCapsuleQueueMagnetPreview();
            FinishEdgeCapsulePointerInteraction();
            _edgeCapsuleHost?.ReleaseContentPointer();
            if (wasReordering || wasDockingReveal)
            {
                _controller.CompleteDeepCapsuleReorderDrag();
                _controller.RefreshFloatingSurfaceZOrder();
            }

            FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
            EndCapsuleDragBackground();
        }
    }

    private bool CanReorderDeepCapsuleSlot()
    {
        return HasDeepCapsuleSlotPlacement &&
            _edgeCapsuleHost?.IsVisible == true &&
            (_paper.IsCollapsed || (_controller.State.ShowDeepCapsuleWhileExpanded && IsDeepCapsuleSlotActive));
    }

    private int DeepCapsuleDropIndexForCurrentPosition()
    {
        var count = _controller.VisibleDeepCapsuleCountForQueue(_paper);
        if (count <= 1)
        {
            return 0;
        }

        var slotHeight = EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap);
        var originalIndex = Math.Clamp(_edgeCapsule.Placement.Index, 0, count - 1);
        if (IsDeepCapsuleDockedReordering &&
            _previewOriginReorderBaseline is { } baseline &&
            TryGetEdgeCapsuleDragSession(out var previewDragSession))
        {
            // A preview owner may be hundreds of DIPs tall. Its compact visual is intentionally
            // recentered under the pointer when drag begins, but that visual rebase must not count
            // as user reorder motion. Logical ordering starts at the original compact slot and is
            // driven only by pointer travel after the original press.
            var pointerDeltaDip =
                (previewDragSession.LastScreenPosition.Y -
                 baseline.PointerDownScreenPosition.Y) /
                baseline.DpiScaleY;
            var rawPreviewIndex = baseline.OriginalIndex +
                pointerDeltaDip / slotHeight;
            var previewIndex = rawPreviewIndex >= baseline.OriginalIndex
                ? (int)Math.Floor(rawPreviewIndex)
                : (int)Math.Ceiling(rawPreviewIndex);
            return Math.Clamp(previewIndex, 0, count - 1);
        }

        var dragBounds = _deepCapsuleFloatingDragHost != null &&
            WindowNative.TryGetWindowDeviceBounds(_deepCapsuleFloatingDragHost, out var floatingBounds)
                ? floatingBounds
                : _edgeCapsule.AppliedPresentation.Bounds;
        if (dragBounds.IsEmpty)
        {
            return originalIndex;
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var centerY = geometry.DeviceYToLocalDip(dragBounds.Top + dragBounds.Height / 2.0);
        // Real capsules start after slot 0 when the master capsule occupies that slot.
        var firstCenterY = DeepCapsuleTopForIndex(_edgeCapsule.Placement.VisualOffset) +
            (PaperLayoutDefaults.CapsuleHeight / 2);
        var rawIndex = (centerY - firstCenterY) / slotHeight;
        var index = rawIndex >= originalIndex
            ? (int)Math.Floor(rawIndex)
            : (int)Math.Ceiling(rawIndex);
        return Math.Clamp(index, 0, count - 1);
    }
}

