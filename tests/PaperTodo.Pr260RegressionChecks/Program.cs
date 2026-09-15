using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

// The scheduler is linked from production. This fixture replaces optional logging only;
// Dispatcher, Rendering and timers are the real WPF implementations, not a scheduler model.
internal static class EdgeCapsulePerformanceDiagnostics
{
    internal static void Trace(string message) { }
}

internal static class Program
{
    private sealed record Observation(bool Passed, int Pending, int Sleeping, string Calls,
        int GraphicsCalls, bool? GuardAfterMutation, string Note);
    private sealed record Result(string Name, bool KnownBaselineDefect, Observation? State,
        double HarnessMilliseconds, string? UnexpectedError);
    private static readonly List<Result> Results = new();
    private static readonly string[] Defects =
    [
        "complete-request-B", "complete-cancel-missing-B", "complete-cancel-existing-B",
        "complete-wake-sleeping-B", "readiness-request-B", "graphics-request-B"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        var baseline = args.Contains("--expect-baseline-defects", StringComparer.Ordinal);
        Console.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; Baseline={baseline}");
        Console.WriteLine("Scope: real WPF scheduler/reentrancy. No DWM admission, input-to-pixel or physical-panel measurement.");
        var window = new Window
        {
            Width = 80, Height = 40, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = new Border { Background = Brushes.LightGray }
        };
        try
        {
            window.Show();
            foreach (var name in Defects.Take(4)) Run(name, true, () => CrossQueueCompletion(name));
            Run("readiness-request-B", true, () => BeforeQueuePreparation(false));
            Run("graphics-request-B", true, () => BeforeQueuePreparation(true));
            foreach (var mutation in new[] { "replace-A", "cancel-A", "cancel-all", "disable-reenable", "interaction-nested-quiet", "dispose" })
                Run(mutation, false, () => PreserveInvalidation(mutation));
            Run("deferred-wake", false, DeferredWake);
            Run("fifo-latest-request", false, FifoLatestRequest);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ENVIRONMENT_OR_HARNESS_FAILURE: " + ex);
            return 2;
        }
        finally { window.Close(); }

        var output = Path.GetFullPath("pr260-coordination-results.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Runtime = Environment.Version.ToString(), Baseline = baseline,
            Scope = "scheduler correctness; harness elapsed is NOT UI/animation latency",
            Results
        }, new JsonSerializerOptions { WriteIndented = true }));
        var failed = Results.Where(result => result.State?.Passed != true).ToArray();
        var unexpected = Results.Any(result => result.UnexpectedError != null);
        var reproduced = !unexpected && failed.Select(result => result.Name).Order().SequenceEqual(Defects.Order()) &&
            failed.All(result => result.State is { Pending: 1, Sleeping: 1 });
        var passed = baseline ? reproduced : !unexpected && failed.Length == 0;
        Console.WriteLine($"SUMMARY scenarios={Results.Count} passed={Results.Count - failed.Length} failed={failed.Length} baselineExactSignature={reproduced} expectedModePassed={passed}");
        Console.WriteLine("RESULT_FILE=" + output);
        return passed ? 0 : 1;
    }

    private static void Run(string name, bool baselineDefect, Func<Observation> scenario)
    {
        var started = Stopwatch.GetTimestamp();
        Observation? observation = null;
        string? error = null;
        try { observation = scenario(); }
        catch (Exception ex) { error = ex.ToString(); }
        var result = new Result(name, baselineDefect, observation,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
        Results.Add(result);
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static Observation CrossQueueCompletion(string name)
    {
        using var fixture = new Fixture();
        if (name == "complete-wake-sleeping-B")
        {
            fixture.Prepare = _ => EdgePrewarmOutcome.Deferred;
            fixture.Coordinator.Request("B");
            Drain(fixture);
            if (fixture.Coordinator.DeferredCount != 1)
                throw new InvalidOperationException("B did not reach the required sleeping precondition.");
        }
        bool? guard = null;
        var missingCancelWasNoop = true;
        fixture.Prepare = key =>
        {
            if (key == "A")
            {
                var valid = CaptureGuard(fixture.Coordinator, key);
                if (!valid()) throw new InvalidOperationException("A was not valid before reentry.");
                switch (name)
                {
                    case "complete-request-B": fixture.Coordinator.Request("B"); break;
                    case "complete-cancel-missing-B":
                        var version = fixture.Coordinator.Version;
                        fixture.Coordinator.Cancel("missing-B");
                        missingCancelWasNoop = version == fixture.Coordinator.Version;
                        break;
                    case "complete-cancel-existing-B": fixture.Coordinator.Cancel("B"); break;
                    case "complete-wake-sleeping-B": fixture.Coordinator.Wake("B"); break;
                }
                guard = valid();
            }
            return EdgePrewarmOutcome.Prepared;
        };
        fixture.Coordinator.Request("A");
        if (name == "complete-cancel-existing-B") fixture.Coordinator.Request("B");
        Drain(fixture);
        var expected = name switch
        {
            "complete-request-B" => "A,B",
            "complete-wake-sleeping-B" => "B,A,B",
            _ => "A"
        };
        return Observe(fixture, guard == true && missingCancelWasNoop &&
            string.Join(",", fixture.Calls) == expected && fixture.Coordinator.PendingCount == 0,
            guard, "Unrelated queue mutation must neither invalidate A's admission guard nor leave A sleeping.");
    }

    private static Observation BeforeQueuePreparation(bool graphics)
    {
        using var fixture = new Fixture();
        var changed = false;
        void Reenter()
        {
            if (changed) return;
            changed = true;
            fixture.Coordinator.Request("B");
        }
        if (graphics) fixture.Graphics = Reenter;
        else fixture.CanPrepare = () => { Reenter(); return true; };
        fixture.Coordinator.Request("A");
        Drain(fixture);
        return Observe(fixture, fixture.Coordinator.PendingCount == 0 &&
            fixture.Calls.SequenceEqual(new[] { "A", "B" }) && fixture.GraphicsCalls == 1,
            null, "Reentry before queue preparation must preserve A and subsequently prepare B once.");
    }

    private static Observation PreserveInvalidation(string mutation)
    {
        using var fixture = new Fixture();
        bool? guard = null;
        fixture.Prepare = key =>
        {
            if (fixture.Calls.Count != 1) return EdgePrewarmOutcome.Prepared;
            var valid = CaptureGuard(fixture.Coordinator, key);
            if (!valid()) throw new InvalidOperationException("Preparation guard was invalid before mutation.");
            switch (mutation)
            {
                case "replace-A": fixture.Coordinator.Request(key); break;
                case "cancel-A": fixture.Coordinator.Cancel(key); break;
                case "cancel-all": fixture.Coordinator.CancelAll(); break;
                case "disable-reenable":
                    fixture.Coordinator.SetEnabled(false);
                    fixture.Coordinator.SetEnabled(true);
                    fixture.Coordinator.Request(key);
                    break;
                case "interaction-nested-quiet":
                    fixture.Coordinator.NotifyInteraction();
                    // Let the quiet timer expire inside the original preparation. Merely testing
                    // !interactionPending would accidentally revive its old publication guard.
                    PumpFor(240);
                    break;
                case "dispose": fixture.Coordinator.Dispose(); break;
            }
            guard = valid();
            return EdgePrewarmOutcome.Prepared;
        };
        fixture.Coordinator.Request("A");
        Drain(fixture);
        var expectedCalls = mutation is "replace-A" or "disable-reenable" or "interaction-nested-quiet" ? 2 : 1;
        return Observe(fixture, guard == false && fixture.Calls.Count == expectedCalls &&
            fixture.Calls.All(key => key == "A") && fixture.Coordinator.PendingCount == 0 && !fixture.Paused,
            guard, "A stale preparation cannot publish or consume a replacement, including after nested quiet expiry.");
    }

    private static Observation DeferredWake()
    {
        using var fixture = new Fixture();
        fixture.Prepare = _ => EdgePrewarmOutcome.Deferred;
        fixture.Coordinator.Request("A");
        Drain(fixture);
        var slept = fixture.Coordinator.PendingCount == 1 && fixture.Coordinator.DeferredCount == 1 &&
            fixture.Calls.Count == 1 && !fixture.Coordinator.HasScheduledWork;
        PumpFor(80);
        slept &= fixture.Calls.Count == 1;
        fixture.Prepare = _ => EdgePrewarmOutcome.Prepared;
        fixture.Coordinator.Wake("A");
        Drain(fixture);
        return Observe(fixture, slept && fixture.Coordinator.PendingCount == 0 && fixture.Calls.Count == 2,
            null, "A genuinely deferred queue sleeps without polling and retries only on explicit readiness.");
    }

    private static Observation FifoLatestRequest()
    {
        using var fixture = new Fixture();
        fixture.Coordinator.Request("A");
        fixture.Coordinator.Request("B");
        fixture.Coordinator.Request("A");
        Drain(fixture);
        return Observe(fixture, fixture.Coordinator.PendingCount == 0 &&
            fixture.Calls.SequenceEqual(new[] { "B", "A" }) && fixture.GraphicsCalls == 1,
            null, "Queue request replacement preserves existing latest-request FIFO semantics.");
    }

    private static Observation Observe(Fixture fixture, bool passed, bool? guard, string note)
    {
        fixture.ThrowCallbackError();
        return new Observation(passed && !fixture.Coordinator.IsPreparing && !fixture.Coordinator.HasScheduledWork,
            fixture.Coordinator.PendingCount, fixture.Coordinator.DeferredCount,
            string.Join(",", fixture.Calls), fixture.GraphicsCalls, guard, note);
    }

    private static Func<bool> CaptureGuard(EdgePrewarmCoordinator coordinator, string queueKey)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var capture = typeof(EdgePrewarmCoordinator).GetMethod("CapturePreparationTicket", flags);
        if (capture == null)
        {
            // Frozen baseline AppController used this exact global-Version condition. This
            // compatibility adapter is not a replacement scheduler or a native publication test.
            var version = coordinator.Version;
            return () => coordinator.Version == version;
        }
        var ticket = capture.Invoke(coordinator, new object[] { queueKey });
        if (ticket == null) return () => false;
        var validate = typeof(EdgePrewarmCoordinator).GetMethod("IsPreparationCurrent", flags)
            ?? throw new InvalidOperationException("Ticket capture exists without its validator.");
        return () => (bool)validate.Invoke(coordinator, new[] { (object)queueKey, ticket })!;
    }

    private static void Drain(Fixture fixture)
    {
        var started = Stopwatch.GetTimestamp();
        while ((fixture.Coordinator.IsPreparing || fixture.Coordinator.HasScheduledWork) &&
            Stopwatch.GetElapsedTime(started).TotalSeconds < 6)
            PumpFor(10);
        fixture.ThrowCallbackError();
        if (fixture.Coordinator.IsPreparing || fixture.Coordinator.HasScheduledWork)
            throw new TimeoutException("The real Dispatcher/Rendering schedule did not drain. This is not an expected baseline defect.");
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        EventHandler handler = (_, _) => frame.Continue = false;
        timer.Tick += handler;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= handler; }
    }

    private sealed class Fixture : IDisposable
    {
        internal EdgePrewarmCoordinator Coordinator { get; }
        internal Func<bool> CanPrepare = () => true;
        internal Action Graphics = () => { };
        internal Func<string, EdgePrewarmOutcome> Prepare = _ => EdgePrewarmOutcome.Prepared;
        internal List<string> Calls { get; } = new();
        internal int GraphicsCalls;
        internal bool Paused;
        private Exception? _callbackError;

        internal Fixture()
        {
            Coordinator = new EdgePrewarmCoordinator(Dispatcher.CurrentDispatcher,
                () => Invoke(CanPrepare),
                () => Invoke(() => { GraphicsCalls++; Graphics(); return true; }),
                key => Invoke(() => { Calls.Add(key); return Prepare(key); }),
                paused => Paused = paused);
            Coordinator.SetEnabled(true);
        }

        private T Invoke<T>(Func<T> callback)
        {
            try { return callback(); }
            catch (Exception ex) { _callbackError ??= ex; throw; }
        }

        internal void ThrowCallbackError()
        {
            if (_callbackError != null)
                throw new InvalidOperationException("The production coordinator caught a fixture exception.", _callbackError);
        }

        public void Dispose() => Coordinator.Dispose();
    }
}
