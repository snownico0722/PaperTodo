namespace PaperTodo;

internal readonly record struct EdgeCapsuleLayoutFacts(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    EdgeCapsulePlacement Placement,
    double QueueStartTopMarginDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HostWidthDip,
    double HeightDip,
    bool CloseSegmentActsAsContent);

/// <summary>
/// Converts measured/environment facts into the planner snapshot. PaperWindow supplies target
/// monitor and text width only; queue top policy lives here and is independently testable.
/// </summary>
internal static class EdgeCapsuleLayoutService
{
    public static EdgeCapsuleLayoutSnapshot Calculate(EdgeCapsuleLayoutFacts facts)
    {
        var placement = facts.Placement.Normalize();
        var localWorkArea = facts.Monitor.LocalWorkAreaDip;
        var normalTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                placement.VisualIndex,
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount)
            : 0;
        var masterTop = placement.IsPlaced
            ? EdgeCapsuleLayout.TopForIndex(
                0,
                facts.QueueStartTopMarginDip,
                localWorkArea,
                placement.SlotCount)
            : normalTop;
        return new EdgeCapsuleLayoutSnapshot(
            facts.Monitor,
            facts.Edge,
            normalTop,
            masterTop,
            facts.RestingWidthDip,
            facts.MaximumCloseWidthDip,
            facts.HostWidthDip,
            facts.HeightDip,
            facts.CloseSegmentActsAsContent);
    }

    public static double TopForVisualIndex(
        MonitorGeometry monitor,
        int visualIndex,
        int slotCount,
        double queueStartTopMarginDip) =>
        EdgeCapsuleLayout.TopForIndex(
            visualIndex,
            queueStartTopMarginDip,
            monitor.LocalWorkAreaDip,
            slotCount);
}
