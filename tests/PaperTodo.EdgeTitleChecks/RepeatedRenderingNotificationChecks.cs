using System.Diagnostics;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void RepeatedRenderingNotificationChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scheduler = EdgeCapsuleFrameScheduler.For(dispatcher);
        var presenter = new EdgeCapsulePresenter();
        const BindingFlags nonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var onRendering = typeof(EdgeCapsuleFrameScheduler)
            .GetMethod("OnRendering", nonPublicInstance)!;
        bool Subscribed() => (bool)typeof(EdgeCapsuleFrameScheduler)
            .GetField("_renderingSubscribed", nonPublicInstance)!.GetValue(scheduler)!;
        var estimatedPresentationTime = TimeSpan.FromSeconds(123);
        RenderingEventArgs NewNotification() => (RenderingEventArgs)Activator.CreateInstance(
            typeof(RenderingEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { estimatedPresentationTime }, culture: null)!;
        void Raise(RenderingEventArgs notification) => onRendering.Invoke(
            scheduler, new object?[] { dispatcher, notification });

        var monitor = new MonitorGeometry("repeat-rendering-notification",
            new DeviceScreenRect(-100000, -100000, -97440, -98560), 1, 1);
        var clock = Stopwatch.GetTimestamp();
        var startedAt = clock;
        var callbacks = 0;
        var applies = 0;
        var reenterDuringApply = false;
        var reentered = false;
        var nestedCallbackCount = -1;
        var nestedApplyCount = -1;
        Func<EdgeCapsuleDirty, EdgeCapsuleDirty> reconcile = dirty =>
        {
            callbacks++;
            return presenter.Reconcile(dirty,
                () => new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Left,
                    40, 0, 100, 28, 40, 300, 360, false, 1, null, 328, 360),
                () => null,
                frame => frame,
                frame =>
                {
                    applies++;
                    if (reenterDuringApply && !reentered)
                    {
                        reentered = true;
                        var beforeCallbacks = callbacks;
                        var beforeApplies = applies;
                        Raise(NewNotification());
                        nestedCallbackCount = callbacks - beforeCallbacks;
                        nestedApplyCount = applies - beforeApplies;
                    }
                    return true;
                },
                nowTimestamp: clock);
        };

        Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
            EdgeCapsulePaperForm.Collapsed, false)).Accepted,
            "Attach repeated Rendering notification presenter");

        void Start()
        {
            reenterDuringApply = false;
            presenter.CancelTransition();
            presenter.ClearDeferredWork();
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(false));
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
                dispatcher, reconcile, clock);
            presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true));
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(
                EdgeCapsuleTransitionReason.Preview, 100));
            startedAt = clock;
            presenter.Flush(EdgeCapsuleDirty.Presentation, dispatcher, reconcile, startedAt);
            Check(presenter.HasActiveTransition && Subscribed(),
                "The deterministic test starts an active shared transition");
            callbacks = applies = 0;
        }

        void At(int milliseconds) => clock = startedAt +
            Stopwatch.Frequency * milliseconds / 1000;

        void Finish()
        {
            At(100);
            Raise(NewNotification());
            Check(!presenter.HasActiveTransition && !Subscribed(),
                "The QPC endpoint completes and releases the Rendering subscription");
        }

        try
        {
            Start();
            var initial = presenter.AppliedPresentation;
            var first = NewNotification();
            var second = NewNotification();
            Check(!ReferenceEquals(first, second) && first.RenderingTime == second.RenderingTime,
                "Distinct WPF notifications may carry the same estimated presentation time");
            At(25);
            Raise(first);
            var intermediate = presenter.AppliedPresentation;
            Check(callbacks == 1 && intermediate != initial && presenter.HasActiveTransition,
                "The first notification advances a QPC transition to its intermediate state");
            At(50);
            Raise(second);
            Check(callbacks == 2 && presenter.AppliedPresentation != intermediate &&
                presenter.HasActiveTransition,
                "A later notification with the same estimate still advances the QPC transition");
            Finish();
            var completedCallbacks = callbacks;
            var completedFrame = presenter.AppliedPresentation;
            At(200);
            Raise(NewNotification());
            Check(callbacks == completedCallbacks && presenter.AppliedPresentation == completedFrame &&
                !Subscribed(), "An endpoint cannot restart itself on another repeated estimate");

            Start();
            reenterDuringApply = true;
            reentered = false;
            At(50);
            Raise(NewNotification());
            Check(reentered && nestedCallbackCount == 0 && nestedApplyCount == 0 && callbacks == 1,
                "A new notification synchronously reentered from Apply cannot start a nested frame");
            reenterDuringApply = false;
            Finish();

            Start();
            presenter.BeginNativeBatchApply();
            var nativeProtectedFrame = presenter.AppliedPresentation;
            At(25);
            Raise(NewNotification());
            Check(callbacks == 0 && presenter.AppliedPresentation == nativeProtectedFrame,
                "An externally owned native apply still suppresses Rendering advancement");
            presenter.CompleteNativeBatchApplySuccess();
            At(50);
            Raise(NewNotification());
            Check(callbacks == 1 && presenter.AppliedPresentation != nativeProtectedFrame,
                "Native barrier release permits a fresh notification with the same estimate");
            Finish();

            Start();
            using (var deferral = presenter.DeferReconcileToVisualTransaction())
            {
                Check(!Subscribed(), "A staged visual transaction retains its queue barrier");
                At(25);
                Raise(NewNotification());
                Check(callbacks == 0, "A blocked queue cannot publish a staged frame");
            }
            Check(Subscribed(), "Visual transaction release restores its existing frame source");
            At(50);
            Raise(NewNotification());
            Check(callbacks == 1,
                "A blocked notification cannot poison the same estimate after owner release");
            Finish();

            Start();
            At(25);
            Raise(NewNotification());
            presenter.CancelTransition();
            var canceledCallbacks = callbacks;
            var canceledFrame = presenter.AppliedPresentation;
            Check(!Subscribed(), "Cancellation releases its active Rendering subscription");
            At(100);
            Raise(NewNotification());
            Check(callbacks == canceledCallbacks && presenter.AppliedPresentation == canceledFrame &&
                !presenter.HasActiveTransition && !Subscribed(),
                "A repeated estimate after cancellation cannot resurrect the transition");
        }
        finally
        {
            presenter.ClearDeferredWork();
            DrainTransactionChecksDispatcher();
        }
    }
}
