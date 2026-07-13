namespace PaperTodo;

/// <summary>
/// Immutable queue placement supplied by the queue coordinator. A presenter never infers its
/// index or stack size from neighbouring windows.
/// </summary>
internal readonly record struct EdgeCapsulePlacement(
    int Index,
    int VisualOffset,
    int SlotCount)
{
    public static EdgeCapsulePlacement None => new(-1, 0, 1);
    public int VisualIndex => Index + VisualOffset;
    public bool IsPlaced => Index >= 0;
    public EdgeCapsulePlacement Normalize() => IsPlaced
        ? new EdgeCapsulePlacement(Math.Max(0, Index), Math.Max(0, VisualOffset), Math.Max(1, SlotCount))
        : None;
}

internal readonly record struct EdgeCapsuleGeometryInput(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    double TopDip,
    double RestingWidthDip,
    double CloseWidthDip,
    double HeightDip);

internal readonly record struct EdgeCapsuleGeometryResult(
    DeviceScreenRect Bounds,
    int RestingWidthDevice,
    int WallDeviceX,
    double DpiScaleX,
    double DpiScaleY);

internal readonly record struct EdgeCapsuleVerticalEdges(int Top, int Bottom);

/// <summary>
/// The only physical-pixel geometry calculator for docked edge capsules. It has no WPF visual,
/// window or model dependency: the same input always returns the same rectangle.
/// </summary>
internal static class EdgeCapsuleGeometry
{
    public static EdgeCapsuleGeometryResult Calculate(EdgeCapsuleGeometryInput input)
    {
        var scaleX = Math.Max(1, input.Monitor.DpiScaleX);
        var scaleY = Math.Max(1, input.Monitor.DpiScaleY);
        var restingWidthDip = Math.Max(1, input.RestingWidthDip);
        var closeWidthDip = Math.Max(0, input.CloseWidthDip);
        var width = Math.Max(1, RoundDevice((restingWidthDip + closeWidthDip) * scaleX));
        var restingWidth = Math.Max(1, RoundDevice(restingWidthDip * scaleX));
        var height = Math.Max(1, RoundDevice(Math.Max(1, input.HeightDip) * scaleY));
        var top = input.Monitor.WorkArea.Top + RoundDevice(input.TopDip * scaleY);
        var wall = input.Edge == EdgeCapsuleEdge.Left
            ? input.Monitor.WorkArea.Left
            : input.Monitor.WorkArea.Right;
        var left = input.Edge == EdgeCapsuleEdge.Left
            ? wall
            : wall - width;

        return new EdgeCapsuleGeometryResult(
            new DeviceScreenRect(left, top, left + width, top + height),
            restingWidth,
            wall,
            scaleX,
            scaleY);
    }

    public static double CloseWidthForAppliedDeviceWidth(
        int appliedWidth,
        int restingWidth,
        double dpiScaleX,
        double maximumCloseWidthDip)
    {
        var width = (appliedWidth - restingWidth) / Math.Max(1, dpiScaleX);
        return Math.Clamp(width, 0, Math.Max(0, maximumCloseWidthDip));
    }

    public static EdgeCapsuleVerticalEdges CalculateVerticalEdges(
        MonitorGeometry monitor,
        double topDip,
        double heightDip)
    {
        var scaleY = Math.Max(1, monitor.DpiScaleY);
        var top = monitor.WorkArea.Top + RoundDevice(topDip * scaleY);
        var height = Math.Max(1, RoundDevice(Math.Max(1, heightDip) * scaleY));
        return new EdgeCapsuleVerticalEdges(top, top + height);
    }

    private static int RoundDevice(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
