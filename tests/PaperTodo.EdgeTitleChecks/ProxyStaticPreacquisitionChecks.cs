using PaperTodo;

internal static partial class Program
{
    private static void ProxyStaticPreacquisitionChecks()
    {
        const string queue = "static-retention-check";
        Check(EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention(queue,
                Array.Empty<EdgeCapsuleQueueProxyCandidate>()) == null,
            "Static preacquisition requires an actual queue");
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        {
            var monitor = new MonitorGeometry("static-monitor",
                new DeviceScreenRect(-2560, -100, 0, 1340), scale, scale);
            EdgeCapsuleQueueProxyCandidate Settled(string id, EdgeCapsulePresentationFrame frame) =>
                new(id, queue, frame, frame, frame,
                    EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State),
                    HostReady: true, Topmost: true, RetainedByCurrentProxy: false,
                    Authority: EdgeCapsuleVisualAuthority.RealDocked);
            var resting = Settled("resting", ProxyCheckFrame(monitor, edge, 40));
            var hovered = Settled("hovered", ProxyCheckFrame(monitor, edge, 84,
                visual: EdgeCapsuleVisualState.Hovered));
            var active = Settled("active", ProxyCheckFrame(monitor, edge, 128,
                visual: EdgeCapsuleVisualState.Active));
            var candidates = new[] { resting, hovered, active };
            var plan = EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention(queue, candidates);
            Check(plan != null && plan.IsStaticPreacquisition && plan.DurationMilliseconds == 0,
                "Settled preacquisition has an explicit zero-duration plan without a fabricated animation");
            Check(plan!.Members.Select(member => member.PaperId)
                    .SequenceEqual(candidates.Select(candidate => candidate.PaperId)),
                "Static acquisition retains every compatible source in queue order");
            foreach (var member in plan.Members)
            {
                Check(member.Start == member.Source && member.Source == member.Target &&
                    !EdgeCapsuleQueueProxyPolicy.RequiresTranslation(member.Start, member.Target),
                    "Static acquisition preserves each source's existing presentation and position");
                Check(EdgeCapsuleQueueProxyGeometry.Contains(plan.Envelope, member.Source.HostBounds),
                    "Static output capacity contains every acquired real source");
            }
            Check(EdgeCapsuleQueueProxyPolicy.TryCreate(queue, candidates) == null &&
                EdgeCapsuleQueueProxyPolicy.TryCreate(queue, candidates, includeStationaryMembers: true) == null,
                "Ordinary transaction admission still rejects a purely stationary queue");

            var translated = ProxyCheckFrame(monitor, edge, 44);
            var changedShape = resting.Source with { ContentOpacity = 0.5 };
            var shiftedHost = resting.Source with { HostBounds = resting.Source.HostBounds with
                { Top = resting.Source.HostBounds.Top + 1, Bottom = resting.Source.HostBounds.Bottom + 1 } };
            var wrongDpi = resting.Source with { DpiScaleX = scale + 0.25 };
            foreach (var invalid in new[]
            {
                resting with { Motion = EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Placement, 200) },
                resting with { Motion = EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.Placement) },
                resting with { Source = changedShape },
                resting with { Target = changedShape },
                resting with { Target = translated },
                resting with { Start = shiftedHost, Source = shiftedHost, Target = shiftedHost },
                resting with { RetainedByCurrentProxy = true },
                resting with { Authority = EdgeCapsuleVisualAuthority.QueueTranslation },
                resting with { Authority = EdgeCapsuleVisualAuthority.FloatingDrag },
                resting with { HostReady = false },
                resting with { Topmost = false },
                resting with { QueueKey = "another-queue" },
                resting with { Start = wrongDpi, Source = wrongDpi, Target = wrongDpi },
                Settled("hidden", resting.Source with { Visible = false }),
                Settled("unusable", resting.Source with { HostBounds = default })
            })
            {
                Check(EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention(queue,
                        new[] { hovered, invalid, active }) == null,
                    "An unsettled, incompatible or independently owned member rejects the entire static acquisition");
            }
            foreach (var surface in new[] { EdgeCapsuleSurfaceKind.DockedPreview,
                EdgeCapsuleSurfaceKind.DockedSuppressed, EdgeCapsuleSurfaceKind.DockedRetracted,
                EdgeCapsuleSurfaceKind.DockedRetracting, EdgeCapsuleSurfaceKind.FloatingFree,
                EdgeCapsuleSurfaceKind.Hidden })
            {
                Check(EdgeCapsuleQueueProxyPolicy.TryCreateForStaticRetention(queue,
                        new[] { hovered, Settled("not-browsable", resting.Source with { Surface = surface }) }) == null,
                    "Static preacquisition cannot take a preview, suppressed, retracted or floating presentation");
            }
        }
        ProxyStaticStartupCompletion();
        Console.WriteLine("PASS proxy-static-preacquisition-admission");
    }

    private static void ProxyStaticStartupCompletion()
    {
        static void MakeStatic(ProxyLifecycleFixture fixture)
        {
            fixture.Set("_plan", fixture.Get<EdgeCapsuleQueueProxyPlan>("_plan") with
                { IsStaticPreacquisition = true, DurationMilliseconds = 0 });
            fixture.Set("_starting", true);
            fixture.KeepAllowedCompletions = true;
        }
        static void Finish(ProxyLifecycleFixture fixture, bool started) =>
            typeof(EdgeCapsuleQueueCompositionProxy).GetMethod("FinishStartup",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(fixture.Proxy, new object[] { started });

        using (var ready = new ProxyLifecycleFixture())
        {
            MakeStatic(ready);
            Check(!ready.Proxy.IsRetainedForQueueBrowsing,
                "A staged static source is not a completed warm queue");
            Finish(ready, started: true);
            Check(ready.Completions.SequenceEqual(new[] { (true, true) }) &&
                ready.Proxy.IsRetainedForQueueBrowsing,
                "Static startup immediately requests verified retention without an animation duration");
            Check(ready.SampleTimer.IsEnabled && !ready.CompletionTimer.IsEnabled,
                "Static retention starts input sampling without scheduling an animation completion timer");
        }

        foreach (var success in new[] { true, false })
        using (var interrupted = new ProxyLifecycleFixture())
        {
            MakeStatic(interrupted);
            interrupted.Proxy.CompleteNow(success);
            Check(interrupted.Completions.Count == 0,
                "Explicit completion waits until static source publication exits its startup boundary");
            Finish(interrupted, started: true);
            Check(interrupted.Completions.SequenceEqual(new[] { (success, false) }) &&
                !interrupted.Proxy.IsRetainedForQueueBrowsing,
                "Pending explicit completion wins over automatic static retention, including a successful release");
            Check(!interrupted.SampleTimer.IsEnabled && !interrupted.CompletionTimer.IsEnabled,
                "A forced static release does not start background sampling or a completion delay");
        }

        using (var failed = new ProxyLifecycleFixture())
        {
            MakeStatic(failed);
            Finish(failed, started: false);
            Check(failed.Completions.Count == 0 && !failed.Proxy.IsRetainedForQueueBrowsing &&
                !failed.CompletionTimer.IsEnabled,
                "A failed static startup cannot report a ready warm queue");
        }
        using (var moving = new ProxyLifecycleFixture())
        {
            moving.Set("_starting", true);
            Finish(moving, started: true);
            Check(moving.Completions.Count == 0 && !moving.Proxy.IsRetainedForQueueBrowsing,
                "Ordinary animated startup still waits for its normal endpoint before requesting retention");
        }
    }
}
