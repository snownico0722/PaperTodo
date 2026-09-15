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

    internal static int DownwardBrowseCapacity(
        DeviceScreenRect compactBounds,
        DeviceScreenRect previewBounds,
        int workAreaBottomDevice)
    {
        if (compactBounds.IsEmpty || previewBounds.IsEmpty)
        {
            return 0;
        }

        // Several downward transfers may retain earlier gaps while the next owner remains under
        // the pointer. One preview-height delta is therefore insufficient. Reserve that owner's
        // possible travel across the work area; followers can extend beyond it on the same output.
        var previewGrowth = (long)previewBounds.Height - compactBounds.Height;
        var pointerSideTravel = (long)workAreaBottomDevice - compactBounds.Bottom;
        return (int)Math.Clamp(Math.Max(previewGrowth, pointerSideTravel), 0, int.MaxValue);
    }

    internal static DeviceScreenRect WithDownwardCapacity(
        DeviceScreenRect bounds,
        int downwardShiftDevice)
    {
        if (bounds.IsEmpty || downwardShiftDevice <= 0)
        {
            return bounds;
        }

        var requestedBottom = Math.Min(
            int.MaxValue,
            (long)bounds.Bottom + downwardShiftDevice);
        // Followers may extend below the work area. This queue's no-redirection output is not
        // a WPF bitmap allocation; retain its finite, queue-derived translation capacity instead
        // of clipping it to the work area (which could even shrink a required source envelope).
        return new DeviceScreenRect(
            bounds.Left,
            bounds.Top,
            bounds.Right,
            (int)requestedBottom);
    }
}
