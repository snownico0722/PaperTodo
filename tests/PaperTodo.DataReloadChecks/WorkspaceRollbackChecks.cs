using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using PaperTodo;

internal static partial class Program
{
    private static async Task WorkspaceRollbackChecks(AppController controller, string directory)
    {
        var catalog = Field<WorkspaceCatalog>(controller, "_workspaceCatalog");
        var mainStore = catalog.MainStore;
        var writerField = typeof(StateStore).GetField("_atomicWriter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalWriter = (IDurableAtomicFileWriter)writerField.GetValue(mainStore)!;
        var processField = typeof(PaperWindow).GetField("PersistentScriptProcesses", BindingFlags.Static | BindingFlags.NonPublic)!;
        var processes = (Dictionary<string, Process>)processField.GetValue(null)!;
        var originalPersistent = controller.State.UsePersistentPowerShellProcess;
        var originalOptions = controller.WorkspaceOptions.Normalized();
        try
        {
            var independent = controller.WorkspaceOptions.Normalized();
            independent.FollowMainSettings[3] = false;
            Require(await controller.ChangeWorkspaceConfigurationAsync(independent), "rollback fixture configuration failed");
            foreach (var oldPersistent in new[] { false, true })
            {
                controller.PublicSettings.Set("scripts.persistent_process", JsonSerializer.SerializeToElement(oldPersistent));
                var target = NewState();
                target.UiLanguage = "en-US";
                target.UsePersistentPowerShellProcess = !oldPersistent;
                target.HideScriptRunWindow = true;
                var targetStore = new StateStore(directory, DurableAtomicFileWriter.Shared, 3);
                targetStore.SaveJsonSync(targetStore.SerializeState(target), 1);
                var reached = false;
                var targetHadExpectedRuntime = false;
                writerField.SetValue(mainStore, new DurableAtomicFileWriter((stage, path) =>
                {
                    // The main catalog is written only after the target's windows, scripts and
                    // own data file are accepted. This injects a deterministic late failure.
                    if (stage == DurableAtomicWriteStage.BeforeTempOpen && path == mainStore.FilePath &&
                        controller.WorkspaceNumber == 3)
                    {
                        reached = true;
                        targetHadExpectedRuntime = (processes.Count > 0) == !oldPersistent;
                        throw new IOException("Injected main-catalog failure after target installation.");
                    }
                }));
                var switched = await controller.SwitchWorkspaceAsync(3);
                writerField.SetValue(mainStore, originalWriter);
                Require(!switched && reached && targetHadExpectedRuntime,
                    "rollback fixture did not fail after installing the target runtime");
                Require(controller.WorkspaceNumber == 1 && controller.State.UsePersistentPowerShellProcess == oldPersistent &&
                    (processes.Count > 0) == oldPersistent,
                    "late rollback retained target script processes or failed to restore original processes");
                Require(J(File.ReadAllText(mainStore.FilePath))["workspaces"]!["active"]!.GetValue<int>() == 1 &&
                    J(controller.PaperBodyPlugins.DataStore.ReadPaperState("fixture", "note-a").Json)["slot"]!.GetValue<int>() == 1,
                    "late rollback changed the active catalog or plugin datastore owner");
                Require(Field<Dictionary<string, PaperWindow>>(controller, "_windows")["note-a"].IsClosed == false,
                    "late rollback did not recreate usable old-workspace windows");
            }
        }
        finally
        {
            writerField.SetValue(mainStore, originalWriter);
            controller.PublicSettings.Set("scripts.persistent_process", JsonSerializer.SerializeToElement(originalPersistent));
            Require(await controller.ChangeWorkspaceConfigurationAsync(originalOptions), "rollback fixture cleanup failed");
        }
        Console.WriteLine("PASS late workspace failure restores script processes, windows, plugin owner and active catalog in both directions");
    }
}
