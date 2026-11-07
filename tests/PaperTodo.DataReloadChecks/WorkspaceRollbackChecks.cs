using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task WorkspaceMainCatalogSingleWriteChecks(AppController controller)
    {
        Require(controller.WorkspaceNumber == 1, "main catalog fixture requires the active main workspace");
        var catalog = Field<WorkspaceCatalog>(controller, "_workspaceCatalog");
        var store = catalog.MainStore;
        var writerField = typeof(StateStore).GetField("_atomicWriter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalWriter = (IDurableAtomicFileWriter)writerField.GetValue(store)!;
        var original = controller.WorkspaceOptions.Normalized();
        var next = controller.WorkspaceOptions.Normalized();
        next.Slots = next.ContainsSlot(5)
            ? next.SlotNumbers.Where(number => number != 5).ToList()
            : next.SlotNumbers.Append(5).OrderBy(number => number).ToList();
        var writes = 0;
        var activeState = controller.State;
        try
        {
            writerField.SetValue(store, new DurableAtomicFileWriter((stage, path) =>
            {
                if (stage == DurableAtomicWriteStage.BeforeTempOpen && path == store.FilePath)
                    writes++;
            }));
            Require(await controller.ChangeWorkspaceConfigurationAsync(next),
                "main workspace configuration update failed");
            Require(writes == 1, "main workspace catalog update performed a second accepting write");
            Require(ReferenceEquals(activeState, controller.State) &&
                    controller.WorkspaceOptions.SlotNumbers.SequenceEqual(next.SlotNumbers) &&
                    controller.State.Workspaces?.SlotNumbers.SequenceEqual(next.SlotNumbers) == true &&
                    J(File.ReadAllText(store.FilePath))["workspaces"]!["slots"]!.AsArray()
                        .Select(value => value!.GetValue<int>()).SequenceEqual(next.SlotNumbers),
                "one-pass main configuration update did not keep disk and live state aligned");
        }
        finally
        {
            writerField.SetValue(store, originalWriter);
            if (!controller.WorkspaceOptions.SlotNumbers.SequenceEqual(original.SlotNumbers))
                Require(await controller.ChangeWorkspaceConfigurationAsync(original),
                    "main catalog fixture could not restore workspace configuration");
        }
        Console.WriteLine("PASS active main workspace catalog and papers persist in one guarded save");
    }

    private static async Task WorkspaceRollbackChecks(AppController controller, string directory)
    {
        var catalog = Field<WorkspaceCatalog>(controller, "_workspaceCatalog");
        var mainStore = catalog.MainStore;
        var writerField = typeof(StateStore).GetField("_atomicWriter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalWriter = (IDurableAtomicFileWriter)writerField.GetValue(mainStore)!;
        var processField = typeof(ScriptExecutionService).GetField("PersistentScriptProcesses", BindingFlags.Static | BindingFlags.NonPublic)!;
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
                var switched = await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(3), dialog =>
                {
                    Require(!Application.Current.Windows.OfType<Window>().Any(window =>
                            window.IsVisible && window.Title == Strings.Get("WorkspaceScriptsStoppedTitle")),
                        "failed workspace switch showed a successful script stop notice");
                    dialog.Close();
                });
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(!Application.Current.Windows.OfType<Window>().Any(window =>
                        window.IsVisible && window.Title == Strings.Get("WorkspaceScriptsStoppedTitle")),
                    "workspace rollback queued a successful script stop notice");
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
