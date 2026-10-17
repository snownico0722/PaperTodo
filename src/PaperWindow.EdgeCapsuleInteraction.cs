using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

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
        _deepCapsuleQueueTransferRequested = ConsumeQueueDragModifier();
        BeginEdgeCapsulePointerInteraction(screenPosition);
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
    /// Whether Ctrl was held when the press landed. Read exactly once, at pointer-down.
    /// </summary>
    private bool ConsumeQueueDragModifier()
    {
        var nativeKeyState = _edgeCapsulePointerDownKeyState;
        // 只消费一次；capture-lost 等窄路径不会再留下上一次手势的陈旧位。
        _edgeCapsulePointerDownKeyState = 0;
        return IsQueueDragModifier(
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
            // No entrance morph on pull-out: the HWND must be ready for caption drag immediately.
            // A 90ms scale-in here only delays ownership hand-off and reads as sticky detach.
            host.ShowWithEntrance(
                pointer,
                animate: false,
                DeepCapsuleCrossQueueDragScaleFrom,
                DeepCapsuleCrossQueueDragMorphMilliseconds);
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
            !ReferenceEquals(host, _deepCapsuleFloatingDragHost) ||
            _deepCapsuleFloatingFullscreenAvoidanceWindow ==
                _controller.FullscreenAvoidanceWindowFor(host))
        {
            return;
        }

        RefreshDeepCapsuleSlotTopmost();
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
        if (!IsDeepCapsuleFloatingReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private void StartDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        _previewOriginReorderBaseline = null;
        if (EdgeCapsuleDragWindow.HasActiveLease)
        {
            CancelDeepCapsuleReorderDrag();
            return;
        }
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

        _controller.BeginDeepCapsuleReorderDrag(_paper);
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
            (_deepCapsuleQueueTransferRequested ||
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

    // Dedup key for the source-queue close-up. Deliberately separate from the cross-queue
    // (queueKey, index) sentinel: the two describe different states, and sharing one key made each
    // clear the other, so a merge would cancel the close-up and the close-up would cancel a merge.
    private string _deepCapsuleVacatedQueueKey = "";
    private long _deepCapsuleQueuePreviewTimestamp;
    private string _deepCapsuleQueuePreviewKey = "";
    private int _deepCapsuleQueuePreviewIndex = -1;

    /// <summary>
    /// Sample the cursor on a timer while the native caption loop owns the drag.
    ///
    /// Cross-edge and cross-monitor drags spend their entire gesture inside that loop, so without
    /// this the most common free-queue merge gets no feedback until release. The timer runs on the
    /// dispatcher, which the modal loop keeps pumping, and the preview's own 32 ms gate still
    /// collapses repeated ticks.
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

    private void OnEdgeCapsuleNativeDragFeedbackTick(object? sender, EventArgs e)
    {
        if (!IsDeepCapsuleFloatingReordering ||
            !WindowNative.TryGetCursorScreenPosition(out var cursor))
        {
            return;
        }

        // A stationary pointer cannot change the target slot; skip the evaluation entirely.
        if (_deepCapsuleNativeDragFeedbackCursor == cursor)
        {
            return;
        }

        _deepCapsuleNativeDragFeedbackCursor = cursor;
        UpdateEdgeCapsuleDragPointer(cursor);
        PreviewDeepCapsuleQueueMagnetForCurrentPosition(
            floatingPhase: true,
            forceEvaluation: true);
    }

    /// <summary>Clear the drag-time feedback gate, outline and held queue at the end of a gesture.</summary>
    private void ResetDeepCapsuleQueueMagnetPreview()
    {
        StopEdgeCapsuleNativeDragFeedback();
        ResetDeepCapsuleQueueMagnetPreviewState();
        _controller.EndDeepCapsuleQueueMagnetPreview();
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

        if (!string.Equals(
                targetKey,
                AppController.QueueKeyForPaper(_paper),
                StringComparison.Ordinal))
        {
            _controller.RestoreDeepCapsuleQueuePreview(targetKey);
        }
    }

    /// <summary>
    /// Close up the queue the dragged capsule came from, once per queue.
    ///
    /// Re-running it every tick would restart the 200 ms transition on every member every 32 ms,
    /// which reads as a stutter rather than a settle.
    /// </summary>
    private void PreviewDeepCapsuleQueueVacatedSourceOnce()
    {
        var sourceQueueKey = AppController.QueueKeyForPaper(_paper);
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
        var sourceQueueKey = AppController.QueueKeyForPaper(_paper);

        // The new outline and cross-queue avoidance exist for one situation only: what this drag
        // carries leaving its own queue to join a different one. Same-queue reordering already had
        // a working avoidance path before free queues existed, so it keeps that and shows no marker.
        var crossQueue = plan.Outcome == EdgeCapsuleQueueMagnetOutcome.Merge &&
                         !string.Equals(plan.TargetQueueKey, sourceQueueKey, StringComparison.Ordinal);

        if (!crossQueue)
        {
            _controller.EndEdgeCapsuleQueueDropTargetOutline();
            // 真正离开吸附区：把上一帧被撑开的目标队列放回真实槽位，空隙随之合拢。
            RestoreDeepCapsuleQueuePreviewTarget();
            if (transfer != null)
            {
                // The queue being carried is already lifted off the wall and hidden; there is no
                // gap left to close, so re-running the close-up would only churn detached members.
                ResetDeepCapsuleQueueMagnetPreviewState();
            }
            else if (floatingPhase)
            {
                // Off the wall there is no slot index to track, but the vacated queue still has to
                // close up. This is what keeps the queue reading as one piece mid-drag.
                PreviewDeepCapsuleQueueVacatedSourceOnce();
            }
            else
            {
                ResetDeepCapsuleQueueMagnetPreviewState();
                PreviewDeepCapsuleReorderForCurrentPosition();
            }

            return;
        }

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

        var sameSlot = !targetChanged && index == _deepCapsuleQueuePreviewIndex;
        if (!sameSlot)
        {
            var feasible = _controller.PreviewDeepCapsuleQueueDrag(
                carried,
                plan.TargetQueueKey,
                index);
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
            _controller.TryBeginEdgeCapsuleQueueTransfer(_paper, out var transfer);
            _deepCapsuleQueueTransferSnapshot = transfer;
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

        EndDeepCapsuleReorderDrag(commit: true);
        ClearCapsuleInteractionKeyboardFocus();
    }

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
                        wasFloatingDrag || _deepCapsuleQueueTransferSnapshot != null);

                    if (_deepCapsuleQueueTransferSnapshot is { } transfer)
                    {
                        _deepCapsuleQueueTransferSnapshot = null;
                        _controller.CommitEdgeCapsuleQueueTransfer(
                            transfer,
                            anchor,
                            _deepCapsuleQueuePreviewKey,
                            _deepCapsuleQueuePreviewIndex);
                    }
                    else
                    {
                        _controller.CommitDeepCapsuleQueueDrop(
                            _paper,
                            anchor,
                            _deepCapsuleQueuePreviewKey,
                            _deepCapsuleQueuePreviewIndex);
                    }

                    return;
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
            if (!commit && _deepCapsuleQueueTransferSnapshot is { } cancelled)
            {
                // Roll the queue back to the exact membership and height it had before the drag.
                _deepCapsuleQueueTransferSnapshot = null;
                _controller.CancelEdgeCapsuleQueueTransfer(cancelled);
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
        }
    }

    private void CancelDeepCapsuleReorderDrag(bool restoreLayout = false)
    {
        var wasReordering = IsDeepCapsuleReordering;
        var wasDockingHandoff = IsDeepCapsuleDockingHandoff;
        var wasDockingReveal = IsDeepCapsuleDockingReveal;
        if (!wasReordering &&
            !wasDockingHandoff &&
            !IsDeepCapsuleSlotPendingClick &&
            _deepCapsuleFloatingDragHost == null)
        {
            _previewOriginReorderBaseline = null;
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

