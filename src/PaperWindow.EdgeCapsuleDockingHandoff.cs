namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Passed through the callback chain rather than stored on PaperWindow, so cancellation or a
    // later drag cannot inherit an exhausted budget from an earlier floating HWND.
    private const int MaximumDeepCapsuleDockingHandoffRestarts = 1;

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
            floatingHost.CompleteHandoff(CloseDeepCapsuleFloatingDragHost);
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
                ? AnimationTiming.ScaleMilliseconds(EdgeCapsuleLayout.DockingHandoffMilliseconds)
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
                        ? AnimationTiming.ScaleMilliseconds(EdgeCapsuleLayout.DockingRevealMilliseconds)
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

        floatingHost.CompleteHandoff(CloseDeepCapsuleFloatingDragHost);
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
                    floatingHost.CompleteHandoff(CloseDeepCapsuleFloatingDragHost);
                }
                else
                {
                    CloseDeepCapsuleFloatingDragHost();
                }
                _controller.RefreshFloatingSurfaceZOrder();
                _controller.ScheduleDisplayMetricsRefresh();
            },
            allowImmediateReplay: false,
            dirty: EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure);
    }
}

