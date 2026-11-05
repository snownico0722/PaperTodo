namespace PaperTodo;

public sealed partial class PaperWindow
{
    internal string EdgeCapsulePreviewEligibilityTrace()
    {
        if (!_controller.State.ExperimentalEdgeCapsuleHoverPreview)
        {
            return "feature-disabled";
        }
        if (_windowLifecycle != PaperWindowLifecycleState.Alive)
        {
            return $"lifecycle={_windowLifecycle}";
        }
        if (!_paper.IsVisible)
        {
            return "paper-hidden";
        }
        if (IsExperimentalPassive)
        {
            return "passive";
        }
        if (_advancedInteractionLocked)
        {
            return "interaction-locked";
        }
        if (!HasDeepCapsuleSlotPlacement)
        {
            return "no-slot-placement";
        }
        if (IsDeepCapsuleRetractedIntoMaster)
        {
            return "retracted";
        }
        if (IsDeepCapsuleSlotRetracting)
        {
            return "retracting";
        }
        if (IsDeepCapsuleReordering)
        {
            return "reordering";
        }
        if (_edgeCapsule.PeerReorderActive)
        {
            return "peer-reorder";
        }
        if (IsDeepCapsuleDockingHandoff)
        {
            return "docking-handoff";
        }
        if (_edgeCapsule.ContextMenuOpen && !IsEdgeCapsulePreviewOpen)
        {
            return "context-menu";
        }
        if (EdgeCapsuleGesture != EdgeCapsuleGestureState.Idle &&
            !(IsEdgeCapsulePreviewOpen &&
              EdgeCapsuleGesture == EdgeCapsuleGestureState.PendingClick))
        {
            return $"gesture={EdgeCapsuleGesture}";
        }
        if (EdgeCapsuleSlot is not (
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.ExpandedReserved))
        {
            return $"slot={EdgeCapsuleSlot}";
        }

        return "ok";
    }
}
