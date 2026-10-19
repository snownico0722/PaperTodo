using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    internal async Task WaitForStartupDisplayTopologyAsync()
    {
        if (!HasPaperGeometryOutsideConnectedWorkAreas()) return;

        // A late secondary display is not the same as a removed display. Preserve saved
        // coordinates until enumeration settles; never save a temporary primary-screen rescue.
        var deadline = Environment.TickCount64 + 5000;
        var readySamples = 0;
        while (!IsExiting && Environment.TickCount64 < deadline)
        {
            await Task.Delay(150);
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            if (HasPaperGeometryOutsideConnectedWorkAreas()) readySamples = 0;
            else if (++readySamples >= 2) return;
        }
        // A genuinely disconnected monitor still uses the existing bounded rescue afterwards.
        WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
    }

    private bool HasPaperGeometryOutsideConnectedWorkAreas()
    {
        var workAreas = WindowWorkAreaHelper.ConnectedMonitorGeometries()
            .Select(monitor => WindowWorkAreaHelper.WorkAreaForDevice(monitor.DeviceName))
            .Where(area => area.HasValue && !area.Value.IsEmpty && area.Value.Width > 0 && area.Value.Height > 0)
            .Select(area => area!.Value).ToList();
        foreach (var paper in State.Papers)
        {
            if (paper.IsCollapsed && State.UseCapsuleMode && State.UseDeepCapsuleMode &&
                CanPaperDisplayAsCapsule(paper)) continue;
            if (!IsFinite(paper.X) || !IsFinite(paper.Y) || !IsFinite(paper.Width) ||
                !IsFinite(paper.Height) || paper.Width <= 0 || paper.Height <= 0) continue;
            var center = new Point(paper.X + paper.Width / 2, paper.Y + paper.Height / 2);
            if (!workAreas.Any(area => area.Contains(center))) return true;
        }
        return false;
    }
}
