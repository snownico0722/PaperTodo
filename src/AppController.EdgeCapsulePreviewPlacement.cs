namespace PaperTodo;

public sealed partial class AppController
{
    internal bool CanKeepEdgeCapsulePreviewAtCurrentTop(
        PaperData paper,
        double previewHeightDip)
    {
        if (!double.IsFinite(previewHeightDip) ||
            previewHeightDip <= 0 ||
            !_windows.TryGetValue(paper.Id, out var window) ||
            !window.TryGetEdgeCapsuleAppliedGeometry(out var geometry) ||
            geometry.Bounds.IsEmpty)
        {
            return false;
        }

        var probe = new DeviceScreenPoint(
            geometry.Bounds.Left,
            geometry.Bounds.Top);
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(
                probe,
                out var monitor))
        {
            return false;
        }

        // Use the already-applied host DPI rather than a fresh monitor-only DPI estimate. During
        // mixed-DPI queue browsing the HWND is the authority for the physical size Windows is
        // actually presenting. The target card alone decides whether pointer anchoring is viable;
        // followers are allowed to extend below the work area by the normal queue policy.
        var scaleY = double.IsFinite(geometry.DpiScaleY)
            ? Math.Max(1, geometry.DpiScaleY)
            : Math.Max(1, monitor.DpiScaleY);
        var previewHeightDevice = Math.Max(
            1,
            (int)Math.Ceiling(previewHeightDip * scaleY));
        var bottomMarginDevice = Math.Max(
            0,
            (int)Math.Ceiling(EdgeCapsuleLayout.TopMargin * scaleY));
        return geometry.Bounds.Top + previewHeightDevice <=
            monitor.WorkArea.Bottom - bottomMarginDevice;
    }
}

internal static class EdgeCapsulePreviewViewportPolicy
{
    public static bool CanKeepCurrentTop(
        PaperData paper,
        double previewHeightDip)
    {
        var controller = AppController.Current;
        return controller != null &&
            controller.CanKeepEdgeCapsulePreviewAtCurrentTop(
                paper,
                previewHeightDip);
    }
}
