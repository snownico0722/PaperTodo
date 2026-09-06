using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly record struct PreviewOriginReorderBaseline(
        int OriginalIndex,
        DeviceScreenPoint PointerDownScreenPosition,
        double DpiScaleY);

    private PreviewOriginReorderBaseline? _previewOriginReorderBaseline;

    internal bool AllowsDeepCapsuleQueueProxyOwnership =>
        EdgeCapsuleQueueProxyPolicy.AllowsQueueProxyOwnership(
            CurrentEdgeCapsuleVisualAuthority);

    private void OnEdgeCapsulePointerPressed(DeviceScreenPoint screenPosition)
    {
        EdgeCapsulePerformanceDiagnostics.Trace(
            $"drag.input phase=pointer-down " +
            $"paper={EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)} " +
            $"pointer={screenPosition.X:F0},{screenPosition.Y:F0}");
        BeginEdgeCapsulePointerInteraction(screenPosition);
        QueueDeepCapsuleFloatingDragInfrastructurePrewarm();
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
            ShouldUnlockDeepCapsuleCrossQueueDrag(currentScreenPos))
        {
            BeginDeepCapsuleFloatingReorder(currentScreenPos);
            return;
        }

        if (IsDeepCapsuleFloatingReordering)
        {
            UpdateEdgeCapsuleDragPointer(currentScreenPos);
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
        PreviewDeepCapsuleReorderForCurrentPosition();
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
            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;
#if DEBUG
            var stateAndZOrderMs =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            stageStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif

            // Push one render pass so the docked blank and new floating HWND share a layout tick.
            // Do not DwmFlush or pump Input here: both freeze this UI thread while the cursor keeps
            // moving, which feels like a sticky pull-out. A one-frame composition race is cheaper
            // than a multi-frame hitch before the system move loop owns the pill. Fast release is
            // handled after SendMessage returns (button already up → Completed at cursor).
            Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Render);
#if DEBUG
            var renderBarrierMs =
                EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(stageStartedAt);
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=host-ready paper={diagnosticId} " +
                $"totalMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(transferStartedAt):F3} " +
                $"transferStateMs={transferStateMs:F3} flushMs={flushMs:F3} " +
                $"createShowMs={createShowMs:F3} " +
                $"stateAndZOrderMs={stateAndZOrderMs:F3} " +
                $"renderBarrierMs={renderBarrierMs:F3}");
#endif

            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleFloatingReordering)
            {
                return;
            }

            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CommitDeepCapsuleFloatingReorderAtCursor(currentScreenPos);
                edgeHost.ReleaseContentPointer();
                return;
            }

            // From here through button release, Windows is the sole drag owner. The reducer stays
            // in FloatingReordering only so queue/layout work remains deferred until we sample the
            // final native cursor position.
#if DEBUG
            var nativeDragStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();
#endif
            var nativeDragOutcome = edgeHost.TransferContentPointerToNativeDrag(
                floatingHost.RunNativeDragFromCursor);
#if DEBUG
            EdgeCapsulePerformanceDiagnostics.Trace(
                $"drag.transfer phase=native-return paper={diagnosticId} " +
                $"result={nativeDragOutcome.Result} " +
                $"nativeCallMs={EdgeCapsulePerformanceDiagnostics.ElapsedMilliseconds(nativeDragStartedAt):F3} " +
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
