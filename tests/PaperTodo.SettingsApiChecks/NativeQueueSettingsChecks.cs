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
        service.Set("capsule.edge_enabled", Json(false));
        Check(!c.State.UseDeepCapsuleMode && !c.State.UseCapsuleFreeQueues && !c.State.UseCapsuleCollapseAll,
            "disabling the parent clears effective children");
        service.Set("capsule.free_queues", Json(true));
        Check(c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode && c.State.UseCapsuleFreeQueues, "free queues enable prerequisite modes");
        Check(!service.Set("capsule.free_queues", Json(true)).Changed, "repeated enable is a no-op");
        var failed = Controller();
        failed.State.UseCapsuleMode = failed.State.UseDeepCapsuleMode = failed.State.UseCapsuleCollapseAll = false;
        var failing = SettingsFor(failed, () => false, _ => throw new InvalidOperationException("failed save published"));
        Throws<PaperSettingsException>(() => failing.Set("capsule.free_queues", Json(true)), "save_failed");
        Check(!failed.State.UseCapsuleFreeQueues && !failed.State.UseCapsuleMode && !failed.State.UseDeepCapsuleMode &&
              !failed.State.UseCapsuleCollapseAll, "failed save restores the free flag and all prerequisite flags");
    }
}
