using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void EdgePrewarmCoordinatorChecks()
    {
        var window = new Window
        {
            Width = 80, Height = 40, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = new Border { Background = Brushes.LightGray }
        };
        window.Show();
        try
        {
            PrewarmRenderingAndMerging();
            PrewarmFailureAndSleeping();
            PrewarmInteractionQuiet();
            PrewarmReentrantOwnership();
        }
        finally { window.Close(); }
        Console.WriteLine("PASS prewarm-rendering-idle-cancel-interaction-and-content-suspension");
    }

    private static void PrewarmRenderingAndMerging()
    {
        using var fixture = new PrewarmCheckFixture();
        fixture.Coordinator.Request("disabled");
        Check(fixture.Coordinator.PendingCount == 0 && !fixture.Coordinator.HasScheduledWork,
            "Disabled preparation does not accept or schedule queue interest");
        fixture.Coordinator.SetEnabled(true);
        var insideRendering = false;
        var rendered = false;
        EventHandler before = (_, _) => { insideRendering = true; rendered = true; };
        EventHandler after = (_, _) => insideRendering = false;
        CompositionTarget.Rendering += before;
        try
        {
            fixture.Coordinator.Request("A");
            fixture.Coordinator.Request("A");
            fixture.Coordinator.Request("B");
            CompositionTarget.Rendering += after;
            var idleBoundaryPassed = false;
            fixture.Prepare = key =>
            {
                Check(rendered && !insideRendering,
                    "Native preparation begins after Rendering returns, never inside its callback");
                if (key == "A")
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        (Action)(() => idleBoundaryPassed = true));
                else Check(idleBoundaryPassed, "Separate queues yield through distinct idle callbacks");
                return EdgePrewarmOutcome.Prepared;
            };
            Check(fixture.Coordinator.PendingCount == 2 && fixture.Prepared.Count == 0,
                "Requests merge by queue and do not synchronously prepare native resources");
            PrewarmPumpUntil(() => fixture.Coordinator.PendingCount == 0, "Merged queue preparation");
            Check(fixture.Prepared.SequenceEqual(new[] { "A", "B" }) && fixture.GraphicsCalls == 1,
                "The latest merged queue requests each run once and share one graphics preparation");
            Check(fixture.Suspensions.SequenceEqual(new[] { true, false, true, false }) &&
                !fixture.Coordinator.HasScheduledWork,
                "Each native preparation pairs content pause/resume and leaves no recurring work");
        }
        finally
        {
            CompositionTarget.Rendering -= before;
            CompositionTarget.Rendering -= after;
        }

        fixture.Coordinator.Request("cancelled-before-render");
        fixture.Coordinator.CancelAll();
        PrewarmPumpFor(60);
        Check(fixture.Prepared.Count == 2 && !fixture.Coordinator.HasScheduledWork,
            "CancelAll removes queued Rendering and idle work before native preparation");
        fixture.Coordinator.Request("cancel-this");
        fixture.Coordinator.Request("keep-this");
        fixture.Coordinator.Cancel("cancel-this");
        fixture.Prepare = _ => EdgePrewarmOutcome.Prepared;
        PrewarmPumpUntil(() => fixture.Coordinator.PendingCount == 0, "Per-queue cancellation");
        Check(fixture.Prepared.Last() == "keep-this" && fixture.Prepared.Count == 3,
            "Cancelling one queue preserves another queue's preparation");
    }

    private static void PrewarmFailureAndSleeping()
    {
        using var fixture = new PrewarmCheckFixture();
        fixture.Coordinator.SetEnabled(true);
        fixture.Prepare = _ => EdgePrewarmOutcome.Deferred;
        fixture.Coordinator.Request("deferred");
        PrewarmPumpUntil(() => fixture.Coordinator.DeferredCount == 1, "Deferred queue sleeps");
        var attempted = fixture.Prepared.Count;
        PrewarmPumpFor(80);
        Check(fixture.Prepared.Count == attempted && !fixture.Coordinator.HasScheduledWork,
            "Deferred preparation sleeps without a self-scheduled readiness poll");
        fixture.Coordinator.Wake("missing");
        Check(fixture.Coordinator.PendingCount == 1,
            "A readiness notification cannot manufacture interest for an absent queue");
        fixture.Prepare = _ => EdgePrewarmOutcome.Prepared;
        fixture.Coordinator.Wake("deferred");
        PrewarmPumpUntil(() => fixture.Coordinator.PendingCount == 0, "Explicit readiness wakes sleeping queue");
        Check(fixture.Prepared.Count == attempted + 1, "Wake retries existing deferred interest once");

        foreach (var result in new[] { EdgePrewarmOutcome.Failed, EdgePrewarmOutcome.Skipped })
        {
            fixture.Prepare = _ => result;
            fixture.Coordinator.Request(result.ToString());
            PrewarmPumpUntil(() => fixture.Coordinator.PendingCount == 0, "Permanent result consumes interest");
            var calls = fixture.Prepared.Count;
            fixture.Coordinator.Wake(result.ToString());
            PrewarmPumpFor(50);
            Check(fixture.Prepared.Count == calls && !fixture.Coordinator.HasScheduledWork,
                "Failed or skipped queue preparation is not retried without a fresh request");
        }
        fixture.Prepare = _ => throw new InvalidOperationException("Injected optional queue failure");
        fixture.Coordinator.Request("throw");
        PrewarmPumpUntil(() => fixture.Coordinator.PendingCount == 0, "Thrown preparation consumes interest");
        Check(!fixture.Paused && !fixture.Coordinator.HasScheduledWork,
            "A queue exception restores content work and leaves no retry loop");

        using var graphicsFailure = new PrewarmCheckFixture();
        graphicsFailure.Graphics = () => throw new InvalidOperationException("Injected graphics failure");
        graphicsFailure.Coordinator.SetEnabled(true);
        graphicsFailure.Coordinator.Request("one");
        graphicsFailure.Coordinator.Request("two");
        PrewarmPumpUntil(() => graphicsFailure.Coordinator.PendingCount == 0, "Optional graphics failure");
        Check(graphicsFailure.GraphicsCalls == 1 && graphicsFailure.Prepared.Count == 2 && !graphicsFailure.Paused,
            "Graphics preparation failure is attempted once and does not block queue preparation or content resume");
    }

    private static void PrewarmInteractionQuiet()
    {
        using var fixture = new PrewarmCheckFixture();
        var available = false;
        fixture.CanPrepare = () => available;
        fixture.Coordinator.SetEnabled(true);
        fixture.Coordinator.Request("mouse-held");
        PrewarmPumpUntil(() => fixture.Coordinator.DeferredCount == 1, "Unavailable queue sleeps");
        Check(fixture.Prepared.Count == 0 && fixture.GraphicsCalls == 0 && fixture.Suspensions.Count == 0,
            "Unavailable readiness sleeps before graphics preparation or content suspension");
        PrewarmPumpFor(60);
        Check(!fixture.Coordinator.HasScheduledWork, "Unavailable readiness cannot poll itself");

        available = true;
        fixture.Coordinator.NotifyInteraction();
        Check(fixture.Paused, "Input immediately pauses optional body preloading");
        PrewarmPumpFor(100);
        var resetAt = Stopwatch.GetTimestamp();
        fixture.Coordinator.NotifyInteraction();
        PrewarmPumpFor(100);
        Check(fixture.Prepared.Count == 0 && fixture.Paused,
            "Repeated input resets the quiet period instead of releasing the previous timer early");
        PrewarmPumpUntil(() => fixture.Prepared.Count == 1 && !fixture.Paused, "Input quiet wakes unavailable queue");
        Check(Stopwatch.GetElapsedTime(resetAt).TotalMilliseconds >= 160 &&
            fixture.Coordinator.PendingCount == 0,
            "A real quiet boundary retries sleeping interest and completes it after the latest input");

        fixture.Coordinator.NotifyInteraction();
        fixture.Coordinator.SetEnabled(false);
        Check(!fixture.Paused && fixture.Coordinator.PendingCount == 0 && !fixture.Coordinator.HasScheduledWork,
            "Disabling during input restores content work and cancels the quiet timer");
        fixture.Coordinator.SetEnabled(true);
        fixture.Coordinator.Wake("mouse-held");
        PrewarmPumpFor(40);
        Check(fixture.Prepared.Count == 1, "Re-enabling cannot resurrect consumed or cancelled candidates");
    }

    private static void PrewarmReentrantOwnership()
    {
        using (var replacement = new PrewarmCheckFixture())
        {
            replacement.Coordinator.SetEnabled(true);
            replacement.Prepare = key =>
            {
                if (replacement.Prepared.Count == 1) replacement.Coordinator.Request(key);
                return EdgePrewarmOutcome.Prepared;
            };
            replacement.Coordinator.Request("latest");
            PrewarmPumpUntil(() => replacement.Prepared.Count == 2 && replacement.Coordinator.PendingCount == 0,
                "Reentrant replacement request");
            Check(replacement.Prepared.SequenceEqual(new[] { "latest", "latest" }),
                "An older completion cannot consume a reentrant replacement request for the same queue");
        }

        foreach (var action in new[] { "cancel", "disable", "dispose" })
        using (var cancelled = new PrewarmCheckFixture())
        {
            cancelled.Coordinator.SetEnabled(true);
            cancelled.Prepare = _ =>
            {
                if (action == "cancel") cancelled.Coordinator.CancelAll();
                else if (action == "disable") cancelled.Coordinator.SetEnabled(false);
                else cancelled.Coordinator.Dispose();
                Check(action != "cancel" || cancelled.Paused,
                    "CancelAll inside native preparation keeps content suspended until preparation returns");
                return EdgePrewarmOutcome.Prepared;
            };
            cancelled.Coordinator.Request("in-flight");
            PrewarmPumpUntil(() => cancelled.Prepared.Count == 1 && !cancelled.Coordinator.IsPreparing,
                "Reentrant cancellation");
            Check(!cancelled.Paused && cancelled.Coordinator.PendingCount == 0 &&
                !cancelled.Coordinator.HasScheduledWork,
                "Reentrant cancellation, disable or disposal restores content and cannot revive work");
        }

        foreach (var nestedQuiet in new[] { false, true })
        using (var interaction = new PrewarmCheckFixture())
        {
            interaction.Coordinator.SetEnabled(true);
            interaction.Prepare = _ =>
            {
                if (interaction.Prepared.Count == 1)
                {
                    interaction.Coordinator.NotifyInteraction();
                    if (nestedQuiet)
                    {
                        PrewarmPumpFor(220);
                        Check(interaction.Paused && interaction.Coordinator.IsPreparing,
                            "A nested quiet timer cannot resume body preloading while native preparation is active");
                    }
                }
                return EdgePrewarmOutcome.Prepared;
            };
            interaction.Coordinator.Request("input-during-publication");
            if (!nestedQuiet)
            {
                PrewarmPumpUntil(() => interaction.Prepared.Count == 1 && !interaction.Coordinator.IsPreparing,
                    "Input interrupts native preparation");
                Check(interaction.Paused && interaction.Suspensions.SequenceEqual(new[] { true }),
                    "Preparation finally does not undo an input pause created by a reentrant callback");
            }
            PrewarmPumpUntil(() => interaction.Prepared.Count == 2 && !interaction.Paused &&
                interaction.Coordinator.PendingCount == 0, "Input-interrupted request resumes after quiet");
        }
    }

    private sealed class PrewarmCheckFixture : IDisposable
    {
        internal EdgePrewarmCoordinator Coordinator { get; }
        internal Func<bool> CanPrepare { get; set; } = () => true;
        internal Action Graphics { get; set; } = () => { };
        internal Func<string, EdgePrewarmOutcome> Prepare { get; set; } = _ => EdgePrewarmOutcome.Prepared;
        internal List<string> Prepared { get; } = new();
        internal List<bool> Suspensions { get; } = new();
        internal int GraphicsCalls { get; private set; }
        internal bool Paused { get; private set; }
        private Exception? _callbackAssertion;

        internal PrewarmCheckFixture()
        {
            Coordinator = new EdgePrewarmCoordinator(Dispatcher.CurrentDispatcher,
                () => Observe(() => { Check(Coordinator!.IsPreparing, "Readiness executes inside the preparation boundary"); return CanPrepare(); }),
                () => Observe(() => { Check(Coordinator!.IsPreparing && Paused, "Graphics preparation pauses optional content first"); GraphicsCalls++; Graphics(); return true; }),
                key => Observe(() => { Check(Coordinator!.IsPreparing && Paused, "Queue preparation pauses optional content first"); Prepared.Add(key); return Prepare(key); }),
                paused => { Paused = paused; Suspensions.Add(paused); });
        }

        private T Observe<T>(Func<T> callback)
        {
            try { return callback(); }
            catch (Exception ex) when (ex.GetType() == typeof(Exception))
            {
                // The production coordinator catches optional-work failures. Preserve Check's
                // assertion exception so that this test cannot mistake it for a handled failure.
                _callbackAssertion ??= ex;
                throw;
            }
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            if (_callbackAssertion != null)
                throw new InvalidOperationException("Coordinator callback assertion failed.", _callbackAssertion);
        }
    }

    private static void PrewarmPumpUntil(Func<bool> finished, string scenario)
    {
        var started = Stopwatch.GetTimestamp();
        while (!finished() && Stopwatch.GetElapsedTime(started).TotalSeconds < 4)
            PrewarmPumpFor(10);
        Check(finished(), scenario + " completes through the real Dispatcher/Rendering schedule");
    }

    private static void PrewarmPumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var deadline = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
        deadline.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { deadline.Stop(); }
    }
}
