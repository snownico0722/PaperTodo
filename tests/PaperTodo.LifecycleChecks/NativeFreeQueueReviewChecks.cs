using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

// Runs in LifecycleChecks' existing isolated-data child, never against user data.
internal static class NativeFreeQueueReviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string A = "|right#qa", B = "|right#qb", C = "|right#qc";

    internal static void Prepare(AppState state)
    {
        state.Papers.Clear();
        state.EnableAnimations = false;
        state.UseCapsuleMode = state.UseDeepCapsuleMode = state.UseCapsuleFreeQueues = true;
        state.UseCapsuleCollapseAll = true;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        for (var i = 0; i < 11; i++)
            state.Papers.Add(new PaperData
            {
                Id = "review-" + i, Type = PaperTypes.Note, Content = "preserved " + i,
                IsVisible = true, IsCollapsed = true, Width = 300, Height = 240,
                X = 60, Y = 60, CapsuleMonitorDeviceName = "",
                CapsuleSide = i == 10 ? DeepCapsuleSides.Left : DeepCapsuleSides.Right,
                CapsuleQueueId = i < 6 ? "qa" : i < 8 ? "qb" : i < 10 ? "qc" : "source"
            });
        state.DeepCapsuleQueueStartTopMargins.Clear();
        state.DeepCapsuleQueueStartTopMargins[A] = 100;
        state.DeepCapsuleQueueStartTopMargins[B] = 200;
        state.DeepCapsuleQueueStartTopMargins[C] = 350;
        state.DeepCapsuleQueueStartTopMargins["|left#source"] = 120;
        state.CapsuleCollapseAllActiveQueues.Clear();
        state.CapsuleCollapseAllActiveQueues[A] = true;
    }

    internal static async Task Run(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store)
    {
        CheckAnchorReconstruction();
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "review monitor");
        var originalContents = c.State.Papers.ToDictionary(p => p.Id, p => p.Content);
        await CheckDisplacedInsertion(c, windows, store, monitor);
        await CheckDragPreviewUpdates(c, windows, monitor);
        await CheckVacatedSourcePreview(c, windows, store, monitor);
        await CheckIndependentGroupPreview(c, windows, store, monitor);
        await CheckFoldedDropAndShortcuts(c, monitor);
        await CheckDisconnectedGesturesAndMerge(c, windows, store, monitor);
        Require(c.State.Papers.Count == originalContents.Count &&
                c.State.Papers.All(p => originalContents[p.Id] == p.Content),
            "queue operations changed paper identities or note contents");
        Console.WriteLine("PASS free-queue review: displaced insertion, anchor roundtrip, folded drops, shortcut geometry, post-disconnect gestures and merge identity");
    }

    private static async Task CheckDisplacedInsertion(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        await Idle();
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        var source = c.State.Papers.Single(p => p.Id == "review-10");
        var firstTarget = c.State.Papers.Single(p => p.Id == "review-6");
        c.ToggleCapsuleCollapseAllActive(A);
        await Idle();
        var masters = Masters(c);
        var beforeB = masters[B].AnchorTop;
        var beforeC = masters[C].AnchorTop;
        Near(100 + 7 * pitch, beforeB, "expanding the upper queue displaces B");
        Near(beforeB + 3 * pitch, beforeC, "C is also passively displaced");
        var savedBefore = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        var targetMemberBefore = MemberTop(c, windows[firstTarget.Id], firstTarget);
        var dropPoint = Point(monitor, beforeB);

        for (var pass = 0; pass < 2; pass++)
        {
            Require(c.BeginNativeEdgeQueueDrag(source, false, Point(monitor, 120 + pitch, left: true), false),
                "begin prepend from the opposite edge");
            c.PreviewNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true);
            var drop = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!;
            Require(drop.Decision.Kind == EdgeFreeQueueDropKind.Insert && drop.DestinationKey == B &&
                    drop.Decision.Index == 0, "a displaced queue must accept its visible head insertion");
            Near(beforeB, masters[B].AnchorTop, "first-slot preview retains the target master");
            Near(beforeC + pitch, masters[C].AnchorTop, "intersected lower neighbour gives way down");
            Near(targetMemberBefore + pitch, MemberTop(c, windows[firstTarget.Id], firstTarget),
                "first-slot insertion moves old members down");
            Require(savedBefore.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "preview wrote saved preferences");
            if (pass == 1)
                Require(c.CommitNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true), "prepend commit handled");
            c.EndNativeEdgeQueueDrag(source.Id);
            await Idle();
            if (pass == 0)
            {
                Near(beforeB, masters[B].AnchorTop, "cancel restores B");
                Near(beforeC, masters[C].AnchorTop, "cancel restores C");
                Require(source.CapsuleQueueId == "source" &&
                        savedBefore.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                    "cancel changed membership or saved anchors");
            }
        }

        Require(c.QueueKeyForPaper(source) == B, "accepted drop actually joined the target");
        Near(100, masters[A].AnchorTop, "upper neighbour remains at its original position");
        Near(beforeB, masters[B].AnchorTop, "commit reproduces B's preview");
        Near(beforeC + pitch, masters[C].AnchorTop, "commit reproduces the lower neighbour preview");
        Near(350, c.State.DeepCapsuleQueueStartTopMargins[C], "C's passive offset was not saved");
        Near(200, c.State.DeepCapsuleQueueStartTopMargins[B], "target preference excludes its passive displacement");
        Near(targetMemberBefore + pitch, MemberTop(c, windows[firstTarget.Id], firstTarget), "commit reproduces downward member positions");
        c.SaveNow(sync: true);
        var loaded = store.Load();
        Require(loaded.Papers.Single(p => p.Id == source.Id).CapsuleQueueId == "qb", "merged membership reloads");
        Near(350, loaded.DeepCapsuleQueueStartTopMargins[C], "saved C preference survives reload");
        // Repeated ordinary arrange must neither ratchet positions nor undo the insertion.
        for (var i = 0; i < 3; i++) c.ArrangeDeepCapsules(animate: false);
        await Idle();
        Near(beforeB, masters[B].AnchorTop, "repeated layout does not add displacement");
        Console.WriteLine("PASS displaced head insertion: cancel, commit, preferences, reload and stable rearrange");
    }

    private static async Task CheckDragPreviewUpdates(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, MonitorGeometry monitor)
    {
        const string sourceKey = "|right#source", unrelatedKey = "|left#unrelated";
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        foreach (var paper in c.State.Papers)
        {
            var i = int.Parse(paper.Id["review-".Length..]);
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = i < 6 ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
            paper.CapsuleQueueId = i < 6 ? "unrelated" : i < 8 ? "qb" : i < 10 ? "qc" : "source";
        }
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 20;
        c.State.DeepCapsuleQueueStartTopMargins[unrelatedKey] = 50;
        c.State.DeepCapsuleQueueStartTopMargins[B] = 160;
        c.State.DeepCapsuleQueueStartTopMargins[C] = 160 + 3 * pitch;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.ArrangeDeepCapsules(false);
        await Idle();

        var source = c.State.Papers.Single(p => p.Id == "review-10");
        var peers = c.State.Papers.Where(p => p != source).ToArray();
        var beforeTops = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
        var beforeMargins = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        var masters = Masters(c);
        var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
        var peerWork = peers.ToDictionary(p => p.Id, p => ReconcileGeneration(windows[p.Id]));
        c.PreviewFreeMasterQueueTop(layout, 28);
        Require(peers.All(p => ReconcileGeneration(windows[p.Id]) == peerWork[p.Id]),
            "moving one master must not queue reconciliation for unchanged peers");
        await Idle();
        Near(28, masters[sourceKey].AnchorTop, "source master follows its temporary anchor");
        Near(28 + pitch, MemberTop(c, windows[source.Id], source), "source member follows its temporary anchor");
        var sourceFrame = Field<EdgeCapsulePresenter>(windows[source.Id], "_edgeCapsule").AppliedPresentation;
        Require(Math.Abs(sourceFrame.Bounds.Top - (monitor.WorkArea.Top + (28 + pitch) * monitor.DpiScaleY)) <= 1,
            "unchanged source slot indices must still refresh actual presented geometry");

        var allWork = c.State.Papers.ToDictionary(p => p.Id, p => ReconcileGeneration(windows[p.Id]));
        var masterWork = masters.ToDictionary(pair => pair.Key, pair => Field<int>(pair.Value, "_moveGeneration"));
        c.PreviewFreeMasterQueueTop(layout, 28);
        Require(c.State.Papers.All(p => ReconcileGeneration(windows[p.Id]) == allWork[p.Id]) &&
                masters.All(pair => Field<int>(pair.Value, "_moveGeneration") == masterWork[pair.Key]),
            "an identical pointer sample must not schedule capsule or master movement");

        var insertionTop = 160 + 2 * pitch;
        c.PreviewFreeMasterQueueTop(layout, insertionTop, Point(monitor, insertionTop));
        var slot = Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview");
        Require(slot.TargetKey == B && slot.Index == 1, "master selects the interior target slot");
        await Idle();
        Near(beforeTops["review-6"], MemberTop(c, windows["review-6"], peers.Single(p => p.Id == "review-6")),
            "target prefix stays in place");
        Near(beforeTops["review-7"] + pitch, MemberTop(c, windows["review-7"], peers.Single(p => p.Id == "review-7")),
            "target suffix reserves one carried slot");
        Require(masters[C].AnchorTop > beforeMargins[C], "intersected neighbouring queue gives way");
        var previewTops = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
        peerWork = peers.ToDictionary(p => p.Id, p => ReconcileGeneration(windows[p.Id]));
        masterWork = masters.Where(pair => pair.Key != sourceKey)
            .ToDictionary(pair => pair.Key, pair => Field<int>(pair.Value, "_moveGeneration"));
        for (var delta = 1; delta <= 3; delta++)
            c.PreviewFreeMasterQueueTop(layout, insertionTop + delta, Point(monitor, insertionTop + delta));
        Require(peers.All(p => ReconcileGeneration(windows[p.Id]) == peerWork[p.Id] &&
                    Math.Abs(MemberTop(c, windows[p.Id], p) - previewTops[p.Id]) < .1) &&
                masterWork.All(pair => Field<int>(masters[pair.Key], "_moveGeneration") == pair.Value),
            "same-slot dragging must retain the gap without resetting or reconciling its peers");
        await Idle();

        c.ArrangeDeepCapsules(false);
        await Idle();
        c.PreviewFreeMasterQueueTop(layout, insertionTop + 3, Point(monitor, insertionTop + 3));
        await Idle();
        Require(peers.All(p => Math.Abs(MemberTop(c, windows[p.Id], p) - previewTops[p.Id]) < .1),
            "a global arrange invalidates the old gap and the next sample restores its current preview");

        c.PreviewFreeMasterQueueTop(layout, 24, Point(monitor, 24));
        await Idle();
        Require(peers.All(p => Math.Abs(MemberTop(c, windows[p.Id], p) - beforeTops[p.Id]) < .1),
            "leaving the slot restores its suffix and displaced neighbours");
        Near(beforeMargins[C], masters[C].AnchorTop, "leaving restores the neighbour master");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Near(20, masters[sourceKey].AnchorTop, "cancel restores the source anchor");
        Require(beforeMargins.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
            "preview and cancel must not persist temporary anchors");

        c.ToggleCapsuleCollapseAllActive(sourceKey);
        await Idle();
        layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
        var hiddenWork = ReconcileGeneration(windows[source.Id]);
        c.PreviewFreeMasterQueueTop(layout, 32);
        Require(ReconcileGeneration(windows[source.Id]) == hiddenWork,
            "moving a folded queue only updates its visible master");
        Near(32, masters[sourceKey].AnchorTop, "folded master still follows the pointer");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Console.WriteLine("PASS selective drag updates: live anchor refresh, unchanged peers, stable slot, neighbour restore, cancel and folded source");
    }

    private static async Task CheckVacatedSourcePreview(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        // Leave enough space below B for its inserted member on the CI desktop;
        // downward-first overflow is covered separately by the policy checks.
        var sourceTop = pitch;
        var targetTop = sourceTop + 7 * pitch;
        var emptyTop = targetTop + 5 * pitch;
        foreach (var hasMaster in new[] { true, false })
        foreach (var floating in new[] { false, true })
        foreach (var ending in new[] { "cancel", "return", "split", "merge" })
        {
            c.State.UseCapsuleCollapseAll = hasMaster;
            c.State.Papers = c.State.Papers.OrderBy(p => int.Parse(p.Id["review-".Length..])).ToList();
            foreach (var paper in c.State.Papers)
            {
                var i = int.Parse(paper.Id["review-".Length..]);
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = i < 8 ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
                paper.CapsuleQueueId = i < 5 ? "qa" : i < 8 ? "qb" : "qc";
            }
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[A] = sourceTop;
            c.State.DeepCapsuleQueueStartTopMargins[B] = targetTop;
            c.State.DeepCapsuleQueueStartTopMargins["|left#qc"] = sourceTop;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();

            var source = c.State.Papers.Take(5).ToArray();
            var owner = source[1];
            var target = c.State.Papers.Skip(5).Take(3).ToArray();
            var peers = c.State.Papers.Where(p => p != owner).ToArray();
            var before = c.State.Papers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            var saved = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
            var order = c.State.Papers.Select(p => p.Id).ToArray();
            var identities = c.State.Papers.ToDictionary(p => p.Id, c.QueueKeyForPaper);
            var start = Point(monitor, before[owner.Id]);
            var outside = Point(monitor, emptyTop, left: floating);
            Require(c.BeginNativeEdgeQueueDrag(owner, false, start, false), "begin single-source drag");
            Require(!c.PreviewNativeEdgeQueueDrag(owner, start, before[owner.Id], false),
                "ordinary sorting remains on its existing path before detachment");
            // Start from an existing in-group sorting preview, not only from rest.
            c.PreviewDeepCapsuleReorder(owner, 3);
            Require(c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating), "detach single member");
            var drag = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag");
            Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Move, "independent destination selected");
            await Idle();
            for (var i = 0; i < source.Length; i++)
                if (i != 1)
                {
                    var expected = before[source[i].Id] - (i > 1 ? pitch : 0);
                    Near(expected, MemberTop(c, windows[source[i].Id], source[i]), "source immediately closes its gap");
                    var frame = Field<EdgeCapsulePresenter>(windows[source[i].Id], "_edgeCapsule").AppliedPresentation;
                    Require(Math.Abs(frame.Bounds.Top - (monitor.WorkArea.Top + expected * monitor.DpiScaleY)) <= 1,
                        "source compaction reaches the visible frame");
                }
            var work = peers.ToDictionary(p => p.Id, p => ReconcileGeneration(windows[p.Id]));
            for (var sample = 0; sample < 10; sample++)
                c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating);
            Require(peers.All(p => ReconcileGeneration(windows[p.Id]) == work[p.Id]),
                "unchanged source gap does not restart peer presentation");

            // Return to the compacted source's second slot, then leave again.
            // The selected index must use the remaining members, not an empty old slot.
            c.PreviewNativeEdgeQueueDrag(owner, start, before[owner.Id], floating);
            Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                    drag.Drop.DestinationKey == A && drag.Drop.Decision.Index == 1,
                "return uses the compacted source insertion index");
            foreach (var paper in source.Where(p => p != owner))
                Near(before[paper.Id], MemberTop(c, windows[paper.Id], paper), "return reopens the source slot");
            var releasePoint = start;
            var releaseTop = before[owner.Id];
            if (ending != "return")
            {
                c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating);
                Near(before[source[2].Id] - pitch, MemberTop(c, windows[source[2].Id], source[2]),
                    "leaving the reopened source closes it again");
                releaseTop = before[target[1].Id];
                releasePoint = Point(monitor, releaseTop);
                c.PreviewNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating);
                Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                        drag.Drop.DestinationKey == B && drag.Drop.Decision.Index == 1, "merge selects target middle");
                Near(before[source[2].Id] - pitch, MemberTop(c, windows[source[2].Id], source[2]),
                    "source remains compact while target opens a slot");
                Near(before[target[1].Id] + pitch, MemberTop(c, windows[target[1].Id], target[1]),
                    "target suffix makes room simultaneously");
                if (ending == "split")
                {
                    releasePoint = outside;
                    releaseTop = emptyTop;
                    c.PreviewNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating);
                    foreach (var paper in target)
                        Near(before[paper.Id], MemberTop(c, windows[paper.Id], paper), "leaving target restores it");
                }
            }
            Require(order.SequenceEqual(c.State.Papers.Select(p => p.Id)) &&
                    c.State.Papers.All(p => c.QueueKeyForPaper(p) == identities[p.Id]) &&
                    saved.Count == c.State.DeepCapsuleQueueStartTopMargins.Count &&
                    saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "source and target previews do not mutate data");
            var preview = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            if (ending != "cancel")
                Require(c.CommitNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating), "commit single-source drag");
            c.EndNativeEdgeQueueDrag(owner.Id);
            await Idle();
            foreach (var paper in peers)
                Near(ending == "cancel" ? before[paper.Id] : preview[paper.Id],
                    MemberTop(c, windows[paper.Id], paper), "cancel or commit matches the source/target preview");
            if (ending is "cancel" or "return")
                Require(c.QueueKeyForPaper(owner) == A, "owner returns to source");
            else
            {
                Require(ending == "merge" ? c.QueueKeyForPaper(owner) == B :
                    c.QueueKeyForPaper(owner) != A && c.QueueKeyForPaper(owner) != B, "committed destination identity");
                c.SaveNow(sync: true);
                var loaded = store.Load();
                Require(loaded.Papers.Single(p => p.Id == owner.Id).CapsuleQueueId == owner.CapsuleQueueId &&
                        loaded.Papers.Where(p => p.CapsuleQueueId == "qa").Select(p => p.Id)
                            .SequenceEqual(source.Where(p => p != owner).Select(p => p.Id)),
                    "saved source membership and destination reload together");
            }
        }
        c.State.UseCapsuleCollapseAll = true;
        Console.WriteLine("PASS vacated source: master on/off, docked/floating, sorting-to-detach, repeat, return, cancel, split, merge and reload");
    }

    private static async Task CheckIndependentGroupPreview(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        var aTop = 4 * pitch;
        var bTop = aTop + 6 * pitch;
        // A = master + four members, B = master + three members. Cover both
        // input owners and both drag phases, with cancellation and real commit.
        foreach (var route in new[] { "ctrl-docked", "ctrl-floating", "master-docked", "master-floating" })
        foreach (var commit in new[] { false, true })
        {
            foreach (var paper in c.State.Papers)
            {
                var i = int.Parse(paper.Id["review-".Length..]);
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = i < 7 ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
                paper.CapsuleQueueId = i < 4 ? "qa" : i < 7 ? "qb" : "qc";
            }
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[A] = aTop;
            c.State.DeepCapsuleQueueStartTopMargins[B] = bTop;
            c.State.DeepCapsuleQueueStartTopMargins["|left#qc"] = 40;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();
            var saved = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
            var targetPapers = c.State.Papers.Where(p => p.CapsuleQueueId == "qa").ToArray();
            var memberTops = targetPapers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            var owner = c.State.Papers.Single(p => p.Id == "review-4");
            var ctrl = route.StartsWith("ctrl", StringComparison.Ordinal);
            var floating = route.EndsWith("floating", StringComparison.Ordinal);
            // The docked Ctrl block touches only A's master region, stopping
            // short of its first-member merge range. Floating selects by shell.
            var top = ctrl && !floating ? aTop - 4 * pitch + 25 : aTop - 2 * pitch;
            var ownerTop = top + pitch;
            var point = Point(monitor, ctrl && !floating ? ownerTop : top);
            FreeMasterDragLayout? layout = null;
            MasterCapsuleQueueTransferSnapshot transfer = default;
            if (ctrl)
            {
                Require(c.BeginNativeEdgeQueueDrag(owner, true, Point(monitor, bTop + pitch), false), route);
                c.PreviewNativeEdgeQueueDrag(owner, point, ownerTop, floating);
                Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!.Decision.Kind == EdgeFreeQueueDropKind.Move,
                    "B above A remains an independent group");
            }
            else
            {
                layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, B)!;
                if (floating)
                {
                    Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, bTop,
                        out transfer, freeLayout: layout, sourceQueueKey: B), route);
                    c.PreviewFreeMasterQueueDropGhost(transfer, point);
                }
                else c.PreviewFreeMasterQueueTop(layout, top, point);
                Require(Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview").Decision?.Kind == EdgeFreeQueueDropKind.Move,
                    "master preview keeps the two groups separate");
            }
            var expectedA = top + 4 * pitch;
            var masters = Masters(c);
            Near(expectedA, masters[A].AnchorTop, route + " previews A master displacement before release");
            foreach (var paper in targetPapers)
                Near(memberTops[paper.Id] + expectedA - aTop, MemberTop(c, windows[paper.Id], paper),
                    route + " previews A as one rigid group");
            var ghost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            Require(Field<Window>(ghost, "_window").Opacity > 0 &&
                    Field<StackPanel>(ghost, "_stack").Children.Count == 4,
                "independent B ghost includes its own master plus three members");
            Require(saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "independent preview must not write saved positions");

            // Identical samples retain the preview instead of pushing A again.
            var work = targetPapers.ToDictionary(p => p.Id, p => ReconcileGeneration(windows[p.Id]));
            if (ctrl) c.PreviewNativeEdgeQueueDrag(owner, point, ownerTop, floating);
            else if (floating) c.PreviewFreeMasterQueueDropGhost(transfer, point);
            else c.PreviewFreeMasterQueueTop(layout!, top, point);
            Require(targetPapers.All(p => ReconcileGeneration(windows[p.Id]) == work[p.Id]),
                "same independent drop does not requeue unchanged target geometry");
            Near(expectedA, masters[A].AnchorTop, "independent preview does not accumulate displacement");

            if (ctrl)
            {
                if (commit) Require(c.CommitNativeEdgeQueueDrag(owner, point, ownerTop, floating), route + " commit");
                c.EndNativeEdgeQueueDrag(owner.Id);
            }
            else if (floating)
            {
                if (commit) Require(c.CommitMasterCapsuleQueueTransfer(transfer, point), route + " commit");
                else c.CancelMasterCapsuleQueueTransfer(transfer);
                c.EndFreeMasterQueueDropGhost();
            }
            else c.FinishFreeMasterQueueDrag(layout!, commit);
            await Idle();
            Near(commit ? expectedA : aTop, masters[A].AnchorTop, route + " A release result matches preview or cancel");
            Near(commit ? top : bTop, masters[B].AnchorTop, route + " B release result matches preview or cancel");
            Require(c.State.Papers.Count(p => p.CapsuleQueueId == "qa") == 4 &&
                    c.State.Papers.Count(p => p.CapsuleQueueId == "qb") == 3,
                "independent placement never merges membership");
            if (!commit)
                Require(saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                    "cancel retains every original anchor");
            else
            {
                c.SaveNow(sync: true);
                var loaded = store.Load();
                Near(top, loaded.DeepCapsuleQueueStartTopMargins[B], "independent landing anchor survives reload");
                foreach (var pair in loaded.DeepCapsuleQueueStartTopMargins)
                    c.State.DeepCapsuleQueueStartTopMargins[pair.Key] = pair.Value;
                c.ArrangeDeepCapsules(false);
                await Idle();
                Near(expectedA, masters[A].AnchorTop, "saved independent order reproduces A's preview");
                Near(top, masters[B].AnchorTop, "saved independent order reproduces B's preview");
            }
        }
        Console.WriteLine("PASS independent group preview: Ctrl/master, docked/floating, ghost slots, cancel, commit and reload");
    }

    private static int ReconcileGeneration(PaperWindow window) =>
        Field<int>(Field<EdgeCapsulePresenter>(window, "_edgeCapsule"), "_reconcileGeneration");

    private static async Task CheckFoldedDropAndShortcuts(AppController c, MonitorGeometry monitor)
    {
        const string shortKey = "|right#short", longKey = "|right#long";
        var shortPaper = c.State.Papers.Single(p => p.Id == "review-10");
        foreach (var paper in c.State.Papers)
        {
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = DeepCapsuleSides.Right;
            paper.CapsuleQueueId = paper.Id == shortPaper.Id ? "short" : "long";
        }
        var bottom = Math.Round(monitor.LocalWorkAreaDip.Height - 80, 1);
        var upper = Math.Max(80, bottom - 320);
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[shortKey] = upper;
        c.State.DeepCapsuleQueueStartTopMargins[longKey] = bottom;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.CapsuleCollapseAllActiveQueues[longKey] = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Near(bottom, Masters(c)[longKey].AnchorTop, "folded queue starts near the bottom");

        // Exercise the actual controller-to-shortcut geometry adapter, not a
        // resolver fed with hand-written correct screen coordinates.
        var plan = EdgeCapsuleQueueCoordinator.Build(c.State.Papers.Select(p =>
            new EdgeCapsuleQueueMember(p, c.QueueKeyForPaper(p))), true);
        var deltas = (IReadOnlyDictionary<string, double>)Call(c, "BuildEffectiveQueueRestingDeltas")!;
        var shortcutQueues = plan.Queues.Select(queue =>
            (EdgeCapsuleShortcutQueue)Call(c, "EdgeShortcutQueue", queue, deltas)!).ToArray();
        foreach (var queue in shortcutQueues)
            Near(Masters(c)[queue.QueueKey].AnchorTop, queue.StartTopDip, "shortcut uses displayed queue top");
        Require(EdgeCapsuleShortcutResolver.Resolve(shortcutQueues, 1)?.Id == shortPaper.Id,
            "first shortcut must select the visibly upper queue, not the folded queue below");
        c.State.UseCapsuleFreeQueues = false;
        try
        {
            var queue = plan.Queues.Single(q => q.Key == longKey);
            var legacy = (EdgeCapsuleShortcutQueue)Call(c, "EdgeShortcutQueue", queue, deltas)!;
            Near(EdgeCapsuleLayout.TopForIndex(0, bottom, monitor.LocalWorkAreaDip,
                    queue.Papers.Count + 1, c.DeepCapsuleGap) + deltas.GetValueOrDefault(longKey),
                legacy.StartTopDip, "legacy shortcut geometry is unchanged");
        }
        finally { c.State.UseCapsuleFreeQueues = true; }

        var currentKey = longKey;
        foreach (var left in new[] { false, true })
        {
            var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, currentKey)!;
            Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, bottom,
                    out var transfer, freeLayout: layout, sourceQueueKey: currentKey), "begin folded floating transfer");
            c.PreviewFreeMasterQueueDropGhost(transfer, Point(monitor, bottom, left));
            var ghost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            Require(Field<Window>(ghost, "_window").Opacity > 0 &&
                    Field<StackPanel>(ghost, "_stack").Children.Count == 1,
                "an actually folded source previews only its retained master slot");
            Near(bottom, Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview").Top,
                "folded preview uses the same visible height as commit");
            Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(monitor, bottom, left)),
                "folded independent drop commits");
            c.EndFreeMasterQueueDropGhost();
            await Idle();
            currentKey = left ? "|left#long" : longKey;
            Near(bottom, Masters(c)[currentKey].AnchorTop, "folded drop uses only the visible master height");
            Near(bottom, c.State.DeepCapsuleQueueStartTopMargins[currentKey], "folded drop saves the requested bottom anchor");
            Require(c.State.CapsuleCollapseAllActiveQueues.GetValueOrDefault(currentKey), "drop retains folded state");
        }
        Console.WriteLine("PASS folded same-edge/cross-edge drops and actual shortcut geometry");
    }

    private static async Task CheckDisconnectedGesturesAndMerge(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        // A missing persisted display exercises the normal fallback without
        // pretending that this fixture physically unplugged a monitor.
        const string missingMonitor = "PaperTodo.Review.DisconnectedDisplay";
        const string targetKey = missingMonitor + "|right#fallback";
        const string sourceKey = "|left#incoming";
        foreach (var paper in c.State.Papers)
        {
            var target = int.Parse(paper.Id["review-".Length..]) < 6;
            paper.CapsuleMonitorDeviceName = target ? missingMonitor : "";
            paper.CapsuleSide = target ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
            paper.CapsuleQueueId = target ? "fallback" : "incoming";
        }
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[targetKey] = 180;
        c.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 100;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.CapsuleCollapseAllActiveQueues[targetKey] = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var master = Masters(c)[targetKey];
        var before = master.AnchorTop;
        var (pill, session, start) = PressMaster(master);
        var pointer = new DeviceScreenPoint(start.X, start.Y + 24 * monitor.DpiScaleY);
        Require((bool)Call(master, "ContinueMasterDrag", pointer, session)!, "post-disconnect move handled");
        Near(before + 24, master.AnchorTop, "a NEW gesture on an already-fallback master can move");
        Near(180, c.State.DeepCapsuleQueueStartTopMargins[targetKey], "new drag has not saved its preview");
        master.CancelQueueTransfer();
        await Idle();
        Require(!pill.IsMouseCaptured, "cancelling the new gesture releases input");
        Near(before, master.AnchorTop, "cancel restores the fallback master");

        var source = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Left, sourceKey)!;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left, source.MasterTopDip,
                out var transfer, freeLayout: source, sourceQueueKey: sourceKey), "begin merge into fallback target");
        c.PreviewFreeMasterQueueDropGhost(transfer, Point(monitor, before));
        var ghost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
        Require(Field<Window>(ghost, "_window").IsVisible, "fallback target has a merge preview");
        Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(monitor, before)), "merge into fallback target commits");
        c.EndFreeMasterQueueDropGhost();
        await Idle();
        Require(c.State.Papers.All(p => c.QueueKeyForPaper(p) == targetKey &&
                    p.CapsuleMonitorDeviceName == missingMonitor && p.CapsuleQueueId == "fallback"),
            "all carried papers must inherit the complete persistent destination identity");
        Require(Masters(c).Count == 1 && Masters(c).ContainsKey(targetKey), "merge leaves one destination master, not a phantom second queue");
        Require(c.State.Papers.All(p => windows[p.Id].IsCollapseAllRetracted), "merge retains the target's folded state");
        Near(before, Masters(c)[targetKey].AnchorTop, "merge keeps target visible at its original anchor");
        c.SaveNow(sync: true);
        var loaded = store.Load();
        Require(loaded.Papers.All(p => p.CapsuleMonitorDeviceName == missingMonitor && p.CapsuleQueueId == "fallback") &&
                loaded.CapsuleCollapseAllActiveQueues.GetValueOrDefault(targetKey),
            "fallback membership and folded state survive reload");
        Console.WriteLine("PASS new post-disconnect master gesture and complete destination identity on merge");
    }

    private static void CheckAnchorReconstruction()
    {
        EdgeQueueAnchor[][] cases =
        [
            [new("a", 50, 346, 50), new("b", 400, 196, 150), new("c", 600, 146, 350)],
            [new("a", 100, 346, 100), new("b", 450, 196, 200), new("c", 650, 146, 350)],
            // A shorter upstream source creates a real gap before the target.
            [new("z", 100, 46, 100), new("b", 200, 146, 120), new("a", 350, 96, 160)]
        ];
        foreach (var rows in cases)
        {
            var anchors = EdgeCapsuleQueueAnchorPlan.Build(rows, 4);
            var queues = rows.Select(row => new EdgeCapsuleRestingQueue("edge", row.Key,
                [new EdgeCapsuleRestingQueueMember(row.Key, anchors[row.Key])],
                anchors[row.Key], anchors[row.Key] + row.HeightDip)).ToArray();
            var offsets = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, 4);
            foreach (var row in rows)
                Near(row.TopDip, anchors[row.Key] + offsets.GetValueOrDefault(row.Key),
                    "ordinary production spacing reproduces the complete chosen layout");
            var repeated = EdgeCapsuleQueueAnchorPlan.Build(rows.Select(row =>
                row with { PreferredTopDip = anchors[row.Key] }).ToArray(), 4);
            Require(anchors.All(pair => Math.Abs(pair.Value - repeated[pair.Key]) < .01),
                "repeated anchor conversion must not ratchet preferences");
        }
        Near(350, EdgeCapsuleQueueAnchorPlan.Build(cases[0], 4)["c"], "unchanged displaced neighbour retains preference");
        Near(350, EdgeCapsuleQueueAnchorPlan.Build(cases[1], 4)["c"], "downstream insertion displacement stays derived");
    }

    private static double MemberTop(AppController c, PaperWindow window, PaperData paper)
    {
        var presenter = Field<EdgeCapsulePresenter>(window, "_edgeCapsule");
        var placement = presenter.Placement;
        return EdgeCapsuleLayout.FreeQueueTopForIndex(placement.VisualIndex,
            c.DeepCapsuleStartTopMarginFor(paper), EdgeCapsuleLayout.LocalWorkAreaForQueue(paper.CapsuleMonitorDeviceName),
            c.DeepCapsuleGap) + placement.TopOffsetDip;
    }

    private static DeviceScreenPoint Point(MonitorGeometry monitor, double top, bool left = false) => new(
        left ? monitor.WorkArea.Left + 10 : monitor.WorkArea.Right - 10,
        monitor.WorkArea.Top + (top + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
    private static Dictionary<string, MasterCapsuleWindow> Masters(AppController c) =>
        Field<Dictionary<string, MasterCapsuleWindow>>(c, "_masterCapsules");
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) =>
        value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static async Task Idle() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    private static void Near(double expected, double actual, string name) =>
        Require(Math.Abs(expected - actual) < .11, $"{name}: expected {expected:F2}, actual {actual:F2}");
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static (FrameworkElement Pill, object Session, DeviceScreenPoint Start) PressMaster(MasterCapsuleWindow master)
    {
        var pill = Field<FrameworkElement>(master, "_pill");
        void IgnoreCaptureMove(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent == Mouse.PreviewMouseMoveEvent ||
                e.StagingItem.Input.RoutedEvent == Mouse.MouseMoveEvent) e.Cancel();
        }
        InputManager.Current.PreProcessInput += IgnoreCaptureMove;
        try
        {
            pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        }
        finally { InputManager.Current.PreProcessInput -= IgnoreCaptureMove; }
        var session = Field<object>(master, "_dragSession");
        Require(pill.IsMouseCaptured && session != null, "master press captures a new session");
        return (pill, session!, (DeviceScreenPoint)session!.GetType().GetProperty("StartScreenPosition")!.GetValue(session)!);
    }
}

