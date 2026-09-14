using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void ProxyMaximumCapacityChecks()
    {
        var products = new[]
        {
            new MaximumCapacityProduct("todo", PaperTypes.Todo, null, new(450, 400)),
            new MaximumCapacityProduct("markdown", PaperTypes.Note, null, new(460, 410)),
            new MaximumCapacityProduct("native-declared", PaperTypes.Note,
                MaximumCapacityDescriptor(PaperBodyPluginKind.Native, new(1000, 700)), new(1000, 700)),
            new MaximumCapacityProduct("native-default", PaperTypes.Note,
                MaximumCapacityDescriptor(PaperBodyPluginKind.Native), new(800, 600)),
            new MaximumCapacityProduct("web-declared", PaperTypes.Note,
                MaximumCapacityDescriptor(PaperBodyPluginKind.Web, new(1000, 700)), new(1000, 700))
        };
        var monitors = new[]
        {
            new MonitorGeometry("capacity-large", new(-1920, 0, 0, 1080), 1, 1),
            new MonitorGeometry("capacity-fractional", new(100, -200, 1700, 800), 1.25, 1.25),
            new MonitorGeometry("capacity-small", new(-640, -480, 0, 0), 2, 2)
        };
        foreach (var monitor in monitors)
        using (new MaximumCapacityMonitorScope(monitor))
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        foreach (var product in products)
        {
            using var fixture = new MaximumCapacityFixture(product, monitor, edge);
            fixture.ReserveBeforeFirstShow();
            var expected = new EdgeCapsulePreviewSize(
                Math.Min(product.Maximum.WidthDip, monitor.LocalWorkAreaDip.Width - 16),
                Math.Min(product.Maximum.HeightDip, monitor.LocalWorkAreaDip.Height - 16));
            var layout = fixture.Capture();
            var source = fixture.Presenter.PlanTargetPresentation(layout).ToFrame();
            var output = fixture.Window.CaptureEdgeCapsuleQueueProxyCapacity();
            var context = $"{product.Name}, {monitor.DeviceName}, {edge}";
            Check(layout.Monitor == monitor && layout.HostCapacityWidthDip == expected.WidthDip &&
                layout.HostCapacityHeightDip == expected.HeightDip,
                $"First-show source reserves the product maximum after the work-area clamp: {context}");
            Check(source.Visible && source.HostBounds == output.PreviewBounds,
                $"Real source planner and queue output reserve the same maximum before any preview: {context}");
            Check(source.HostBounds.Width == (int)Math.Round(expected.WidthDip * monitor.DpiScaleX, MidpointRounding.AwayFromZero) &&
                source.HostBounds.Height == (int)Math.Round(expected.HeightDip * monitor.DpiScaleY, MidpointRounding.AwayFromZero),
                $"Capacity remains in DIPs until physical target planning: {context}");
            Check(edge == EdgeCapsuleEdge.Left
                    ? source.HostBounds.Left == monitor.WorkArea.Left
                    : source.HostBounds.Right == monitor.WorkArea.Right,
                $"Maximum reservation preserves the selected wall: {context}");
            if (product.Descriptor?.Kind == PaperBodyPluginKind.Native)
                Check(!GetCapacityCheckField<bool>(fixture.Window, "_pluginMiniEffectiveMaximumLocked"),
                    "Native capacity metadata does not run first preferred-size describe or lock its result");
        }

        using (new MaximumCapacityMonitorScope(monitors[0]))
        foreach (var pending in new[] { new EdgeCapsulePreviewSize(120, 90), new(1100, 200), new(200, 760) })
        {
            using var fixture = new MaximumCapacityFixture(products[0], monitors[0], EdgeCapsuleEdge.Right);
            SetCapacityCheckField(fixture.Window, "_edgeCapsulePendingPreviewCapacity", pending);
            fixture.ReserveBeforeFirstShow();
            var layout = fixture.Capture();
            Check(layout.HostCapacityWidthDip == Math.Max(450, pending.WidthDip) &&
                layout.HostCapacityHeightDip == Math.Max(400, pending.HeightDip),
                "First-show reservation preserves both product maximum and each larger pending dimension");
            Check(GetCapacityCheckField<EdgeCapsulePreviewSize?>(fixture.Window,
                    "_edgeCapsulePendingPreviewCapacity") == null,
                "A successfully reserved first-show pending request is consumed");
        }

        NativeMaximumCapacityGrowth(products[3]);
        VisibleMaximumCapacityPreacquisition(products[0]);
        Console.WriteLine("PASS proxy-product-maximum-source-and-output-capacity");
    }

    private static void VisibleMaximumCapacityPreacquisition(MaximumCapacityProduct product)
    {
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Visible preacquisition capacity test monitor");
        using var fixture = new MaximumCapacityFixture(product, monitor, EdgeCapsuleEdge.Left);
        var controller = GetCapacityCheckField<AppController>(fixture.Window, "_controller");
        controller.State.ExperimentalEdgeCapsuleHoverPreview = false;
        EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty) => fixture.Presenter.Reconcile(
            dirty, fixture.Capture, () => null, frame => frame, fixture.Host.Apply);
        fixture.Presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        fixture.Presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
            fixture.Host.Dispatcher, Reconcile);
        var small = fixture.Presenter.AppliedPresentation;
        Check(small.Visible && fixture.Host.MatchesPresentation(small),
            "Preview-disabled startup applies a real compact source without a preview reservation");
        controller.State.ExperimentalEdgeCapsuleHoverPreview = true;
        var ready = true;
        var changed = false;
        fixture.WithoutIdlePrewarm(() => ready = fixture.Window.PrepareEdgeCapsulePreacquisitionCapacity(out changed));
        Check(!ready && changed,
            "Enabling preview grows an existing compact source and defers preacquisition");
        CheckCapacityCheckNativeBounds(fixture.Host.Handle, small.HostBounds,
            "Reservation alone cannot claim that the existing native source has already grown");
        Check(!fixture.Window.PrepareEdgeCapsulePreacquisitionCapacity(out var changedBeforeApply) && !changedBeforeApply,
            "Pending source presentation cannot be reacquired before its next applied frame");
        fixture.Presenter.Flush(EdgeCapsuleDirty.None, fixture.Host.Dispatcher, Reconcile);
        var grown = fixture.Presenter.AppliedPresentation;
        fixture.WithoutIdlePrewarm(() => ready = fixture.Window.PrepareEdgeCapsulePreacquisitionCapacity(out changed));
        Check(ready && !changed && grown.HostBounds.Height > small.HostBounds.Height &&
            grown.HostBounds == fixture.Window.CaptureEdgeCapsuleQueueProxyCapacity().PreviewBounds,
            "The next real applied frame makes the source ready with the same product maximum as output");
        CheckCapacityCheckNativeBounds(fixture.Host.Handle, grown.HostBounds,
            "Preacquisition readiness follows an actually enlarged native HWND");
    }

    private static void NativeMaximumCapacityGrowth(MaximumCapacityProduct product)
    {
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Native maximum fallback test monitor");
        using var fixture = new MaximumCapacityFixture(product, monitor, EdgeCapsuleEdge.Right);
        using var proxy = new ProxyLifecycleFixture();
        fixture.ReserveBeforeFirstShow();
        var validate = typeof(PaperWindow).GetMethod("ValidatePluginMiniPreferredSize", CapacityCheckFields)!
            .CreateDelegate<Func<EdgeCapsulePreviewSize, EdgeCapsulePreviewSize>>(fixture.Window);
        var preferred = new EdgeCapsulePreviewSize(1100, 760);
        Check(validate(preferred) == preferred &&
            GetCapacityCheckField<EdgeCapsulePreviewSize>(fixture.Window, "_pluginMiniEffectiveMaximum") == preferred,
            "Undeclared Native first preferred size can lock a maximum above the 800x600 reservation");
        var rejected = false;
        try { validate(preferred with { WidthDip = preferred.WidthDip + 1 }); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A later Native preferred size cannot silently grow its already locked maximum");

        var demand = preferred.Normalize(monitor.LocalWorkAreaDip.Width - 16,
            monitor.LocalWorkAreaDip.Height - 16);
        using var transaction = fixture.Presenter.DeferReconcileToVisualTransaction();
        EdgeCapsuleDirty Reconcile(EdgeCapsuleDirty dirty) => fixture.Presenter.Reconcile(
            dirty, fixture.Capture, () => null, frame => frame, fixture.Host.Apply);
        fixture.Presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        fixture.Presenter.Flush(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation,
            fixture.Host.Dispatcher, Reconcile);
        var initial = fixture.Presenter.AppliedPresentation;
        Check(initial.IsUsable && initial.Visible && fixture.Host.MatchesPresentation(initial),
            "Native maximum fallback starts with a real applied WPF source");
        var member = new EdgeCapsuleQueueProxyMemberPlan(fixture.Paper.Id, initial, initial, initial);
        proxy.Set("_members", new[] { new EdgeCapsuleQueueCompositionProxyMember(fixture.Window, member, fixture.Host.Handle) });
        proxy.Set("_plan", new EdgeCapsuleQueueProxyPlan("maximum-growth", initial.HostBounds,
            initial.Edge, initial.WallDeviceX, initial.DpiScaleX, initial.DpiScaleY,
            200, false, new[] { member }));
        proxy.Proxy.RetainForQueueBrowsing();
        fixture.Retained.Add(fixture.Window, proxy.Proxy);
        var prepare = typeof(PaperWindow).GetMethod("PrepareEdgeCapsuleHostCapacity", CapacityCheckFields)!
            .CreateDelegate<Func<EdgeCapsulePreviewSize, bool>>(fixture.Window);
        var initialCapacity = fixture.Capture();
        var needsGrowth = demand.WidthDip > initialCapacity.HostCapacityWidthDip ||
            demand.HeightDip > initialCapacity.HostCapacityHeightDip;
        Check(prepare(demand) == !needsGrowth,
            "Native first preferred-size growth waits for release unless the monitor clamp already fits");
        if (needsGrowth)
        {
            Check(proxy.Completions.Count == 1 && !proxy.Completions[0].AllowRetention &&
                GetCapacityCheckField<EdgeCapsulePreviewSize?>(fixture.Window,
                    "_edgeCapsulePendingPreviewCapacity") == demand,
                "Native growth requests verified handoff and preserves the demand on failure");
            Check(fixture.Presenter.PlanTargetPresentation(fixture.Capture()).HostBounds == initial.HostBounds,
                "Failed Native handoff cannot resize the retained source during a layout recapture");
            fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
            Check(GetCapacityCheckField<EdgeCapsulePreviewSize?>(fixture.Window,
                    "_edgeCapsulePendingPreviewCapacity") == demand,
                "Native growth cannot consume pending capacity while its source remains retained");
            CheckCapacityCheckNativeBounds(fixture.Host.Handle, initial.HostBounds,
                "Native source stays physically unchanged until its authority is released");
        }
        fixture.Retained.Clear();
        fixture.Window.ResumeEdgeCapsuleSourceInvalidationsAfterProxyRelease();
        fixture.Presenter.Flush(EdgeCapsuleDirty.None, fixture.Host.Dispatcher, Reconcile);
        var finalLayout = fixture.Capture();
        Check(finalLayout.HostCapacityWidthDip >= demand.WidthDip &&
            finalLayout.HostCapacityHeightDip >= demand.HeightDip &&
            GetCapacityCheckField<EdgeCapsulePreviewSize?>(fixture.Window,
                "_edgeCapsulePendingPreviewCapacity") == null,
            "Confirmed Native source release resumes the full legal preferred-size demand");
        Check(fixture.Presenter.AppliedPresentation.HostBounds ==
            fixture.Window.CaptureEdgeCapsuleQueueProxyCapacity().PreviewBounds,
            "Source and output converge on the locked Native maximum after growth");
        CheckCapacityCheckNativeBounds(fixture.Host.Handle, fixture.Presenter.AppliedPresentation.HostBounds,
            "The expanded Native fallback capacity reaches the actual HWND");
        fixture.Presenter.ClearDeferredWork();
    }

    private sealed record MaximumCapacityProduct(string Name, string PaperType,
        PaperBodyPluginDescriptor? Descriptor, EdgeCapsulePreviewSize Maximum);

    private static PaperBodyPluginDescriptor MaximumCapacityDescriptor(PaperBodyPluginKind kind,
        EdgeCapsulePreviewSize? maximum = null) => new(
            $"capacity-{kind}", "Capacity check", "", new Version(1, 0), "2.1", 1,
            kind, PaperBodyCapabilities.None, new HashSet<string>(), "", "", "capacity-check",
            Manifest: new PaperBodyPluginManifest
            {
                Kind = kind == PaperBodyPluginKind.Web ? "web" : "native",
                MiniEntry = kind == PaperBodyPluginKind.Web ? "mini.html" : "",
                MiniSize = kind == PaperBodyPluginKind.Web ? new() { Width = 320, Height = 220 } : null,
                MiniMaxSize = maximum is { } size ? new() { Width = size.WidthDip, Height = size.HeightDip } : null
            });

    private sealed class MaximumCapacityFixture : IDisposable
    {
        internal EdgeCapsuleHost Host { get; } = NewProxyCheckHost("maximum-capacity-check");
        internal EdgeCapsulePresenter Presenter { get; } = new();
        internal PaperWindow Window { get; } = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
        internal Dictionary<PaperWindow, EdgeCapsuleQueueCompositionProxy> Retained { get; } = new();
        internal PaperData Paper { get; }
        internal Func<EdgeCapsuleLayoutSnapshot> Capture { get; }

        internal MaximumCapacityFixture(MaximumCapacityProduct product, MonitorGeometry monitor, EdgeCapsuleEdge edge)
        {
            Paper = new PaperData { Type = product.PaperType, Title = "A", IsVisible = true, IsCollapsed = true,
                CapsuleMonitorDeviceName = monitor.DeviceName,
                CapsuleSide = edge == EdgeCapsuleEdge.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right,
                BodyProviderId = product.Descriptor?.Id ?? PaperBodyProviderIds.Markdown };
            var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            typeof(AppController).GetProperty(nameof(AppController.State))!.SetValue(controller,
                new AppState { Papers = new List<PaperData> { Paper }, ExperimentalEdgeCapsuleHoverPreview = true });
            SetCapacityCheckField(controller, "_windows", new Dictionary<string, PaperWindow>());
            SetCapacityCheckField(controller, "_advancedTransparentCapsuleIds", new HashSet<string>());
            SetCapacityCheckField(controller, "_edgeCapsuleQueueCompositionProxyByWindow", Retained);
            var registry = (PaperBodyPluginRegistry)RuntimeHelpers.GetUninitializedObject(typeof(PaperBodyPluginRegistry));
            var descriptors = new Dictionary<string, PaperBodyPluginDescriptor>(StringComparer.Ordinal);
            if (product.Descriptor != null) descriptors.Add(product.Descriptor.Id, product.Descriptor);
            SetCapacityCheckField(registry, "_descriptors", descriptors);
            SetCapacityCheckField(controller, "_paperBodyPlugins", registry);
            SetCapacityCheckField(Window, "_controller", controller);
            SetCapacityCheckField(Window, "_paper", Paper);
            SetCapacityCheckField(Window, "_edgeCapsule", Presenter);
            SetCapacityCheckField(Window, "_edgeCapsuleHost", Host);
            if (product.Descriptor != null) SetCapacityCheckField(Window, "_bodyDescriptor", product.Descriptor);
            typeof(DispatcherObject).GetFields(CapacityCheckFields)
                .Single(field => field.FieldType == typeof(Dispatcher)).SetValue(Window, Host.Dispatcher);
            var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", CapacityCheckFields)!;
            lifecycle.SetValue(Window, Enum.Parse(lifecycle.FieldType, "Alive"));
            Check(Presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach maximum-capacity source");
            Capture = typeof(PaperWindow).GetMethod("CaptureEdgeCapsuleLayoutSnapshot", CapacityCheckFields)!
                .CreateDelegate<Func<EdgeCapsuleLayoutSnapshot>>(Window);
        }

        internal void ReserveBeforeFirstShow()
            => WithoutIdlePrewarm(() => typeof(PaperWindow)
                .GetMethod("ReserveEdgeCapsulePreviewCapacityBeforeFirstShow", CapacityCheckFields)!
                .CreateDelegate<Action>(Window)());

        internal void WithoutIdlePrewarm(Action action)
        {
            // Keep the production first-show method intact, but do not execute its unrelated
            // idle DComp warmup in a capacity check. No preview provider/plugin is instantiated.
            var posted = new List<DispatcherOperation>();
            void Posted(object? sender, DispatcherHookEventArgs args)
            {
                if (args.Operation.Priority == DispatcherPriority.ApplicationIdle) posted.Add(args.Operation);
            }
            Host.Dispatcher.Hooks.OperationPosted += Posted;
            try
            {
                action();
            }
            finally
            {
                Host.Dispatcher.Hooks.OperationPosted -= Posted;
                foreach (var operation in posted) operation.Abort();
            }
        }

        public void Dispose()
        {
            Retained.Clear();
            Presenter.CancelTransition();
            Presenter.ClearDeferredWork();
            Host.Dispose();
        }
    }

    private sealed class MaximumCapacityMonitorScope : IDisposable
    {
        private static readonly FieldInfo Cache = typeof(WindowWorkAreaHelper)
            .GetField("_cachedMonitors", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previous = Cache.GetValue(null);

        internal MaximumCapacityMonitorScope(MonitorGeometry monitor)
        {
            // Replace monitor discovery, preserving all production capacity/clamp/layout code.
            // Hidden hosts have no HWND DPI override; the fake handle cannot match a live monitor.
            var entryType = typeof(WindowWorkAreaHelper).GetNestedType("MonitorEntry", BindingFlags.NonPublic)!;
            var rectangleType = typeof(WindowWorkAreaHelper).GetNestedType("NativeRect", BindingFlags.NonPublic)!;
            var rectangle = Activator.CreateInstance(rectangleType)!;
            foreach (var (field, value) in new[] { ("Left", monitor.WorkArea.Left), ("Top", monitor.WorkArea.Top),
                         ("Right", monitor.WorkArea.Right), ("Bottom", monitor.WorkArea.Bottom) })
                rectangleType.GetField(field)!.SetValue(rectangle, value);
            var entry = Activator.CreateInstance(entryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { new IntPtr(-1), monitor.DeviceName, rectangle, true, monitor.DpiScaleX, monitor.DpiScaleY }, null)!;
            var entries = Array.CreateInstance(entryType, 1);
            entries.SetValue(entry, 0);
            Cache.SetValue(null, entries);
        }

        public void Dispose() => Cache.SetValue(null, _previous);
    }
}
