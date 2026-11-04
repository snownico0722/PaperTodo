using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class NativeFreeQueueChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Part(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o)!;
    private static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Private)!.Invoke(o, args);
    private static EdgeCapsulePlacement Placement(PaperWindow w) => ((EdgeCapsulePresenter)Part(w, "_edgeCapsule")).Placement;
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    internal static void Prepare(AppState state)
    {
        state.UseCapsuleFreeQueues = true;
        state.UseCapsuleCollapseAll = false;
        state.EnableAnimations = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        for (var i = 0; i < state.Papers.Count; i++) state.Papers[i].CapsuleQueueId = i < 2 ? "qa" : i < 4 ? "qb" : "qc";
        state.DeepCapsuleQueueStartTopMargins["|right#qa"] = 100;
        state.DeepCapsuleQueueStartTopMargins["|right#qb"] = 350;
        state.DeepCapsuleQueueStartTopMargins["|right#qc"] = Math.Min(600, SystemParameters.WorkArea.Height - 80);
    }

    internal static async Task Run(AppController c, IReadOnlyDictionary<string, PaperWindow> windows, StateStore store)
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "fixture monitor");
        await Idle();
        var a = c.State.Papers.Single(p => p.Id == "fixture-0");
        var aPeer = c.State.Papers.Single(p => p.Id == "fixture-1");
        var b = c.State.Papers.Single(p => p.Id == "fixture-2");
        var bPeer = c.State.Papers.Single(p => p.Id == "fixture-3");
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        DeviceScreenPoint Point(double top, bool distant = false, bool left = false) => new(
            left ? monitor.WorkArea.Left + 10 : distant ? monitor.WorkArea.Left + monitor.WorkArea.Width * .65 : monitor.WorkArea.Right - 10,
            monitor.WorkArea.Top + (top + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
        double Top(PaperData paper)
        {
            var placement = Placement(windows[paper.Id]);
            var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(paper.CapsuleMonitorDeviceName);
            var margin = c.DeepCapsuleStartTopMarginFor(paper);
            return (c.State.UseCapsuleFreeQueues
                ? EdgeCapsuleLayout.FreeQueueTopForIndex(placement.VisualIndex, margin, area, c.DeepCapsuleGap)
                : EdgeCapsuleLayout.TopForIndex(placement.VisualIndex, margin, area, placement.SlotCount, c.DeepCapsuleGap))
                + placement.TopOffsetDip;
        }
        var margins = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        // Exercise the real PaperWindow input entry points, not just the controller primitives.
        var ownerWindow = windows[a.Id];
        ownerWindow.GetType().GetField("_nativeQueuePointerDownKeys", Private)!.SetValue(ownerWindow, 8);
        Call(ownerWindow, "OnEdgeCapsulePointerPressed", Point(100));
        Call(ownerWindow, "OnEdgeCapsulePointerMoved", Point(140), true);
        await Idle();
        var presenter = (EdgeCapsulePresenter)Part(ownerWindow, "_edgeCapsule");
        Require(presenter.State.Gesture == EdgeCapsuleGestureState.DockedReordering && PartOrNull(ownerWindow, "_deepCapsuleFloatingDragHost") == null,
            "Ctrl plus vertical motion must stay docked and must not rent a floating host");
        Require(Math.Abs(Top(aPeer) - 190) < .1, "other members follow the docked Ctrl owner as a rigid block");
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value), "pointer-move preview must not persist anchors");
        Call(ownerWindow, "CancelDeepCapsuleReorderDrag", true);
        await Idle();
        Require(Math.Abs(Top(aPeer) - 150) < .1, "cancel restores the source layout through the existing arrange path");

        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin controller whole queue");
        var ownerTop = c.NativeEdgeQueueOwnerTop(a.Id, Point(140), 140);
        Require(Math.Abs(ownerTop - 140) < .1, "whole-queue pointer offset");
        c.PreviewNativeEdgeQueueDrag(a, Point(140), ownerTop, false);
        Require(Math.Abs(Top(aPeer) - 190) < .1, "whole queue placement preview");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        var originalTargetTop = Top(b);
        var originalNextTargetTop = Top(bPeer);
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin prepend");
        var headTop = originalTargetTop - (2 * pitch - c.DeepCapsuleGap);
        Require(c.PreviewNativeEdgeQueueDrag(a, Point(headTop), headTop, false), "prepend preview handled");
        Require(Math.Abs(Top(b) - originalTargetTop) < .1 && Math.Abs(Top(bPeer) - originalNextTargetTop) < .1,
            "prepending must not push the existing target down");
        Require(a.CapsuleQueueId == "qa" && b.CapsuleQueueId == "qb", "preview does not change queue membership");
        Require(c.CommitNativeEdgeQueueDrag(a, Point(headTop), headTop, false), "whole queue prepend handled");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(a.CapsuleQueueId == "qb" && aPeer.CapsuleQueueId == "qb", "whole-queue merge commits all carried members");
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb").Select(p => p.Id)
            .SequenceEqual(["fixture-0", "fixture-1", "fixture-2", "fixture-3"]), "block insertion preserves member order");
        Require(Math.Abs(Top(b) - originalTargetTop) < .1 && Math.Abs(Top(bPeer) - originalNextTargetTop) < .1,
            "committed prepend exactly reproduces the old target's visible position");
        Require(!c.State.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qa"), "vacated independent anchor is removed");

        Require(c.BeginNativeEdgeQueueDrag(a, false, Point(Top(a)), false), "begin single detach");
        c.PreviewNativeEdgeQueueDrag(a, Point(100), 100, false);
        c.CommitNativeEdgeQueueDrag(a, Point(100), 100, false);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(a.CapsuleQueueId != "qb" && Math.Abs(Top(a) - 100) < .1, "a single capsule forms a queue at the requested height");
        var beforeMove = c.State.Papers.Select(p => (p.Id, p.CapsuleQueueId)).ToArray();
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin independent drop near another queue");
        c.PreviewNativeEdgeQueueDrag(a, Point(300, distant: true), 300, true);
        c.CommitNativeEdgeQueueDrag(a, Point(300, distant: true), 300, true);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(c.State.Papers.Select(p => (p.Id, p.CapsuleQueueId)).SequenceEqual(beforeMove) &&
                Math.Abs(Top(a) - 300) < .1, "no free-space search or space-based refusal; the requested independent anchor wins");

        c.State.UseCapsuleFreeQueues = false;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var rememberedIds = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        var legacyTop = Top(a);
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(legacyTop), false), "Ctrl is independent of the free-queue switch");
        c.PreviewNativeEdgeQueueDrag(a, Point(legacyTop + 60), legacyTop + 60, false);
        c.CommitNativeEdgeQueueDrag(a, Point(legacyTop + 60), legacyTop + 60, false);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(Math.Abs(Top(a) - legacyTop - 60) < .1, "Ctrl moves an ordinary queue without a master");
        Require(c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(rememberedIds), "legacy mode preserves free grouping memory");
        c.State.UseCapsuleFreeQueues = true;
        c.State.DeepCapsuleQueueStartTopMargins["|right"] = 48;
        c.ArrangeDeepCapsules(false);
        await Idle();
        c.PublicSettings.Set("capsule.master_enabled", JsonSerializer.SerializeToElement(true));
        await Idle();
        Require(c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll,
            "free queues and master coexist via the settings API");
        var masters = (Dictionary<string, MasterCapsuleWindow>)Part(c, "_masterCapsules");
        var aKey = c.QueueKeyForPaper(a);
        Require(masters.Count == 3 && masters.ContainsKey(aKey) &&
                masters.ContainsKey("|right#qb") && masters.ContainsKey("|right#qc"),
            "every independent group has its own master on the same edge");
        var idsBeforeCollection = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        margins = new(c.State.DeepCapsuleQueueStartTopMargins);
        c.ToggleCapsuleCollapseAllActive("|right#qb");
        await Idle();
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
                    .All(p => windows[p.Id].IsCollapseAllRetracted) &&
                c.State.Papers.Where(p => p.CapsuleQueueId != "qb")
                    .All(p => !windows[p.Id].IsCollapseAllRetracted),
            "toggling one master retracts only its own members");
        // Turning the optional free mode off must not discard an individual
        // queue's collapsed choice. Legacy mode can temporarily use a side-wide master.
        c.State.UseCapsuleFreeQueues = false;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(c.State.CapsuleCollapseAllActiveQueues.TryGetValue("|right#qb", out var rememberedFold) &&
                rememberedFold, "legacy mode retains the free queue's remembered collapsed state");
        c.State.UseCapsuleFreeQueues = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(masters.Count == 3 && windows[b.Id].IsCollapseAllRetracted &&
                !windows[a.Id].IsCollapseAllRetracted,
            "returning to free mode restores only the previously folded queue");

        // A collapsed target accepts Ctrl-whole-queue drops only near its
        // visible master, never over its hidden expanded member positions.
        var foldedMasterTop = masters["|right#qb"].AnchorTop;
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin Ctrl whole-queue probe against folded target");
        c.PreviewNativeEdgeQueueDrag(a, Point(foldedMasterTop + 3 * pitch), Top(a), true);
        var phantomDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(phantomDrop?.DestinationKey != "|right#qb" ||
                phantomDrop.Decision.Kind != EdgeFreeQueueDropKind.Insert,
            "hidden member region is not a folded queue merge target");
        c.PreviewNativeEdgeQueueDrag(a, Point(foldedMasterTop), Top(a), true);
        var masterDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(masterDrop?.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                masterDrop.DestinationKey == "|right#qb",
            "Ctrl whole-queue merges when the pointer reaches the visible master");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();

        c.ToggleCapsuleCollapseAllActive("|right#qb");
        await Idle();
        Require(windows.Values.All(w => !w.IsCollapseAllRetracted) &&
                c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(idsBeforeCollection) &&
                margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "expanding a single master restores the original groups and anchors");

        var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qb")!;
        c.PreviewFreeMasterQueueTop(layout, layout.MasterTopDip - 20);
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "master preview never leaks to autosave");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "canceled individual master movement preserves preferences");
        layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qb")!;
        var otherAnchor = c.State.DeepCapsuleQueueStartTopMargins["|right#qc"];
        c.PreviewFreeMasterQueueTop(layout, layout.MasterTopDip - 10);
        c.FinishFreeMasterQueueDrag(layout, true);
        await Idle();
        Require(Math.Abs(masters["|right#qb"].AnchorTop - layout.MasterTopDip + 10) < .1,
            "one master commits its queue translation");
        Require(c.State.DeepCapsuleQueueStartTopMargins["|right#qc"] == otherAnchor,
            "moving a master never rewrites another queue anchor");

        var transferIds = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        var transferTops = c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
            .ToDictionary(p => p.Id, Top);
        var masterTop = masters["|right#qb"].AnchorTop;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, masterTop, out var transfer,
            sourceQueueKey: "|right#qb"), "individual master transfer begins");
        Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(masterTop, left: true)),
            "individual master transfer commits");
        await Idle();
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb").All(p => p.CapsuleSide == DeepCapsuleSides.Left) &&
                c.State.Papers.Where(p => p.CapsuleQueueId != "qb").All(p => p.CapsuleSide == DeepCapsuleSides.Right) &&
                c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(transferIds),
            "cross-edge master movement moves only its own independent queue");
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
                .All(p => Math.Abs(Top(p) - transferTops[p.Id]) < .1),
            "cross-edge transfer retains member positions");
        Require(masters.Count == 3 && masters.ContainsKey("|left#qb") &&
                masters.ContainsKey("|right#qc") && masters.ContainsKey(aKey),
            "only the transferred master changes its physical edge");

        // Vertical preview must be withdrawn before floating, including same-edge release.
        var sameEdgeLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Left, "|left#qb")!;
        c.PreviewFreeMasterQueueTop(sameEdgeLayout, sameEdgeLayout.MasterTopDip + 12);
        Require(PartOrNull(c, "_freeMasterDragPreview") != null, "master has a docked preview before floating");
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left, sameEdgeLayout.MasterTopDip,
            out var sameEdgeTransfer, freeLayout: sameEdgeLayout, sourceQueueKey: "|left#qb"),
            "same-edge master transfer begins after preview");
        Require(PartOrNull(c, "_freeMasterDragPreview") == null,
            "floating transfer clears the old docked preview on entry");
        Require(c.CommitMasterCapsuleQueueTransfer(sameEdgeTransfer,
                Point(sameEdgeLayout.MasterTopDip + 12, left: true)),
            "same-edge master transfer commits after preview");
        await Idle();
        Require(Math.Abs(masters["|left#qb"].AnchorTop - sameEdgeLayout.MasterTopDip - 12) < .1,
            "master uses the committed same-edge anchor");

        c.SaveNow(sync: true);
        var saved = store.Load();
        Require(saved.UseCapsuleFreeQueues && saved.UseCapsuleCollapseAll &&
                saved.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(transferIds),
            "separate masters and their groups survive a storage round trip");

        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin external-mutation cancellation");
        a.CapsuleQueueId = "external";
        c.EndNativeEdgeQueueDrag(a.Id);
        Require(a.CapsuleQueueId == "external",
            "cancellation does not overwrite a legitimate external identity change");

        // A neighboring queue can be pushed down for display without changing its saved margin.
        a.CapsuleSide = DeepCapsuleSides.Left;
        c.State.UseCapsuleCollapseAll = false;
        c.State.DeepCapsuleQueueStartTopMargins["|left#external"] = 90;
        const double preferredSourceTop = 100;
        c.State.DeepCapsuleQueueStartTopMargins["|left#qb"] = preferredSourceTop;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var displacedSourceTop = Top(aPeer);
        Require(displacedSourceTop > preferredSourceTop + 1,
            "source has a passive display offset");
        Require(c.BeginNativeEdgeQueueDrag(aPeer, false, Point(displacedSourceTop, left: true), false),
            "begin detaching from a passively displaced queue");
        var detachedTop = Math.Min(420, monitor.LocalWorkAreaDip.Height - 140);
        var detachedPoint = Point(detachedTop, left: true);
        Require(c.PreviewNativeEdgeQueueDrag(aPeer, detachedPoint, detachedTop, false),
            "single detach preview is handled");
        Require(c.CommitNativeEdgeQueueDrag(aPeer, detachedPoint, detachedTop, false),
            "single detach drop is handled");
        c.EndNativeEdgeQueueDrag(aPeer.Id);
        await Idle();
        Require(aPeer.CapsuleQueueId != "qb", "single capsule was detached");
        Require(Math.Abs(c.State.DeepCapsuleQueueStartTopMargins["|left#qb"] - preferredSourceTop) < .1,
            "single detach never saves the source's passive displacement");
        // Merging two independently controlled groups keeps the target master, including
        // when the target was already collapsed, and retires only the source master.
        foreach (var paper in c.State.Papers)
        {
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = DeepCapsuleSides.Right;
            paper.CapsuleQueueId = paper.Id is "fixture-0" or "fixture-1" ? "qm" : "qn";
        }
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins["|right#qm"] = 80;
        c.State.DeepCapsuleQueueStartTopMargins["|right#qn"] = 330;
        c.State.UseCapsuleCollapseAll = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(masters.Count == 2 && masters.ContainsKey("|right#qm") && masters.ContainsKey("|right#qn"),
            "two groups create two independent masters");
        var targetOldTop = Top(b);
        c.ToggleCapsuleCollapseAllActive("|right#qn");
        await Idle();
        var mergeMasterTop = masters["|right#qn"].AnchorTop;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
            masters["|right#qm"].AnchorTop, out var phantomTransfer, sourceQueueKey: "|right#qm"),
            "begin master drag toward a folded queue's old member area");
        Require(c.CommitMasterCapsuleQueueTransfer(phantomTransfer, Point(mergeMasterTop + 3 * pitch)),
            "independent drop in hidden member area succeeds without merging");
        await Idle();
        Require(masters.Count == 2 && masters.ContainsKey("|right#qm") &&
                masters.ContainsKey("|right#qn"),
            "master drag cannot merge into a collapsed queue through invisible slots");
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
            masters["|right#qm"].AnchorTop, out var mergeTransfer, sourceQueueKey: "|right#qm"),
            "begin one-master merge into another master");
        Require(c.CommitMasterCapsuleQueueTransfer(mergeTransfer, Point(mergeMasterTop)),
            "independent masters merge into the existing target");
        await Idle();
        Require(c.State.Papers.All(p => p.CapsuleQueueId == "qn") &&
                masters.Count == 1 && masters.ContainsKey("|right#qn"),
            "merged source master retires while target master remains");
        Require(c.State.Papers.All(p => windows[p.Id].IsCollapseAllRetracted),
            "merging into a collapsed master preserves the target's collapse choice");
        Require(Math.Abs(Top(b) - targetOldTop) < .1 &&
                !c.State.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qm"),
            "merging into a master preserves old target members and removes the vacant anchor");

        Console.WriteLine("PASS native-free-queue-input-controller-master-and-roundtrip");
    }

    private static object? PartOrNull(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o);
}
