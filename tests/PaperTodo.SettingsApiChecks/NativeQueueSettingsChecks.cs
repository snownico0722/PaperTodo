using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void NativeQueueSettingsBehavior()
    {
        var c = Controller();
        var service = SettingsFor(c, () => true, _ => { });
        service.Set("capsule.free_queues", Json(true));
        Check(c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll, "enabling free queues retains master collection");
        service.Set("capsule.master_enabled", Json(false));
        Check(c.State.UseCapsuleFreeQueues && !c.State.UseCapsuleCollapseAll, "disabling master retains free queues");
        service.Set("capsule.master_enabled", Json(true));
        Check(c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll, "enabling master retains free queues");
        c.State.Papers.Add(new PaperData { Type = PaperTypes.Note, CapsuleSide = DeepCapsuleSides.Left });
        c.State.CapsuleCollapseAllActiveQueues["|left"] = true;
        service.Set("capsule.edge_enabled", Json(false));
        Check(c.State.UseCapsuleMode && !c.State.UseDeepCapsuleMode &&
              c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll &&
              !c.EdgeCapsuleFreeQueuesEnabled &&
              c.State.CapsuleCollapseAllActiveQueues.ContainsKey("|left"),
            "disabling edge capsules suspends presentation without clearing saved children or folded queues");
        service.Set("capsule.edge_enabled", Json(true));
        Check(c.State.UseDeepCapsuleMode && c.State.UseCapsuleFreeQueues &&
              c.State.UseCapsuleCollapseAll && c.State.CapsuleCollapseAllActiveQueues.ContainsKey("|left"),
            "re-enabling edge capsules restores saved choices and folded groups");

        service.Set("capsule.enabled", Json(false));
        Check(!c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode &&
              c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll &&
              !c.EdgeCapsuleFreeQueuesEnabled &&
              c.State.CapsuleCollapseAllActiveQueues.ContainsKey("|left"),
            "disabling all capsules preserves the edge settings subtree without running its mode");
        service.Set("capsule.enabled", Json(true));
        Check(c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode &&
              c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll &&
              c.State.CapsuleCollapseAllActiveQueues.ContainsKey("|left"),
            "re-enabling capsules restores the saved edge, master and free-queue choices");

        service.Set("capsule.master_enabled", Json(false));
        Check(c.State.UseCapsuleFreeQueues && !c.State.UseCapsuleCollapseAll &&
              c.State.CapsuleCollapseAllActiveQueues.Count == 0,
            "explicitly disabling master still clears the old collapsed queue state");
        service.Set("capsule.master_enabled", Json(true));
        service.Set("capsule.enabled", Json(false));
        Check(service.Set("capsule.master_enabled", Json(true)).Changed &&
              c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode,
            "explicitly enabling an already-selected master restores missing prerequisites");
        service.Set("capsule.enabled", Json(false));
        Check(service.Set("capsule.free_queues", Json(true)).Changed &&
              c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode && c.State.UseCapsuleFreeQueues,
            "explicitly enabling remembered free queues restores missing prerequisites");
        Check(!service.Set("capsule.free_queues", Json(true)).Changed, "repeated enable is a no-op");
        var failed = Controller();
        failed.State.UseCapsuleMode = failed.State.UseDeepCapsuleMode = failed.State.UseCapsuleCollapseAll = false;
        var failing = SettingsFor(failed, () => false, _ => throw new InvalidOperationException("failed save published"));
        Throws<PaperSettingsException>(() => failing.Set("capsule.free_queues", Json(true)), "save_failed");
        Check(!failed.State.UseCapsuleFreeQueues && !failed.State.UseCapsuleMode && !failed.State.UseDeepCapsuleMode &&
              !failed.State.UseCapsuleCollapseAll, "failed save restores the free flag and all prerequisite flags");
    }
}
