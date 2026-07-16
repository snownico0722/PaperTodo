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
    DeviceScreenRect InteractiveBounds,
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

        var bounds = new DeviceScreenRect(left, top, left + width, top + height);
        return new EdgeCapsuleGeometryResult(
            bounds,
            InteractiveBoundsForAppliedBounds(
                bounds,
                input.Edge,
                scaleX,
                scaleY,
                EdgeCapsuleLayout.WindowChromeMargin),
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

    public static DeviceScreenRect InteractiveBoundsForAppliedBounds(
        DeviceScreenRect bounds,
        EdgeCapsuleEdge edge,
        double dpiScaleX,
        double dpiScaleY,
        double chromeMarginDip)
    {
        if (bounds.IsEmpty)
        {
            return default;
        }

        var horizontalMargin = Math.Max(0, RoundDevice(chromeMarginDip * Math.Max(1, dpiScaleX)));
        var verticalMargin = Math.Max(0, RoundDevice(chromeMarginDip * Math.Max(1, dpiScaleY)));
        var left = edge == EdgeCapsuleEdge.Left
            ? bounds.Left
            : Math.Min(bounds.Right, bounds.Left + horizontalMargin);
        var right = edge == EdgeCapsuleEdge.Left
            ? Math.Max(bounds.Left, bounds.Right - horizontalMargin)
            : bounds.Right;
        var top = Math.Min(bounds.Bottom, bounds.Top + verticalMargin);
        var bottom = Math.Max(top, bounds.Bottom - verticalMargin);
        return new DeviceScreenRect(left, top, right, bottom);
    }

    public static bool Contains(DeviceScreenRect bounds, DeviceScreenPoint point) =>
        !bounds.IsEmpty &&
        point.X >= bounds.Left &&
        point.X < bounds.Right &&
        point.Y >= bounds.Top &&
        point.Y < bounds.Bottom;

    /// <summary>
    /// Keeps the transparent composition host pinned to the same wall and vertical frame as the
    /// visible capsule. Capacity comes from the fully expanded target and does not follow hover
    /// width, so a horizontal transition never moves or resizes the HWND.
    /// </summary>
    public static DeviceScreenRect HostBoundsForVisibleBounds(
        DeviceScreenRect visibleBounds,
        EdgeCapsuleEdge edge,
        int wallDeviceX,
        int hostWidthDevice)
    {
        if (visibleBounds.IsEmpty || hostWidthDevice <= 0)
        {
            return default;
        }

        var left = edge == EdgeCapsuleEdge.Left
            ? wallDeviceX
            : wallDeviceX - hostWidthDevice;
        return new DeviceScreenRect(
            left,
            visibleBounds.Top,
            left + hostWidthDevice,
            visibleBounds.Bottom);
    }

    /// <summary>
    /// Final floating-HWND rectangle whose pill body matches the docked tag. Its symmetric
    /// wall-side chrome margin finishes just outside the work area, while the interior edge stays
    /// aligned with the docked surface for a gap-free visual hand-off.
    /// </summary>
    public static DeviceScreenRect FloatingHandoffBoundsForDockedBounds(
        DeviceScreenRect dockedBounds,
        EdgeCapsuleEdge edge,
        double dpiScaleX,
        double chromeMarginDip)
    {
        if (dockedBounds.IsEmpty)
        {
            return default;
        }

        var wallMargin = Math.Max(0, RoundDevice(
            Math.Max(0, chromeMarginDip) * Math.Max(1, dpiScaleX)));
        return edge == EdgeCapsuleEdge.Left
            ? new DeviceScreenRect(
                dockedBounds.Left - wallMargin,
                dockedBounds.Top,
                dockedBounds.Right,
                dockedBounds.Bottom)
            : new DeviceScreenRect(
                dockedBounds.Left,
                dockedBounds.Top,
                dockedBounds.Right + wallMargin,
                dockedBounds.Bottom);
    }

    public static DeviceScreenRect InterpolateDeviceBounds(
        DeviceScreenRect start,
        DeviceScreenRect target,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return new DeviceScreenRect(
            LerpDevice(start.Left, target.Left, progress),
            LerpDevice(start.Top, target.Top, progress),
            LerpDevice(start.Right, target.Right, progress),
            LerpDevice(start.Bottom, target.Bottom, progress));
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

    private static int LerpDevice(int from, int to, double progress) =>
        RoundDevice(from + (to - from) * progress);
}
