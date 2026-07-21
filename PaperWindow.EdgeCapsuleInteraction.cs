using System.Windows;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Passed through the callback chain rather than stored on PaperWindow, so cancellation or a
    // later drag cannot inherit an exhausted budget from an earlier floating HWND.
    private const int MaximumDeepCapsuleDockingHandoffRestarts = 1;

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

        var canReorder = CanReorderDeepCapsuleSlot();
        var movedEnough = WindowWorkAreaHelper.ExceedsDragThreshold(
            session.PointerDownScreenPosition,
            currentScreenPosition,
            additionalDistanceDip: canReorder ? DeepCapsuleReorderDragExtraThreshold : 0);
        if (canReorder)
        {
            if (movedEnough)
            {
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
        host.LocationChanged += OnDeepCapsuleFloatingDragHostLocationChanged;

        _deepCapsuleFloatingDragHost = host;
        _deepCapsuleFloatingFullscreenAvoidanceWindow = IntPtr.Zero;
        try
        {
            host.ShowWithEntrance(
                pointer,
                _controller.State.EnableAnimations,
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
                host.CloseFromOwner();
            }
            catch
            {
                // Preserve the original Show failure; the interaction caller owns rollback.
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
            host.CloseFromOwner();
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

    private void AwaitDeepCapsuleDockedPresentation(
        EdgeCapsuleDragWindow floatingHost,
        Action<bool> completed,
        bool allowImmediateReplay = true,
        bool flushImmediately = true,
        EdgeCapsuleDirty dirty = EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure)
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
                    allowImmediateReplay: false,
                    flushImmediately: true,
                    dirty: dirty);
                return;
            }

            completed(settled);
        });
        if (!flushImmediately)
        {
            return;
        }
        FlushEdgeCapsulePresentation(
            EdgeCapsuleTransitionReason.FloatingTransfer,
            dirty);
    }

    private void CompleteDeepCapsuleFloatingDragDrop(
        int handoffRestartsRemaining = MaximumDeepCapsuleDockingHandoffRestarts)
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

            if (!settled)
            {
                // Confirmation hides a rejected docked host. Keep the real floating cover alive
                // and return to the same hand-off pipeline instead of exposing an empty frame.
                floatingHost.RestoreDockingCover();
                if (BeginEdgeCapsuleDockingHandoff())
                {
                    BeginDeepCapsuleFloatingDockingHandoff(handoffRestartsRemaining);
                }
                else
                {
                    // The queue can disappear while native confirmation is pending. There is no
                    // valid transaction to resume, so do not leave an ownerless floating HWND.
                    CancelDeepCapsuleReorderDrag();
                    _controller.ScheduleDisplayMetricsRefresh();
                }
                return;
            }

            // ContextIdle has let WPF submit the revealed docked surface. Do not destroy the
            // floating cover until DWM has presented that update, otherwise two independent
            // layered HWNDs can expose the desktop for one or two refresh frames.
            WindowNative.FlushDesktopComposition();
            CloseDeepCapsuleFloatingDragHost();
        });
    }

    private void BeginDeepCapsuleFloatingDockingHandoff(
        int handoffRestartsRemaining = MaximumDeepCapsuleDockingHandoffRestarts)
    {
        var floatingHost = _deepCapsuleFloatingDragHost;
        if (floatingHost == null ||
            !IsDeepCapsuleDockingFlight ||
            !HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost == null)
        {
            FinishEdgeCapsulePointerInteraction();
            CompleteDeepCapsuleFloatingDragDrop(handoffRestartsRemaining);
            return;
        }

        // First commit the destination host invisibly. This supplies the animation with the same
        // verified physical frame that will be revealed at the end, including mixed-DPI geometry.
        AwaitDeepCapsuleDockedPresentation(floatingHost, settled =>
        {
            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleDockingFlight)
            {
                return;
            }

            var targetEdge = default(EdgeCapsuleEdge);
            var targetBounds = settled
                ? CurrentDeepCapsuleFloatingHandoffTargetBounds(out targetEdge)
                : default;
            if (!settled || targetBounds.IsEmpty)
            {
                RecoverDeepCapsuleFloatingDockingHandoff(
                    floatingHost,
                    handoffRestartsRemaining);
                return;
            }

            AnimateDeepCapsuleFloatingDockingHandoff(
                floatingHost,
                targetBounds,
                targetEdge,
                handoffRestartsRemaining);
        });
    }

    private DeviceScreenRect CurrentDeepCapsuleFloatingHandoffTargetBounds(
        out EdgeCapsuleEdge edge)
    {
        var frame = _edgeCapsule.AppliedPresentation;
        edge = frame.Edge;
        return frame.Visible &&
            frame.Surface is not (
                EdgeCapsuleSurfaceKind.Hidden or
                EdgeCapsuleSurfaceKind.FloatingFree) &&
            !frame.Bounds.IsEmpty
                ? EdgeCapsuleGeometry.FloatingHandoffBoundsForDockedBounds(
                    frame.Bounds,
                    frame.Edge,
                    frame.DpiScaleX,
                    WindowChromeMargin)
                : default;
    }

    private static bool DeepCapsuleFloatingHandoffTargetMatches(
        DeviceScreenRect firstBounds,
        EdgeCapsuleEdge firstEdge,
        DeviceScreenRect secondBounds,
        EdgeCapsuleEdge secondEdge) =>
        firstEdge == secondEdge &&
        EdgeCapsuleGeometry.DeviceBoundsMatch(
            firstBounds,
            secondBounds,
            tolerance: 1);

    private void AnimateDeepCapsuleFloatingDockingHandoff(
        EdgeCapsuleDragWindow floatingHost,
        DeviceScreenRect targetBounds,
        EdgeCapsuleEdge targetEdge,
        int handoffRestartsRemaining)
    {
        floatingHost.AnimateDockingHandoff(
            targetBounds,
            targetEdge,
            _controller.State.EnableAnimations
                ? DeepCapsuleDockingHandoffMilliseconds
                : 1,
            floatingSettled =>
            {
                if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                    !IsDeepCapsuleDockingFlight)
                {
                    return;
                }

                // The queue or monitor topology can change during the short native flight. Ask
                // the Presenter for its latest verified suppressed frame before revealing it.
                AwaitDeepCapsuleDockedPresentation(floatingHost, dockedSettled =>
                {
                    if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                        !IsDeepCapsuleDockingFlight)
                    {
                        return;
                    }

                    var latestTargetEdge = targetEdge;
                    var latestTargetBounds = dockedSettled
                        ? CurrentDeepCapsuleFloatingHandoffTargetBounds(out latestTargetEdge)
                        : default;
                    if (!dockedSettled || latestTargetBounds.IsEmpty)
                    {
                        RecoverDeepCapsuleFloatingDockingHandoff(
                            floatingHost,
                            handoffRestartsRemaining);
                        return;
                    }
                    if (!DeepCapsuleFloatingHandoffTargetMatches(
                            latestTargetBounds,
                            latestTargetEdge,
                            targetBounds,
                            targetEdge))
                    {
                        if (handoffRestartsRemaining <= 0)
                        {
                            CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(
                                floatingHost);
                            return;
                        }

                        // A topology/measure update during the flight becomes a new authoritative
                        // target. One physical pixel is normal mixed-DPI rounding, not a new flight;
                        // larger changes consume the shared hand-off restart budget.
                        AnimateDeepCapsuleFloatingDockingHandoff(
                            floatingHost,
                            latestTargetBounds,
                            latestTargetEdge,
                            handoffRestartsRemaining - 1);
                        return;
                    }
                    if (!floatingSettled)
                    {
                        // Replaying the same stable target cannot repair a floating HWND endpoint.
                        // Finish through the bounded no-animation path instead of flying again.
                        CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(
                            floatingHost);
                        return;
                    }

                    BeginDeepCapsuleDockingReveal(
                        floatingHost,
                        latestTargetBounds,
                        latestTargetEdge,
                        handoffRestartsRemaining);
                });
            });
    }

    private void BeginDeepCapsuleDockingReveal(
        EdgeCapsuleDragWindow floatingHost,
        DeviceScreenRect coverBounds,
        EdgeCapsuleEdge coverEdge,
        int handoffRestartsRemaining)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
            !IsDeepCapsuleDockingFlight ||
            !BeginEdgeCapsuleDockingReveal())
        {
            RecoverDeepCapsuleFloatingDockingHandoff(
                floatingHost,
                handoffRestartsRemaining);
            return;
        }

        // Build and fully confirm the permanent surface underneath the opaque floating cover.
        // Only then is the cover faded, so there is never a frame in which both HWNDs are absent.
        AwaitDeepCapsuleDockedPresentation(
            floatingHost,
            dockedSettled =>
            {
                if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                    !IsDeepCapsuleDockingReveal)
                {
                    return;
                }
                if (!dockedSettled)
                {
                    RollBackDeepCapsuleDockingReveal(
                        floatingHost,
                        handoffRestartsRemaining);
                    return;
                }
                var currentCoverBounds = CurrentDeepCapsuleFloatingHandoffTargetBounds(
                    out var currentCoverEdge);
                if (!DeepCapsuleFloatingHandoffTargetMatches(
                        currentCoverBounds,
                        currentCoverEdge,
                        coverBounds,
                        coverEdge))
                {
                    RollBackDeepCapsuleDockingReveal(
                        floatingHost,
                        handoffRestartsRemaining);
                    return;
                }

                floatingHost.AnimateDockingReveal(
                    _controller.State.EnableAnimations
                        ? DeepCapsuleDockingRevealMilliseconds
                        : 1,
                    floatingFaded => CompleteDeepCapsuleDockingReveal(
                        floatingHost,
                        floatingFaded,
                        coverBounds,
                        coverEdge,
                        handoffRestartsRemaining));
            },
            allowImmediateReplay: false,
            dirty: EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
    }

    private void CompleteDeepCapsuleDockingReveal(
        EdgeCapsuleDragWindow floatingHost,
        bool floatingFaded,
        DeviceScreenRect coverBounds,
        EdgeCapsuleEdge coverEdge,
        int handoffRestartsRemaining)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
            !IsDeepCapsuleDockingReveal)
        {
            return;
        }
        if (!floatingFaded)
        {
            RollBackDeepCapsuleDockingReveal(
                floatingHost,
                handoffRestartsRemaining);
            return;
        }
        var currentCoverBounds = CurrentDeepCapsuleFloatingHandoffTargetBounds(
            out var currentCoverEdge);
        if (!DeepCapsuleFloatingHandoffTargetMatches(
                currentCoverBounds,
                currentCoverEdge,
                coverBounds,
                coverEdge))
        {
            RollBackDeepCapsuleDockingReveal(
                floatingHost,
                handoffRestartsRemaining);
            return;
        }

        // Reveal already confirmed all visible geometry. The final state change only enables input,
        // so it can be committed synchronously while the transparent cover is still available.
        if (!FinishEdgeCapsuleDockingHandoff())
        {
            RollBackDeepCapsuleDockingReveal(
                floatingHost,
                handoffRestartsRemaining);
            return;
        }
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.FloatingTransfer);
        var applied = _edgeCapsule.AppliedPresentation;
        if (!applied.IsHitTestVisible ||
            _edgeCapsuleHost?.ConfirmPresentationSettled(applied) != true)
        {
            RollBackDeepCapsuleDockingReveal(
                floatingHost,
                handoffRestartsRemaining);
            return;
        }

        WindowNative.FlushDesktopComposition();
        CloseDeepCapsuleFloatingDragHost();
        _controller.CompleteDeepCapsuleReorderDrag();
        _controller.RefreshFloatingSurfaceZOrder();
    }

    private void RollBackDeepCapsuleDockingReveal(
        EdgeCapsuleDragWindow floatingHost,
        int handoffRestartsRemaining)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost))
        {
            return;
        }

        floatingHost.RestoreDockingCover();
        if (IsDeepCapsuleDockingReveal)
        {
            FinishEdgeCapsulePointerInteraction();
        }
        if (!IsDeepCapsuleDockingFlight && !BeginEdgeCapsuleDockingHandoff())
        {
            CompleteDeepCapsuleFloatingDragDrop(handoffRestartsRemaining);
            return;
        }

        // Leaving Reveal releases any display/arrange batch deferred across the ownership switch.
        // Its fresh target is then consumed by the normal stable-target hand-off path.
        _controller.CompleteDeepCapsuleReorderDrag();
        if (handoffRestartsRemaining <= 0)
        {
            CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(floatingHost);
            return;
        }

        BeginDeepCapsuleFloatingDockingHandoff(handoffRestartsRemaining - 1);
    }

    private void RecoverDeepCapsuleFloatingDockingHandoff(
        EdgeCapsuleDragWindow floatingHost,
        int handoffRestartsRemaining)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
            !IsDeepCapsuleDockingFlight ||
            !HasDeepCapsuleSlotPlacement ||
            _edgeCapsuleHost == null)
        {
            FinishEdgeCapsulePointerInteraction();
            CloseDeepCapsuleFloatingDragHost();
            return;
        }

        if (handoffRestartsRemaining <= 0)
        {
            CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(floatingHost);
            return;
        }

        floatingHost.RestoreDockingCover();
        AwaitDeepCapsuleDockedPresentation(
            floatingHost,
            settled =>
            {
                if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                    !IsDeepCapsuleDockingFlight)
                {
                    return;
                }
                if (!settled)
                {
                    CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(floatingHost);
                    return;
                }

                var targetBounds = CurrentDeepCapsuleFloatingHandoffTargetBounds(
                    out var targetEdge);
                if (targetBounds.IsEmpty)
                {
                    CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(floatingHost);
                    return;
                }
                AnimateDeepCapsuleFloatingDockingHandoff(
                    floatingHost,
                    targetBounds,
                    targetEdge,
                    handoffRestartsRemaining - 1);
            },
            allowImmediateReplay: true,
            flushImmediately: false);
        _controller.ScheduleDisplayMetricsRefresh();
    }

    private void CompleteDeepCapsuleFloatingDockingHandoffWithoutAnimation(
        EdgeCapsuleDragWindow floatingHost)
    {
        if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
            !IsDeepCapsuleDockingFlight)
        {
            return;
        }

        // The destination queue mutation is already committed. If its native host still cannot
        // be verified after the normal replay and display refresh, end the visual transaction at
        // that destination instead of waiting for unrelated Presenter work forever.
        floatingHost.RestoreDockingCover();
        if (!FinishEdgeCapsulePointerInteraction())
        {
            CancelDeepCapsuleReorderDrag();
            _controller.ScheduleDisplayMetricsRefresh();
            return;
        }

        // Finishing the gesture changes the permanent host to its final interactive frame. Give
        // that frame one active, bounded Presenter settle before releasing the floating cover.
        // Presenter apply retries terminate on their own, so a broken HWND still cannot leave the
        // hand-off alive forever.
        AwaitDeepCapsuleDockedPresentation(
            floatingHost,
            settled =>
            {
                if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost))
                {
                    return;
                }

                if (settled)
                {
                    WindowNative.FlushDesktopComposition();
                }
                CloseDeepCapsuleFloatingDragHost();
                _controller.RefreshFloatingSurfaceZOrder();
                _controller.ScheduleDisplayMetricsRefresh();
            },
            allowImmediateReplay: false,
            dirty: EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
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
            if (!BeginEdgeCapsuleFloatingReorder())
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }
            WindowNative.BringToFrontNoActivate(floatingHost);
            RefreshDeepCapsuleSlotTopmost();
            Mouse.OverrideCursor = Cursors.SizeAll;

            // The staged docked-content blank and this brand-new layered window's first frame must
            // reach the screen as one swap. A fresh HWND's first presentation can lag one
            // composition behind an existing surface's dirty update, which showed as a one-frame
            // hole between hiding the docked pill and revealing the floating pill. Submit both in
            // one render pass, then wait for the compositor before the modal move loop starts.
            Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Render);
            WindowNative.FlushDesktopComposition();

            // DwmFlush can span the physical button release. Let queued WPF input run while the
            // docked content still owns capture, so a fast release completes through the ordinary
            // PreviewMouseLeftButtonUp path instead of a global button-state query.
            Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Input);

            if (!ReferenceEquals(floatingHost, _deepCapsuleFloatingDragHost) ||
                !IsDeepCapsuleFloatingReordering)
            {
                return;
            }

            // From here through button release, Windows is the sole drag owner. The reducer stays
            // in FloatingReordering only so queue/layout work remains deferred until we sample the
            // final native cursor position.
            var nativeDragOutcome = edgeHost.TransferContentPointerToNativeDrag(
                floatingHost.RunNativeDragFromCursor);
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
            if (!UpdateEdgeCapsuleDragPointer(nativeDragOutcome.DropPosition))
            {
                CancelDeepCapsuleReorderDrag(restoreLayout: true);
                return;
            }
            EndDeepCapsuleReorderDrag(commit: true);
            ClearCapsuleInteractionKeyboardFocus();
        }
        catch
        {
            CancelDeepCapsuleReorderDrag(restoreLayout: true);
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
        var wasDockingHandoff = IsDeepCapsuleDockingHandoff;
        var wasDockingReveal = IsDeepCapsuleDockingReveal;
        if (!wasReordering &&
            !wasDockingHandoff &&
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
