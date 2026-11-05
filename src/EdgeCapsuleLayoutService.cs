namespace PaperTodo;

internal readonly record struct EdgeCapsuleLayoutFacts(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    EdgeCapsulePlacement Placement,
    double QueueStartTopMarginDip,
    double GapDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HeightDip,
    double PreviewWidthDip,
    double PreviewHeightDip,
    bool CloseSegmentActsAsContent,
    double RestingContentOpacity,
    double? ForcedContentOpacity,
    double HostCapacityWidthDip = 0,
    double HostCapacityHeightDip = 0,
    double ExpandedWidthDip = 0,
    bool HideRestingTitle = false,
    bool PreserveFreeQueueAnchor = false,
    double MasterExtraHeightDip = 0);

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
        var extra = placement.VisualOffset > 0 ? facts.MasterExtraHeightDip : 0;
        double Top(int index) => facts.PreserveFreeQueueAnchor
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(index, facts.QueueStartTopMarginDip, localWorkArea, facts.GapDip, extra)
            : EdgeCapsuleLayout.TopForIndex(index, facts.QueueStartTopMarginDip, localWorkArea, placement.SlotCount, facts.GapDip, extra);
        var normalTop = placement.IsPlaced ? Top(placement.VisualIndex) + placement.TopOffsetDip : 0;
        var masterTop = placement.IsPlaced ? Top(0) : normalTop;
        return new EdgeCapsuleLayoutSnapshot(
            facts.Monitor,
            facts.Edge,
            normalTop,
            masterTop,
            facts.RestingWidthDip,
            facts.MaximumCloseWidthDip,
            facts.HeightDip,
            facts.PreviewWidthDip,
            facts.PreviewHeightDip,
            facts.CloseSegmentActsAsContent,
            facts.RestingContentOpacity,
            facts.ForcedContentOpacity,
            facts.HostCapacityWidthDip,
            facts.HostCapacityHeightDip,
            facts.ExpandedWidthDip,
            facts.HideRestingTitle);
    }

    public static double TopForVisualIndex(
        MonitorGeometry monitor,
        int visualIndex,
        int slotCount,
        double queueStartTopMarginDip,
        double gapDip,
        bool preserveFreeQueueAnchor = false, double masterExtraHeightDip = 0) =>
        preserveFreeQueueAnchor
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(visualIndex, queueStartTopMarginDip, monitor.LocalWorkAreaDip, gapDip, masterExtraHeightDip)
            : EdgeCapsuleLayout.TopForIndex(visualIndex, queueStartTopMarginDip, monitor.LocalWorkAreaDip, slotCount, gapDip, masterExtraHeightDip);
}
