namespace PaperTodo;

internal static class EdgeCapsuleOpenOriginPolicy
{
    internal static bool ShouldMarkOpenedFromEdge(
        EdgeCapsuleSlotState currentSlot,
        EdgeCapsulePaperForm targetForm)
    {
        if (targetForm != EdgeCapsulePaperForm.Expanded)
        {
            return false;
        }

        // Open origin describes where expansion started, not whether the docked slot remains
        // visible afterwards. Capture the collapsed-slot state before PaperFormChanged can detach it.
        return currentSlot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractingCollapsed;
    }
}
