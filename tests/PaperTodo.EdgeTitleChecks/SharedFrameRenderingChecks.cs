using System.Diagnostics;
using System.Reflection;
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
        bool Subscribed() => (bool)typeof(EdgeCapsuleFrameScheduler)
            .GetField("_renderingSubscribed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
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
            // No extra Rendering observer or recurring pump drives this test. The only timer is
            // a test failure deadline; it does not apply frames, post work or restart the scheduler.
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
            Check(!Subscribed(), "Idle presenters do not keep a frame subscription");
            samples.Clear();
            Start(0, true);
            Check(Subscribed(), "First transition attaches to WPF's frame source");
            Finish(0, "first activation");
            Check(samples.Count > 1 && !Subscribed(), "Rendering advances and unsubscribes after settlement");

            // A pending registration from an idle member of another queue is not a global stop.
            Hold(presenters[1]);
            Start(0, false);
            Check(Subscribed(), "Unrelated queue work cannot suspend an active queue");
            Finish(0, "other queue pending");
            Release(presenters[1]);

            // Multiple callbacks from the same owner must release exactly once each.
            Hold(presenters[0]); Hold(presenters[0]);
            Start(0, true);
            var version = presenters[0].AppliedPresentationVersion;
            Check(!Subscribed(), "A blocked queue does not spin composition callbacks");
            DrainTransactionChecksDispatcher();
            Check(presenters[0].AppliedPresentationVersion == version, "Pending owner work cannot leak a frame");
            Release(presenters[0]);
            Check(!Subscribed(), "One completion cannot release a nested registration");
            Release(presenters[0]);
            Check(Subscribed(), "The final owner release directly restores Rendering");
            Finish(0, "pending owner released");

            using (var deferral = presenters[0].DeferReconcileToVisualTransaction())
            {
                Start(0, false);
                Check(!Subscribed(), "An explicit Flush does not end the transaction's frame barrier");
                deferral.Dispose();
                Check(Subscribed(), "A consumed dirty set still resumes its active transition on owner release");
                Finish(0, "visual transaction released");
            }

            // An idle transaction member still owns a barrier for its cross-queue group.
            const long group = 123456;
            presenters[0].JoinNativeBatchTransactionGroup(group);
            presenters[1].JoinNativeBatchTransactionGroup(group);
            using (var deferral = presenters[1].DeferReconcileToVisualTransaction())
            {
                Start(0, true);
                version = presenters[0].AppliedPresentationVersion;
                Check(!Subscribed(), "An idle deferred member blocks the complete cross-queue transaction");
                DrainTransactionChecksDispatcher();
                Check(presenters[0].AppliedPresentationVersion == version,
                    "Cross-queue transaction members cannot commit around an idle owner");
                deferral.Dispose();
                Finish(0, "cross-queue transaction released");
            }
            Check(presenters.All(p => p.NativeBatchTransactionGroupId == 0),
                "The coordinated group releases after all members settle");

            Start(0, false);
            presenters[0].BeginNativeBatchApply();
            version = presenters[0].AppliedPresentationVersion;
            typeof(EdgeCapsuleFrameScheduler).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scheduler, new object?[] { null, EventArgs.Empty });
            Check(presenters[0].AppliedPresentationVersion == version,
                "Native transaction reentry keeps the existing shared-boundary protection");
            presenters[0].CompleteNativeBatchApplySuccess();
            Finish(0, "native transaction released");

            if (WindowNative.TrySetWindowCloaked(handle, true))
            {
                Start(0, true);
                Finish(0, "cloaked WPF source");
                Check(WindowNative.TrySetWindowCloaked(handle, false), "Reveal cloaked test source");
            }
            else Console.WriteLine("  Rendering liveness cloak check unavailable on this desktop");

            Start(0, !presenters[0].Preview.Equals(EdgeCapsulePreviewState.Open));
            presenters[0].CancelTransition();
            presenters[0].ClearDeferredWork();
            version = presenters[0].AppliedPresentationVersion;
            DrainTransactionChecksDispatcher();
            Check(!Subscribed() && presenters[0].AppliedPresentationVersion == version,
                "Cancellation cannot be revived by queued Rendering work");
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
    }

    private static void PreviewClipReuse(EdgeCapsuleHost host)
    {
        Check(host.StagePreviewContent(new Border(), 280, 180), "Stage preview for clip reuse check");
        var type = typeof(EdgeCapsuleHost);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewport = (Border)type.GetField("_previewViewportLayer", flags)!.GetValue(host)!;
        var content = (Border)type.GetProperty("ContentArea", flags)!.GetValue(host)!;
        var apply = type.GetMethod("ApplyPreviewViewportClip", flags)!;
        var frame = EdgeCapsulePresentationFrame.Hidden with { BodyWindowWidthDevice = 280, DpiScaleX = 1 };
        content.CornerRadius = new CornerRadius(0, 15, 15, 0);
        apply.Invoke(host, new object[] { frame, 180.0 });
        var clip = viewport.Clip;
        apply.Invoke(host, new object[] { frame with { WallDeviceX = 100 }, 180.0 });
        Check(ReferenceEquals(clip, viewport.Clip), "Translation-only samples reuse the same rounded clip");
        apply.Invoke(host, new object[] { frame with { BodyWindowWidthDevice = 300 }, 180.0 });
        Check(!ReferenceEquals(clip, viewport.Clip), "A new shape width rebuilds the clip");
        clip = viewport.Clip;
        content.CornerRadius = new CornerRadius(15, 0, 0, 15);
        apply.Invoke(host, new object[] { frame with { BodyWindowWidthDevice = 300 }, 180.0 });
        Check(!ReferenceEquals(clip, viewport.Clip), "Mirrored corners cannot reuse the opposite edge clip");
        clip = viewport.Clip;
        apply.Invoke(host, new object[] { frame with { BodyWindowWidthDevice = 300, DpiScaleX = 1.5 }, 180.0 });
        Check(!ReferenceEquals(clip, viewport.Clip), "DPI changes recompute the actual clip dimensions");
        host.ClearPreviewContent();
    }
}
