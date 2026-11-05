using System.Reflection;
using System.Windows;
using System.Windows.Input;
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
        await CheckYieldAnimations(c, windows, monitor);
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
            Near(beforeB - pitch, masters[B].AnchorTop, "preview moves the target master up one slot");
            Near(beforeC, masters[C].AnchorTop, "unchanged lower neighbour stays in place");
            Near(targetMemberBefore, MemberTop(c, windows[firstTarget.Id], firstTarget),
                "prepend preserves the old target member position");
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
        Near(100 - pitch, masters[A].AnchorTop, "upper neighbour has the selected final position");
        Near(beforeB - pitch, masters[B].AnchorTop, "commit reproduces B's preview");
        Near(beforeC, masters[C].AnchorTop, "commit does not reorder C above B");
        Near(350, c.State.DeepCapsuleQueueStartTopMargins[C], "C's passive offset was not saved");
        Near(200 - pitch, c.State.DeepCapsuleQueueStartTopMargins[B], "target preference excludes its passive displacement");
        Near(targetMemberBefore, MemberTop(c, windows[firstTarget.Id], firstTarget), "commit preserves old target members");
        c.SaveNow(sync: true);
        var loaded = store.Load();
        Require(loaded.Papers.Single(p => p.Id == source.Id).CapsuleQueueId == "qb", "merged membership reloads");
        Near(350, loaded.DeepCapsuleQueueStartTopMargins[C], "saved C preference survives reload");
        // Repeated ordinary arrange must neither ratchet positions nor undo the insertion.
        for (var i = 0; i < 3; i++) c.ArrangeDeepCapsules(animate: false);
        await Idle();
        Near(beforeB - pitch, masters[B].AnchorTop, "repeated layout does not add displacement");
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

    private static async Task CheckYieldAnimations(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, MonitorGeometry monitor)
    {
        const string sourceKey = "|right#source";
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.ArrangeDeepCapsules(false);
        await Idle();
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        var masters = Masters(c);
        var source = c.State.Papers.Single(p => p.Id == "review-10");
        c.State.EnableAnimations = true;
        try
        {
            // Exercise ordinary reorder through the public controller path.
            var first = c.State.Papers.Single(p => p.Id == "review-6");
            c.PreviewDeepCapsuleReorder(first, 1);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(B, out var ordinary), "ordinary reorder owns a queue translation");
            Require(!ordinary!.Members.Any(m => ReferenceEquals(m.Window, windows[first.Id])),
                "ordinary reorder excludes the directly dragged capsule");
            c.PreviewDeepCapsuleReorder(first, 0);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(B, out var reversed) && ReferenceEquals(reversed, ordinary),
                "rapid reverse reuses the same real queue compositor and live sources");
            ordinary.CompleteNow(true);
            Require(!proxies.ContainsKey(B) &&
                    windows["review-7"].VerifyEdgeCapsuleQueueProxyEndpoint(
                        Field<EdgeCapsulePresenter>(windows["review-7"], "_edgeCapsule").AppliedPresentation),
                "retarget handoff leaves the real capsule at its current endpoint");
            c.ArrangeDeepCapsules(false);
            await Idle();

            await CheckAnimatedPrepend(c, source, monitor, pitch);

            var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            var insertion = 160 + 2 * pitch;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(B, out var target), "merge preview translates the target queue");
            Require(proxies.TryGetValue(C, out var neighbour),
                "merge preview translates the neighbouring queue independently");
            Require(neighbour!.Members.Any(m => ReferenceEquals(m.Window, masters[C])) &&
                    neighbour.Members.Any(m => m.Window is PaperWindow),
                "passive neighbour master and members share the same native translation");
            Require(!proxies.ContainsKey(sourceKey), "carried queue remains direct while merging");
            var sourceFrame = Field<EdgeCapsulePresenter>(windows[source.Id], "_edgeCapsule").AppliedPresentation;
            Require(Math.Abs(sourceFrame.Bounds.Top -
                    (monitor.WorkArea.Top + (insertion + pitch) * monitor.DpiScaleY)) <= 1,
                "carried queue follows the pointer without waiting for passive animation");
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            Require(ReferenceEquals(proxies[B], target) && ReferenceEquals(proxies[C], neighbour),
                "same insertion sample retains both active translations");

            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.PreviewFreeMasterQueueTop(layout, 24, Point(monitor, 24));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.ContainsKey(B) && proxies.ContainsKey(C),
                "leaving the merge slot animates both suffix and neighbour back");
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.FinishFreeMasterQueueDrag(layout, false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            Require(proxies.Count == 0, "completion releases all queue covers");

            c.State.EnableAnimations = false;
            c.State.CapsuleCollapseAllActiveQueues[C] = true;
            c.ArrangeDeepCapsules(false);
            await Idle();
            layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            c.State.EnableAnimations = true;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(C, out var folded) && folded.Members.Count == 1 &&
                    ReferenceEquals(folded.Members[0].Window, masters[C]),
                "a folded neighbour animates its master without fabricating a paper member");
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.FinishFreeMasterQueueDrag(layout, false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);

            c.State.EnableAnimations = false;
            layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.Count == 0, "disabled animations leave all sources directly owned");
            c.FinishFreeMasterQueueDrag(layout, false);
        }
        finally
        {
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(false);
            c.State.EnableAnimations = false;
            c.ArrangeDeepCapsules(false);
            await Idle();
        }
        Console.WriteLine("PASS ordinary and merge yield: independent queues, shared master clock, direct drag, stable slot and animated return");
    }

    private static async Task CheckAnimatedPrepend(AppController c, PaperData source,
        MonitorGeometry monitor, double pitch)
    {
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        var master = Masters(c)[B];
        var insertion = 160 + 2 * pitch;
        Require(c.BeginNativeEdgeQueueDrag(source, false, Point(monitor, 20 + pitch), false),
            "begin a single-capsule transfer for animated prepend");
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, insertion), insertion, floating: true);
        Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop?.Decision.Index == 1,
            "select the interior slot before testing the head transition");
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.TryGetValue(B, out var interior), "interior insertion starts a real translation");
        Require(WindowNative.TryGetWindowDeviceBounds(master, out var masterBeforeHead),
            "capture the unshifted destination master");
        var outputBeforeHead = interior!.OutputHandle;

        // Admit a new master source before the suffix's first animation finishes.
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, 160), 160, floating: true);
        Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop?.Decision.Index == 0,
            "the second sample selects prepend, not append to the master");
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.TryGetValue(B, out var head) && !ReferenceEquals(head, interior) &&
                head.OutputHandle == outputBeforeHead &&
                head.Members.Any(m => ReferenceEquals(m.Window, master)),
            "interior-to-head joins the master through a successor on the existing output");
        Require(head!.TryGetPresentationAt(master,
                Field<long>(head, "_animationStartedAtTimestamp"), out var masterStart) &&
                masterStart.Bounds.Top == masterBeforeHead.Top,
            "the newly admitted master starts at its visible position without a jump");
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, insertion), insertion, floating: true);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.TryGetValue(B, out var backToMiddle) && ReferenceEquals(backToMiddle, head),
            "head-to-interior reverses the master and suffix through the retained live sources");
        c.EndNativeEdgeQueueDrag(source.Id);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
        await Idle();
        Console.WriteLine("PASS animated prepend: interior -> head -> interior without draining the old animation");
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
            Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(monitor, bottom, left)),
                "folded independent drop commits");
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
