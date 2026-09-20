using System.Diagnostics;
using PaperTodo;

internal static partial class Program
{
    private static void Routes12R5TimerDiagnostics()
    {
        const string route = "route1-r5-native-timer-diagnostics";
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), route + ": monitor available");
        var bounds = new DeviceScreenRect(
            monitor.WorkArea.Left + 620,
            monitor.WorkArea.Top + 180,
            monitor.WorkArea.Left + 900,
            monitor.WorkArea.Top + 520);
        var initial = new DeviceScreenRect(bounds.Left + 20, bounds.Top + 20, bounds.Left + 68, bounds.Top + 68);
        var target = new DeviceScreenRect(initial.Left, initial.Top + 120, initial.Right, initial.Bottom + 120);

        using var pair = EdgeCapsuleQueueProxyWindow.TryCreate(
            bounds,
            true,
            static _ => false,
            static _ => { },
            static () => { },
            static () => { },
            static () => { });
        Check(pair != null, route + ": production queue pair created");
        Check(pair!.TrySetInputRegions([initial]) && pair.Show(bounds, true), route + ": initial region shown");

        var startFrame = Routes12R3Frame(initial);
        var targetFrame = Routes12R3Frame(target);
        var member = new EdgeCapsuleQueueProxyMemberPlan("r5-timer", startFrame, startFrame, targetFrame);
        var startedAt = Stopwatch.GetTimestamp();
        const int durationMilliseconds = 700;
        var ticket = new EdgeCapsuleQueueInputAnimationTicket(startedAt, durationMilliseconds, [member]);

        var directLater = ticket.Sample(startedAt + (long)(Stopwatch.Frequency * 0.20));
        Check(directLater.Length == 1 && directLater[0].Top >= initial.Top + 40,
            route + ": immutable ticket itself advances without WPF state");
        var baseline = pair.InputRegionUpdateCount;
        Check(pair.TryStartInputAnimation(ticket), route + ": dedicated input animation starts");
        var afterStart = pair.InputRegionUpdateCount;
        Thread.Sleep(220);
        var afterAutomatic = pair.InputRegionUpdateCount;
        var automaticSpan = Routes12CaptureInputSpan(pair.InputHandle, bounds, initial.Left + 24, vertical: true);

        // If automatic WM_TIMER delivery is the failing link, inject the same message manually.
        // This does not claim a fix; it distinguishes timer delivery from ticket sampling/SetWindowRgn.
        var beforeManual = pair.InputRegionUpdateCount;
        for (var i = 0; i < 5; i++)
        {
            Check(Routes12R3PostMessage(pair.InputHandle, 0x0113, new IntPtr(1), IntPtr.Zero),
                route + ": manual WM_TIMER post accepted");
            Thread.Sleep(18);
        }
        var afterManual = pair.InputRegionUpdateCount;
        var manualSpan = Routes12CaptureInputSpan(pair.InputHandle, bounds, initial.Left + 24, vertical: true);

        Console.WriteLine(
            $"R5_TIMER_DIAG baseline={baseline} afterStart={afterStart} afterAutomatic={afterAutomatic} " +
            $"beforeManual={beforeManual} afterManual={afterManual} automaticSpan={automaticSpan} manualSpan={manualSpan} " +
            $"active={pair.IsInputAnimationActive}");

        Check(afterStart > baseline, route + ": synchronous activation publishes the first ticket sample");
        // Do not fail on automatic delivery here; the production-boundary check remains the gate.
        // The diagnostic line above is preserved even when that later gate fails.
        pair.Hide();
    }
}
