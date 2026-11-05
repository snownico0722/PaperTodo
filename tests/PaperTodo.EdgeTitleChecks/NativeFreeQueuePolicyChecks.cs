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
        Require(prepend.Accepted && prepend.TopDip == 300, "prepend grows above the existing target");
        Require(prepend.TopDip + 2 * pitch == target.TopDip, "prepend preserves the first old member");
        var append = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 2, 2, 900, pitch, gap);
        Require(append.Accepted && append.TopDip == target.TopDip, "append does not move the old head");
        var interior = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 1, 2, 900, pitch, gap);
        Require(interior.Accepted && interior.TopDip == 400 && interior.Index == 1, "interior insertion keeps the prefix");
        Require(EdgeCapsuleFreeQueuePolicy.GhostSlotTop(prepend, false, pitch) == 300 &&
                EdgeCapsuleFreeQueuePolicy.GhostSlotTop(interior, false, pitch) == 450,
            "instant ghost matches exactly the slot the commit will occupy");
        var returning = EdgeCapsuleFreeQueuePolicy.Insert([target], target.Key, 0, 1, 900, pitch, gap, prependAbove: false);
        Require(returning.Accepted && returning.TopDip == 400, "same-queue sorting retains the legacy anchor");
        var near = target with { Key = "|right#c", TopDip = 520, MemberCount = 1 };
        var far = target with { Key = "|right#d", TopDip = 780, MemberCount = 1 };
        var chain = EdgeCapsuleFreeQueuePolicy.Insert([target, near, far], target.Key, 1, 2, 900, pitch, gap);
        Require(chain.Accepted && chain.Tops[near.Key] == 600 && !chain.Tops.ContainsKey(far.Key), "only the intersected neighbour moves");
        var upper = target with { Key = "|right#a", TopDip = 250 };
        var upperInsert = EdgeCapsuleFreeQueuePolicy.Insert([upper, target], target.Key, 0, 2, 900, pitch, gap);
        Require(upperInsert.Accepted && upperInsert.TopDip == 300 && upperInsert.Tops[upper.Key] == 200,
            "the upper neighbour moves upward instead of pushing the target towards the screen bottom");
        Require(EdgeCapsuleFreeQueuePolicy.Insert([target with { TopDip = 60 }], target.Key, 0, 2, 900, pitch, gap).TopDip == -40,
            "prepend may extend above the work area without moving the target's existing head");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 304, 96, 10, pitch, gap) is { Index: 0 },
            "a whole visible block can attach from above without its first member crossing the target");
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
        Require(overlap.Accepted && overlap.TopDip == 420 && overlap.Tops[target.Key] == 320,
            "an independent drop keeps its exact anchor and moves only the intersecting neighbour");
        var lower = target with { TopDip = 146, MemberCount = 1 };
        var crowded = EdgeCapsuleFreeQueuePolicy.Place([lower], 100, 1, 200, pitch, gap);
        Require(crowded.Accepted && crowded.Tops[lower.Key] == 150,
            "a full edge allows the drop and pushes the neighbour instead of rejecting it");
        var exact = EdgeCapsuleFreeQueuePolicy.Place([lower], 96, 1, 200, pitch, gap);
        Require(exact.Accepted && exact.TopDip == 96, "a legal exact position stays exact");
        Require(EdgeCapsuleFreeQueuePolicy.Place([], 500, 1, 900, pitch, gap).TopDip == 500,
            "empty space never changes the requested anchor");
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
                m.LocalWorkAreaDip.Height, pitch, gap).TopDip == 300, "DPI does not change prepend anchoring");
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
