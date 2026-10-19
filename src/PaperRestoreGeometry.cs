using System.Windows;

namespace PaperTodo;

// Restore position is always physical. WPF layout sizes use the destination monitor's DPI.
internal readonly record struct PaperRestoreGeometry(
    DeviceScreenRect Bounds, double DpiScaleX, double DpiScaleY)
{
    public double WidthDip => Bounds.Width / DpiScaleX;
    public double HeightDip => Bounds.Height / DpiScaleY;

    internal static bool TryGetDeviceBounds(Rect savedDip, double savedScale, out DeviceScreenRect bounds)
    {
        bounds = default;
        if (savedDip.IsEmpty || !double.IsFinite(savedScale) || savedScale <= 0)
        {
            return false;
        }

        var left = Math.Round(savedDip.Left * savedScale);
        var top = Math.Round(savedDip.Top * savedScale);
        var right = Math.Round(savedDip.Right * savedScale);
        var bottom = Math.Round(savedDip.Bottom * savedScale);
        if (!ValidEdge(left) || !ValidEdge(top) || !ValidEdge(right) || !ValidEdge(bottom) ||
            right <= left || bottom <= top || right - left > int.MaxValue || bottom - top > int.MaxValue)
        {
            return false;
        }

        bounds = new DeviceScreenRect((int)left, (int)top, (int)right, (int)bottom);
        return true;
    }

    internal static PaperRestoreGeometry ClampToMonitor(
        DeviceScreenRect remembered, double widthDip, double heightDip, MonitorGeometry monitor)
    {
        var area = monitor.WorkArea;
        var marginX = (int)Math.Ceiling(8 * monitor.DpiScaleX);
        var marginY = (int)Math.Ceiling(8 * monitor.DpiScaleY);
        var minWidth = Math.Ceiling(PaperLayoutDefaults.MinWidth * monitor.DpiScaleX);
        var minHeight = Math.Ceiling(PaperLayoutDefaults.MinHeight * monitor.DpiScaleY);
        var width = (int)Math.Clamp(Math.Round(widthDip * monitor.DpiScaleX),
            minWidth, Math.Max(minWidth, area.Width - 2 * marginX));
        var height = (int)Math.Clamp(Math.Round(heightDip * monitor.DpiScaleY),
            minHeight, Math.Max(minHeight, area.Height - 2 * marginY));
        var left = Math.Clamp(remembered.Left, area.Left + marginX,
            Math.Max(area.Left + marginX, area.Right - width - marginX));
        var top = Math.Clamp(remembered.Top, area.Top + marginY,
            Math.Max(area.Top + marginY, area.Bottom - height - marginY));
        return new PaperRestoreGeometry(new DeviceScreenRect(left, top, left + width, top + height),
            monitor.DpiScaleX, monitor.DpiScaleY);
    }

    private static bool ValidEdge(double value)
        => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;
}
