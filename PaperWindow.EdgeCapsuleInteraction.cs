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
        if (!IsDeepCapsuleFloatingReordering)
        {
            return;
        }

        CancelDeepCapsuleReorderDrag(restoreLayout: true);
    }

    private void CompleteDeepCapsuleFloatingDragDrop()
    {
        if (_deepCapsuleFloatingDragHost == null)
        {
            return;
        }

        if (!HasDeepCapsuleSlotPlacement || _edgeCapsuleHost == null)
        {
            CloseDeepCapsuleFloatingDragHost();
            return;
        }

        // The controller has already resolved the destination queue. Snap the permanently docked
        // host while hidden, then destroy the floating HWND before revealing the docked tree.
        // Keeping this hand-off synchronous avoids a third, mixed-DPI transition state.
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
        CloseDeepCapsuleFloatingDragHost();
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
                return;
            }

            _controller.ArrangeDeepCapsules(animate: true);
        }
        finally
        {
            if (wasFloatingDrag || _deepCapsuleFloatingDragHost != null)
            {
                CompleteDeepCapsuleFloatingDragDrop();
            }
            // Keep the gesture state alive through the controller operation. Queue arrange calls
            // are then deferred instead of racing the drag session, and a floating docked host
            // remains suppressed until its replacement HWND has been destroyed.
            FinishEdgeCapsulePointerInteraction();
            _controller.CompleteDeepCapsuleReorderDrag();
            _controller.RefreshFloatingSurfaceZOrder();
            FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
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
