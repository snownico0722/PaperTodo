using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void OnEdgeCapsulePointerPressed(DeviceScreenPoint screenPosition) =>
        BeginEdgeCapsulePointerInteraction(screenPosition);

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

        var deltaX = Math.Abs(currentScreenPosition.X - session.PointerDownScreenPosition.X);
        var deltaY = Math.Abs(currentScreenPosition.Y - session.PointerDownScreenPosition.Y);
        if (CanReorderDeepCapsuleSlot())
        {
            if (deltaY >= SystemParameters.MinimumVerticalDragDistance + DeepCapsuleReorderDragExtraThreshold ||
                deltaX >= SystemParameters.MinimumHorizontalDragDistance + DeepCapsuleReorderDragExtraThreshold)
            {
                StartDeepCapsuleReorderDrag(currentScreenPosition);
                return true;
            }
            return false;
        }

        if (deltaX >= SystemParameters.MinimumHorizontalDragDistance ||
            deltaY >= SystemParameters.MinimumVerticalDragDistance)
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
            ActivateFromDeepCapsuleSlot();
        }
        finally
        {
            ClearCapsuleInteractionKeyboardFocus();
        }
        return true;
    }

    private EdgeCapsuleCaptureAction OnEdgeCapsuleCaptureLost(bool leftButtonPressed)
    {
        var action = _edgeCapsule.HandleCaptureLost(leftButtonPressed);
        if (action == EdgeCapsuleCaptureAction.CancelDrag)
        {
            EndDeepCapsuleReorderDrag(commit: false);
            ClearCapsuleInteractionKeyboardFocus();
        }
        return action;
    }

    private void OnEdgeCapsuleCloseInvoked()
    {
        _controller.HidePaper(_paper);
        ClearCapsuleInteractionKeyboardFocus();
    }

    private EdgeCapsuleDragWindow CreateDeepCapsuleFloatingDragHost(
        DeviceScreenPoint pointer,
        EdgeCapsuleFloatingShape shape)
    {
        CloseDeepCapsuleFloatingDragHost();

        var outlineMargin = WindowChromeMargin - DeepCapsuleSlotOutlineThickness + DeepCapsuleSlotOutlineOverlap;
        var host = new EdgeCapsuleDragWindow(new EdgeCapsuleDragWindowOptions
        {
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
        });
        host.UnexpectedlyClosed += OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;

        _deepCapsuleFloatingDragHost = host;
        try
        {
            host.ShowWithEntrance(
                pointer,
                _controller.State.EnableAnimations,
                DeepCapsuleCrossQueueDragScaleFrom,
                DeepCapsuleCrossQueueDragMorphMilliseconds);
            return host;
        }
        catch
        {
            _deepCapsuleFloatingDragHost = null;
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            try
            {
                host.CloseFromOwner();
            }
            catch
            {
                // Preserve the original Show failure; the interaction caller owns rollback.
            }
            throw;
        }
    }

    private void MoveDeepCapsuleFloatingDragHost(DeviceScreenPoint pointer)
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        _deepCapsuleFloatingDragHost.MoveCenteredAt(pointer);
    }

    private void CloseDeepCapsuleFloatingDragHost()
    {
        _edgeCapsule.ClearPresentationSettleNotification();
        var host = _deepCapsuleFloatingDragHost;
        _deepCapsuleFloatingDragHost = null;
        if (host != null)
        {
            host.UnexpectedlyClosed -= OnDeepCapsuleFloatingDragHostUnexpectedlyClosed;
            host.CloseFromOwner();
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
        _deepCapsuleFloatingDragHost = null;
        _edgeCapsule.ClearPresentationSettleNotification();
        if (IsDeepCapsuleDockingHandoff)
        {
            // The visual cover disappeared unexpectedly. Reveal the already-committed destination
            // through the normal Presenter path and let the topology settle pass verify it.
            FinishEdgeCapsulePointerInteraction();
            FlushEdgeCapsulePresentation(
                EdgeCapsuleTransitionReason.FloatingTransfer,
                EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
            _controller.ScheduleDisplayMetricsRefresh();
            return;
        }
        if (!IsDeepCapsuleFloatingReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private void AwaitDeepCapsuleDockedPresentation(
        EdgeCapsuleDragWindow floatingHost,
        Action<bool> completed,
        bool allowImmediateReplay = true)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
            !HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost == null)
        {
            completed(false);
            return;
        }

        _edgeCapsule.NotifyWhenPresentationSettled(pipelineSettled =>
        {
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost))
            {
                return;
            }

            // Pipeline-settled only means the Presenter has no queued work. The native host still
            // needs a later layout check after WPF has processed the destination monitor's DPI.
            var settled = pipelineSettled &&
                _edgeCapsuleHost?.ConfirmPresentationSettled(
                    _edgeCapsule.AppliedPresentation) == true;
            if (!settled && allowImmediateReplay)
            {
                // Confirm has hidden the rejected host. Bring the Presenter's applied frame back
                // to the same state, then replay once through the existing dirty/reconcile path
                // while the floating HWND continues to cover the hand-off.
                _edgeCapsule.ResetPresentation();
                InvalidateEdgeCapsuleDisplayMetrics();
                AwaitDeepCapsuleDockedPresentation(
                    floatingHost,
                    completed,
                    allowImmediateReplay: false);
                return;
            }

            completed(settled);
        });
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.FloatingTransfer,
            EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
    }

    private void CompleteDeepCapsuleFloatingDragDrop()
    {
        var floatingHost = _deepCapsuleFloatingDragHost;
        if (floatingHost == null)
        {
            return;
        }

        if (!HasDeepCapsuleSlotPlacement || _edgeCapsuleHost == null)
        {
            CloseDeepCapsuleFloatingDragHost();
            return;
        }

        // Keep the floating HWND as a cover until Host.Apply has both accepted and verified the
        // permanent docked HWND after WPF's later layout priorities.
        AwaitDeepCapsuleDockedPresentation(floatingHost, settled =>
        {
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost))
            {
                return;
            }

            CloseDeepCapsuleFloatingDragHost();
            if (!settled)
            {
                // Confirmation makes an unsettled host transparent before the cover is removed;
                // the debounced topology pass then restores it from a fresh monitor snapshot.
                _controller.ScheduleDisplayMetricsRefresh();
            }
        });
    }

    private void BeginDeepCapsuleFloatingDockingHandoff()
    {
        var floatingHost = _deepCapsuleFloatingDragHost;
        if (floatingHost == null ||
            !IsDeepCapsuleDockingHandoff ||
            !HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost == null)
        {
            FinishEdgeCapsulePointerInteraction();
            CompleteDeepCapsuleFloatingDragDrop();
            return;
        }

        // First commit the destination host invisibly. This supplies the animation with the same
        // verified physical frame that will be revealed at the end, including mixed-DPI geometry.
        AwaitDeepCapsuleDockedPresentation(floatingHost, settled =>
        {
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleDockingHandoff)
            {
                return;
            }

            var targetBounds = settled
                ? CurrentDeepCapsuleFloatingHandoffTargetBounds()
                : default;
            if (targetBounds.IsEmpty)
            {
                FinishEdgeCapsulePointerInteraction();
                CompleteDeepCapsuleFloatingDragDrop();
                return;
            }

            AnimateDeepCapsuleFloatingDockingHandoff(
                floatingHost,
                targetBounds,
                allowRetarget: true);
        });
    }

    private DeviceScreenRect CurrentDeepCapsuleFloatingHandoffTargetBounds()
    {
        var frame = _edgeCapsule.AppliedPresentation;
        return frame.Visible &&
            frame.Surface == EdgeCapsuleSurfaceKind.DockedSuppressed &&
            !frame.Bounds.IsEmpty
                ? EdgeCapsuleGeometry.FloatingHandoffBoundsForDockedBounds(
                    frame.Bounds,
                    frame.Edge,
                    frame.DpiScaleX,
                    WindowChromeMargin)
                : default;
    }

    private void AnimateDeepCapsuleFloatingDockingHandoff(
        EdgeCapsuleDragWindow floatingHost,
        DeviceScreenRect targetBounds,
        bool allowRetarget)
    {
        floatingHost.AnimateDockingHandoff(
            targetBounds,
            DeepCapsuleDockingHandoffMilliseconds,
            floatingSettled =>
            {
                if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                    !IsDeepCapsuleDockingHandoff)
                {
                    return;
                }

                // The queue or monitor topology can change during the short native flight. Ask
                // the Presenter for its latest verified suppressed frame before revealing it.
                AwaitDeepCapsuleDockedPresentation(floatingHost, dockedSettled =>
                {
                    if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                        !IsDeepCapsuleDockingHandoff)
                    {
                        return;
                    }

                    var latestTargetBounds = dockedSettled
                        ? CurrentDeepCapsuleFloatingHandoffTargetBounds()
                        : default;
                    if (allowRetarget &&
                        !latestTargetBounds.IsEmpty &&
                        (!floatingSettled || latestTargetBounds != targetBounds))
                    {
                        AnimateDeepCapsuleFloatingDockingHandoff(
                            floatingHost,
                            latestTargetBounds,
                            allowRetarget: false);
                        return;
                    }

                    // Reveal the permanent host only after the floating pill reaches its exact
                    // current cover rectangle. If one retry still races topology, the existing
                    // settle/Confirm path owns the reliable synchronous fallback.
                    FinishEdgeCapsulePointerInteraction();
                    CompleteDeepCapsuleFloatingDragDrop();
                    _controller.RefreshFloatingSurfaceZOrder();
                });
            });
    }

    private void StartDeepCapsuleReorderDrag(DeviceScreenPoint currentScreenPos)
    {
        if (!CanReorderDeepCapsuleSlot() ||
            _edgeCapsuleHost == null ||
            !_edgeCapsule.AppliedPresentation.Visible ||
            _edgeCapsule.AppliedPresentation.Bounds.IsEmpty)
        {
            return;
        }

        if (!TryGetEdgeCapsuleDragSession(out var session))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        var appliedBounds = _edgeCapsule.AppliedPresentation.Bounds;
        var startMonitorDeviceName = WindowWorkAreaHelper
            .MonitorAtDeviceScreenPoint(session.PointerDownScreenPosition)?.DeviceName ?? "";
        var pointerOffsetY = currentScreenPos.Y - appliedBounds.Top;
        var topDip = DeepCapsuleMonitorGeometry().DeviceYToLocalDip(appliedBounds.Top);
        if (!BeginEdgeCapsuleDockedReorder(
                currentScreenPos,
                startMonitorDeviceName,
                pointerOffsetY,
                topDip))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }

        _controller.BeginDeepCapsuleReorderDrag(_paper);
        CloseDeepCapsuleFloatingDragHost();
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Drag);
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
            MoveDeepCapsuleFloatingDragHost(currentScreenPos);
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

        var edgeHost = _edgeCapsuleHost;
        if (!BeginEdgeCapsuleFloatingTransfer(currentScreenPos))
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
            return;
        }
        try
        {
            FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
            var floatingHost = CreateDeepCapsuleFloatingDragHost(
                currentScreenPos,
                _edgeCapsule.FloatingShape);
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                ClearCapsuleInteractionKeyboardFocus();
                return;
            }
            if (!BeginEdgeCapsuleFloatingReorder())
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }
            WindowNative.BringToFrontNoActivate(floatingHost);
            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;
            // Show() of the floating top-level window steals capture from the docked content.
            // Re-capture so subsequent move/up keep driving the floating host.
            if (Mouse.LeftButton == MouseButtonState.Pressed &&
                !edgeHost.IsContentPointerCaptured)
            {
                edgeHost.CaptureContentPointer();
            }
        }
        catch
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
        }
        finally
        {
            if (IsDeepCapsuleFloatingReordering)
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    CancelDeepCapsuleReorderDrag(restoreLayout: true);
                    ClearCapsuleInteractionKeyboardFocus();
                }
                else if (!edgeHost.IsContentPointerCaptured)
                {
                    edgeHost.CaptureContentPointer();
                }
            }
        }
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
        if (!wasReordering &&
            !IsDeepCapsuleSlotPendingClick &&
            _deepCapsuleFloatingDragHost == null)
        {
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
            FinishEdgeCapsulePointerInteraction();
            _edgeCapsuleHost?.ReleaseContentPointer();
            if (wasReordering)
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

        var dragBounds = _deepCapsuleFloatingDragHost != null &&
            WindowNative.TryGetWindowDeviceBounds(_deepCapsuleFloatingDragHost, out var floatingBounds)
                ? floatingBounds
                : _edgeCapsule.AppliedPresentation.Bounds;
        if (dragBounds.IsEmpty)
        {
            return Math.Clamp(_edgeCapsule.Placement.Index, 0, count - 1);
        }

        var geometry = DeepCapsuleMonitorGeometry();
        var centerY = geometry.DeviceYToLocalDip(dragBounds.Top + dragBounds.Height / 2.0);
        // Real capsules start after slot 0 when the master capsule occupies that slot.
        var firstCenterY = DeepCapsuleTopForIndex(_edgeCapsule.Placement.VisualOffset) +
            (PaperLayoutDefaults.CapsuleHeight / 2);
        var slotHeight = PaperLayoutDefaults.CapsuleHeight + DeepCapsuleGap;
        var originalIndex = Math.Clamp(_edgeCapsule.Placement.Index, 0, count - 1);
        var rawIndex = (centerY - firstCenterY) / slotHeight;
        var index = rawIndex >= originalIndex
            ? (int)Math.Floor(rawIndex)
            : (int)Math.Ceiling(rawIndex);
        return Math.Clamp(index, 0, count - 1);
    }
}
