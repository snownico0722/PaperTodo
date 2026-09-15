using System.Reflection;
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

            foreach (var point in movingPoints)
            {
                NativeInputMove(point);
                NativeInputPumpFor(8);
                Pr260InvokeSampleTick(fixture.Proxy);
                movingSamples++;
                fixture.ThrowIfFailed();

                if (fixture.ReleaseCount == 1)
                {
                    releasedDuringMovement = true;
                    break;
                }

                // Let the production Presenter consume the Pointer dirty work without allowing
                // another proxy sample. The baseline bug requires another *unchanged* proxy tick
                // after every changed-coordinate tick even though the Presenter has already settled.
                NativeInputUntil(
                    () => fixture._presenter.IsSettledForPreacquisition,
                    $"Moving sample {movingSamples}: the real Presenter settles before the next coordinate",
                    () => $"releaseCount={fixture.ReleaseCount} cloaked={NativeInputIsCloaked(fixture.Host.Handle)}");
                fixture.ThrowIfFailed();
                Check(fixture.ReleaseCount == 0,
                    "No hidden timer tick releases the source between explicit moving samples");
            }

            if (expectDefect)
            {
                Check(!releasedDuringMovement && fixture.ReleaseCount == 0,
                    "Pinned #260 must reproduce the defect: changed coordinates repeatedly prevent selective handoff");

                // Do not move the cursor again. A later unchanged sample is the exact escape hatch
                // in the old implementation: it stops manufacturing Pointer dirty work, so the
                // already-settled source can finally be handed back to WPF.
                while (fixture.ReleaseCount == 0 && stationarySamples < 4)
                {
                    Pr260InvokeSampleTick(fixture.Proxy);
                    stationarySamples++;
                    fixture.ThrowIfFailed();
                    if (fixture.ReleaseCount == 0)
                    {
                        NativeInputUntil(
                            () => fixture._presenter.IsSettledForPreacquisition,
                            $"Stationary follow-up {stationarySamples}: any stale presentation work settles",
                            () => $"releaseCount={fixture.ReleaseCount} cloaked={NativeInputIsCloaked(fixture.Host.Handle)}");
                    }
                }
                Check(fixture.ReleaseCount == 1 && stationarySamples > 0,
                    "Pinned #260 releases only after an unchanged follow-up sample");
            }
            else
            {
                Check(releasedDuringMovement && fixture.ReleaseCount == 1,
                    "Continuous in-card pointer movement must no longer require a stationary proxy sample before handoff");
                Check(stationarySamples == 0,
                    "The fixed path completes selective handoff without injecting a stationary follow-up tick");
            }

            fixture.AssertPeerRetained(control);
            fixture.CompleteRemainingPeer();
            Console.WriteLine(
                $"RESULT pr260-moving-handoff expectDefect={expectDefect} " +
                $"releasedDuringMovement={releasedDuringMovement} movingSamples={movingSamples} " +
                $"stationarySamples={stationarySamples}");
        }
        finally
        {
            NativeInputSend(0, 0, 0x0004);
            NativeInputMove(new DeviceScreenPoint(original.X, original.Y));
        }
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

    private static void Pr260InvokeSampleTick(EdgeCapsuleQueueCompositionProxy proxy)
    {
        var method = typeof(EdgeCapsuleQueueCompositionProxy).GetMethod(
            "OnSampleTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Production proxy sample callback is unavailable");
        try
        {
            method.Invoke(proxy, new object?[] { null, EventArgs.Empty });
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
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
