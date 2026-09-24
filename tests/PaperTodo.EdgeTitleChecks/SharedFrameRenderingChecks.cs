using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void SharedFrameRenderingLiveness()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scheduler = EdgeCapsuleFrameScheduler.For(dispatcher);
        var shape = new Border { Background = Brushes.CornflowerBlue };
        var window = new Window
        {
            Content = shape, Width = 420, Height = 400, ShowInTaskbar = false,
            ShowActivated = false, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent
        };
        var presenters = new[] { new EdgeCapsulePresenter(), new EdgeCapsulePresenter() };
        var callbacks = new Func<EdgeCapsuleDirty, EdgeCapsuleDirty>[2];
        var samples = new List<long>();
        for (var i = 0; i < presenters.Length; i++)
        {
            var index = i;
            var presenter = presenters[i];
            Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach Rendering test presenter");
            var monitor = new MonitorGeometry("rendering-queue-" + i,
                new DeviceScreenRect(-100000, -100000, -97440, -98560), 1, 1);
            callbacks[i] = dirty => presenter.Reconcile(dirty,
                () => new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                    40, 0, 100, 28, 40, 300, 360, false, 1, null, 328, 360),
                () => null, frame => frame, frame =>
                {
                    if (index == 0)
                    {
                        shape.Width = frame.Bounds.Width;
                        shape.Height = frame.Bounds.Height;
                        samples.Add(Stopwatch.GetTimestamp());
                    }
                    return true;
                });
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, callbacks[i]);
        }

        void Start(int index, bool open)
        {
            var presenter = presenters[index];
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(open));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 120));
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, callbacks[index]);
            Check(presenter.HasActiveTransition, "Test starts a real transition");
        }

        void Finish(int index, string scenario)
        {
            var frame = new DispatcherFrame();
            var completed = false;
            var success = false;
            presenters[index].NotifyWhenPresentationSettled(result =>
            {
                completed = true;
                success = result;
                frame.Continue = false;
            });
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
                { Interval = TimeSpan.FromSeconds(4) };
            timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
            var started = Stopwatch.GetTimestamp();
            timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); presenters[index].ClearPresentationSettleNotification(); }
            Check(completed && success && !presenters[index].HasActiveTransition,
                scenario + " finishes through WPF Rendering without a rescue");
            Console.WriteLine($"  Rendering liveness {scenario}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        }

        void LetRenderingRun(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
                { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }

        var pendingOwners = new List<EdgeCapsulePresenter>();
        void Hold(EdgeCapsulePresenter owner)
        {
            scheduler.RegisterRenderReconcile(owner);
            pendingOwners.Add(owner);
        }
        void Release(EdgeCapsulePresenter owner)
        {
            scheduler.CompleteRenderReconcile(owner);
            pendingOwners.Remove(owner);
        }

        var handle = IntPtr.Zero;
        try
        {
            window.Show();
            DrainTransactionChecksDispatcher();
            handle = new WindowInteropHelper(window).Handle;

            samples.Clear();
            Start(0, true);
            Finish(0, "first activation");
            Check(samples.Count > 1, "A real transition advances across multiple WPF frames");

            // Work owned by another queue must not stop this queue from completing.
            Hold(presenters[1]);
            Start(0, false);
            Finish(0, "other queue pending");
            Release(presenters[1]);

            // Nested ownership blocks publication until the final owner releases.
            Hold(presenters[0]);
            Hold(presenters[0]);
            Start(0, true);
            var version = presenters[0].AppliedPresentationVersion;
            LetRenderingRun(80);
            Check(presenters[0].AppliedPresentationVersion == version,
                "Pending owner work cannot leak a presentation frame");
            Release(presenters[0]);
            LetRenderingRun(60);
            Check(presenters[0].AppliedPresentationVersion == version,
                "One completion cannot release a nested owner");
            Release(presenters[0]);
            Finish(0, "pending owner released");

            // An explicit visual transaction blocks publication, then resumes the same transition.
            var visualDeferral = presenters[0].DeferReconcileToVisualTransaction();
            try
            {
                Start(0, false);
                version = presenters[0].AppliedPresentationVersion;
                LetRenderingRun(80);
                Check(presenters[0].AppliedPresentationVersion == version,
                    "Visual transaction blocks presentation publication");
            }
            finally { visualDeferral.Dispose(); }
            Finish(0, "visual transaction released");

            // Cross-queue transactions also block until every participant releases.
            const long group = 123456;
            presenters[0].JoinNativeBatchTransactionGroup(group);
            presenters[1].JoinNativeBatchTransactionGroup(group);
            var crossQueueDeferral = presenters[1].DeferReconcileToVisualTransaction();
            try
            {
                Start(0, true);
                version = presenters[0].AppliedPresentationVersion;
                LetRenderingRun(80);
                Check(presenters[0].AppliedPresentationVersion == version,
                    "Cross-queue transaction cannot publish around another owner");
            }
            finally { crossQueueDeferral.Dispose(); }
            Finish(0, "cross-queue transaction released");
            Check(presenters.All(p => p.NativeBatchTransactionGroupId == 0),
                "Coordinated transaction membership is released after settlement");

            // Native apply ownership blocks real Rendering notifications without invoking a private callback.
            Start(0, false);
            presenters[0].BeginNativeBatchApply();
            version = presenters[0].AppliedPresentationVersion;
            LetRenderingRun(80);
            Check(presenters[0].AppliedPresentationVersion == version,
                "Native apply ownership prevents WPF frame publication");
            presenters[0].CompleteNativeBatchApplySuccess();
            Finish(0, "native transaction released");

            if (WindowNative.TrySetWindowCloaked(handle, true))
            {
                Start(0, true);
                Finish(0, "cloaked WPF source");
                Check(WindowNative.TrySetWindowCloaked(handle, false), "Reveal cloaked test source");
            }
            else Console.WriteLine("  Rendering liveness cloak check unavailable on this desktop");

            // Cancellation is observable as a stable final frame; queued notifications cannot revive it.
            Start(0, !presenters[0].Preview.Equals(EdgeCapsulePreviewState.Open));
            presenters[0].CancelTransition();
            presenters[0].ClearDeferredWork();
            version = presenters[0].AppliedPresentationVersion;
            LetRenderingRun(180);
            Check(!presenters[0].HasActiveTransition &&
                presenters[0].AppliedPresentationVersion == version,
                "Cancellation cannot be revived by later WPF Rendering notifications");
        }
        finally
        {
            foreach (var owner in pendingOwners.ToArray()) Release(owner);
            foreach (var presenter in presenters)
            {
                presenter.CancelTransition();
                presenter.ClearDeferredWork();
            }
            if (handle != IntPtr.Zero) WindowNative.TrySetWindowCloaked(handle, false);
            window.Close();
            DrainTransactionChecksDispatcher();
        }

        FrameSchedulerShutdownDoesNotHang();
    }

    private static void FrameSchedulerShutdownDoesNotHang()
    {
        foreach (var blocked in new[] { false, true })
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Dispatcher? dispatcher = null;
                EdgeCapsuleFrameScheduler? scheduler = null;
                EdgeCapsulePresenter? presenter = null;
                var held = false;
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    scheduler = EdgeCapsuleFrameScheduler.For(dispatcher);
                    presenter = new EdgeCapsulePresenter();
                    var monitor = new MonitorGeometry("rendering-shutdown-" + blocked,
                        new DeviceScreenRect(-100000, -100000, -97440, -98560), 1, 1);
                    Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty => presenter.Reconcile(dirty,
                        () => new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                            40, 0, 100, 28, 40, 300, 360, false, 1, null, 328, 360),
                        () => null, frame => frame, frame => true);

                    presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1), EdgeCapsulePaperForm.Collapsed, false));
                    presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
                    presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
                    presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
                    presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 120));
                    presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile);
                    Check(presenter.HasActiveTransition, "Shutdown fixture starts an active transition");

                    if (blocked)
                    {
                        scheduler.RegisterRenderReconcile(presenter);
                        held = true;
                    }

                    dispatcher.InvokeShutdown();
                    Check(dispatcher.HasShutdownFinished,
                        "Dispatcher shutdown completes while rendering work is active");
                    held = false; // Scheduler owns shutdown cleanup after this point.
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                    {
                        try
                        {
                            if (held && scheduler != null && presenter != null)
                                scheduler.CompleteRenderReconcile(presenter);
                            dispatcher.InvokeShutdown();
                        }
                        catch (Exception error) { failure ??= error; }
                    }
                }
            }) { IsBackground = true, Name = "PaperTodo frame-scheduler shutdown check" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(5)),
                "Active or blocked frame scheduling cannot hang Dispatcher shutdown");
            if (failure != null)
                throw new Exception("Frame scheduler shutdown scenario failed; blocked=" + blocked, failure);
        }

        Console.WriteLine("PASS frame scheduling shutdown without private scheduler-state assertions");
    }
}
