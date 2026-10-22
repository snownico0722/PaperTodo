namespace PaperTodo;

// Compare actual HWND bounds in physical pixels. A small usable rectangle still counts as
// accessible; never snap an intentionally partly off-screen paper back into place.
internal static class ExpandedPaperRecoveryPolicy
{
    internal static bool HasReachablePixels(
        DeviceScreenRect paper,
        IReadOnlyList<MonitorGeometry> monitors)
    {
        if (paper.IsEmpty)
            return false;

        foreach (var monitor in monitors)
        {
            var area = monitor.WorkArea;
            if (area.IsEmpty)
                continue;

            var width = Math.Min(paper.Right, area.Right) -
                Math.Max(paper.Left, area.Left);
            var height = Math.Min(paper.Bottom, area.Bottom) -
                Math.Max(paper.Top, area.Top);
            var minWidth = Math.Min(paper.Width,
                Math.Max(1, (int)Math.Ceiling(48 * monitor.DpiScaleX)));
            var minHeight = Math.Min(paper.Height,
                Math.Max(1, (int)Math.Ceiling(48 * monitor.DpiScaleY)));
            if (width >= minWidth && height >= minHeight)
                return true;
        }
        return false;
    }
}
