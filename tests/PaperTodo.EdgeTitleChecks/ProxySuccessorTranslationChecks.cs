using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    private static void ProxySuccessorTranslation()
    {
        var iid = typeof(IDCompositionDesktopDevice).GUID;
        Marshal.ThrowExceptionForHR(CreateProxyEvidenceDevice(IntPtr.Zero, ref iid, out var pointer));
        using var device = new IDCompositionDesktopDevice(pointer);

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        foreach (var duration in new[] { 100, 200 })
        {
            int Px(double value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
            var wall = edge == EdgeCapsuleEdge.Left ? -1280 : 1920;
            var hostLeft = edge == EdgeCapsuleEdge.Left ? wall : wall - Px(400);
            var output = new DeviceScreenRect(hostLeft, 0, hostLeft + Px(400), Px(2000));
            EdgeCapsulePresentationFrame Frame(int top, int width, int height, double opacity)
            {
                var left = edge == EdgeCapsuleEdge.Left ? wall : wall - Px(width);
                var bounds = new DeviceScreenRect(left, Px(top), left + Px(width), Px(top + height));
                return new EdgeCapsulePresentationFrame(true, EdgeCapsuleSurfaceKind.DockedResting,
                    bounds, new DeviceScreenRect(hostLeft, Px(top), hostLeft + Px(400), Px(top + 700)),
                    EdgeCapsuleGeometry.InteractiveBoundsForAppliedBounds(bounds, edge, scale, scale,
                        EdgeCapsuleLayout.WindowChromeMargin),
                    edge, Px(width - 24), wall, scale, scale, 24,
                    opacity, opacity, false, true, false);
            }

            var start = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            long At(long origin, double milliseconds) => origin +
                (long)Math.Round(Stopwatch.Frequency * milliseconds / 1000.0);
            var firstPlan = new EdgeCapsuleQueueProxyMemberPlan("successor",
                Frame(100, 160, 40, 0.6), Frame(100, 160, 40, 0.6), Frame(420, 120, 44, 0.9));
            using var first = new SuccessorTranslationFixture(device, firstPlan, output, duration, start);
            first.Rebase(start);
            first.CheckTimeline();
            var captured = first.Sample(At(start, duration * 0.2));
            var secondStart = At(start, duration * 0.2 + 40);
            var secondPlan = new EdgeCapsuleQueueProxyMemberPlan("successor",
                captured, firstPlan.Target, Frame(100, 140, 40, 1));
            using var second = new SuccessorTranslationFixture(device, secondPlan, output,
                duration, secondStart, first);

            // Exercise the production rebase itself. Native endpoint publication may take 25ms
            // after plan capture; no sleeps or dispatcher races are needed to make that explicit.
            second.Rebase(At(start, duration * 0.2 + 25));
            Check(Math.Abs(second.StartHost.Top - captured.Bounds.Top) > Px(40),
                "The rebase fixture must advance by more than a compact capsule height");
            second.CheckTimeline();
            second.CheckInputAtStartAndEnd();

            var thirdPlan = new EdgeCapsuleQueueProxyMemberPlan("successor",
                second.Sample(At(secondStart, duration * 0.1)), secondPlan.Target, Frame(500, 180, 42, 0.7));
            using var third = new SuccessorTranslationFixture(device, thirdPlan, output, duration,
                At(secondStart, duration * 0.1 + 40), second);
            third.Rebase(At(secondStart, duration * 0.1 + 25));
            third.CheckTimeline();
            third.CheckInputAtStartAndEnd();
            using var retarget = new SuccessorTranslationFixture(device, firstPlan, output, duration, start);
            retarget.Rebase(start);
            retarget.CheckRetarget();
        }
        Console.WriteLine("PASS successor/third-generation live translation, hit geometry, cover sources and endpoint coordinates (both edges, four DPIs, normal/short durations)");
    }

    // Inject only the already-owned plan, visual and clock. RebaseVisualStarts configures a real
    // DComp visual, and assertions call existing production sampling/input entry points. This
    // fixture also compiles against the old implementation for a deterministic regression A/B;
    // it does not claim a physical desktop frame was displayed.
    private sealed class SuccessorTranslationFixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ProxyType = typeof(EdgeCapsuleQueueCompositionProxy);
        private readonly EdgeCapsuleQueueCompositionProxy _proxy;
        private readonly PaperWindow _paper;
        private readonly EdgeCapsuleQueueProxyMemberPlan _plan;
        private readonly object _state;
        private readonly IDCompositionVisual2 _visual;
        private readonly DeviceScreenRect _output;
        private readonly int _duration;
        private readonly long _start;
        private readonly IntPtr _handle = new(73);

        internal SuccessorTranslationFixture(IDCompositionDesktopDevice device,
            EdgeCapsuleQueueProxyMemberPlan plan, DeviceScreenRect output, int duration, long start,
            SuccessorTranslationFixture? predecessor = null)
        {
            _plan = plan; _output = output; _duration = duration; _start = start;
            _paper = predecessor?._paper ?? CreateInputEligiblePaper();
            _proxy = (EdgeCapsuleQueueCompositionProxy)RuntimeHelpers.GetUninitializedObject(ProxyType);
            var member = new EdgeCapsuleQueueCompositionProxyMember(_paper, plan, _handle);
            Set("_plan", new EdgeCapsuleQueueProxyPlan("successor", output, plan.Target.Edge,
                plan.Target.WallDeviceX, plan.Target.DpiScaleX, plan.Target.DpiScaleY, duration, true, [plan]));
            Set("_members", new[] { member });
            Set("_device", device);
            Set("_sampleTimer", new System.Windows.Threading.DispatcherTimer());
            Set("_completionTimer", new System.Windows.Threading.DispatcherTimer());
            Set("_outputBounds", output);
            Set("_animationStartedAtTimestamp", start);
            Set("_coverPublished", true);
            if (predecessor != null) Set("_predecessor", predecessor._proxy);

            device.CreateVisual(out IDCompositionVisual2 visual).CheckError();
            _visual = visual;
            var statesField = ProxyType.GetField("_visuals", Private)!;
            var states = (IList)Activator.CreateInstance(statesField.FieldType)!;
            var stateType = statesField.FieldType.GetGenericArguments()[0];
            _state = Activator.CreateInstance(stateType, nonPublic: true)!;
            void State(string name, object value) => stateType.GetProperty(name)!.SetValue(_state, value);
            var source = plan.Source.HostBounds;
            var presented = EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(plan.Start);
            State("Member", member); State("PresentedSourceHandle", _handle);
            State("SourceBounds", source); State("Visual", visual);
            State("StartOffsetX", (float)(presented.Left - output.Left));
            State("StartOffsetY", (float)(presented.Top - output.Top));
            State("TargetOffsetX", (float)(plan.Target.HostBounds.Left - output.Left));
            State("TargetOffsetY", (float)(plan.Target.HostBounds.Top - output.Top));
            states.Add(_state);
            statesField.SetValue(_proxy, states);
        }

        private static PaperWindow CreateInputEligiblePaper()
        {
            var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            typeof(AppController).GetProperty(nameof(AppController.State))!.SetValue(controller,
                new AppState { ExperimentalEdgeCapsuleHoverPreview = true });
            typeof(AppController).GetField("_edgeCapsuleQueueCompositionProxyByWindow", Private)!
                .SetValue(controller, new Dictionary<System.Windows.Window, EdgeCapsuleQueueCompositionProxy>());
            var paper = (PaperWindow)RuntimeHelpers.GetUninitializedObject(typeof(PaperWindow));
            typeof(PaperWindow).GetField("_controller", Private)!.SetValue(paper, controller);
            typeof(PaperWindow).GetField("_paper", Private)!.SetValue(paper,
                new PaperData { Id = "successor", IsVisible = true, IsCollapsed = true });
            var lifecycle = typeof(PaperWindow).GetField("_windowLifecycle", Private)!;
            lifecycle.SetValue(paper, Enum.Parse(lifecycle.FieldType, "Alive"));
            var presenter = new EdgeCapsulePresenter();
            Check(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
                EdgeCapsulePaperForm.Collapsed, false)).Accepted, "Attach input fixture presenter");
            typeof(PaperWindow).GetField("_edgeCapsule", Private)!.SetValue(paper, presenter);
            Check(paper.CanRouteEdgeCapsuleQueueProxyInput, "Input fixture has normal preview eligibility");
            return paper;
        }

        private void Set(string name, object value) => ProxyType.GetField(name, Private)!.SetValue(_proxy, value);
        internal DeviceScreenRect StartHost
        {
            get
            {
                var left = _output.Left + (float)_state.GetType().GetProperty("StartOffsetX")!.GetValue(_state)!;
                var top = _output.Top + (float)_state.GetType().GetProperty("StartOffsetY")!.GetValue(_state)!;
                return new((int)left, (int)top, (int)left + _plan.Source.HostBounds.Width,
                    (int)top + _plan.Source.HostBounds.Height);
            }
        }
        internal void Rebase(long timestamp) => ProxyType.GetMethod("RebaseVisualStarts", Private)!
            .Invoke(_proxy, [timestamp]);
        internal EdgeCapsulePresentationFrame Sample(long timestamp)
        {
            Check(_proxy.TryGetPresentationAt(_paper, timestamp, out var frame), "Sample live generation");
            return frame;
        }

        internal void CheckTimeline()
        {
            foreach (var fraction in new[] { 0.0, 0.25, 0.5, 1.0 })
            {
                var ticks = (long)Math.Round(Stopwatch.Frequency * _duration / 1000.0);
                var now = _start + (long)Math.Round(ticks * fraction);
                var q = (now - _start) / (double)ticks;
                // The polynomial installed by CreateEaseOutCubicAnimation, evaluated separately
                // from the logical frame policy. Its origin must be the actual configured visual.
                var delta = _plan.Target.HostBounds.Top - StartHost.Top;
                var expectedTop = (int)Math.Round(StartHost.Top + delta * (3*q - 3*q*q + q*q*q),
                    MidpointRounding.AwayFromZero);
                var frame = Sample(now);
                Check(frame.Bounds.Top == expectedTop,
                    $"Successor presentation origin must match configured live DComp origin: expected={expectedTop}, actual={frame.Bounds.Top}, progress={fraction}");
                var logical = EdgeCapsuleQueueProxyPolicy.SampleLogicalFrame(_plan, _start, _duration, now);
                Check(frame.Bounds.Width == logical.Bounds.Width && frame.Bounds.Height == logical.Bounds.Height &&
                    (frame with { Bounds = logical.Bounds, InteractiveBounds = logical.InteractiveBounds }) == logical,
                    "Translation correction preserves WPF shape, material, hit eligibility and native endpoint capacity");
                Check(frame.InteractiveBounds.Top - frame.Bounds.Top ==
                    logical.InteractiveBounds.Top - logical.Bounds.Top,
                    "Visible shape and hit bounds keep the same local offset");
                var sources = (IEnumerable)ProxyType.GetMethod("SnapshotStaticCoverSources", Private)!
                    .Invoke(_proxy, [now])!;
                var cover = sources.Cast<object>().Single();
                var coverBounds = (DeviceScreenRect)cover.GetType().GetProperty("PresentedBounds")!.GetValue(cover)!;
                Check(coverBounds == EdgeCapsuleQueueProxyPolicy.PresentedHostBounds(frame),
                    "Successor union cover samples the same current presentation as pointer routing");
                if (fraction == 1) Check(frame == _plan.Target, "Canonical terminal frame remains exact");
            }
        }

        internal void CheckInputAtStartAndEnd()
        {
            try
            {
                foreach (var end in new[] { false, true })
                {
                    // Pin input to an endpoint independently of wall-clock speed; no timing race.
                    Set("_animationStartedAtTimestamp", Stopwatch.GetTimestamp() +
                        (end ? -60 : 60) * Stopwatch.Frequency);
                    Check(_proxy.TryGetPresentation(_paper, out var frame), "Sample pinned input endpoint");
                    var hit = frame.InteractiveBounds;
                    var point = new DeviceScreenPoint(hit.Left + hit.Width / 2.0, hit.Top + hit.Height / 2.0);
                    var contains = ProxyType.GetMethod("ContainsVisual", Private)!
                        .CreateDelegate<Func<DeviceScreenPoint, bool>>(_proxy);
                    Check(contains(point) && _proxy.TryResolveInputTarget(point, out var handle, out var endpoint) &&
                        handle == _handle && endpoint.X == point.X &&
                        endpoint.Y == _plan.Target.HostBounds.Top + point.Y - frame.Bounds.Top,
                        "Visible rebased capsule accepts input and forwards its exact local position to the real endpoint");
                    if (!end)
                    {
                        var stale = _plan.Start.InteractiveBounds;
                        var stalePoint = new DeviceScreenPoint(stale.Left + stale.Width / 2.0,
                            stale.Top + stale.Height / 2.0);
                        if (!EdgeCapsuleGeometry.Contains(hit, stalePoint))
                            Check(!contains(stalePoint) && !_proxy.TryResolveInputTarget(stalePoint, out _, out _),
                                "Old plan coordinates cannot remain a ghost input target after rebase");
                    }
                }
            }
            finally { Set("_animationStartedAtTimestamp", _start); }
        }

        internal void CheckRetarget()
        {
            var oldClock = Stopwatch.GetTimestamp() - Stopwatch.Frequency * _duration / 2000;
            Set("_animationStartedAtTimestamp", oldClock);
            var startHost = StartHost;
            var current = (EdgeCapsuleQueueProxyPlan)ProxyType.GetField("_plan", Private)!.GetValue(_proxy)!;
            var nextMember = _plan with { Start = Sample(Stopwatch.GetTimestamp()),
                Source = _plan.Target, Target = _plan.Start };
            var next = current with { Members = [nextMember] };
            Check(EdgeCapsuleQueueProxyPolicy.CanRetarget(current, next, _output),
                "Reversed same-source translation can reuse live visuals");
            Check(!EdgeCapsuleQueueProxyPolicy.CanRetarget(current,
                    next with { QueueKey = "other" }, _output) &&
                  !EdgeCapsuleQueueProxyPolicy.CanRetarget(current,
                    next with { Members = [nextMember with { PaperId = "replacement" }] }, _output),
                "Changing queue/source requires a successor handoff");
            Check(!EdgeCapsuleQueueProxyPolicy.CanRetarget(current, next,
                    _plan.Target.HostBounds), "Retarget cannot escape its native output capacity");
            var extraReserve = next with
            {
                Envelope = _output with { Top = _output.Top - 50, Bottom = _output.Bottom + 50 }
            };
            Check(EdgeCapsuleQueueProxyGeometry.Contains(_output,
                    EdgeCapsuleQueueProxyPolicy.RequiredTranslationBounds(extraReserve)),
                "A successor's optional future reserve cannot reject motion that already fits");
            var outsideHost = nextMember.Target.HostBounds with
            {
                Top = _output.Bottom - nextMember.Target.HostBounds.Height + 1,
                Bottom = _output.Bottom + 1
            };
            var outsideMotion = next with
            {
                Members = [nextMember with { Target = nextMember.Target with { HostBounds = outsideHost } }]
            };
            Check(!EdgeCapsuleQueueProxyGeometry.Contains(_output,
                    EdgeCapsuleQueueProxyPolicy.RequiredTranslationBounds(outsideMotion)),
                "A successor whose real target exceeds the output must still be rejected");
            var member = new EdgeCapsuleQueueCompositionProxyMember(_paper, nextMember, _handle);
            long newClock = 0;
            EdgeCapsulePresentationFrame expectedStart = default;
            var releasing = current with
            {
                Members = [_plan with
                {
                    Start = _plan.Start with { Surface = EdgeCapsuleSurfaceKind.DockedRetracted }
                }]
            };
            Check(!EdgeCapsuleQueueProxyPolicy.CanRetarget(releasing, next, _output) &&
                  !EdgeCapsuleQueueProxyPolicy.CanRetarget(current, releasing, _output),
                "Retarget cannot cross the native interactive/passthrough boundary in either direction");
            Set("_plan", releasing);
            Check(_proxy.TryReserveForSuccessor(), "Reserve the expanding queue for a new placement");
            var endpointCommits = 0;
            Check(!_proxy.TryRetarget(next, [member], _ => { endpointCommits++; return true; }, _ => true) &&
                    endpointCommits == 0 &&
                    ReferenceEquals(ProxyType.GetField("_plan", Private)!.GetValue(_proxy), releasing),
                "Production retarget rejects input-role changes before moving sources or publishing a new role");
            // The rejected request retains the old authority. Restore the compatible
            // plan in this fixture, then exercise the normal successful retarget.
            Set("_plan", current);
            Check(_proxy.TryRetarget(next, [member], _ => true, timestamp =>
            {
                newClock = timestamp;
                expectedStart = EdgeCapsuleQueueProxyPolicy.SamplePresentedFrame(_plan,
                    new DeviceScreenPoint(startHost.Left, startHost.Top),
                    new DeviceScreenPoint(_plan.Target.HostBounds.Left, _plan.Target.HostBounds.Top),
                    oldClock, _duration, timestamp);
                return true;
            }), "Production retarget consumes the reversed placement");
            var rebased = Sample(newClock);
            Check(rebased.Bounds.Top == expectedStart.Bounds.Top,
                "Retarget begins at the current visible position after endpoint work");
            Check(Sample(newClock + Stopwatch.Frequency) == nextMember.Target,
                "Retarget reaches the exact reversed endpoint");
        }

        public void Dispose()
        {
            ((System.Windows.Threading.DispatcherTimer)ProxyType.GetField("_sampleTimer", Private)!.GetValue(_proxy)!).Stop();
            ((System.Windows.Threading.DispatcherTimer)ProxyType.GetField("_completionTimer", Private)!.GetValue(_proxy)!).Stop();
            (_state.GetType().GetProperty("OffsetXAnimation")!.GetValue(_state) as IDisposable)?.Dispose();
            (_state.GetType().GetProperty("OffsetYAnimation")!.GetValue(_state) as IDisposable)?.Dispose();
            _visual.Dispose();
        }
    }
}
