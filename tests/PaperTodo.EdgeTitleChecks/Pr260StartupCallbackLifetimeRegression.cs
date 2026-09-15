using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

internal static partial class Program
{
    internal static void RunPr260StartupCallbackLifetimeRegression(bool expectDefect)
    {
        Check(NativeInputGetCursorPos(out var original),
            "Startup-callback lifetime regression requires an interactive desktop cursor");
        Check((NativeInputGetAsyncKeyState(1) & 0x8000) == 0 &&
            (NativeInputGetAsyncKeyState(2) & 0x8000) == 0,
            "Startup-callback lifetime regression starts with physical mouse buttons released");

        try
        {
            using var fixture = new NativeInputHostFixture(withPeer: true, topDip: 260);
            var retired = Pr260ReleaseOneSourceAndDropFixturePredecessor(fixture);

            var aliveAfterCollection = Pr260ForceCollectionAndCheck(retired);
            if (expectDefect)
            {
                Check(aliveAfterCollection,
                    "Pinned #260 must reproduce the defect: the live successor startup callback strongly retains its retired predecessor");
            }
            else
            {
                Check(!aliveAfterCollection,
                    "A live selective successor must not keep its retired predecessor alive after startup validation completes");
            }

            // Keep proving that collection does not come from prematurely destroying the current
            // generation. Its peer source and native output remain live until normal final release.
            fixture.AssertPeerRetained(fixture.CheckBox);
            fixture.CompleteRemainingPeer();
            Console.WriteLine(
                $"RESULT pr260-startup-callback-lifetime expectDefect={expectDefect} " +
                $"retiredAliveAfterGc={aliveAfterCollection} releaseCount={fixture.ReleaseCount}");
        }
        finally
        {
            NativeInputSend(0, 0, 0x0004);
            NativeInputMove(new DeviceScreenPoint(original.X, original.Y));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Pr260ReleaseOneSourceAndDropFixturePredecessor(
        NativeInputHostFixture fixture)
    {
        fixture.RetainAwayFromControl();
        fixture.PausePointerSampling();
        var retired = new WeakReference(fixture.Proxy);

        // Use the production retained sampler/selective-handoff path. This helper deliberately
        // seeds the already-reconciled pointer state so the lifetime test is independent of H2;
        // it tests only what the successor retains after its synchronous startup has completed.
        fixture.ObserveSettledPointerEntry(fixture.CheckBox);
        NativeInputUntil(() => fixture.Released,
            "Selective handoff publishes the peer-only successor before lifetime observation");
        fixture.ThrowIfFailed();
        Check(fixture.ReleaseCount == 1,
            "Exactly one source was selectively returned to real WPF ownership");

        // NativeInputHostFixture keeps this reference only so other assertions can compare the two
        // generations. Remove that test-only root; production controller mappings already point at
        // the successor. Any remaining predecessor lifetime now comes from production references.
        var originalGeneration = typeof(NativeInputHostFixture).GetField(
            "_originalGeneration", BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Native fixture original-generation field is unavailable");
        originalGeneration.SetValue(fixture, null);
        return retired;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Pr260ForceCollectionAndCheck(WeakReference reference)
    {
        for (var attempt = 0; attempt < 4 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            NativeInputPumpFor(10);
        }
        return reference.IsAlive;
    }
}

internal static class Pr260StartupCallbackLifetimeProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var expectDefect = args.Contains(
                "--expect-startup-callback-retention", StringComparer.Ordinal);
            Program.RunPr260StartupCallbackLifetimeRegression(expectDefect);
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
