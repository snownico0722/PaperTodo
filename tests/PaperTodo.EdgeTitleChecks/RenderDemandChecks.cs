using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private sealed class RenderDemandClock : TimeProvider
    {
        internal long Now = 1000;
        internal ManualTimer? Timer;
        internal int TimerCount;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCount++;
            Timer = new ManualTimer(callback, state);
            Timer.Change(dueTime, period);
            return Timer;
        }
        internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Armed;
            internal bool Disposed;
            internal TimeSpan Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == Timeout.InfiniteTimeSpan, "Render demand never arms a recurring timer");
                ObjectDisposedException.ThrowIf(Disposed, this);
                Due = dueTime;
                Armed = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
            // Deliberately permits a stale callback after Change/Dispose, like an in-flight
            // ThreadPool callback. This clock never drives a WPF animation or application frame.
            internal void FireLate() { Armed = false; callback(state); }
            public void Dispose() { Disposed = true; Armed = false; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static void RenderDemandChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var a = new EdgeCapsuleNativeBatchGroup("demand-a", EdgeCapsuleEdge.Left, 0);
        var b = new EdgeCapsuleNativeBatchGroup("demand-b", EdgeCapsuleEdge.Right, 100);
        void Drain() => DrainTransactionChecksDispatcher();

        var offClock = new RenderDemandClock();
        using (var off = new EdgeCapsuleRenderDemand(dispatcher, _ => true,
            () => throw new Exception("Disabled request"), false, offClock))
        {
            off.ReadyGroupsChanged([a]);
            off.GroupSampled(a, offClock.Now);
            Check(offClock.TimerCount == 0 && !off.HasScheduledDemand,
                "Control mode creates no timer and no demand");
        }

        var clock = new RenderDemandClock();
        var ready = new HashSet<EdgeCapsuleNativeBatchGroup>();
        var requests = 0;
        using (var demand = new EdgeCapsuleRenderDemand(dispatcher, ready.Contains,
            () => { Check(dispatcher.CheckAccess(), "Only the owning Dispatcher requests WPF"); requests++; },
            clock: clock))
        {
            Check(clock.TimerCount == 1 && !clock.Timer!.Armed, "One initially disarmed timer per demand owner");
            clock.Now += 1000;
            clock.Timer!.FireLate();
            Check(!demand.HasPendingOperation && requests == 0, "Idle cannot queue work even on a late callback");
            ready.UnionWith([a, b]);
            demand.ReadyGroupsChanged(ready);
            var started = clock.Now;
            clock.Now += 6;
            demand.GroupSampled(a, clock.Now);
            clock.Now = started + 12;
            var posted = 0;
            DispatcherHookEventHandler countPosted = (_, args) =>
            { if (args.Operation.Priority == DispatcherPriority.Render) posted++; };
            dispatcher.Hooks.OperationPosted += countPosted;
            try { for (var i = 0; i < 20; i++) clock.Timer!.FireLate(); }
            finally { dispatcher.Hooks.OperationPosted -= countPosted; }
            Check(posted == 1 && demand.HasPendingOperation,
                "B's deadline survives A's sample; timer callbacks share one pending operation");
            Drain();
            Check(requests == 1 && !demand.HasPendingOperation && clock.Timer.Armed,
                "One WPF request consumes due demand without creating a presentation frame");
            clock.Now = started + 18;
            clock.Timer!.FireLate();
            Drain();
            Check(requests == 2, "A keeps its independent sample deadline after B's request");

            // A late request consumes elapsed time once, rather than replaying missed periods.
            clock.Now += 1000;
            clock.Timer!.FireLate();
            Drain();
            Check(requests == 3, "A long delay issues one global request, with no catch-up frames");
            clock.Timer!.FireLate();
            Check(!demand.HasPendingOperation, "A callback from an old arm cannot consume the new deadline early");

            // Complete owner barriers remove demand while the normal Input dispatcher still runs.
            ready.Clear();
            demand.ReadyGroupsChanged(ready);
            var inputServed = false;
            clock.Now += 1000;
            for (var i = 0; i < 20; i++) clock.Timer!.FireLate();
            dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => inputServed = true));
            Drain();
            Check(inputServed && requests == 3 && !clock.Timer.Armed && !demand.HasPendingOperation,
                "A long blocked interval produces no requests and leaves Input serviceable");

            ready.Add(a);
            demand.ReadyGroupsChanged(ready);
            clock.Now += 12;
            clock.Timer!.FireLate();
            Check(demand.HasPendingOperation, "The ready restart has its own pending generation");
            ready.Clear();
            demand.ReadyGroupsChanged(ready);
            ready.Add(a);
            demand.ReadyGroupsChanged(ready);
            Drain();
            Check(requests == 3, "A queued old epoch cannot consume a cancelled-and-restarted animation");
            clock.Now += 12;
            clock.Timer!.FireLate();
            Drain();
            Check(requests == 4, "The successor still receives its request at its own deadline");

            // Readiness is checked at execution too, including external native apply not yet
            // reported by a caller. No public WPF request crosses that barrier.
            clock.Now += 12;
            clock.Timer!.FireLate();
            ready.Clear();
            Drain();
            Check(requests == 4 && !demand.HasScheduledDemand,
                "Late execution rechecks the native/group barrier and disarms rejected demand");
            demand.Dispose();
            clock.Now += 1000;
            clock.Timer!.FireLate();
            Check(clock.Timer.Disposed && !demand.HasPendingOperation,
                "Disposal rejects even a timer callback already in flight");
        }

        // The application samples QPC before native work. A slow native completion must not
        // move that timestamp forward and hide the actual age of the sampled state.
        var sampleClock = new RenderDemandClock();
        var sampleRequests = 0;
        using (var sampled = new EdgeCapsuleRenderDemand(dispatcher, _ => true,
            () => sampleRequests++, clock: sampleClock))
        {
            sampled.ReadyGroupsChanged([a]);
            var actualSample = sampleClock.Now + 2;
            sampleClock.Now += 20;
            sampled.GroupSampled(a, actualSample);
            sampleClock.Timer!.FireLate();
            Drain();
            Check(sampleRequests == 1, "Deadline uses the supplied frame sample, not post-native completion time");
        }

        var nestedClock = new RenderDemandClock();
        var nestedReady = new HashSet<EdgeCapsuleNativeBatchGroup> { a, b };
        var nestedRequests = 0;
        using (var nested = new EdgeCapsuleRenderDemand(dispatcher, nestedReady.Contains,
            () => nestedRequests++, clock: nestedClock))
        {
            nested.ReadyGroupsChanged(nestedReady);
            nestedClock.Now += 12;
            var changed = false;
            DispatcherHookEventHandler hook = (_, args) =>
            {
                if (changed || args.Operation.Priority != DispatcherPriority.Render) return;
                changed = true;
                nestedReady.Remove(a);
                nested.ReadyGroupsChanged(nestedReady);
            };
            dispatcher.Hooks.OperationPosted += hook;
            try { nestedClock.Timer!.FireLate(); }
            finally { dispatcher.Hooks.OperationPosted -= hook; }
            Drain();
            Check(changed && nestedRequests == 1 && nested.ReadyGroupCount == 1,
                "A synchronous posting Hook can cancel A while preserving B's already-due demand");
            nestedClock.Now += 12;
            changed = false;
            DispatcherHookEventHandler cancel = (_, args) =>
            {
                if (changed || args.Operation.Priority != DispatcherPriority.Render) return;
                changed = true;
                nestedReady.Clear();
                nested.ReadyGroupsChanged(nestedReady);
            };
            dispatcher.Hooks.OperationPosted += cancel;
            try { nestedClock.Timer.FireLate(); }
            finally { dispatcher.Hooks.OperationPosted -= cancel; }
            Drain();
            Check(changed && nestedRequests == 1 && !nested.HasPendingOperation && !nested.HasScheduledDemand,
                "A Hook cancels the posting generation before its operation handle is assigned");
        }
        var disposeClock = new RenderDemandClock();
        var disposeRequests = 0;
        using (var disposed = new EdgeCapsuleRenderDemand(dispatcher, _ => true,
            () => disposeRequests++, clock: disposeClock))
        {
            disposed.ReadyGroupsChanged([a]);
            disposeClock.Now += 12;
            disposeClock.Timer!.FireLate();
            Check(disposed.HasPendingOperation, "Disposal fixture has a queued Render operation");
            disposed.Dispose();
            disposeClock.Timer.FireLate();
            Drain();
            Check(disposeRequests == 0 && !disposed.HasPendingOperation && disposeClock.Timer.Disposed,
                "Disposing queued demand aborts it without changing the already-disposed timer");
        }
        var racingClock = new RenderDemandClock();
        var racingRequests = 0;
        using (var racing = new EdgeCapsuleRenderDemand(dispatcher, _ => true,
            () => racingRequests++, clock: racingClock))
        using (var postedOnWorker = new ManualResetEventSlim())
        using (var releaseWorker = new ManualResetEventSlim())
        {
            racing.ReadyGroupsChanged([a]);
            racingClock.Now += 12;
            DispatcherHookEventHandler holdPost = (_, args) =>
            {
                if (dispatcher.CheckAccess() || args.Operation.Priority != DispatcherPriority.Render) return;
                postedOnWorker.Set();
                if (!releaseWorker.Wait(TimeSpan.FromSeconds(3)))
                    throw new Exception("Timed out waiting to complete the controlled timer post");
            };
            dispatcher.Hooks.OperationPosted += holdPost;
            Task? worker = null;
            try
            {
                worker = Task.Run(() => racingClock.Timer!.FireLate());
                Check(postedOnWorker.Wait(TimeSpan.FromSeconds(2)), "The timer worker entered BeginInvoke before returning its handle");
                racing.ReadyGroupsChanged([]);
                racing.ReadyGroupsChanged([a]);
                racingClock.Timer!.FireLate();
                Check(racing.HasPendingOperation,
                    "A cancelled posting slot remains single-owned until the worker can retire it");
            }
            finally
            {
                releaseWorker.Set();
                if (worker != null)
                {
                    Check(worker.Wait(TimeSpan.FromSeconds(2)), "The controlled timer worker finishes without a UI-lock deadlock");
                    worker.GetAwaiter().GetResult();
                }
                dispatcher.Hooks.OperationPosted -= holdPost;
            }
            Drain();
            Check(racingRequests == 0 && !racing.HasPendingOperation,
                "The in-flight old epoch is aborted without consuming restarted demand");
            racingClock.Now += 12;
            racingClock.Timer!.FireLate();
            Drain();
            Check(racingRequests == 1, "The successor deadline remains live after the old posting slot retires");
        }
        RenderDemandAbortedHookRestartCheck();
        RenderDemandRunBeforeHandleCheck();
        RenderDemandSchedulerOwnershipCheck();
        RenderDemandShutdownChecks();
        Console.WriteLine("PASS render demand: deadlines, groups, idle, barriers, epochs and synchronous Hooks");
    }

    private static void RenderDemandAbortedHookRestartCheck()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var a = new EdgeCapsuleNativeBatchGroup("abort-demand-a", EdgeCapsuleEdge.Left, 0);
        var b = new EdgeCapsuleNativeBatchGroup("abort-demand-b", EdgeCapsuleEdge.Right, 100);
        var clock = new RenderDemandClock();
        var ready = new HashSet<EdgeCapsuleNativeBatchGroup> { a };
        var requests = 0;
        using var demand = new EdgeCapsuleRenderDemand(dispatcher, ready.Contains, () => requests++, clock: clock);
        demand.ReadyGroupsChanged(ready);
        clock.Now += 12;
        DispatcherOperation? original = null;
        DispatcherHookEventHandler capture = (_, args) =>
        { if (args.Operation.Priority == DispatcherPriority.Render) original = args.Operation; };
        dispatcher.Hooks.OperationPosted += capture;
        try { clock.Timer!.FireLate(); }
        finally { dispatcher.Hooks.OperationPosted -= capture; }
        Check(original is { Status: DispatcherOperationStatus.Pending }, "Abort fixture owns a pending demand operation");
        var restarted = false;
        DispatcherHookEventHandler restart = (_, args) =>
        {
            if (restarted || !ReferenceEquals(args.Operation, original)) return;
            restarted = true;
            ready.Clear();
            demand.ReadyGroupsChanged(ready);
            ready.Add(b);
            demand.ReadyGroupsChanged(ready);
            clock.Now += 12;
            // The successor is already posted before the outer RetireOperation resumes.
            clock.Timer!.FireLate();
        };
        dispatcher.Hooks.OperationAborted += restart;
        try
        {
            ready.Clear();
            demand.ReadyGroupsChanged(ready);
        }
        finally { dispatcher.Hooks.OperationAborted -= restart; }
        Check(restarted && demand.ReadyGroupCount == 1 && demand.HasPendingOperation,
            "OperationAborted can cancel A and post B without outer retirement clearing B's slot");
        DrainTransactionChecksDispatcher();
        Check(requests == 1 && !demand.HasPendingOperation && demand.HasScheduledDemand,
            "Only restarted B requests work; its next deadline remains live");
        Console.WriteLine("PASS render demand OperationAborted cancel/restart");
    }

    private static void RenderDemandRunBeforeHandleCheck()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var group = new EdgeCapsuleNativeBatchGroup("run-before-handle", EdgeCapsuleEdge.Left, 0);
        var clock = new RenderDemandClock();
        var requests = 0;
        using var postedOnWorker = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var demand = new EdgeCapsuleRenderDemand(dispatcher, _ => true, () => requests++, clock: clock);
        demand.ReadyGroupsChanged([group]);
        clock.Now += 12;
        DispatcherHookEventHandler hold = (_, args) =>
        {
            if (dispatcher.CheckAccess() || args.Operation.Priority != DispatcherPriority.Render) return;
            postedOnWorker.Set();
            if (!releaseWorker.Wait(TimeSpan.FromSeconds(3)))
                throw new Exception("Run-before-handle fixture did not release its posting worker");
        };
        dispatcher.Hooks.OperationPosted += hold;
        Task? worker = null;
        try
        {
            worker = Task.Run(() => clock.Timer!.FireLate());
            Check(postedOnWorker.Wait(TimeSpan.FromSeconds(2)), "Worker pauses before BeginInvoke returns its operation handle");
            DrainTransactionChecksDispatcher();
            Check(requests == 1 && !worker.IsCompleted && !demand.HasPendingOperation,
                "UI Run/FinishWake can complete while the producer still has no operation handle");
            clock.Now += 12;
            clock.Timer!.FireLate();
            Check(demand.HasPendingOperation, "A successor can occupy the slot before the old worker returns");
        }
        finally
        {
            releaseWorker.Set();
            try
            {
                if (worker != null)
                {
                    Check(worker.Wait(TimeSpan.FromSeconds(2)), "The late handle producer completes without waiting on UI work");
                    worker.GetAwaiter().GetResult();
                }
            }
            finally { dispatcher.Hooks.OperationPosted -= hold; }
        }
        Check(demand.HasPendingOperation && requests == 1,
            "Assigning the old completed handle cannot retire the successor's pending operation");
        DrainTransactionChecksDispatcher();
        Check(requests == 2 && !demand.HasPendingOperation,
            "Both valid generations execute once, including the successor after late-handle acknowledgement");
        Console.WriteLine("PASS render demand Run before producer receives handle");
    }

    private static void RenderDemandShutdownChecks()
    {
        // Each scenario uses a real, independent STA Dispatcher. InvokeShutdown is irreversible;
        // it must not shut down the main test Dispatcher or create a visible application window.
        foreach (var blockedAtShutdown in new[] { false, true })
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Dispatcher? dispatcher = null;
                EventHandler? laterShutdown = null;
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    var scheduler = EdgeCapsuleFrameScheduler.For(dispatcher);
                    var presenter = new EdgeCapsulePresenter();
                    var monitor = new MonitorGeometry("demand-shutdown-" + blockedAtShutdown,
                        new DeviceScreenRect(-100000, -100000, -97440, -98560), 1, 1);
                    var callbacks = 0;
                    Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty =>
                    {
                        callbacks++;
                        return presenter.Reconcile(dirty,
                            () => new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                                40, 0, 100, 28, 40, 300, 360, false, 1, null, 328, 360),
                            () => null, frame => frame, frame => true);
                    };
                    presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false));
                    presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
                    presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
                    presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
                    presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 120));
                    presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    bool Subscribed() => (bool)typeof(EdgeCapsuleFrameScheduler).GetField("_renderingSubscribed", flags)!
                        .GetValue(scheduler)!;
                    var demand = (EdgeCapsuleRenderDemand)typeof(EdgeCapsuleFrameScheduler).GetField("_renderDemand", flags)!
                        .GetValue(scheduler)!;
                    var onRendering = typeof(EdgeCapsuleFrameScheduler).GetMethod("OnRendering", flags)!;
                    var advance = typeof(EdgeCapsuleFrameScheduler).GetMethod("AdvanceSharedFrame", flags)!;
                    Check(presenter.HasActiveTransition && Subscribed(), "Shutdown fixture starts an actual active presenter");
                    if (blockedAtShutdown) scheduler.RegisterRenderReconcile(presenter);
                    Check(Subscribed() != blockedAtShutdown, "Shutdown fixture has the intended ready/blocked subscription");
                    var laterRan = false;
                    laterShutdown = (_, _) =>
                    {
                        laterRan = true;
                        Check(!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished,
                            "Real ShutdownStarted handlers execute before WPF exposes HasShutdownStarted");
                        Check(!Subscribed() && !demand.HasScheduledDemand,
                            "The scheduler has already latched shutdown and withdrawn subscription/demand");
                        if (blockedAtShutdown) scheduler.CompleteRenderReconcile(presenter);
                        scheduler.Activate(presenter);
                        scheduler.Activate(new EdgeCapsulePresenter());
                        Check(!Subscribed() && !demand.HasScheduledDemand,
                            "Later shutdown barrier release and Activate cannot revive rendering ownership");
                        var before = callbacks;
                        onRendering.Invoke(scheduler, new object?[] { dispatcher, EventArgs.Empty });
                        advance.Invoke(scheduler, new object?[] { null, "shutdown-check" });
                        Check(callbacks == before,
                            "Rendering notification and frame entry cannot advance during ShutdownStarted callbacks");
                    };
                    dispatcher.ShutdownStarted += laterShutdown;
                    dispatcher.InvokeShutdown();
                    Check(laterRan && dispatcher.HasShutdownFinished && !Subscribed() && !demand.HasScheduledDemand,
                        "InvokeShutdown completes with rendering ownership still retired");
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    if (dispatcher != null)
                    {
                        if (laterShutdown != null) dispatcher.ShutdownStarted -= laterShutdown;
                        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                        {
                            try { dispatcher.InvokeShutdown(); }
                            catch (Exception error) { failure ??= error; }
                        }
                    }
                }
            }) { IsBackground = true, Name = "PaperTodo render-demand shutdown check" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(5)), "The dedicated Dispatcher shutdown scenario finishes without hanging");
            if (failure != null) throw new Exception("Render demand shutdown scenario failed; blocked=" + blockedAtShutdown, failure);
        }
        Console.WriteLine("PASS render demand real Dispatcher.InvokeShutdown and later lifecycle callbacks");
    }

    private static void RenderDemandSchedulerOwnershipCheck()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scheduler = EdgeCapsuleFrameScheduler.For(dispatcher);
        var presenter = new EdgeCapsulePresenter();
        var monitor = new MonitorGeometry("demand-subscription-owner",
            new DeviceScreenRect(-100000, -100000, -97440, -98560), 1, 1);
        presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false));
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty => presenter.Reconcile(dirty,
            () => new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                40, 0, 100, 28, 40, 300, 360, false, 1, null, 328, 360),
            () => null, frame => frame, frame => true);
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        bool Subscribed() => (bool)typeof(EdgeCapsuleFrameScheduler).GetField("_renderingSubscribed", flags)!
            .GetValue(scheduler)!;
        var demand = (EdgeCapsuleRenderDemand)typeof(EdgeCapsuleFrameScheduler).GetField("_renderDemand", flags)!
            .GetValue(scheduler)!;

        presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
        presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 120));
        presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
        Check(presenter.HasActiveTransition && demand.HasScheduledDemand == demand.Enabled,
            "A real no-proxy transition supplies activity without pointer samples");
        presenter.BeginNativeBatchApply();
        Check(!demand.HasScheduledDemand, "External native apply immediately withdraws render demand");
        presenter.CompleteNativeBatchApplySuccess();
        Check(demand.HasScheduledDemand == demand.Enabled,
            "Native completion restores demand only for the still-active ready transition");
        presenter.CancelTransition();
        presenter.ClearDeferredWork();
        Check(!Subscribed() && !demand.HasScheduledDemand, "Real cancellation releases subscription and deadline");
        presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(false));
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);

        // Controlled test setup only: observe WPF's publicly posted operation, then park that
        // operation via its public Priority setter. The next Rendering.add must promote it and
        // synchronously enter a Hook. Production never changes another component's priority.
        DispatcherOperation? observed = null;
        EventHandler marker = (_, _) => { };
        DispatcherHookEventHandler capturePosted = (_, args) =>
        { if (args.Operation.Priority == DispatcherPriority.Render) observed = args.Operation; };
        // This hook runs inside the Priority setter before operation.Priority is updated. During
        // our own Rendering.add scope, retain the operation being promoted without testing its
        // still-old priority against the requested Render priority.
        DispatcherHookEventHandler capturePromoted = (_, args) => observed = args.Operation;
        for (var attempt = 0; observed == null && attempt < 3; attempt++)
        {
            DrainTransactionChecksDispatcher();
            dispatcher.Hooks.OperationPosted += capturePosted;
            dispatcher.Hooks.OperationPriorityChanged += capturePromoted;
            try { CompositionTarget.Rendering += marker; }
            finally
            {
                CompositionTarget.Rendering -= marker;
                dispatcher.Hooks.OperationPosted -= capturePosted;
                dispatcher.Hooks.OperationPriorityChanged -= capturePromoted;
            }
        }
        Check(observed is { Status: DispatcherOperationStatus.Pending },
            "The ownership fixture observes a pending WPF operation through public Hooks");
        observed!.Priority = DispatcherPriority.Inactive;
        Check(observed.Priority == DispatcherPriority.Inactive,
            "The cancellation fixture starts from a genuinely parked operation");
        var canceled = false;
        var activationArmed = false;
        var hookCount = 0;
        DispatcherPriority? priorityInsideHook = null;
        DispatcherPriority? priorityAfterActivation = null;
        DispatcherHookEventHandler cancel = (_, args) =>
        {
            if (!activationArmed || canceled || !ReferenceEquals(args.Operation, observed)) return;
            hookCount++;
            priorityInsideHook = args.Operation.Priority;
            canceled = true;
            presenter.CancelTransition();
            presenter.ClearDeferredWork();
        };
        dispatcher.Hooks.OperationPriorityChanged += cancel;
        try
        {
            activationArmed = true;
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 120));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
            // Capture before cleanup's public Priority assignment, which must not manufacture
            // evidence that the actual Rendering.add promoted the observed operation.
            priorityAfterActivation = observed.Priority;
        }
        finally
        {
            activationArmed = false;
            dispatcher.Hooks.OperationPriorityChanged -= cancel;
            if (observed.Status == DispatcherOperationStatus.Pending)
                observed.Priority = DispatcherPriority.Render;
        }
        string State() => $"canceled={canceled} hooks={hookCount} active={presenter.HasActiveTransition} " +
            $"subscribed={Subscribed()} demand={demand.HasScheduledDemand} pending={demand.HasPendingOperation} " +
            $"hookPriority={priorityInsideHook?.ToString() ?? "none"} " +
            $"returnedPriority={priorityAfterActivation?.ToString() ?? "none"}";
        Check(canceled && hookCount == 1 && priorityAfterActivation == DispatcherPriority.Render,
            "The actual Rendering.add promotion must invoke the armed cancellation Hook once: " + State());
        Check(canceled && !presenter.HasActiveTransition && !Subscribed() && !demand.HasScheduledDemand,
            "Cancellation inside public Rendering.add cannot resurrect a stale subscription or demand: " + State());
        DrainTransactionChecksDispatcher();
        Check(!Subscribed() && !demand.HasPendingOperation,
            "Cancelled ownership remains idle after the WPF operation drains: " + State());
        Console.WriteLine("PASS public Rendering.add cancellation Hook: " + State());
        presenter.ClearDeferredWork();
        // Existing SharedFrameRenderingLiveness, run by QueuedPreviewTransactions, supplies
        // real WPF completion with no proxy/mouse movement, native barriers and cloaked sources.
    }
}
