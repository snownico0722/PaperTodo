using System.IO;
using System.Windows.Input;
using PaperTodo;

internal static class NativeFreeQueuePolicyChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        const double pitch = 50, gap = 4;
        var target = new EdgeFreeQueueBox("|right#b", 400, 2, false, 100);
        var prepend = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 0, 2, 900, pitch, gap);
        Require(prepend.Accepted && prepend.TopDip == 400, "prepend keeps the existing queue head");
        Require(prepend.TopDip + 2 * pitch == 500, "prepend makes room by moving old members down");
        // A has its own master plus four members; B contributes three members
        // on merge. A1/A2, A2/A3 and A3/A4 must choose the same direction.
        var masterTarget = target with { HasMaster = true, MemberCount = 4, TopDip = 200 };
        for (var slot = 0; slot < 4; slot++)
        {
            var roomy = EdgeCapsuleFreeQueuePolicy.Insert([masterTarget], target.Key, slot, 3, 900, pitch, gap);
            Require(roomy.TopDip == 200 &&
                    EdgeCapsuleFreeQueuePolicy.GhostSlotTop(roomy, true, pitch) == 250 + slot * pitch,
                "all master-enabled insertion slots prefer downward space and retain the prefix");
            var bottomTarget = masterTarget with { TopDip = 600 };
            var bounded = EdgeCapsuleFreeQueuePolicy.Insert([bottomTarget], target.Key, slot, 3, 900, pitch, gap);
            Require(bounded.TopDip == 504,
                "every insertion slot lifts only by the missing space below (96 DIP)");
        }
        foreach (var requested in new[] { 100d, 120d, 150d, 180d })
        {
            var independentGroup = EdgeCapsuleFreeQueuePolicy.Place([masterTarget], requested, 4, 900, pitch, gap);
            Require(independentGroup.Kind == EdgeFreeQueueDropKind.Move && independentGroup.TopDip == 0 &&
                    independentGroup.Tops.Count == 0 &&
                    EdgeCapsuleFreeQueuePolicy.GhostSlotTop(independentGroup, true, pitch) == 0,
                "B's complete ghost fits above A without moving A as the pointer approaches");
        }
        var crampedTarget = masterTarget with { TopDip = 120 };
        foreach (var requested in new[] { 0d, 20d, 60d, 100d })
        {
            var cramped = EdgeCapsuleFreeQueuePolicy.Place([crampedTarget], requested, 4, 900, pitch, gap);
            Require(cramped.TopDip == 0 && cramped.Tops[target.Key] == 200,
                "insufficient top space moves A down only by the same 80-DIP deficit");
        }
        var restored = EdgeCapsuleFreeQueuePolicy.Place([crampedTarget], 380, 4, 900, pitch, gap);
        Require(restored.Tops.Count == 0 && restored.TopDip == 380,
            "moving below A uses its original position, not the prior displaced preview");
        var upperGap = new EdgeFreeQueueBox("|right#upper", 0, 2, false, 100);
        var lowerGap = target with { TopDip = 120, MemberCount = 1 };
        foreach (var requested in new[] { 100d, 110d })
        {
            var between = EdgeCapsuleFreeQueuePolicy.Place([upperGap, lowerGap], requested, 1, 900, pitch, gap);
            Require(between.TopDip == 100 && between.Tops.Count == 1 && between.Tops[target.Key] == 150,
                "a short adjacent gap moves only its lower neighbour by the fixed deficit");
        }
        var append = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 2, 2, 900, pitch, gap);
        Require(append.Accepted && append.TopDip == target.TopDip, "append does not move the old head");
        var interior = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 1, 2, 900, pitch, gap);
        Require(interior.Accepted && interior.TopDip == 400 && interior.Index == 1, "interior insertion keeps the prefix");
        Require(EdgeCapsuleFreeQueuePolicy.GhostSlotTop(prepend, false, pitch) == 400 &&
                EdgeCapsuleFreeQueuePolicy.GhostSlotTop(interior, false, pitch) == 450,
            "instant ghost matches exactly the slot the commit will occupy");
        var returning = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 0, 1, 900, pitch, gap);
        Require(returning.Accepted && returning.TopDip == 400, "same-queue sorting retains the legacy anchor");
        var near = target with { Key = "|right#c", TopDip = 520, MemberCount = 1 };
        var far = target with { Key = "|right#d", TopDip = 780, MemberCount = 1 };
        var chain = EdgeCapsuleFreeQueuePolicy.Insert([target, near, far], target.Key, 1, 2, 900, pitch, gap);
        Require(chain.Accepted && chain.Tops[near.Key] == 600 && !chain.Tops.ContainsKey(far.Key), "only the intersected neighbour moves");
        var upper = target with { Key = "|right#a", TopDip = 250 };
        var upperInsert = EdgeCapsuleFreeQueuePolicy.Insert([upper, target], target.Key, 0, 2, 900, pitch, gap);
        Require(upperInsert.Accepted && upperInsert.TopDip == 400 && !upperInsert.Tops.ContainsKey(upper.Key),
            "enough room below keeps the upper neighbour and target head unchanged");
        Require(EdgeCapsuleFreeQueuePolicy.Insert([target with { TopDip = 60 }], target.Key, 0, 2, 900, pitch, gap).TopDip == 60,
            "inserting at the first slot does not consume available space above");
        Require(EdgeCapsuleFreeQueuePolicy.Insert([target with { TopDip = 780 }], target.Key, 2, 2, 900, pitch, gap).TopDip ==
                    900 - (4 * pitch - gap),
            "an expanded merge is lifted just enough to keep its full block inside the display");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 304, 96, 10, pitch, gap) is { Index: 0 },
            "a whole visible block can attach from above without its first member crossing the target");
        var aimedQueue = target with { TopDip = 350 };
        var lowerNeighbour = target with { Key = "|right#neighbour", TopDip = 470, MemberCount = 1 };
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion(
                [aimedQueue, lowerNeighbour], 400, 96, 10, pitch, gap,
                pointerTopDip: 423) is { Key: "|right#b" },
            "when a long carried block overlaps two queues, the queue under the pointer wins");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 304, 96, 500, pitch, gap) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 304, 96, 500, pitch, gap,
                    floating: true) is { Index: 0 },
            "docked drags use the wall gate; floating drags can preview the same slot anywhere in the half-screen");
        var folded = new EdgeFreeQueueBox("|right#folded", 400, 5, true, 55, Collapsed: true);
        Require(folded.Height(pitch, gap) == PaperLayoutDefaults.CapsuleHeight,
            "a collapsed queue occupies only the visible master, not its old expanded slots");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([folded], 390, 300, 10, pitch, gap,
                    pointerTopDip: 415) is { Key: "|right#folded", Index: 5 },
            "pointer near the visible master can append a carried queue into a collapsed target");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([folded], 390, 300, 10, pitch, gap,
                    pointerTopDip: 600) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([folded], 390, 300, 10, pitch, gap,
                    pointerTopDip: 280) == null,
            "the expanded-but-hidden member area is not a collapsed target even when the carried block overlaps it");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([folded], 390, 300, 90, pitch, gap,
                    pointerTopDip: 415) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([folded], 390, 46, 500, pitch, gap,
                    pointerTopDip: 415, floating: true) is { Index: 5 },
            "a distant floating master can join at the visible collapsed master, never its hidden members");
        var belowFolded = target with { Key = "|right#below", TopDip = 470, MemberCount = 1 };
        var foldedMerge = EdgeCapsuleFreeQueuePolicy.Insert([folded, belowFolded], folded.Key, 0, 2,
            900, pitch, gap);
        Require(foldedMerge.Accepted && foldedMerge.Index == folded.MemberCount &&
                foldedMerge.TopDip == folded.TopDip && !foldedMerge.Tops.ContainsKey(belowFolded.Key),
            "joining a collapsed master appends, preserves its head, and does not push neighbours through invisible slots");
        Require(EdgeCapsuleFreeQueuePolicy.GhostSlotTop(foldedMerge, true, pitch, targetCollapsed: true) == folded.TopDip,
            "collapsed-target feedback points to the visible master instead of an invisible insertion slot");

        var overlap = EdgeCapsuleFreeQueuePolicy.Place([target], 420, 1, 900, pitch, gap);
        Require(overlap.Accepted && overlap.TopDip == 500 && overlap.Tops.Count == 0,
            "an independent ghost below A clears its tail instead of pushing A upward");
        var lower = target with { TopDip = 146, MemberCount = 1 };
        var crowded = EdgeCapsuleFreeQueuePolicy.Place([lower], 100, 1, 200, pitch, gap);
        Require(crowded.Accepted && crowded.TopDip == 96 && crowded.Tops.Count == 0,
            "the ghost consumes available space above before moving its neighbour");
        var legacy = EdgeCapsuleFreeQueuePolicy.Place([target], 420, 1, 900, pitch, gap, stableNeighbours: false);
        Require(legacy.TopDip == 420 && legacy.Tops[target.Key] == 320,
            "legacy Ctrl placement retains its existing exact-anchor behavior");
        var exact = EdgeCapsuleFreeQueuePolicy.Place([lower], 96, 1, 200, pitch, gap);
        Require(exact.Accepted && exact.TopDip == 96, "a legal exact position stays exact");
        Require(EdgeCapsuleFreeQueuePolicy.Place([], 500, 1, 900, pitch, gap).TopDip == 500,
            "valid independent placement preserves the requested anchor");
        Require(EdgeCapsuleFreeQueuePolicy.Place([], -120, 1, 900, pitch, gap).TopDip == 0 &&
                EdgeCapsuleFreeQueuePolicy.Place([], 1000, 1, 900, pitch, gap).TopDip ==
                    900 - PaperLayoutDefaults.CapsuleHeight,
            "dragged capsules stop at both work-area edges instead of leaving the screen");
        Require(EdgeCapsuleFreeQueuePolicy.Place([], 500, 20, 500, pitch, gap) is
                    { Accepted: true, TopDip: 0 },
            "an oversized group still drops successfully with its first capsule visible");
        Require(!EdgeCapsuleFreeQueuePolicy.ShouldFloat(0, false) && !EdgeCapsuleFreeQueuePolicy.ShouldFloat(55, false) &&
                EdgeCapsuleFreeQueuePolicy.ShouldFloat(56, false) && EdgeCapsuleFreeQueuePolicy.ShouldFloat(0, true),
            "free and ordinary queues unlock without additional sticky distance");
        Require(!EdgeCapsuleFreeQueuePolicy.ShouldDetachSingle(false, 400, 400, 2, pitch) &&
                EdgeCapsuleFreeQueuePolicy.ShouldDetachSingle(false, 300, 400, 2, pitch), "sorting and explicit detachment remain distinct");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 325, 46, 10, pitch, gap) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 325, 46, 10, pitch, gap, target.Key) is { Key: "|right#b" } &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 310, 46, 10, pitch, gap, target.Key) == null,
            "held target survives pointer jitter but releases after the larger exit threshold");
        // Four members: free sorting within the entire original queue, including
        // moving the third onto the fourth. The two outer slots behave symmetrically.
        var firstMemberTop = 400d;
        var lastMemberTop = firstMemberTop + 3 * pitch;
        var snap = new EdgeQueueExitSnapState();
        double Visual(double top) =>
            EdgeCapsuleFreeQueuePolicy.SnapLeavingSource(top, firstMemberTop, lastMemberTop, snap);
        Require(EdgeCapsuleFreeQueuePolicy.ExitSnapDistance == 18 &&
                Visual(firstMemberTop + 2 * pitch) == firstMemberTop + 2 * pitch &&
                Visual(lastMemberTop) == lastMemberTop,
            "dragging the third into the fourth slot has no magnetic effect");
        Require(Visual(lastMemberTop + 5) == lastMemberTop &&
                Visual(lastMemberTop + 15) == lastMemberTop &&
                Visual(lastMemberTop + 19) == lastMemberTop + 19,
            "crossing the bottom edge snaps directly to the last slot, then releases at 18 DIP");
        Require(Visual(lastMemberTop + 9) == lastMemberTop + 9 &&
                Visual(lastMemberTop) == lastMemberTop &&
                Visual(lastMemberTop + 7) == lastMemberTop,
            "dragging inward is free; returning into the queue rearms the next outward snap");
        Require(Visual(firstMemberTop + pitch) == firstMemberTop + pitch &&
                Visual(firstMemberTop - 6) == firstMemberTop &&
                Visual(firstMemberTop - 17) == firstMemberTop &&
                Visual(firstMemberTop - 20) == firstMemberTop - 20,
            "upward dragging snaps to the first slot and releases at the same 18-DIP threshold");
        Require(Visual(firstMemberTop - 10) == firstMemberTop - 10 &&
                Visual(firstMemberTop) == firstMemberTop &&
                Visual(firstMemberTop - 8) == firstMemberTop,
            "upward reentry is free and upward outward snapping retriggers on every crossing");
        var narrowTarget = target with { WidthDip = 60 };
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([narrowTarget], 410, 46, 500, pitch, gap) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([narrowTarget], 410, 46, 500,
                    pitch, gap, floating: true) is { Key: "|right#b" },
            "a floating capsule may join from any horizontal distance while its vertical target is valid");
        Require(EdgeCapsuleLayout.FreeQueueTopForIndex(0, -40, new System.Windows.Rect(0, 0, 100, 900), gap) == -40,
            "free queues do not clamp anchors back into the work area");
        Require(PaperWindow.NativeQueueControlPressed(8, ModifierKeys.None, false) &&
                PaperWindow.NativeQueueControlPressed(0, ModifierKeys.Control, false) &&
                PaperWindow.NativeQueueControlPressed(0, ModifierKeys.None, true) &&
                !PaperWindow.NativeQueueControlPressed(0, ModifierKeys.Shift, false), "native, WPF and non-activating host Ctrl input");
        Require(WindowNative.IsAsyncKeyDown(unchecked((short)0x8000)) && !WindowNative.IsAsyncKeyDown(1),
            "only the current OS key state selects a queue");
        var a = new PaperData { Id = "a" };
        var b = new PaperData { Id = "b" };
        var plan = EdgeCapsuleQueueCoordinator.Build([new(a, "|right"), new(b, "|right#b")], true, key => key == "|right");
        Require(plan.Placements[a.Id].VisualOffset == 1 && plan.Placements[b.Id].VisualOffset == 0,
            "a side-wide collection control does not reserve a header slot in every independent group");
        // Logical DIP invariants do not depend on whether a monitor is 100%, 150% or 200% scaled.
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var m = new MonitorGeometry("test", new DeviceScreenRect(0, 0, (int)(1000 * scale), (int)(900 * scale)), scale, scale);
            var y = m.DeviceYToLocalDip(400 * scale);
            Require(y == 400 && EdgeCapsuleFreeQueuePolicy.Insert([target with { TopDip = y }], target.Key, 0, 2,
                m.LocalWorkAreaDip.Height, pitch, gap).TopDip == 400, "DPI does not change downward-first anchoring");
        }
        Persistence();
        Console.WriteLine("PASS native-free-queue-policy-and-persistence");
    }

    private static void Persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "papertodo-native-queues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new StateStore(directory, DurableAtomicFileWriter.Shared);
            var state = new AppState
            {
                UseCapsuleMode = true, UseDeepCapsuleMode = true, UseCapsuleFreeQueues = true,
                UseCapsuleCollapseAll = true, DeepCapsuleMonitorDeviceName = "LEGACY-SECONDARY",
                Papers = [new PaperData { Id = "a", CapsuleSide = "right", CapsuleQueueId = "qa", CapsuleMonitorDeviceName = "" },
                    new PaperData { Id = "b", CapsuleSide = "right", CapsuleQueueId = "qb", CapsuleMonitorDeviceName = "" }]
            };
            state.DeepCapsuleQueueStartTopMargins["|right#qa"] = 100;
            state.DeepCapsuleQueueStartTopMargins["|right#qb"] = 400;
            state.CapsuleCollapseAllActiveQueues["|right#qb"] = true;
            File.WriteAllText(store.FilePath, store.SerializeState(state));
            var loaded = store.Load();
            Require(loaded.UseCapsuleFreeQueues && loaded.UseCapsuleCollapseAll, "free queues and master coexist after loading");
            Require(loaded.Papers.All(p => p.CapsuleMonitorDeviceName == ""), "explicit primary must not inherit a legacy secondary");
            Require(loaded.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(["qa", "qb"]), "membership survives persistence");
            Require(loaded.DeepCapsuleQueueStartTopMargins["|right#qa"] == 100 && loaded.DeepCapsuleQueueStartTopMargins["|right#qb"] == 400,
                "instance anchors remain separate");
            loaded.UseCapsuleFreeQueues = false;
            File.WriteAllText(store.FilePath, store.SerializeState(loaded));
            var disabled = store.Load();
            Require(disabled.Papers[0].CapsuleQueueId == "qa" && disabled.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qb"),
                "disabling the mode preserves layout memory");
            Require(disabled.CapsuleCollapseAllActiveQueues.TryGetValue("|right#qb", out var foldedChoice) && foldedChoice,
                "disabling free queues also preserves the independent master's collapse memory");
            File.WriteAllText(store.FilePath, "{\"deepCapsuleMonitorDeviceName\":\"LEGACY-SECONDARY\",\"papers\":[{\"id\":\"legacy\"}]}");
            Require(store.Load().Papers[0].CapsuleMonitorDeviceName == "LEGACY-SECONDARY", "genuinely old papers still inherit the global anchor");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

