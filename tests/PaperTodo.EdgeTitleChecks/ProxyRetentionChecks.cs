using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void ProxyRetentionChecks()
    {
        ProxyBrowseAdmission();
        ProxyBrowseCapacity();
        ProxyRetentionLifecycle();
        ProxyRetainedAppliedGeometry();
        ProxySourceCapacityInvalidation();
        ProxyMaximumCapacityChecks();
        ProxyStaticPreacquisitionChecks();
        EdgePrewarmCoordinatorChecks();
        Console.WriteLine("PASS proxy-browse-admission-and-retention-lifecycle");
    }

    private static void ProxyBrowseCapacity()
    {
        var compact = new DeviceScreenRect(-300, 100, 0, 140);
        var preview = new DeviceScreenRect(-300, 100, 0, 460);
        var originalEnvelope = new DeviceScreenRect(-328, 100, 0, 1800);
        var capacity = EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(compact, preview, 1340);
        Check(capacity == 1200 && capacity > preview.Height - compact.Height,
            "Browse capacity includes successive downward owner anchors beyond one preview growth");
        var reserved = EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(originalEnvelope, capacity);
        Check(reserved.Left == originalEnvelope.Left && reserved.Top == originalEnvelope.Top &&
            reserved.Right == originalEnvelope.Right && reserved.Bottom == 3000,
            "Capacity preserves the original queue footprint, including followers below the work area");
        var output = EdgeCapsuleQueueProxyGeometry.OutputBounds(reserved);
        var successive = originalEnvelope with { Bottom = originalEnvelope.Bottom + 900 };
        Check(EdgeCapsuleQueueProxyGeometry.Contains(output,
                EdgeCapsuleQueueProxyGeometry.OutputBounds(successive)),
            "Successive downward browse translations fit the existing reserved output");
        Check(!EdgeCapsuleQueueProxyGeometry.Contains(output,
                EdgeCapsuleQueueProxyGeometry.OutputBounds(reserved with { Bottom = reserved.Bottom + 1 })),
            "Capacity stays finite: a successor needing more area must use the growth fallback");
        Check(EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(
                compact with { Top = 1400, Bottom = 1440 },
                preview with { Top = 1400, Bottom = 1760 }, 1340) == 320,
            "An owner below the work area still reserves its actual preview growth");
        Check(EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(compact,
                preview with { Bottom = 120 }, 100) == 0,
            "A smaller preview with no downward travel does not grow the output");
        Check(EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(default, preview, 1340) == 0 &&
            EdgeCapsuleQueueProxyGeometry.DownwardBrowseCapacity(compact, default, 1340) == 0,
            "Missing source geometry cannot request browse capacity");
        foreach (var shift in new[] { 0, -10 })
            Check(EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(originalEnvelope, shift) == originalEnvelope,
                "Nonpositive capacity never shrinks the existing authority envelope");
        Check(EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(default, 100).IsEmpty,
            "Empty geometry does not create an output window");
        var nearLimit = new DeviceScreenRect(0, int.MaxValue - 100, 100, int.MaxValue - 50);
        Check(EdgeCapsuleQueueProxyGeometry.WithDownwardCapacity(nearLimit, 100).Bottom == int.MaxValue,
            "Capacity arithmetic does not wrap a physical screen coordinate");
    }

    private static void ProxyRetainedAppliedGeometry()
    {
        using var host = NewProxyCheckHost("proxy-retained-check");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "Retained geometry test monitor");
        var compact = ProxyCheckFrame(monitor, EdgeCapsuleEdge.Right, 40);
        var expanded = ProxyCheckFrame(monitor, EdgeCapsuleEdge.Right, 40, preview: true);
        Check(host.Apply(expanded), "Apply original retained endpoint to a real WPF host");

        // PaperWindow is only the source adapter here: no application/controller/data is created.
        // Its raw applied-frame accessor reads this real host and the lifecycle field below.
        var paper = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PaperWindow).GetField("_edgeCapsuleHost", fields)!.SetValue(paper, host);
        var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", fields)!;
        lifecycle.SetValue(paper, Enum.Parse(lifecycle.FieldType, "Alive"));
        using var fixture = new ProxyLifecycleFixture();
        var memberPlan = new EdgeCapsuleQueueProxyMemberPlan("applied-shape", compact, compact, expanded);
        fixture.Set("_members", new[] { new EdgeCapsuleQueueCompositionProxyMember(paper, memberPlan, host.Handle) });
        fixture.Set("_plan", new EdgeCapsuleQueueProxyPlan("applied-check", expanded.HostBounds,
            expanded.Edge, expanded.WallDeviceX, expanded.DpiScaleX, expanded.DpiScaleY,
            200, false, new[] { memberPlan }));
        Check(host.TryGetAppliedPresentation(out var originalEndpoint) &&
            fixture.Proxy.HasCompatibleEndpoint(paper, originalEndpoint),
            "The bound source window accepts its verified real WPF endpoint");
        var unrelatedPaper = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
        Check(!fixture.Proxy.HasCompatibleEndpoint(unrelatedPaper, originalEndpoint),
            "Identical endpoint geometry cannot substitute another source window identity");
        var end = fixture.Get<long>("_animationStartedAtTimestamp") + Stopwatch.Frequency;
        Check(fixture.Proxy.TryGetPresentationAt(paper, end, out var initial) && initial == expanded,
            "Active transaction still samples its coordinated logical plan");
        var shouldSample = typeof(EdgeCapsuleQueueCompositionProxy)
            .GetMethod("ShouldDispatchPointerSample", fields)!
            .CreateDelegate<Func<DeviceScreenPoint?, bool>>(fixture.Proxy);
        var pointer = new DeviceScreenPoint(compact.InteractiveBounds.Left + 1,
            compact.InteractiveBounds.Top + 1);
        Check(shouldSample(pointer) && shouldSample(pointer) && shouldSample(null) && shouldSample(null),
            "Active animation continues sampling even when the physical cursor has not moved");
        fixture.Proxy.RetainForQueueBrowsing();
        Check(shouldSample(pointer) && !shouldSample(pointer),
            "A settled retained queue dispatches its first sample, then skips unchanged pointer and shape");

        Check(host.Apply(compact), "Apply a later local WPF shape without replacing the proxy");
        Check(shouldSample(pointer) && !shouldSample(pointer),
            "A WPF shape change resamples a stationary pointer exactly once");
        Check(fixture.Proxy.TryGetPresentationAt(paper, end, out var shrunk) && shrunk == compact,
            "Retained presentation follows the current real WPF shape instead of the old preview target");
        Check(shrunk != originalEndpoint && shrunk.HostBounds == originalEndpoint.HostBounds &&
            fixture.Proxy.HasCompatibleEndpoint(paper, shrunk),
            "A changed real WPF shape remains compatible without requiring whole-frame equality");
        var oldPreviewOnly = new DeviceScreenPoint(expanded.InteractiveBounds.Left + 1,
            expanded.InteractiveBounds.Bottom - 1);
        Check(EdgeCapsuleGeometry.Contains(expanded.InteractiveBounds, oldPreviewOnly) &&
            !EdgeCapsuleGeometry.Contains(shrunk.InteractiveBounds, oldPreviewOnly),
            "Collapsed retained geometry no longer exposes the former preview hit area");
        var disabled = compact with { IsHitTestVisible = false, InteractiveBounds = default,
            ContentOpacity = 0.4, TitleVisible = false };
        Check(host.Apply(disabled) && fixture.Proxy.TryGetPresentationAt(paper, end, out var changed) &&
            changed == disabled,
            "Retained presentation also follows current opacity, title and input eligibility");
        Check(host.TryGetAppliedPresentation(out var disabledEndpoint) &&
            fixture.Proxy.HasCompatibleEndpoint(paper, disabledEndpoint),
            "Endpoint compatibility preserves the native surface while WPF changes title, opacity or input");
        Check(!fixture.Proxy.HasCompatibleEndpoint(paper,
                ProxyCheckFrame(monitor, EdgeCapsuleEdge.Right, 41)) &&
            !fixture.Proxy.HasCompatibleEndpoint(paper,
                disabledEndpoint with { DpiScaleX = disabledEndpoint.DpiScaleX + 0.25 }),
            "A moved native endpoint or different DPI cannot reuse the retained surface identity");
        Check(shouldSample(pointer) && !shouldSample(pointer),
            "Changes to applied input eligibility resample the pointer without requiring movement");
        var movedPointer = pointer with { X = pointer.X + 1 };
        Check(shouldSample(movedPointer) && !shouldSample(movedPointer),
            "Pointer movement wakes a settled queue and the following unchanged sample is skipped");
        Check(shouldSample(null) && !shouldSample(null),
            "Losing cursor availability dispatches once instead of continually reconciling");
        Check(shouldSample(movedPointer) && !shouldSample(movedPointer),
            "Restoring cursor availability dispatches even at the previous physical position");

        lifecycle.SetValue(paper, Enum.Parse(lifecycle.FieldType, "Closing"));
        Check(shouldSample(movedPointer) && !shouldSample(movedPointer),
            "A source becoming unavailable resamples the stationary pointer");
        Check(!fixture.Proxy.TryGetPresentationAt(paper, end, out _),
            "A closing source cannot continue serving stale retained presentation");
        lifecycle.SetValue(paper, Enum.Parse(lifecycle.FieldType, "Alive"));
        var grownHost = compact with { HostBounds = compact.HostBounds with
            { Bottom = compact.HostBounds.Bottom + 1 } };
        Check(host.Apply(grownHost) && !fixture.Proxy.TryGetPresentationAt(paper, end, out _),
            "A changed native capacity invalidates the retained surface instead of using a stale target");
        Check(host.TryGetAppliedPresentation(out var grownEndpoint) &&
            !fixture.Proxy.HasCompatibleEndpoint(paper, grownEndpoint),
            "A real applied native-capacity change rejects endpoint retention");
        Check(host.Apply(EdgeCapsulePresentationFrame.Hidden) &&
            !fixture.Proxy.TryGetPresentationAt(paper, end, out _),
            "A hidden source cannot retain the old visible hit area");
        Check(host.TryGetAppliedPresentation(out var hiddenEndpoint) &&
            !fixture.Proxy.HasCompatibleEndpoint(paper, hiddenEndpoint),
            "A hidden real WPF endpoint rejects retention even for the original window");
    }

    private static EdgeCapsuleHost NewProxyCheckHost(string diagnosticId) =>
        EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin: 4, ChromeCornerRadius: 16, InnerCornerRadius: 15,
            OutlineThickness: 2, OutlineOverlap: 1, BodyHeight: 32,
            LeftPadding: 6, IconGap: 4, IconText: "✓", IconFontSize: 13,
            LabelFontSize: 12, LabelFontWeight: FontWeights.Normal, CloseToolTip: "Close",
            PaperBrush: Brushes.White, PaperBorderBrush: Brushes.Gray, OutlineBrush: Brushes.Blue,
            HoverBrush: Brushes.LightGray, IconBrush: Brushes.Gray,
            StrongTextBrush: Brushes.Black, TextBrush: Brushes.Gray,
            UiFontFamily: new FontFamily("Segoe UI"), SymbolFontFamily: new FontFamily("Segoe UI Symbol"),
            Language: XmlLanguage.GetLanguage("en-US"), Topmost: false, DiagnosticId: diagnosticId));

    private static EdgeCapsulePresentationFrame ProxyCheckFrame(
        MonitorGeometry monitor, EdgeCapsuleEdge edge, double top, bool preview = false,
        EdgeCapsuleVisualState visual = EdgeCapsuleVisualState.Resting)
    {
        var model = EdgeCapsuleModel.Initial with
        {
            State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                visual, EdgeCapsuleGestureState.Idle,
                EdgeCapsuleOpenOrigin.Normal),
            Placement = new EdgeCapsulePlacement(0, 0, 1),
            Preview = preview ? EdgeCapsulePreviewState.Open : EdgeCapsulePreviewState.Closed
        };
        return EdgeCapsuleTargetPlanner.Calculate(model,
            new EdgeCapsuleLayoutSnapshot(monitor, edge, top, 0,
                100, 28, 40, 300, 360, false, 1, null, 328, 360)).Docked.ToFrame();
    }

    private static void ProxyBrowseAdmission()
    {
        const string queue = "retention-check";
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        {
            var monitor = new MonitorGeometry("retention-monitor",
                new DeviceScreenRect(-2560, -100, 0, 1340), scale, scale);
            EdgeCapsuleQueueProxyCandidate Candidate(string id, double top,
                double targetTop, bool preview = false) => new(id, queue,
                ProxyCheckFrame(monitor, edge, top),
                ProxyCheckFrame(monitor, edge, top),
                ProxyCheckFrame(monitor, edge, targetTop, preview),
                EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 200),
                HostReady: true, Topmost: true, RetainedByCurrentProxy: false);
            var above = Candidate("above", 40, 40);
            var owner = Candidate("owner", 84, 84, preview: true);
            var follower = Candidate("follower", 128, 448);
            var candidates = new[] { above, owner, follower };

            var ordinary = EdgeCapsuleQueueProxyPolicy.TryCreate(queue, candidates);
            Check(ordinary != null && ordinary.Members.Select(m => m.PaperId)
                    .SequenceEqual(new[] { "follower" }),
                "Ordinary translation does not take over stationary WPF members");
            var browse = EdgeCapsuleQueueProxyPolicy.TryCreate(queue, candidates,
                includeStationaryMembers: true);
            Check(browse != null && browse.Members.Select(m => m.PaperId)
                    .SequenceEqual(new[] { "above", "owner", "follower" }),
                "Browse cover includes compatible stationary peers and the morphing owner");
            Check(EdgeCapsuleQueueProxyPolicy.TryCreate(queue, new[] { above, owner },
                    includeStationaryMembers: true) == null,
                "Pure WPF shape changes must not create a translation cover");
            Check(EdgeCapsuleQueueProxyPolicy.TryCreate(queue,
                    new[] { above, owner, follower with { HostReady = false } },
                    includeStationaryMembers: true) == null,
                "An ineligible moving member cannot pull stationary members into a new cover");

            foreach (var invalid in new[]
            {
                above with { HostReady = false },
                above with { Topmost = false },
                above with { QueueKey = "different-queue" },
                above with { Target = above.Target with { Visible = false } },
                above with { Target = ProxyCheckFrame(monitor, edge, 40) with
                    { HostBounds = above.Target.HostBounds with
                        { Bottom = above.Target.HostBounds.Bottom + 1 } } }
            })
            {
                var fresh = EdgeCapsuleQueueProxyPolicy.TryCreate(queue,
                    new[] { invalid, follower }, includeStationaryMembers: true);
                Check(fresh != null && fresh.Members.Count == 1 &&
                    fresh.Members[0].PaperId == follower.PaperId,
                    "Incompatible fresh sources stay under their existing real authority");
                Check(EdgeCapsuleQueueProxyPolicy.TryCreate(queue,
                        new[] { invalid with { RetainedByCurrentProxy = true }, follower },
                        includeStationaryMembers: true) == null,
                    "An incompatible retained source rejects admission instead of disappearing");
            }

            foreach (var authority in new[] { EdgeCapsuleVisualAuthority.FloatingDrag,
                EdgeCapsuleVisualAuthority.DockingOverlap, EdgeCapsuleVisualAuthority.AuthoritySwap })
            {
                var direct = EdgeCapsuleQueueProxyPolicy.TryCreate(queue,
                    new[] { owner with { Authority = authority }, follower },
                    includeStationaryMembers: true);
                Check(direct != null && direct.Members.Count == 1 &&
                    direct.Members[0].PaperId == follower.PaperId,
                    "Stationary admission preserves an independent drag/handoff authority");
            }

            var retained = above with
            {
                RetainedByCurrentProxy = true,
                Authority = EdgeCapsuleVisualAuthority.QueueTranslation,
                Motion = EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.Placement)
            };
            var successor = EdgeCapsuleQueueProxyPolicy.TryCreate(queue,
                new[] { retained, follower });
            Check(successor != null && successor.Members.Select(m => m.PaperId)
                    .SequenceEqual(new[] { "above", "follower" }),
                "Successor carries retained stationary peers even without browse expansion");
            Check(EdgeCapsuleQueueProxyPolicy.HasStableLiveSurfaceIdentity(
                    owner.Target, owner.Source),
                "A WPF morph can change visible shape while retaining the same live source");
            Check(!EdgeCapsuleQueueProxyPolicy.HasStableLiveSurfaceIdentity(
                    owner.Target with { DpiScaleX = scale + 0.25 }, owner.Source),
                "A DPI change invalidates the retained live source identity");

            foreach (var member in browse!.Members)
            {
                Check(EdgeCapsuleQueueProxyGeometry.Contains(browse.Envelope,
                        EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(member.Start)) &&
                    EdgeCapsuleQueueProxyGeometry.Contains(browse.Envelope, member.Target.HostBounds),
                    "Browse envelope contains stationary and moving sources at both endpoints");
            }
        }
    }

    private static void ProxyRetentionLifecycle()
    {
        // Call the real lifecycle methods with real DispatcherTimers and a recorded completion
        // callback. No DComp/native authority is simulated: these checks prove callback/timer
        // sequencing only; endpoint verification and visible handoff still require desktop checks.
        using (var retained = new ProxyLifecycleFixture())
        {
            retained.KeepAllowedCompletions = true;
            retained.CompletionTimer.Start();
            retained.FireCompletionTimer();
            Check(retained.Completions.SequenceEqual(new[] { (true, true) }),
                "Logical animation completion asks the controller whether preview retention is valid");
            Check(retained.SampleTimer.IsEnabled && !retained.CompletionTimer.IsEnabled,
                "A retained preview keeps input sampling without polling completion");
            Check(retained.Get<bool>("_retainedAfterAnimation"),
                "Verified browse retention enters the live applied-geometry phase");
            retained.Proxy.CompleteNow(success: true);
            Check(retained.Completions.SequenceEqual(new[] { (true, true), (true, false) }),
                "Explicit browse/environment completion must not request another retention");
            Check(!retained.SampleTimer.IsEnabled && !retained.CompletionTimer.IsEnabled,
                "Explicit completion suspends both timers before handing authority back");
            retained.Proxy.CompleteNow(success: true);
            Check(retained.Completions.Count == 2,
                "Reentrant explicit completion cannot request duplicate handoffs");
        }

        foreach (var failureRequested in new[] { false, true })
        using (var held = new ProxyLifecycleFixture())
        {
            held.KeepAllowedCompletions = true;
            held.FireCompletionTimer();
            Check(held.Proxy.TryReserveForSuccessor(),
                "A retained preview can reserve its current authority for a successor");
            Check(!held.SampleTimer.IsEnabled && !held.CompletionTimer.IsEnabled,
                "A successor hold stops old generation input and completion callbacks");
            Check(!held.Proxy.TryReserveForSuccessor(),
                "One current authority cannot be reserved by two successors");
            held.Proxy.CompleteNow(success: !failureRequested);
            held.Proxy.CompleteNow(success: true, allowBrowseRetention: true);
            Check(held.Completions.Count == 1,
                "Completion waits while a successor owns the publication boundary");
            held.Proxy.CompleteAfterFailedSuccessor(success: true);
            Check(held.Completions.Count == 2 &&
                held.Completions[1] == (!failureRequested, false),
                "Failed successor releases the hold and preserves a pending failure without retention");
        }

        using (var resumed = new ProxyLifecycleFixture(durationMilliseconds: 60000))
        {
            Check(resumed.Proxy.TryReserveForSuccessor(), "Reserve a moving predecessor");
            resumed.Proxy.CompleteAfterFailedSuccessor(success: true);
            Check(resumed.Completions.Count == 0 && resumed.SampleTimer.IsEnabled &&
                resumed.CompletionTimer.IsEnabled,
                "Admission failure during animation resumes the existing predecessor until its endpoint");
        }

        foreach (var (field, value) in new[]
        {
            ("_disposed", true), ("_starting", true), ("_finishing", true),
            ("_coverLost", true), ("_sourcesReleased", true), ("_coverPublished", false)
        })
        using (var unavailable = new ProxyLifecycleFixture())
        {
            unavailable.Set(field, value);
            Check(!unavailable.Proxy.TryReserveForSuccessor(),
                "Unavailable authority cannot become a retained successor source: " + field);
        }
    }

    private sealed class ProxyLifecycleFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        internal EdgeCapsuleQueueCompositionProxy Proxy { get; } =
            (EdgeCapsuleQueueCompositionProxy)RuntimeHelpers.GetUninitializedObject(
                typeof(EdgeCapsuleQueueCompositionProxy));
        internal DispatcherTimer SampleTimer { get; } = new() { Interval = TimeSpan.FromSeconds(1) };
        internal DispatcherTimer CompletionTimer { get; } = new() { Interval = TimeSpan.FromSeconds(1) };
        internal List<(bool Success, bool AllowRetention)> Completions { get; } = new();
        internal bool KeepAllowedCompletions { get; set; }

        internal ProxyLifecycleFixture(int durationMilliseconds = 200)
        {
            var frame = EdgeCapsulePresentationFrame.Hidden with
                { Surface = EdgeCapsuleSurfaceKind.DockedResting };
            Set("_plan", new EdgeCapsuleQueueProxyPlan("lifecycle-check",
                new DeviceScreenRect(0, 0, 100, 40), EdgeCapsuleEdge.Left,
                0, 1, 1, durationMilliseconds, true,
                new[] { new EdgeCapsuleQueueProxyMemberPlan("test", frame, frame, frame) }));
            Set("_members", Array.Empty<EdgeCapsuleQueueCompositionProxyMember>());
            // GetUninitializedObject skips field initializers. This fixture owns no compositor
            // visuals or cloaked sources, but real retention/routing methods still enumerate the
            // corresponding collections. Keep the ordinary WPF-source branch explicit instead of
            // relying on product code to accept a partially constructed proxy.
            var visualsField = typeof(EdgeCapsuleQueueCompositionProxy).GetField("_visuals", Fields)!;
            Set("_visuals", Activator.CreateInstance(visualsField.FieldType)!);
            Set("_cloakedRealSourceHandles", new HashSet<IntPtr>());
            Set("_sampleTimer", SampleTimer);
            Set("_completionTimer", CompletionTimer);
            Set("_coverPublished", true);
            Set("_completionRetrySuccess", true);
            Set("_pendingStartCompletionSuccess", true);
            Set("_pendingSuccessorCompletionSuccess", true);
            Set("_animationStartedAtTimestamp", Stopwatch.GetTimestamp());
            Set("_completed", (Action<EdgeCapsuleQueueCompositionProxy, bool, bool>)
                ((proxy, success, allowRetention) =>
                {
                    Completions.Add((success, allowRetention));
                    if (KeepAllowedCompletions && success && allowRetention)
                        proxy.RetainForQueueBrowsing();
                }));
        }

        internal void Set(string name, object value) =>
            typeof(EdgeCapsuleQueueCompositionProxy).GetField(name, Fields)!.SetValue(Proxy, value);
        internal T Get<T>(string name) =>
            (T)typeof(EdgeCapsuleQueueCompositionProxy).GetField(name, Fields)!.GetValue(Proxy)!;
        internal void FireCompletionTimer() =>
            typeof(EdgeCapsuleQueueCompositionProxy).GetMethod("OnCompletionTimerTick", Fields)!
                .Invoke(Proxy, new object?[] { null, EventArgs.Empty });
        public void Dispose()
        {
            SampleTimer.Stop();
            CompletionTimer.Stop();
        }
    }
}
