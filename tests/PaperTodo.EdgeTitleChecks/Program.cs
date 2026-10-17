using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static int assertions;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            DetachedPointerAdmissionChecks();
            CycleAndUnicode();
            Console.WriteLine("PASS title-cycle-and-unicode");
            QueueInstanceKeys();
            Console.WriteLine("PASS queue-instance-key-codec");
            QueueMagnet();
            Console.WriteLine("PASS queue-magnet-merge-detach-reorder");
            EdgeQueueAvoidance();
            Console.WriteLine("PASS edge-wide-preview-avoidance");
            Persistence();
            Console.WriteLine("PASS legacy-and-zero-persistence");
            Geometry();
            Console.WriteLine("PASS title-presentation-and-transition-geometry");
            HostContentVisibility();
            Console.WriteLine("PASS host-title-plugin-and-icon-slot-layout");
            QueuedPreviewTransactions();
            Console.WriteLine("PASS queued-preview-transaction-ordering");
            Console.WriteLine($"Edge title checks: {assertions} assertions passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    // A free queue narrows (monitor, side) with an instance id. The whole backward-compatibility
    // story rests on one invariant: an empty id must compose to the byte-identical pre-D-058 key.
    private static void QueueInstanceKeys()
    {
        const string edge = "DISPLAY1|right";
        Check(EdgeCapsuleQueueIds.Compose(edge, "") == edge, "Empty id keeps the legacy key byte-identical");
        Check(EdgeCapsuleQueueIds.Compose(edge, null) == edge, "Null id keeps the legacy key");
        Check(EdgeCapsuleQueueIds.Compose(edge, "q1") == $"{edge}#q1", "Instance id appends after the separator");

        // Normalization is what keeps hand-edited or hostile data from producing bogus keys.
        Check(EdgeCapsuleQueueIds.Normalize(null) == "", "Null id");
        Check(EdgeCapsuleQueueIds.Normalize("   ") == "", "Whitespace-only id");
        Check(EdgeCapsuleQueueIds.Normalize("!!!") == "", "Id with no usable characters");
        Check(EdgeCapsuleQueueIds.Normalize(" Q1 ") == "q1", "Trim and lowercase");
        Check(EdgeCapsuleQueueIds.Normalize("a-b_c") == "abc", "Disallowed characters are dropped, not rejected");
        Check(EdgeCapsuleQueueIds.Compose(edge, " Q1 ") == $"{edge}#q1", "Compose normalizes before appending");
        Check(EdgeCapsuleQueueIds.Normalize(new string('x', 64)).Length == EdgeCapsuleQueueIds.MaxLength,
            "Over-long id is truncated");

        Check(EdgeCapsuleQueueIds.TrySplit($"{edge}#q1", out var splitEdge, out var splitId) &&
              splitEdge == edge && splitId == "q1", "Split a suffixed key");
        Check(!EdgeCapsuleQueueIds.TrySplit(edge, out var plainEdge, out var plainId) &&
              plainEdge == edge && plainId == "", "Unsuffixed key splits to an empty id");

        // The primary display serializes to an empty monitor segment; the separator must survive.
        Check(EdgeCapsuleQueueIds.TrySplit("|left#a", out var bareEdge, out var bareId) &&
              bareEdge == "|left" && bareId == "a", "Empty monitor segment");

        Check(EdgeCapsuleQueueIds.StripInstance($"{edge}#q1") == edge, "Strip removes the instance");
        Check(EdgeCapsuleQueueIds.StripInstance(edge) == edge, "Strip is a no-op without an instance");
        Check(EdgeCapsuleQueueIds.StripInstance("|right#a") == "|right", "Strip preserves an empty monitor segment");

        Check(EdgeCapsuleQueueIds.New() != EdgeCapsuleQueueIds.New(), "New ids differ");
        Check(EdgeCapsuleQueueIds.New().Length <= EdgeCapsuleQueueIds.MaxLength, "New id fits the length bound");
    }

    // The magnet is a Schmitt trigger: enter at 18 DIP, leave at 32 DIP, dead band between.
    // Everything it decides must be derivable from (position, candidates, held), so the whole
    // behaviour is checkable without a window, a pointer, or a clock.
    private static void QueueMagnet()
    {
        const double slot = 50;   // capsule height 46 + standard gap 4
        const double height = 46;

        EdgeCapsuleQueueMagnetPlan Evaluate(double center, string source,
            EdgeCapsuleQueueMagnetCandidate[] candidates, string held = "") =>
            EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
                center, source, candidates, slot, height,
                EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
                EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
                held));

        // Queue "a" spans one slot at top 100; "b" sits far below at 600.
        EdgeCapsuleQueueMagnetCandidate[] TwoQueues(int excludedInA = -1) =>
        [
            new("a", 1, 100, slot, excludedInA),
            new("b", 1, 600, slot, -1)
        ];

        Check(Evaluate(100, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.Merge,
            "Dragging onto a queue merges into it");
        Check(Evaluate(100, "", TwoQueues()).TargetQueueKey == "a", "Merges into the nearest queue");

        // Nothing within merge range -> the capsule becomes its own queue at the drop height.
        var fresh = Evaluate(300, "", TwoQueues());
        Check(fresh.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue, "A far drop starts a new queue");
        Check(Math.Abs(fresh.NewQueueStartTopDip - (300 - height / 2)) < 0.001,
            "A new queue anchors the drop by its capsule top, not its centre");

        // Queue "a" occupies 100 +/- 23, so its span is [77, 123]. The dead band is therefore
        // centres 141..155 (distance 18..32): too far to acquire from scratch, close enough to keep
        // holding. That asymmetry is the whole point of the trigger.
        const double deadBandCentre = 148;   // 25 DIP from the span
        Check(Evaluate(deadBandCentre, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "The dead band is too far to acquire a queue from scratch");
        Check(Evaluate(deadBandCentre, "", TwoQueues(), held: "a").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.Merge,
            "The dead band keeps an already-held queue attached");

        const double beyondDetach = 160;     // 37 DIP from the span
        Check(Evaluate(beyondDetach, "", TwoQueues(), held: "a").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A held queue is released once the detach distance is exceeded");

        // Same rules govern the source queue, so leaving it is distance-based too.
        var sourceQueue = new EdgeCapsuleQueueMagnetCandidate("src", 3, 100, slot, 1);
        Check(Evaluate(100 + 2 * slot, "src", [sourceQueue], held: "src").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.Reorder,
            "Staying inside the source queue reorders rather than merging");
        Check(Evaluate(400, "src", [sourceQueue], held: "src").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "Dragging well past the source queue leaves it");

        // A single-member source queue has nothing left behind: any displacement detaches.
        var solo = new EdgeCapsuleQueueMagnetCandidate("solo", 1, 100, slot, 0);
        Check(Evaluate(101, "solo", [solo], held: "solo").Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A solo source queue cannot hold its member by proximity");

        // Ties must resolve deterministically, not by enumeration order.
        var tieA = Evaluate(100, "", [new("bbb", 1, 100, slot, -1), new("aaa", 1, 100, slot, -1)]);
        var tieB = Evaluate(100, "", [new("aaa", 1, 100, slot, -1), new("bbb", 1, 100, slot, -1)]);
        Check(tieA.TargetQueueKey == tieB.TargetQueueKey && tieA.TargetQueueKey == "aaa",
            "Equidistant candidates resolve to the same queue regardless of input order");

        // Reordering within the source queue reports Reorder; merging into another reports Merge.
        Check(Evaluate(100, "a", TwoQueues(), held: "a").Outcome == EdgeCapsuleQueueMagnetOutcome.Reorder,
            "Held source queue yields Reorder, not Merge");
        Check(Evaluate(600, "a", TwoQueues(), held: "b").Outcome == EdgeCapsuleQueueMagnetOutcome.Merge,
            "Held foreign queue yields Merge");

        // Degenerate inputs must never throw, never match, and never leak a NaN into layout.
        Check(Evaluate(double.NaN, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.None,
            "A NaN centre resolves to no plan rather than matching every queue");
        Check(Evaluate(double.PositiveInfinity, "", TwoQueues()).Outcome == EdgeCapsuleQueueMagnetOutcome.None,
            "An infinite centre resolves to no plan");
        Check(Evaluate(100, "", [new("a", 0, 100, slot, -1)]).Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A zero-slot candidate is ignored");
        Check(Evaluate(100, "", [new("a", 1, double.NaN, slot, -1)]).Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A NaN queue top is ignored");
        Check(Evaluate(100, "", System.Array.Empty<EdgeCapsuleQueueMagnetCandidate>(), held: "ghost").Outcome ==
                  EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A held queue that vanished from the candidate set does not resurrect");

        DragTimeHoldStaysStableAcrossFrames();
    }

    // The drag loop feeds each frame's target back in as the next frame's held queue. If that
    // round trip were unstable the outline and the avoidance animation would strobe while the
    // pointer merely wobbles, so the hold behaviour is pinned across a simulated frame sequence.
    private static void DragTimeHoldStaysStableAcrossFrames()
    {
        const double slot = 50;
        const double height = 46;
        EdgeCapsuleQueueMagnetCandidate[] OneQueue() => [new("q1", 2, 100, slot, -1)];

        // Queue spans 100..150, so its reach is [77, 173] and the dead band (18..32 DIP out) is
        // centres 191..205. Inside that band nothing can be acquired fresh, so staying attached
        // is possible only via the hold. Frame 0 acquires from inside merge range, then the
        // pointer drifts into the dead band: a drag loop that dropped the hold each frame would
        // release on frame 1 and report 1 attachment instead of 12.
        string held = "";
        var attached = 0;
        var centres = new List<double> { 180.0 };
        centres.AddRange(Enumerable.Range(0, 12).Select(i => 195.0 + (i % 2 == 0 ? -1 : 1)));

        foreach (var centre in centres)
        {
            var plan = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
                centre, "other", OneQueue(), slot, height,
                EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
                EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
                held));
            if (plan.Outcome is EdgeCapsuleQueueMagnetOutcome.Merge or EdgeCapsuleQueueMagnetOutcome.Reorder)
            {
                attached++;
                held = plan.TargetQueueKey;
            }
            else
            {
                held = "";
            }
        }

        Check(attached == 13,
            "Only the held queue can survive the dead band; a dropped hold would release on frame 1");

        // Crossing the detach distance must release, and the release must survive the next frame
        // rather than immediately snapping back because the pointer is still nearby.
        var released = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
            220, "other", OneQueue(), slot, height,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            "q1"));
        Check(released.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "Crossing the detach distance releases the held queue");
        Check(released.TargetQueueKey == "",
            "A released plan must not still name the queue it left");

        // With the hold cleared, that same position is genuinely out of range, so a released drag
        // settles on a new queue rather than oscillating.
        var afterRelease = EdgeCapsuleQueueMagnet.Evaluate(new EdgeCapsuleQueueMagnetInput(
            220, "other", OneQueue(), slot, height,
            EdgeCapsuleQueueMagnet.DefaultMergeDistanceDip,
            EdgeCapsuleQueueMagnet.DefaultDetachDistanceDip,
            ""));
        Check(afterRelease.Outcome == EdgeCapsuleQueueMagnetOutcome.NewQueue,
            "A released position stays released on the following frame");
        Check(afterRelease.Outcome == released.Outcome,
            "Release and re-evaluation agree, so the drag does not oscillate");
    }

    // A mini-view is placed by a pure relative displacement, so the same number applies to any
    // queue on that edge (D-058). These checks pin the scope rule: everything below the owner's
    // card is pushed, everything above is left alone, and a member that already clears the gap
    // stays put rather than the whole stack closing up.
    private static void EdgeQueueAvoidance()
    {
        const double compact = 46;
        const double gap = 4;
        var slot = EdgeCapsuleLayout.SlotHeight(gap); // 50

        static EdgeCapsuleAvoidanceQueue Q(string key, double top, params string[] ids) =>
            new(key, ids, top);

        // A card grown to 200 DIP starting at 100 ends at 300; the next member must clear it.
        Dictionary<string, double> Push(
            double cardBottom,
            params EdgeCapsuleAvoidanceQueue[] queues) =>
            EdgeCapsulePreviewLayoutCoordinator.BuildEdgeAvoidanceOffsets(
                queues, "A", cardBottom, compact, gap);

        // Nothing on the edge: no displacement at all.
        Check(Push(300).Count == 0, "An empty edge produces no avoidance");

        // A card spanning [100, 300]; a queue starting at 260 overlaps it and must be pushed.
        var one = Push(300, Q("A", 100, "a1", "a2"), Q("B", 260, "b1", "b2"));
        Check(one.Count == 2, "Every member of the overlapping queue is displaced");
        Check(one["b1"] > 0, "The member overlapping the card is pushed down");
        // Pitch lives in absolute tops, so a uniformly pushed queue has *equal* offsets.
        Check(Math.Abs(one["b1"] - one["b2"]) < 1e-9,
            "A uniformly pushed queue keeps its even pitch");

        // A queue that already clears card bottom + gap is left exactly where the user put it.
        var clearOfGap = Push(300, Q("A", 100, "a1"), Q("B", 310, "b1"));
        Check(clearOfGap.Count == 0, "A queue one slot-height below the card is not moved");

        // A queue entirely clear of the card is left alone — no chasing, no compaction.
        var clear = Push(300, Q("A", 100, "a1"), Q("B", 600, "b1", "b2"));
        Check(clear.Count == 0, "A queue already clear of the card does not move");

        // A queue above the owner is never touched: a card only grows downward.
        var above = Push(300, Q("B", 40, "b1"), Q("A", 100, "a1"), Q("C", 260, "c1"));
        Check(!above.ContainsKey("b1"), "A queue above the owner is never displaced");
        Check(above.ContainsKey("c1"), "A queue below the owner still is");

        // The owner's own queue is skipped: the session already placed those members.
        var own = Push(300, Q("A", 100, "a1", "a2"));
        Check(own.Count == 0, "The owner's own queue is left to the single-queue policy");

        // Partial overlap: the first member is pushed, the rest follow the chain.
        var chain = Push(300, Q("A", 100, "a1"), Q("B", 260, "b1", "b2", "b3"));
        Check(chain["b1"] > 0, "The overlapping head of the chain moves");
        Check(Math.Abs(chain["b1"] - chain["b2"]) < 1e-9 &&
              Math.Abs(chain["b2"] - chain["b3"]) < 1e-9,
            "Every member of the chain shifts by the same amount, so pitch survives");

        // Two queues in sequence: the chain must carry across the queue boundary.
        var across = Push(300, Q("A", 100, "a1"), Q("B", 260, "b1"), Q("C", 260, "c1", "c2"));
        Check(across.ContainsKey("b1") && across.ContainsKey("c1"),
            "Displacement carries from one queue into the next");
        Check(Math.Abs(across["c1"] - across["c2"]) < 1e-9,
            "The second queue shifts uniformly");
        // C's head must sit one slot below B's head *after* B was pushed, not before.
        Check(Math.Abs(across["c1"] - across["b1"] - slot) < 1e-9,
            "A queue inherits the previous queue's already-displaced bottom");

        // The regression this guards: hovering a NON-bottom member. The single-queue policy
        // pushes that queue's own lower members below the card, so the next queue must clear
        // those — anchoring on the card alone lands them exactly on top of the pushed member.
        //
        // A = [A1@152, A2@202, A3@252, A4@302], B1@386, card 250 DIP tall.
        // Hovering A3 puts the card at [252, 502] and A4 at 506 (card + gap). A chain anchored at
        // the card bottom would push B1 to 506 too — the very overlap this fixes.
        EdgeCapsuleAvoidanceQueue[] ab() => [Q("A", 152, "a1", "a2", "a3", "a4"), Q("B", 386, "b1")];

        // Hovering A3: card occupies [252, 502]; PushFollowingMembers then drops A4 to
        // 252 + 250 + gap = 506, so A4's settled bottom is 506 + 46 = 552.
        const double pushedFollowerBottom = 552;
        var correct = Push(pushedFollowerBottom, ab());
        Check(correct.TryGetValue("b1", out var correctOffset) && Math.Abs(correctOffset - 170) < 1e-9,
            "The next queue clears the pushed follower's bottom");

        // The card-only anchor lands B1 exactly on A4's new top — the overlap being fixed.
        var cardOnly = Push(252 + 250, ab());
        Check(cardOnly.TryGetValue("b1", out var cardOnlyOffset) && cardOnlyOffset < correct["b1"],
            "Anchoring on the card alone would overlap the pushed follower");

        // Hovering A4: the card is the lowest thing in its queue, so the anchor is the card.
        var bottomMember = Push(302 + 250, ab());
        Check(bottomMember.TryGetValue("b1", out var bottomOffset) && Math.Abs(bottomOffset - 170) < 1e-9,
            "Hovering the bottom member still anchors on the card");

        // Queues must arrive in ascending rest order or the chain walks the wrong way.
        var descending = Push(300, Q("A", 100, "a1"), Q("C", 600, "c1"), Q("B", 260, "b1"));
        Check(descending.ContainsKey("b1"), "Order is the caller's contract; a caller that sorts wrong is visible");
        Check(!descending.ContainsKey("c1"), "A queue the walk already passed stays put");
    }

    private static void CycleAndUnicode()    {
        var cycle = new[] { EdgeCapsuleTitleLimit.Unlimited, EdgeCapsuleTitleLimit.Hidden }
            .Concat(Enumerable.Range(1, PaperTitles.MaxConfigurableTitleLength)).ToArray();
        for (var i = 0; i < cycle.Length; i++)
        {
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], true) == cycle[(i + 1) % cycle.Length], "Forward cycle");
            Check(EdgeCapsuleTitleLimit.Step(cycle[i], false) == cycle[(i + cycle.Length - 1) % cycle.Length], "Backward cycle");
        }
        const string text = "中👨‍👩‍👧‍👦e\u0301文";
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 0) == text, "Legacy unlimited retains full title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, -1) == "", "Zero measures no title");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 2) == "中👨‍👩‍👧‍👦", "Do not split emoji clusters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure(text, 3) == "中👨‍👩‍👧‍👦e\u0301", "Do not split combining characters");
        Check(EdgeCapsuleTitleLimit.TextForMeasure("", -1) == "", "Empty title");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MinValue) == 0, "Invalid old negatives remain unlimited");
        Check(EdgeCapsuleTitleLimit.Normalize(int.MaxValue) == 20, "Upper bound");
    }

    private static void Persistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "papertodo-edge-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new StateStore(dir, DurableAtomicFileWriter.Shared);
            foreach (var value in new[] { 0, -1, 1, 20 })
            {
                File.WriteAllText(store.FilePath, $"{{\"papers\":[],\"deepCapsuleTitleMeasureCharacterLimit\":{value}}}");
                var state = store.Load();
                Check(state.DeepCapsuleTitleMeasureCharacterLimit == value, "Load/normalization retains semantics");
                File.WriteAllText(store.FilePath, store.SerializeState(state));
                Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == value, "Round-trip retains zero/unlimited");
            }
            File.WriteAllText(store.FilePath, "{\"papers\":[]}");
            Check(store.Load().DeepCapsuleTitleMeasureCharacterLimit == 0, "Missing legacy field remains unlimited");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void HostContentVisibility()
    {
        using var host = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin: 4, ChromeCornerRadius: 16, InnerCornerRadius: 15,
            OutlineThickness: 2, OutlineOverlap: 1, BodyHeight: 32,
            LeftPadding: 6, IconGap: 4, IconText: "✓", IconFontSize: 13,
            LabelFontSize: 12, LabelFontWeight: FontWeights.Normal, CloseToolTip: "Close",
            PaperBrush: Brushes.White, PaperBorderBrush: Brushes.Gray, OutlineBrush: Brushes.Blue,
            HoverBrush: Brushes.LightGray, IconBrush: Brushes.Gray,
            StrongTextBrush: Brushes.Black, TextBrush: Brushes.Gray,
            UiFontFamily: new FontFamily("Segoe UI"), SymbolFontFamily: new FontFamily("Segoe UI Symbol"),
            Language: XmlLanguage.GetLanguage("en-US"), Topmost: false, DiagnosticId: "title-checks"));
        T HostPart<T>(string name) where T : class => (T)typeof(EdgeCapsuleHost)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var label = HostPart<TextBlock>("Label");
        var icon = HostPart<TextBlock>("Icon");
        var contentGrid = HostPart<Grid>("ContentGrid");
        var window = HostPart<Window>("Window");
        Check(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "Host test monitor");
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, EdgeCapsuleEdge.Right, 40, 10,
            40, 28, 40, 178, 40, false, 1, null, ExpandedWidthDip: 150);
        var model = EdgeCapsuleModel.Initial with
        {
            State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
            Placement = new EdgeCapsulePlacement(0, 0, 1)
        };
        var pluginContent = new TextBlock { Text = "Plugin clock", Background = Brushes.Transparent };
        var material = (SkinBorder)HostPart<Border>("Chrome");
        var compact = EdgeCapsuleTargetPlanner.Calculate(model, layout).Docked.ToFrame();
        Check(host.Apply(compact with { Surface = EdgeCapsuleSurfaceKind.DockedPreview }) && material.UseLightweightMaterial,
            "Presented preview frame selects lightweight material before any geometry animation");
        host.RefreshSkin();
        Check(material.UseLightweightMaterial, "Theme refresh must not re-enable heavy preview material");
        Check(host.Apply(compact) && !material.UseLightweightMaterial,
            "Presented compact frame restores normal material without changing HWND authority");

        foreach (var hideTitle in new[] { false, true })
        foreach (var hovered in new[] { false, true })
        {
            var frame = EdgeCapsuleTargetPlanner.Calculate(
                model with { State = model.State with { Visual = hovered
                    ? EdgeCapsuleVisualState.Hovered : EdgeCapsuleVisualState.Resting } },
                layout with { HideRestingTitle = hideTitle }).Docked.ToFrame();
            var expectedTitle = !hideTitle || hovered ? Visibility.Visible : Visibility.Collapsed;
            host.SetLabel("Ordinary title", "Stored title");
            host.SetPluginContent(pluginContent, "Plugin tooltip");
            Check(host.Apply(frame), "Apply real host frame with plugin content");
            Check(label.Visibility == Visibility.Collapsed && icon.Visibility == Visibility.Collapsed,
                "Frame application must not reveal defaults beneath plugin content");
            Check(pluginContent.IsVisible, "Custom plugin content remains visible");

            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && icon.Visibility == Visibility.Visible,
                "Removing plugin content restores defaults according to the applied title state");
            Check(host.Apply(frame), "Apply ordinary host frame");
            Check(label.Visibility == expectedTitle, "Ordinary frame honors title visibility");
            // Theme/title refresh uses this sequence. There is deliberately no Apply afterwards:
            // an unchanged target makes the presenter skip reapplying the frame.
            host.SetLabel("Refreshed title", "Refreshed tooltip");
            host.SetPluginContent(null, null);
            Check(label.Visibility == expectedTitle && label.Text == "Refreshed title" &&
                icon.Visibility == Visibility.Visible,
                "Refreshing ordinary content preserves hidden titles without a new frame");
        }

        // The fixed slot must affect the real WPF layout, not only the outer width calculation.
        // Measure the following label after switching between the two default glyphs.
        var visibleFrame = EdgeCapsuleTargetPlanner.Calculate(
            model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } },
            layout with { HideRestingTitle = false }).Docked.ToFrame();
        host.SetPluginContent(null, null);
        host.SetDefaultIconSlotWidth(40);
        Check(Math.Abs(host.DefaultIconSlotWidthForChecks - 40) < 0.01,
            "Host accepts a real default icon slot width");
        Check(host.Apply(visibleFrame), "Apply host frame for icon-slot alignment");
        host.SetLabel("Title", "Title");
        icon.Text = "✓";
        window.UpdateLayout();
        var todoLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        icon.Text = "✎";
        window.UpdateLayout();
        var noteLabelX = label.TranslatePoint(new Point(0, 0), contentGrid).X;
        Check(Math.Abs(todoLabelX - noteLabelX) < 0.01,
            "Todo and note glyphs share the same real layout slot");
        host.SetDefaultIconSlotWidth(0);
        Check(host.DefaultIconSlotWidthForChecks < 0.01,
            "Script/natural icon layout can release the default slot");
    }

    private static void Geometry()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        foreach (var hiddenClose in new[] { false, true })
        {
            var monitor = new MonitorGeometry("test", new DeviceScreenRect(-1920, -100, 0, 980), scale, scale);
            var layout = new EdgeCapsuleLayoutSnapshot(monitor, edge, 40, 10,
                40, 28, 40, 260, 180, hiddenClose, 1, null,
                ExpandedWidthDip: 150, HideRestingTitle: true);
            var model = EdgeCapsuleModel.Initial with
            {
                State = new EdgeCapsuleState(EdgeCapsuleSlotState.CollapsedDocked,
                    EdgeCapsuleVisualState.Resting, EdgeCapsuleGestureState.Idle, EdgeCapsuleOpenOrigin.Normal),
                Placement = new EdgeCapsulePlacement(0, 0, 1)
            };
            EdgeCapsuleTargetPresentation Plan(EdgeCapsuleModel m, EdgeCapsuleLayoutSnapshot l) =>
                EdgeCapsuleTargetPlanner.Calculate(m, l).Docked;
            var resting = Plan(model, layout);
            var hoverModel = model with { State = model.State with { Visual = EdgeCapsuleVisualState.Hovered } };
            var hovered = Plan(hoverModel, layout);
            Check(!resting.TitleVisible && hovered.TitleVisible, "Zero title appears only while expanded");
            Check(resting.HostBounds == hovered.HostBounds, "Reserve full title capacity before hover");
            var expectedCloseWidth = hiddenClose ? 0 : 28;
            var expected = EdgeCapsuleGeometry.Calculate(new(monitor, edge, 40, 150, expectedCloseWidth, 40));
            Check(hovered.Bounds == expected.Bounds && hovered.InteractiveBounds == expected.InteractiveBounds,
                "Visible width and hit region remove the hidden close strip");
            Check(!hovered.CloseSegmentActsAsContent,
                "Hidden close mode leaves no invisible content segment");
            var transition = new EdgeCapsuleTransition(resting.ToFrame(), hovered, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            var previousWidth = resting.Bounds.Width;
            for (var tick = 0; tick <= 100; tick += 5)
            {
                var frame = EdgeCapsuleTransitionPolicy.Sample(transition, tick).Frame;
                Check(frame.IsUsable && frame.HostBounds == resting.HostBounds, "Every animation frame stays inside stable host");
                Check(frame.Bounds.Width >= previousWidth, "No width reversal during hover");
                previousWidth = frame.Bounds.Width;
            }
            var retract = new EdgeCapsuleTransition(hovered.ToFrame(), resting, 0, 100,
                EdgeCapsuleTransitionReason.Pointer);
            Check(!EdgeCapsuleTransitionPolicy.Sample(retract, 100).Frame.TitleVisible,
                "Mouse leave restores hidden title");
            Check(EdgeCapsuleTransitionPolicy.Create(resting.ToFrame(), hovered,
                EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.Pointer), false, 0, 1000) == null,
                "Animation disabled remains an immediate apply");
            var explicitPreserve = EdgeCapsuleTransitionPolicy.Create(
                resting.ToFrame(),
                hovered,
                EdgeCapsuleMotion.Preserve(EdgeCapsuleTransitionReason.Measure, 60),
                transitionAlreadyActive: true,
                nowTimestamp: 0,
                timestampFrequency: 1000);
            Check(explicitPreserve?.DurationTimestampTicks == 60,
                "Preserve transition uses the explicit caller-provided duration");
            var previewsEnabled = layout with { ExpandedWidthDip = layout.RestingWidthDip };
            var ordinaryHover = Plan(hoverModel, previewsEnabled);
            Check(!ordinaryHover.TitleVisible && ordinaryHover.BodyWindowWidthDevice == resting.BodyWindowWidthDevice,
                "Preview-enabled compact hover retains the configured title limit");
            var preview = Plan(hoverModel with { Preview = EdgeCapsulePreviewState.Open }, layout);
            Check(preview.Surface == EdgeCapsuleSurfaceKind.DockedPreview &&
                preview.Bounds.Width == (int)Math.Round(260 * scale),
                "Preview card keeps its independent close segment");
            var active = hoverModel with { State = hoverModel.State with { Visual = EdgeCapsuleVisualState.Active } };
            var handoff = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingHandoff } }, layout);
            var reveal = Plan(active with { State = active.State with { Gesture = EdgeCapsuleGestureState.DockingReveal } }, layout);
            Check(handoff.Bounds == reveal.Bounds && reveal.Bounds == Plan(active, layout).Bounds,
                "Docking handoff/reveal/active retain identical title geometry");
        }
    }
}
