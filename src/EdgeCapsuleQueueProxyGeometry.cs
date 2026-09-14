namespace PaperTodo;

/// <summary>
/// Physical-pixel geometry for the translation-only queue compositor. The
/// output HWND has no redirection bitmap, so it may reserve a bounded queue
/// envelope without allocating another full RGBA WPF surface.
/// </summary>
internal static class EdgeCapsuleQueueProxyGeometry
{
    internal const int OutputOverscanPixels = 4;

    internal static DeviceScreenRect OutputBounds(
        DeviceScreenRect envelope)
    {
        if (envelope.IsEmpty)
        {
            return default;
        }
        return new DeviceScreenRect(
            envelope.Left - OutputOverscanPixels,
            envelope.Top - OutputOverscanPixels,
            envelope.Right + OutputOverscanPixels,
            envelope.Bottom + OutputOverscanPixels);
    }

    internal static bool Contains(
        DeviceScreenRect outer,
        DeviceScreenRect inner) =>
        !outer.IsEmpty &&
        !inner.IsEmpty &&
        inner.Left >= outer.Left &&
        inner.Top >= outer.Top &&
        inner.Right <= outer.Right &&
        inner.Bottom <= outer.Bottom;

    internal static DeviceScreenRect Union(
        DeviceScreenRect first,
        DeviceScreenRect second)
    {
        if (first.IsEmpty)
        {
            return second;
        }
        if (second.IsEmpty)
        {
            return first;
        }
        return new DeviceScreenRect(
            Math.Min(first.Left, second.Left),
            Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right),
            Math.Max(first.Bottom, second.Bottom));
    }

    internal static DeviceScreenRect WithDownwardCapacity(
        DeviceScreenRect bounds,
        int downwardShiftDevice,
        int workAreaBottomDevice)
    {
        if (bounds.IsEmpty || downwardShiftDevice <= 0)
        {
            return bounds;
        }

        var requestedBottom = Math.Min(
            int.MaxValue,
            (long)bounds.Bottom + downwardShiftDevice);
        return new DeviceScreenRect(
            bounds.Left,
            bounds.Top,
            bounds.Right,
            (int)Math.Min(
                requestedBottom,
                workAreaBottomDevice));
    }
}
