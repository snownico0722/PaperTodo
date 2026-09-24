using PaperTodo;

internal static partial class Program
{
    private static void EdgeOpenOriginPolicyChecks()
    {
        Check(
            EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsulePaperForm.Expanded),
            "Expanding a docked collapsed edge capsule keeps edge-open origin even when the slot will detach");
        Check(
            EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.RetractedCollapsed,
                EdgeCapsulePaperForm.Expanded),
            "Expanding a retracted collapsed edge capsule keeps edge-open origin");
        Check(
            EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.RetractingCollapsed,
                EdgeCapsulePaperForm.Expanded),
            "Interrupted collapsed-slot retraction still counts as an edge-origin expansion");
        Check(
            !EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.ExpandedReserved,
                EdgeCapsulePaperForm.Expanded),
            "Refreshing an already-expanded reservation does not manufacture a new edge-open origin");
        Check(
            !EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.None,
                EdgeCapsulePaperForm.Expanded),
            "Ordinary paper expansion without an edge slot does not count as an edge-origin expansion");
        Check(
            !EdgeCapsuleOpenOriginPolicy.ShouldMarkOpenedFromEdge(
                EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsulePaperForm.Collapsed),
            "Remaining collapsed never marks an edge-open origin");
    }
}
