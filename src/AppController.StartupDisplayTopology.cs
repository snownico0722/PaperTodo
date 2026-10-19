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
        return HasPaperGeometryOutsideWorkAreas(workAreas);
    }

    private bool HasPaperGeometryOutsideWorkAreas(IReadOnlyList<Rect> workAreas)
    {
        foreach (var paper in State.Papers)
        {
            // Startup rescue visits every paper, including hidden and docked collapsed ones.
            // Their ordinary geometry must be protected before that rescue can write it back.
            if (IsFinite(paper.X) && IsFinite(paper.Y) && IsFinite(paper.Width) &&
                IsFinite(paper.Height) && paper.Width > 0 && paper.Height > 0)
            {
                var center = new Point(paper.X + paper.Width / 2, paper.Y + paper.Height / 2);
                if (!workAreas.Any(area => area.Contains(center))) return true;
            }

            // Expanded papers can keep their latest position separately from ordinary X/Y.
            // Inspect the same eligible snapshot as restore, before it is clamped to a screen.
            if (State.UseDeepCapsuleMode && CanPaperDisplayAsCapsule(paper) &&
                TryGetUnclampedDeepCapsuleExpandedGeometry(
                    paper, paper.Width, paper.Height, out _, out var expandedCenter) &&
                !workAreas.Any(area => area.Contains(expandedCenter))) return true;
        }
        return false;
    }
}
