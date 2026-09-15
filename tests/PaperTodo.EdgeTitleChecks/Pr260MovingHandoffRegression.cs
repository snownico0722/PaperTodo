using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using PaperTodo;

internal static partial class Program
{
    internal static void RunPr260MovingHandoffRegression(bool expectDefect)
    {
        Check(NativeInputGetCursorPos(out var original),
            "Moving-handoff regression requires an interactive desktop cursor");
        Check((NativeInputGetAsyncKeyState(1) & 0x8000) == 0 &&
            (NativeInputGetAsyncKeyState(2) & 0x8000) == 0,
            "Moving-handoff regression starts with physical mouse buttons released");

        try
        {
            using var fixture = new NativeInputHostFixture(withPeer: true);
            fixture.RetainAwayFromControl();
            fixture.PausePointerSampling();

            var targetWindow = Pr260WindowFor(fixture);
            var control = fixture.CheckBox;
            var movingPoints = new[]
            {
                Pr260PointInside(control, 0.30),
                Pr260PointInside(control, 0.50),
                Pr260PointInside(control, 0.70)
            };
            var movingSamples = 0;
            var stationarySamples = 0;
            var releasedDuringMovement = false;
            var filteredNoOpSamples = 0;

            foreach (var point in movingPoints)
            {
                NativeInputMove(point);
                NativeInputPumpFor(8);
                var proxy = fixture.Proxy;
                var dirtiedWindows = new List<PaperWindow>();
                foreach (var member in proxy.Members)
                {
                    if (Pr260NeedsPresenterPointerReconcile(member.Window, proxy, point))
                    {
                        // NativeInputHostFixture deliberately owns only the presentation/native
                        // adapter, not a fully started AppController. Keep the real PaperWindow ->
                        // Presenter dirty/reconcile path, but hold the controller notification batch
                        // exactly as a visual transaction does until the Presenter has settled.
                        // Otherwise the focused harness crashes in unrelated preview activation
                        // code before it can observe H2's ordering.
                        Pr260DeferControllerNotifications(member.Window, true);
                        dirtiedWindows.Add(member.Window);
                        Pr260InvalidateLocalPointer(member.Window);
                    }
                    else
                    {
                        filteredNoOpSamples++;
                    }
                }
                movingSamples++;

                // This is the part of OnSampleTimerTick that matters for H2, deliberately isolated
                // from the fixture's intentionally uninitialised AppController. It still uses the
                // production Presenter dirty path, production settled predicate and production
                // selective-release delegate. Baseline has no filter helper and therefore dirties
                // every member on every changed coordinate, exactly like #260.
                if (proxy.ShouldReleaseForPointerInput(point))
                {
                    proxy.SettledInputRequested?.Invoke(proxy, targetWindow);
                }
                fixture.ThrowIfFailed();

                if (fixture.ReleaseCount == 1)
                {
                    releasedDuringMovement = true;
                    foreach (var window in dirtiedWindows)
                        Pr260DeferControllerNotifications(window, false);
                    break;
                }

                NativeInputUntil(
                    () => proxy.Members.All(member => member.Window.IsEdgeCapsuleQueueProxyInputSettled),
                    $"Moving sample {movingSamples}: production Presenter work settles before the next coordinate",
                    () => $"releaseCount={fixture.ReleaseCount} cloaked={NativeInputIsCloaked(fixture.Host.Handle)}");
                foreach (var window in dirtiedWindows)
                    Pr260DeferControllerNotifications(window, false);
                fixture.ThrowIfFailed();
                Check(fixture.ReleaseCount == 0,
                    "No hidden timer tick releases the source between explicit moving samples");
            }

            if (expectDefect)
            {
                Check(!releasedDuringMovement && fixture.ReleaseCount == 0,
                    "Pinned #260 must reproduce the defect: each changed coordinate manufactures fresh Pointer work");

                // A later unchanged proxy sample does not dispatch pointer invalidation. Model that
                // exact old escape hatch by running only the settled release test/delegate.
                while (fixture.ReleaseCount == 0 && stationarySamples < 4)
                {
                    var proxy = fixture.Proxy;
                    var point = movingPoints[^1];
                    if (proxy.ShouldReleaseForPointerInput(point))
                    {
                        proxy.SettledInputRequested?.Invoke(proxy, targetWindow);
                    }
                    stationarySamples++;
                    fixture.ThrowIfFailed();
                    if (fixture.ReleaseCount == 0)
                    {
                        NativeInputUntil(
                            () => proxy.Members.All(member => member.Window.IsEdgeCapsuleQueueProxyInputSettled),
                            $"Stationary follow-up {stationarySamples}: stale presentation work settles",
                            () => $"releaseCount={fixture.ReleaseCount} cloaked={NativeInputIsCloaked(fixture.Host.Handle)}");
                    }
                }
                Check(fixture.ReleaseCount == 1 && stationarySamples > 0,
                    "Pinned #260 releases only after a sample that creates no new Pointer dirty work");
                Check(filteredNoOpSamples == 0,
                    "Pinned #260 exposes no queue-proxy no-op pointer filter");
            }
            else
            {
                Check(releasedDuringMovement && fixture.ReleaseCount == 1,
                    "Continuous in-card pointer movement must no longer require a stationary proxy sample before handoff");
                Check(stationarySamples == 0 && filteredNoOpSamples > 0,
                    "The fixed path filters reducer-no-op Pointer work and releases during changed-coordinate input");
            }

            fixture.AssertPeerRetained(control);
            fixture.CompleteRemainingPeer();
            Console.WriteLine(
                $"RESULT pr260-moving-handoff expectDefect={expectDefect} " +
                $"releasedDuringMovement={releasedDuringMovement} movingSamples={movingSamples} " +
                $"stationarySamples={stationarySamples} filteredNoOpSamples={filteredNoOpSamples}");
        }
        finally
        {
            NativeInputSend(0, 0, 0x0004);
            NativeInputMove(new DeviceScreenPoint(original.X, original.Y));
        }
    }

    private static PaperWindow Pr260WindowFor(NativeInputHostFixture fixture)
    {
        var field = typeof(NativeInputHostFixture).GetField(
            "_paperWindow",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Native input fixture PaperWindow is unavailable");
        return field.GetValue(fixture) as PaperWindow ??
            throw new InvalidOperationException("Native input fixture PaperWindow is missing");
    }

    private static bool Pr260NeedsPresenterPointerReconcile(
        PaperWindow window,
        EdgeCapsuleQueueCompositionProxy proxy,
        DeviceScreenPoint pointer)
    {
        var method = typeof(PaperWindow).GetMethod(
            "ShouldInvalidateEdgeCapsuleQueueProxyPointer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null)
        {
            // Pinned #260 unconditionally called InvalidateEdgeCapsulePointer after every changed
            // retained-pointer sample. Absence of the helper is therefore the baseline behaviour.
            return true;
        }
        Check(proxy.TryGetPresentation(window, out var frame),
            "Moving-handoff filter receives the actual retained presentation frame");
        try
        {
            return (bool)(method.Invoke(window, new object?[] { pointer, frame }) ?? true);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Pr260InvalidateLocalPointer(PaperWindow window)
    {
        var method = typeof(PaperWindow).GetMethod(
            "InvalidateEdgeCapsulePointer",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Production local pointer invalidation is unavailable");
        try
        {
            method.Invoke(window, null);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Pr260DeferControllerNotifications(PaperWindow window, bool deferred)
    {
        var field = typeof(PaperWindow).GetField(
            "_edgeCapsuleVisualTransactionNotificationDeferred",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Production reconcile notification deferral is unavailable");
        field.SetValue(window, deferred);
    }

    private static DeviceScreenPoint Pr260PointInside(
        FrameworkElement control,
        double horizontalFraction)
    {
        Check(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0,
            "Moving-handoff control has arranged native geometry");
        var point = control.PointToScreen(new Point(
            control.ActualWidth * horizontalFraction,
            control.ActualHeight * 0.5));
        return DeviceScreenPoint.FromPoint(point);
    }
}

internal static class Pr260MovingHandoffProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            Program.RunPr260MovingHandoffRegression(
                args.Contains("--expect-moving-handoff-defect", StringComparer.Ordinal));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
