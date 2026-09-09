using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private static readonly TimeSpan StartupDisplayTopologyPollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StartupDisplayTopologySettleTimeout = TimeSpan.FromSeconds(5);
    private const int StartupDisplayTopologyReadySamples = 2;

    internal async Task WaitForStartupDisplayTopologyAsync()
    {
        if (!HasPaperGeometryOutsideConnectedWorkAreas())
        {
            return;
        }

        // During Windows logon the primary monitor can be enumerable a little before a secondary
        // monitor. Starting the normal off-screen rescue against that transient topology would
        // permanently overwrite the secondary-screen X/Y in data.json. Only the ambiguous case
        // waits; ordinary startup continues immediately, and timeout keeps the existing unplugged-
        // monitor rescue behavior intact.
        var deadline = DateTimeOffset.UtcNow + StartupDisplayTopologySettleTimeout;
        var readySamples = 0;
        while (!IsExiting && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(StartupDisplayTopologyPollInterval);
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();

            if (HasPaperGeometryOutsideConnectedWorkAreas())
            {
                readySamples = 0;
                continue;
            }

            if (++readySamples >= StartupDisplayTopologyReadySamples)
            {
                return;
            }
        }

        // The existing StartAsync rescue remains the authority after timeout. Make sure it reads
        // a fresh monitor snapshot rather than the incomplete topology that triggered this wait.
        WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
    }

    private bool HasPaperGeometryOutsideConnectedWorkAreas()
    {
        var workAreas = ConnectedStartupWorkAreas();

        foreach (var paper in State.Papers)
        {
            // Deep-capsule placement is owned by its persisted queue/monitor identity, not ordinary
            // PaperData X/Y. Do not delay startup because of a parked deep-capsule paper window.
            if (paper.IsCollapsed &&
                State.UseCapsuleMode &&
                State.UseDeepCapsuleMode &&
                CanPaperDisplayAsCapsule(paper))
            {
                continue;
            }

            if (!IsFinite(paper.X) ||
                !IsFinite(paper.Y) ||
                !IsFinite(paper.Width) ||
                !IsFinite(paper.Height) ||
                paper.Width <= 0 ||
                paper.Height <= 0)
            {
                // Invalid geometry is unambiguous corruption; let the normal rescue fix it now.
                continue;
            }

            // Treat the paper center as its monitor ownership point. A paper that only clips the
            // primary screen by a few pixels can still belong to a secondary monitor that Windows
            // has not enumerated yet; using any rectangle intersection would miss that #187 path.
            // Conversely, an ordinary paper whose center is on a live monitor should not pay the
            // startup wait merely because part of its window is intentionally near/off an edge.
            var center = new Point(
                paper.X + (paper.Width / 2.0),
                paper.Y + (paper.Height / 2.0));
            if (workAreas.Exists(area => area.Contains(center)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static List<Rect> ConnectedStartupWorkAreas()
    {
        var result = new List<Rect>();
        foreach (var monitor in WindowWorkAreaHelper.ConnectedMonitorGeometries())
        {
            var area = WindowWorkAreaHelper.WorkAreaForDevice(monitor.DeviceName);
            if (!area.HasValue || area.Value.IsEmpty || area.Value.Width <= 0 || area.Value.Height <= 0)
            {
                continue;
            }

            result.Add(area.Value);
        }

        return result;
    }
}
