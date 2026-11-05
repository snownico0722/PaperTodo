namespace PaperTodo;

internal readonly record struct MasterCapsuleQueueTransferSnapshot(
    string SourceQueueKey,
    string SourceMonitorDeviceName,
    EdgeCapsuleEdge SourceEdge,
    string[] PaperIds,
    string[] SourceMonitorAliases,
    bool SourceWasCollapsed,
    bool SourceHadStartTopMargin,
    double SourceStartTopMargin);

internal readonly record struct MasterCapsuleQueueTransferTarget(
    string MonitorDeviceName,
    EdgeCapsuleEdge Edge,
    double StartTopMargin);

internal static class MasterCapsuleQueueTransferPolicy
{
    internal static bool TryResolveTarget(
        DeviceScreenPoint dropPoint,
        string fallbackMonitorDeviceName,
        EdgeCapsuleEdge fallbackEdge,
        int slotCount,
        double gap,
        out MasterCapsuleQueueTransferTarget target)
    {
        target = default;
        MonitorGeometry geometry;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                dropPoint,
                out geometry) &&
            !WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                fallbackMonitorDeviceName,
                out geometry))
        {
            return false;
        }

        var edge = dropPoint.X <
            geometry.WorkArea.Left + geometry.WorkArea.Width / 2.0
                ? EdgeCapsuleEdge.Left
                : EdgeCapsuleEdge.Right;
        if (geometry.WorkArea.IsEmpty)
        {
            edge = fallbackEdge;
        }

        var area = geometry.LocalWorkAreaDip;
        var dropY = geometry.DeviceYToLocalDip(dropPoint.Y);
        var desiredStartTopMargin =
            dropY - area.Top - PaperLayoutDefaults.CapsuleHeight / 2.0;
        var startTopMargin = EdgeCapsuleLayout.NormalizeStartTopMargin(
            desiredStartTopMargin,
            area,
            Math.Max(1, slotCount),
            gap);

        target = new MasterCapsuleQueueTransferTarget(
            WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(geometry.DeviceName),
            edge,
            startTopMargin);
        return true;
    }
}


