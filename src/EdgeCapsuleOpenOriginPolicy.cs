namespace PaperTodo;

internal static class EdgeCapsuleOpenOriginPolicy
{
    internal static bool ShouldMarkOpenedFromEdge(EdgeCapsuleSlotState slot, EdgeCapsulePaperForm form)
        => form == EdgeCapsulePaperForm.Expanded && slot is
            EdgeCapsuleSlotState.CollapsedDocked or
            EdgeCapsuleSlotState.RetractedCollapsed or
            EdgeCapsuleSlotState.RetractingCollapsed;
}
