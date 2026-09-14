using System.Reflection;
using System.Runtime.CompilerServices;
using PaperTodo;

internal static partial class Program
{
    private static void ProxySourceCapacityInvalidation()
    {
        // Keep native/WPF presentation real, while replacing only application lifetime and the
        // compositor completion callback. This exercises the actual invalidation, layout and
        // resume entry points without loading a user's StateStore or a plugin runtime.
        using var host = NewProxyCheckHost("proxy-capacity-check");
        using var proxy = new ProxyLifecycleFixture();
        var presenter = new EdgeCapsulePresenter();
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        var paperWindow = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
        var paper = new PaperData { Title = "A", IsCollapsed = true, IsVisible = true };
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Source-capacity invalidation test monitor");
        paper.CapsuleMonitorDeviceName = monitor.DeviceName;
        var state = new AppState
        {
            Papers = new List<PaperData> { paper },
            ExperimentalEdgeCapsuleHoverPreview = true,
            DeepCapsuleTitleMeasureCharacterLimit = EdgeCapsuleTitleLimit.Unlimited
        };
        typeof(AppController).GetProperty(nameof(AppController.State))!.SetValue(controller, state);
        SetCapacityCheckField(controller, "_windows", new Dictionary<string, PaperWindow>());
        SetCapacityCheckField(controller, "_advancedTransparentCapsuleIds", new HashSet<string>());
        var retainedSources = new Dictionary<PaperWindow, EdgeCapsuleQueueCompositionProxy>();
        SetCapacityCheckField(controller, "_edgeCapsuleQueueCompositionProxyByWindow", retainedSources);
        SetCapacityCheckField(paperWindow, "_paper", paper);
        SetCapacityCheckField(paperWindow, "_controller", controller);
        SetCapacityCheckField(paperWindow, "_edgeCapsule", presenter);
        SetCapacityCheckField(paperWindow, "_edgeCapsuleHost", host);
        var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", CapacityCheckFields)!;
        lifecycle.SetValue(paperWindow, Enum.Parse(lifecycle.FieldType, "Alive"));
        Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
            EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach the real capacity-check presenter");

        var capture = typeof(PaperWindow).GetMethod("CaptureEdgeCapsuleLayoutSnapshot", CapacityCheckFields)!
            .CreateDelegate<Func<EdgeCapsuleLayoutSnapshot>>(paperWindow);
        var invalidate = typeof(PaperWindow).GetMethod("InvalidateEdgeCapsule", CapacityCheckFields)!
            .CreateDelegate<Action<EdgeCapsuleDirty>>(paperWindow);
        EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty) => presenter.Reconcile(
            dirty, capture, () => null, frame => frame, host.Apply);
        // Defer asynchronous controller notifications; explicit Flush still executes the real
        // presenter, production layout getter and WPF/native apply used by the handoff path.
        using var transaction = presenter.DeferReconcileToVisualTransaction();
        try
        {
            presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, host.Dispatcher, Reconcile);
            var initial = presenter.AppliedPresentation;
            Check(initial.Visible && initial.IsUsable && host.MatchesPresentation(initial),
                "Initial native source agrees with the real presenter");
            CheckCapacityCheckNativeBounds(host.Handle, initial.HostBounds,
                "Initial native HWND has only its current bounded capacity");

            var member = new EdgeCapsuleQueueProxyMemberPlan(paper.Id, initial, initial, initial);
            proxy.Set("_members", new[] { new EdgeCapsuleQueueCompositionProxyMember(paperWindow, member, host.Handle) });
            proxy.Set("_plan", new EdgeCapsuleQueueProxyPlan("capacity-check", initial.HostBounds,
                initial.Edge, initial.WallDeviceX, initial.DpiScaleX, initial.DpiScaleY,
                200, false, new[] { member }));
            proxy.Proxy.RetainForQueueBrowsing();
            retainedSources.Add(paperWindow, proxy.Proxy);

            paper.Title = new string('W', 10);
            invalidate(EdgeCapsuleDirty.Measure);
            Check(proxy.Completions.Count == 1 && !proxy.Completions[0].AllowRetention,
                "A larger title explicitly requests source handoff before measure can resize the HWND");
            var pending = GetCapacityCheckField<EdgeCapsulePreviewSize?>(paperWindow,
                "_edgeCapsulePendingPreviewCapacity");
            Check(pending is { } requested &&
                requested.WidthDip > initial.HostBounds.Width / initial.DpiScaleX,
                "A failed handoff retains the larger capacity request");
            CheckCapacityCheckNativeBounds(host.Handle, initial.HostBounds,
                "Failed handoff leaves the actual retained HWND capacity unchanged");

            // Handoff itself may recapture layout after the title changed. That pure getter and
            // the downstream planner must still be incapable of resizing a retained live source.
            var constrained = capture();
            Check(presenter.PlanTargetPresentation(constrained).HostBounds == initial.HostBounds,
                "Reentrant planning keeps retained native capacity despite the new title metrics");
            Check(proxy.Completions.Count == 1,
                "Capturing layout cannot initiate another authority handoff");
            presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation, host.Dispatcher, Reconcile);
            CheckCapacityCheckNativeBounds(host.Handle, initial.HostBounds,
                "Real WPF/native apply remains bounded while the failed handoff owns the source");

            // A later request must survive too: do not lose the latest measure behind an older
            // pending capacity, and do not rely on another hover or preview session to resume it.
            paper.Title = new string('W', PaperTitles.MaxTitleLength);
            invalidate(EdgeCapsuleDirty.Measure);
            var newestPending = GetCapacityCheckField<EdgeCapsulePreviewSize?>(paperWindow,
                "_edgeCapsulePendingPreviewCapacity");
            Check(newestPending.HasValue && pending.HasValue &&
                newestPending.Value.WidthDip > pending.Value.WidthDip,
                "Later title growth updates the deferred capacity request");
            paperWindow.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            Check(GetCapacityCheckField<EdgeCapsulePreviewSize?>(paperWindow,
                    "_edgeCapsulePendingPreviewCapacity") == newestPending,
                "Resume cannot consume pending measure before source ownership is released");
            CheckCapacityCheckNativeBounds(host.Handle, initial.HostBounds,
                "Premature resume cannot grow a retained source");

            retainedSources.Remove(paperWindow);
            paperWindow.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            Check(GetCapacityCheckField<EdgeCapsulePreviewSize?>(paperWindow,
                    "_edgeCapsulePendingPreviewCapacity") == null,
                "Confirmed source release consumes the pending capacity request");
            // No dirty bits are supplied here. The production Resume method must have queued the
            // missing Measure/Presentation work itself; otherwise the old native width stays put.
            presenter.Flush(EdgeCapsuleDirty.None, host.Dispatcher, Reconcile);
            var grown = presenter.AppliedPresentation;
            Check(grown.HostBounds.Width > initial.HostBounds.Width &&
                grown.Bounds.Width > initial.Bounds.Width && host.MatchesPresentation(grown),
                "Release resumes the latest title measure and applies its larger real WPF surface");
            CheckCapacityCheckNativeBounds(host.Handle, grown.HostBounds,
                "Native capacity grows only after confirmed release");
        }
        finally
        {
            retainedSources.Clear();
            presenter.CancelTransition();
            presenter.ClearDeferredWork();
        }
        Console.WriteLine("PASS proxy-source-capacity-invalidation-and-resume");
    }

    private const BindingFlags CapacityCheckFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void SetCapacityCheckField(object owner, string name, object value) =>
        owner.GetType().GetField(name, CapacityCheckFields)!.SetValue(owner, value);
    private static T GetCapacityCheckField<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, CapacityCheckFields)!.GetValue(owner)!;
    private static void CheckCapacityCheckNativeBounds(IntPtr handle, DeviceScreenRect expected, string message) =>
        Check(GetProxyCheckWindowRect(handle, out var actual) &&
            new DeviceScreenRect(actual.Left, actual.Top, actual.Right, actual.Bottom) == expected, message);
}
