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

internal readonly record struct EdgeCapsuleFloatingHandoffGeometry(
    DeviceScreenRect HostStartBounds,
    DeviceScreenRect HostTargetBounds,
    DeviceScreenRect SurfaceStartBounds,
    DeviceScreenRect SurfaceTargetBounds)
{
    public bool IsUsable =>
        !HostStartBounds.IsEmpty &&
        !HostTargetBounds.IsEmpty &&
        !SurfaceStartBounds.IsEmpty &&
        !SurfaceTargetBounds.IsEmpty;
}

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
    /// Docking anchor whose wall-side chrome margin finishes just outside the work area, while its
    /// interior edge matches the docked surface. This may be narrower than a FloatingFree window;
    /// use <see cref="FloatingHandoffGeometry"/> to keep native capacity separate from that surface.
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

    /// <summary>
    /// Builds a fixed-capacity native flight and a separate visible-surface flight. The HWND keeps
    /// the larger endpoint capacity while the FloatingFree visual can reflow to the exact docking
    /// anchor inside it, so shrinking the pill never clips it at the native window boundary.
    /// </summary>
    public static EdgeCapsuleFloatingHandoffGeometry FloatingHandoffGeometry(
        DeviceScreenRect startSurfaceBounds,
        DeviceScreenRect dockingAnchorBounds,
        EdgeCapsuleEdge edge)
    {
        if (startSurfaceBounds.IsEmpty || dockingAnchorBounds.IsEmpty)
        {
            return default;
        }

        var hostWidth = Math.Max(startSurfaceBounds.Width, dockingAnchorBounds.Width);
        var hostHeight = Math.Max(startSurfaceBounds.Height, dockingAnchorBounds.Height);
        var hostStartLeft = edge == EdgeCapsuleEdge.Left
            ? startSurfaceBounds.Left
            : startSurfaceBounds.Right - hostWidth;
        var hostTargetLeft = edge == EdgeCapsuleEdge.Left
            ? dockingAnchorBounds.Left
            : dockingAnchorBounds.Right - hostWidth;
        var hostStartTop = startSurfaceBounds.Top - RoundDevice(
            (hostHeight - startSurfaceBounds.Height) / 2.0);
        var hostTargetTop = dockingAnchorBounds.Top - RoundDevice(
            (hostHeight - dockingAnchorBounds.Height) / 2.0);
        return new EdgeCapsuleFloatingHandoffGeometry(
            new DeviceScreenRect(
                hostStartLeft,
                hostStartTop,
                hostStartLeft + hostWidth,
                hostStartTop + hostHeight),
            new DeviceScreenRect(
                hostTargetLeft,
                hostTargetTop,
                hostTargetLeft + hostWidth,
                hostTargetTop + hostHeight),
            startSurfaceBounds,
            dockingAnchorBounds);
    }

    public static DeviceScreenRect InterpolateDeviceBounds(
        DeviceScreenRect start,
        DeviceScreenRect target,
        double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var left = LerpDevice(start.Left, target.Left, progress);
        var top = LerpDevice(start.Top, target.Top, progress);
        var width = LerpDevice(start.Width, target.Width, progress);
        var height = LerpDevice(start.Height, target.Height, progress);
        return new DeviceScreenRect(
            left,
            top,
            left + width,
            top + height);
    }

    public static bool DeviceBoundsMatch(
        DeviceScreenRect first,
        DeviceScreenRect second,
        int tolerance) =>
        Math.Abs(first.Left - second.Left) <= tolerance &&
        Math.Abs(first.Top - second.Top) <= tolerance &&
        Math.Abs(first.Right - second.Right) <= tolerance &&
        Math.Abs(first.Bottom - second.Bottom) <= tolerance;

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
