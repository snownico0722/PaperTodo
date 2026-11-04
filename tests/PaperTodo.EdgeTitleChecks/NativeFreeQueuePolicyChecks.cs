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
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 304, 96, 500, pitch, gap) == null,
            "vertical alignment in the middle of the screen is not a magnetic hit");
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
                EdgeCapsuleFreeQueuePolicy.ShouldFloat(56, false) && EdgeCapsuleFreeQueuePolicy.ShouldFloat(0, true) &&
                !EdgeCapsuleFreeQueuePolicy.ShouldFloat(60, false, freeQueues: true) &&
                EdgeCapsuleFreeQueuePolicy.ShouldFloat(68, false, freeQueues: true),
            "leaving a free queue requires slightly more pull; ordinary cross-edge gestures stay unchanged");
        Require(!EdgeCapsuleFreeQueuePolicy.ShouldDetachSingle(false, 400, 400, 2, pitch) &&
                EdgeCapsuleFreeQueuePolicy.ShouldDetachSingle(false, 300, 400, 2, pitch), "sorting and explicit detachment remain distinct");
        Require(EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 325, 46, 10, pitch, gap) == null &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 325, 46, 10, pitch, gap, target.Key) is { Key: "|right#b" } &&
                EdgeCapsuleFreeQueuePolicy.FindInsertion([target], 310, 46, 10, pitch, gap, target.Key) == null,
            "held target survives pointer jitter but releases after the larger exit threshold");
        Require(EdgeCapsuleFreeQueuePolicy.NudgeToward(100, 120, 6) == 106 &&
                EdgeCapsuleFreeQueuePolicy.ResistLeavingSource(395, 400, 450) > 395 &&
                EdgeCapsuleFreeQueuePolicy.ResistLeavingSource(320, 400, 450) == 320,
            "join has a small nudge; leaving is sticky without a release jump");
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
            File.WriteAllText(store.FilePath, "{\"deepCapsuleMonitorDeviceName\":\"LEGACY-SECONDARY\",\"papers\":[{\"id\":\"legacy\"}]}");
            Require(store.Load().Papers[0].CapsuleMonitorDeviceName == "LEGACY-SECONDARY", "genuinely old papers still inherit the global anchor");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
