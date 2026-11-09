using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PaperTodo;

internal static partial class Program
{
    private static string WriteHistoryFixture(string directory, DateTime stamp,
        params (string Name, string Json)[] documents)
    {
        var backups = Path.Combine(directory, "PaperTodo-backups");
        Directory.CreateDirectory(backups);
        var path = Path.Combine(backups,
            "PaperTodo-core-" + stamp.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var document in documents)
        {
            using var output = new StreamWriter(zip.CreateEntry(document.Name).Open(), new UTF8Encoding(false));
            output.Write(document.Json);
        }
        return path;
    }

    private static void HistoryRecoveryChecks()
    {
        var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var name = Path.GetFileName(store.FilePath);
            var older = NewState();
            older.Papers[0].Content = "older history";
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(-1), (name, store.SerializeState(older)));
            var latest = NewState();
            latest.Papers[0].Content = "latest valid history";
            var selected = WriteHistoryFixture(scope.DirectoryPath, stamp, (name, store.SerializeState(latest)),
                (number == 1 ? "data2.json" : "data.json", "{unrelated broken workspace"));
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(1), (name, "{}"));
            WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(2), ("data5.json", store.SerializeState(older)));
            var brokenZip = WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(3), (name, store.SerializeState(older)));
            File.WriteAllText(brokenZip, "not a ZIP");
            var crcZip = WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(4), (name, store.SerializeState(older)));
            var damagedBytes = File.ReadAllBytes(crcZip);
            var central = -1;
            for (var offset = damagedBytes.Length - 20; offset >= 0; offset--)
                if (damagedBytes[offset] == 0x50 && damagedBytes[offset + 1] == 0x4B &&
                    damagedBytes[offset + 2] == 0x01 && damagedBytes[offset + 3] == 0x02)
                { central = offset; break; }
            Require(central >= 0, "history CRC fixture missing central directory");
            damagedBytes[central + 16] ^= 0x80;
            File.WriteAllBytes(crcZip, damagedBytes);
            var archiveBytes = File.ReadAllBytes(selected);
            File.WriteAllText(store.FilePath, "{unrecoverable primary");
            File.WriteAllText(store.BackupPath, "{unrecoverable backup");
            var orphan = store.FilePath + ".orphaned.20260101.tmp";
            File.WriteAllText(orphan, "{partial");
            var otherPath = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
                number == 1 ? 2 : 1).FilePath;
            File.WriteAllText(otherPath, "other workspace must stay untouched");

            Throws<StateRecoveryException>(() => store.Load());
            var prompts = 0;
            Throws<OperationCanceledException>(() => store.Load(out _, time =>
            {
                prompts++;
                Require(time == stamp, "offer did not choose the latest valid workspace entry");
                return WorkspaceRecoveryChoice.Cancel;
            }));
            Require(prompts == 1 && !store.NeedsStartupRecoverySave && File.Exists(orphan) &&
                    File.ReadAllText(store.FilePath) == "{unrecoverable primary" &&
                    File.ReadAllBytes(selected).SequenceEqual(archiveBytes),
                "declined history recovery changed old data or tried another backup");
            Throws<WorkspaceRecoveryExitException>(() => store.Load(out _, _ => WorkspaceRecoveryChoice.Exit));
            Require(!store.NeedsStartupRecoverySave && File.ReadAllText(store.FilePath) == "{unrecoverable primary",
                "exit from history recovery changed the unavailable workspace");

            var reject = true;
            var writer = new DurableAtomicFileWriter((stage, target) =>
            {
                if (reject && target == store.FilePath && stage == DurableAtomicWriteStage.BeforeReplace)
                    throw new IOException("Injected historical recovery write failure.");
            });
            var recovering = new StateStore(scope.DirectoryPath, writer, number);
            var state = recovering.Load(out var empty, time => time == stamp ? WorkspaceRecoveryChoice.Restore : WorkspaceRecoveryChoice.Cancel);
            Require(!empty && recovering.NeedsStartupRecoverySave &&
                    state.Papers[0].Content == "latest valid history" &&
                    !recovering.TryCollectProtectedImageIds(state, out _) && File.Exists(orphan),
                "history was not staged as a protected full recovery");
            Throws<IOException>(() => recovering.SaveJsonSync(recovering.SerializeState(state), 2));
            Require(File.ReadAllText(store.FilePath) == "{unrecoverable primary" && File.Exists(orphan),
                "failed historical recovery changed primary or cleaned orphans");
            reject = false;
            recovering.SaveJsonSync(recovering.SerializeState(state), 3);
            Require(!recovering.NeedsStartupRecoverySave && !File.Exists(orphan) &&
                    recovering.Load().Papers[0].Content == "latest valid history" &&
                    File.ReadAllText(otherPath) == "other workspace must stay untouched" &&
                    File.ReadAllText(store.BackupPath) == "{unrecoverable backup" &&
                    File.ReadAllBytes(selected).SequenceEqual(archiveBytes),
                "history restore did not commit exactly one workspace and retain its ZIP");
        }

        foreach (var number in new[] { 1, 2 })
        {
            using var scope = new Scope();
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared, number);
            var oldState = NewState();
            if (number == 1) oldState.Workspaces = new() { Enabled = true, Slots = [1, 2, 3], Active = 2 };
            var archive = WriteHistoryFixture(scope.DirectoryPath, stamp,
                (Path.GetFileName(store.FilePath), store.SerializeState(oldState)));
            var archiveBytes = File.ReadAllBytes(archive);
            File.WriteAllText(store.FilePath, "{old unavailable data");
            var fail = true;
            var restarting = new StateStore(scope.DirectoryPath, new DurableAtomicFileWriter((stage, path) =>
            {
                if (fail && path == store.FilePath && stage == DurableAtomicWriteStage.BeforeReplace)
                    throw new IOException("Restart write failure.");
            }), number);
            var blank = restarting.Load(out var empty, _ => WorkspaceRecoveryChoice.CreateEmpty);
            Require(empty && blank.Papers.Count == 0 && restarting.NeedsStartupRecoverySave &&
                    File.ReadAllText(store.FilePath) == "{old unavailable data",
                "new workspace was not staged as an explicit blank choice");
            Require(number != 1 || blank.Workspaces is { Active: 1 } catalog && catalog.ContainsSlot(3),
                "restarting main hid other workspace entries or selected another workspace");
            Throws<IOException>(() => restarting.SaveJsonSync(restarting.SerializeState(blank), 2));
            Require(File.ReadAllText(store.FilePath) == "{old unavailable data", "failed restart overwrote old data");
            fail = false;
            restarting.SaveJsonSync(restarting.SerializeState(blank), 3);
            Require(restarting.Load().Papers.Count == 0 && File.ReadAllBytes(archive).SequenceEqual(archiveBytes),
                "explicit restart restored old content or removed the historical backup");
        }

        foreach (var choice in new[] { WorkspaceRecoveryChoice.Restore, WorkspaceRecoveryChoice.CreateEmpty })
        foreach (var changed in new[] { "primary", "archive", "shutdown" })
        {
            using var scope = new Scope();
            var json = scope.Store.SerializeState(NewState());
            var archive = WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", json));
            File.WriteAllText(scope.Store.FilePath, "{broken");
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            WorkspaceRecoveryChoice Confirm(DateTime? _)
            {
                if (changed == "primary") File.WriteAllText(store.FilePath, json);
                if (changed == "archive") File.AppendAllText(archive, "changed during prompt");
                if (changed == "shutdown") store.StopWritesForTermination();
                return choice;
            }
            if (changed == "shutdown")
            {
                Throws<OperationCanceledException>(() => store.Load(out _, Confirm));
                Require(!store.NeedsStartupRecoverySave, "shutdown accepted pending history recovery");
            }
            else
            {
                var state = store.Load(out _, Confirm);
                if (choice == WorkspaceRecoveryChoice.CreateEmpty && changed == "archive")
                {
                    store.SaveJsonSync(store.SerializeState(state), 2);
                    Require(store.Load().Papers.Count == 0, "blank rebuild unnecessarily depended on historical bytes");
                    continue;
                }
                Throws<IOException>(() => store.SaveJsonSync(store.SerializeState(state), 2));
            }
            Require(File.ReadAllText(store.FilePath) == (changed == "primary" ? json : "{broken"),
                "confirmation allowed stale or terminating historical recovery to overwrite primary");
        }

        foreach (var source in new[] { "primary", "backup", "temp", "none", "busy", "fresh" })
        {
            using var scope = new Scope();
            var json = File.ReadAllText(scope.Store.FilePath);
            WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", source == "none" ? "{}" : json));
            if (source != "primary" && source != "busy") File.WriteAllText(scope.Store.FilePath, "{broken");
            if (source == "backup") File.WriteAllText(scope.Store.BackupPath, json);
            if (source == "temp") File.WriteAllText(scope.Store.FilePath + ".tmp", json);
            if (source == "fresh") File.Delete(scope.Store.FilePath);
            using var held = source == "busy"
                ? new FileStream(scope.Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            WorkspaceRecoveryChoice UnexpectedPrompt(DateTime? _) => throw new InvalidOperationException("Unexpected history offer: " + source);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            if (source == "none")
            {
                Throws<StateRecoveryException>(() => store.Load());
                foreach (var choice in new[] { WorkspaceRecoveryChoice.Cancel, WorkspaceRecoveryChoice.Exit,
                             WorkspaceRecoveryChoice.Fallback })
                {
                    Throws<OperationCanceledException>(() => store.Load(out _, time =>
                    {
                        Require(time == null, "unavailable sources offered a historical timestamp");
                        return choice;
                    }));
                    Require(!store.NeedsStartupRecoverySave && File.ReadAllText(store.FilePath) == "{broken",
                        "dismissal, exit or fallback changed the corrupt workspace");
                }
                var blank = store.Load(out var empty, time =>
                {
                    Require(time == null, "unavailable sources offered a historical timestamp");
                    return WorkspaceRecoveryChoice.CreateEmpty;
                });
                Require(empty && blank.Papers.Count == 0 && store.NeedsStartupRecoverySave,
                    "explicit rebuild did not stage blank content");
            }
            else if (source == "busy") Throws<IOException>(() => store.Load(out _, UnexpectedPrompt));
            else store.Load(out _, UnexpectedPrompt);
        }

        // One locked old candidate must not veto another complete temp or history.
        foreach (var source in new[] { "temp", "history", "none" })
        {
            using var scope = new Scope();
            var json = scope.BaselineJson;
            File.WriteAllText(scope.Store.FilePath, "{broken");
            var orphan = scope.Store.FilePath + ".orphaned.old.tmp";
            File.WriteAllText(orphan, json);
            if (source == "temp") File.WriteAllText(scope.Store.FilePath + ".tmp", json);
            if (source == "history") WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", json));
            using var held = new FileStream(orphan, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            if (source == "none")
            {
                Throws<IOException>(() => store.Load(out _, _ =>
                    throw new InvalidOperationException("Locked data was reported as corrupt.")));
                continue;
            }
            var loaded = store.Load(out var empty, time =>
            {
                Require(source == "history" && time == stamp, "temp recovery unexpectedly prompted");
                return WorkspaceRecoveryChoice.Restore;
            });
            Require(!empty && loaded.Papers[0].Content == "original", "locked orphan vetoed usable recovery");
            store.SaveJsonSync(store.SerializeState(loaded), 2);
            Require(File.ReadAllText(store.FilePath).Contains("original"), "locked orphan vetoed recovery commit");
        }

        // A locked newer history ZIP still permits an older readable snapshot.
        using (var scope = new Scope())
        {
            File.WriteAllText(scope.Store.FilePath, "{broken");
            var older = WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", scope.BaselineJson));
            var newer = WriteHistoryFixture(scope.DirectoryPath, stamp.AddHours(1), ("data.json", scope.BaselineJson));
            using var held = new FileStream(newer, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var store = new StateStore(scope.DirectoryPath, DurableAtomicFileWriter.Shared);
            store.Load(out _, time =>
            {
                Require(time == stamp, "locked history prevented selection of older usable ZIP");
                return WorkspaceRecoveryChoice.Restore;
            });
            File.Delete(older);
            Throws<IOException>(() => store.Load(out _, _ =>
                throw new InvalidOperationException("Locked history was reported as corrupt.")));
        }

        // The same callback is used for main startup and numbered workspace entry.
        using (var scope = new Scope())
        {
            var json = scope.Store.SerializeState(NewState());
            WriteHistoryFixture(scope.DirectoryPath, stamp, ("data.json", json), ("data2.json", json));
            File.WriteAllText(scope.Store.FilePath, "{broken main");
            File.WriteAllText(Path.Combine(scope.DirectoryPath, "data2.json"), "{broken secondary");
            var offered = new List<int>();
            var catalog = new WorkspaceCatalog(scope.DirectoryPath, DurableAtomicFileWriter.Shared,
                (store, time) => { offered.Add(store.WorkspaceNumber); return time == stamp ? WorkspaceRecoveryChoice.Restore : WorkspaceRecoveryChoice.Cancel; });
            var main = catalog.OpenInitial();
            var second = catalog.Open(2, catalog.Options, main.State);
            Require(offered.SequenceEqual(new[] { 1, 2 }) && !main.Store.NeedsStartupRecoverySave &&
                    second.Store.NeedsStartupRecoverySave && second.State.Papers[0].Content == "original",
                "workspace entry did not route historical recovery through the shared store");
        }
        Console.WriteLine("PASS historical recovery: latest valid ZIP entry, confirmation, isolation, retry and source/termination fences");
    }

    private static int RunHistoryActionsFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker))) return 2;
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var seed = NewState();
                seed.UiLanguage = "en-US";
                seed.Workspaces = new() { Enabled = true, Slots = [1, 2], Active = 1 };
                var main = new StateStore();
                main.SaveJsonSync(main.SerializeState(seed), 1);
                var target = new StateStore(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared, 2);
                var archive = WriteHistoryFixture(AppContext.BaseDirectory, DateTime.UtcNow,
                    ("data2.json", target.SerializeState(NewState())));
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                controller.StartCoreHistoryBackupPolicy();
                var runningBackup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(AppController).GetField("_coreHistoryBackupTask", Private)!.SetValue(controller, runningBackup.Task);
                File.WriteAllText(target.FilePath, "{broken target");
                var responded = false;
                Require(await WithWorkspaceFailureDialog(async () =>
                {
                    var switching = controller.SwitchWorkspaceAsync(2);
                    Require(!responded, "recovery selected a source before the running backup finished");
                    // Model the publication/pruning at the end of the in-flight round.
                    File.Delete(archive);
                    archive = WriteHistoryFixture(AppContext.BaseDirectory, DateTime.UtcNow.AddSeconds(-1),
                        ("data2.json", target.SerializeState(NewState())));
                    runningBackup.SetResult();
                    return await switching;
                }, dialog =>
                {
                    responded = true;
                    ClickFailureAction(dialog, "HistoryRecoveryRestore");
                }, "HistoryRecoveryTitle"), "recovery did not wait for the in-flight history round");
                Require(await controller.SwitchWorkspaceAsync(1), "return from in-flight recovery failed");

                // A due history round would evict the selected recent source.
                foreach (var age in new[] { TimeSpan.FromDays(30), TimeSpan.FromDays(3), TimeSpan.FromHours(6) })
                    WriteHistoryFixture(AppContext.BaseDirectory, DateTime.UtcNow - age,
                        ("data2.json", target.SerializeState(NewState())));
                File.Delete(archive);
                archive = WriteHistoryFixture(AppContext.BaseDirectory, DateTime.UtcNow.AddMinutes(-20),
                    ("data2.json", target.SerializeState(NewState())));
                var archiveBytes = File.ReadAllBytes(archive);
                foreach (var choice in new[] { WorkspaceRecoveryChoice.Restore, WorkspaceRecoveryChoice.CreateEmpty })
                {
                    File.WriteAllText(target.FilePath, "{broken target");
                    File.Delete(target.BackupPath);
                    Require(await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                    {
                        typeof(AppController).GetMethod("OnCoreHistoryTimerTick", Private)!.Invoke(controller, [null, EventArgs.Empty]);
                        Field<Task>(controller, "_coreHistoryBackupTask").GetAwaiter().GetResult();
                        Require(File.ReadAllBytes(archive).SequenceEqual(archiveBytes), "history maintenance changed the offered source");
                        ClickFailureAction(dialog, choice == WorkspaceRecoveryChoice.Restore
                            ? "HistoryRecoveryRestore" : "HistoryRecoveryCreate");
                    }, "HistoryRecoveryTitle"),
                        "history action failed to enter its target workspace");
                    Require(controller.WorkspaceNumber == 2 && (choice == WorkspaceRecoveryChoice.Restore
                            ? controller.State.Papers[0].Content == "original"
                            : controller.State.Papers.Count == 1 && controller.State.Papers[0].Content != "original"),
                        "history action restored content when blank was selected or lost restored content");
                    Require(File.ReadAllBytes(archive).SequenceEqual(archiveBytes), "history action changed its source ZIP");
                    Require(await controller.SwitchWorkspaceAsync(1), "return from historical workspace failed");
                }

                // A blocked fixed backup offers user actions, never an automatic fallback.
                foreach (var choice in new[] { WorkspaceRecoveryChoice.Cancel, WorkspaceRecoveryChoice.Retry,
                             WorkspaceRecoveryChoice.CreateEmpty })
                {
                    File.WriteAllText(target.FilePath, "{broken target");
                    File.WriteAllText(target.BackupPath, target.SerializeState(NewState()));
                    using var held = new FileStream(target.BackupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var entered = await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                    {
                        var buttons = FailureDialogNodes(dialog).OfType<System.Windows.Controls.Button>();
                        Require(Equals(buttons.Single(button => button.IsDefault).Content, Strings.Get("WorkspaceFailureRetry")),
                            "backup read failure did not default to retry");
                        if (choice == WorkspaceRecoveryChoice.Cancel) dialog.Close();
                        else
                        {
                            if (choice == WorkspaceRecoveryChoice.Retry) held.Dispose();
                            ClickFailureAction(dialog, choice == WorkspaceRecoveryChoice.Retry ? "WorkspaceFailureRetry" : "HistoryRecoveryCreate");
                        }
                    }, "HistoryRecoveryTitle");
                    Require(entered == (choice != WorkspaceRecoveryChoice.Cancel) &&
                            (entered ? controller.WorkspaceNumber == 2 : File.ReadAllText(target.FilePath) == "{broken target"),
                        "backup failure ignored the selected action");
                    if (entered)
                    {
                        Require(choice == WorkspaceRecoveryChoice.Retry
                                ? controller.State.Papers[0].Content == "original"
                                : controller.State.Papers.Count == 1 && controller.State.Papers[0].Content != "original",
                            "retry/rebuild used the wrong contents");
                        held.Dispose();
                        Require(await controller.SwitchWorkspaceAsync(1), "return from backup failure action failed");
                    }
                }
                var historyDirectory = Path.Combine(AppContext.BaseDirectory, "PaperTodo-backups");
                var previousArchives = Directory.GetFiles(historyDirectory, "*.zip").ToHashSet();
                // Simulate the due time passing while paused, then observe a real ZIP.
                typeof(AppController).GetField("_coreHistoryBackupDueAt", Private)!
                    .SetValue(controller, Environment.TickCount64 - 1);
                Require(await controller.SwitchWorkspaceAsync(2), "overdue history switch failed");
                var expectedSnapshot = File.ReadAllBytes(target.FilePath);
                await Until(() => Directory.GetFiles(historyDirectory, "*.zip").Any(path => !previousArchives.Contains(path)),
                    "overdue history check did not publish a new backup after resuming");
                await Field<Task>(controller, "_coreHistoryBackupTask");
                var published = new CoreHistoryBackup(AppContext.BaseDirectory).ReadWorkspaceSnapshots(2)
                    .Single(snapshot => !previousArchives.Contains(snapshot.ArchivePath));
                Require(published.Bytes.SequenceEqual(expectedSnapshot),
                    "resumed history check did not preserve the active workspace snapshot");
                Require(await controller.SwitchWorkspaceAsync(1), "return after overdue history check failed");
                foreach (var zip in Directory.GetFiles(historyDirectory, "*.zip"))
                    File.Delete(zip);
                File.WriteAllText(target.FilePath, "{broken target");
                File.Delete(target.BackupPath);
                Require(!await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                {
                    var button = FailureDialogNodes(dialog).OfType<System.Windows.Controls.Button>().Single(candidate =>
                        Equals(candidate.Content, Strings.Format("WorkspaceRecoveryFallback", 1)));
                    button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }, "HistoryRecoveryTitle") && controller.WorkspaceNumber == 1 && controller.IsRunning &&
                        File.ReadAllText(target.FilePath) == "{broken target",
                    "explicit fallback did not retain the live workspace and corrupt target");
                File.WriteAllText(target.FilePath, "{broken target");
                File.Delete(target.BackupPath);
                Require(!await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                {
                    controller.State.Papers[0].Content = "edit saved on unavailable exit";
                    controller.MarkDirty();
                    ClickFailureAction(dialog, "WorkspaceFailureExit");
                }, "HistoryRecoveryTitle") && !controller.IsRunning,
                    "unavailable Exit did not leave through normal application exit");
                Require(new StateStore().Load().Papers[0].Content == "edit saved on unavailable exit" &&
                        File.ReadAllText(target.FilePath) == "{broken target",
                    "unavailable exit lost active edits or replaced the unavailable target");
                Console.WriteLine("PASS live history choices: restore, restart current number, retain ZIP and normal exit");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { if (controller?.IsRunning == true) controller.Dispose(); app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static int RunHistoryDialogFixture()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        try
        {
            // These three dialogs expose destructive choices. Keep their actual action
            // mapping and safe defaults, without a permanent translation/layout matrix.
            var create = (WorkspaceRecoveryChoice.CreateEmpty, Strings.Get("HistoryRecoveryCreate"));
            var exit = (WorkspaceRecoveryChoice.Exit, Strings.Get("WorkspaceFailureExit"));
            var dialogs = new (Func<WorkspaceRecoveryChoice> Show,
                (WorkspaceRecoveryChoice Choice, string Label)[] Actions)[]
            {
                (() => PaperNoticeDialog.ShowHistoryRecovery(null, 2, "2026-01-01 12:00"),
                    [create, (WorkspaceRecoveryChoice.Restore, Strings.Get("HistoryRecoveryRestore")), exit]),
                (() => PaperNoticeDialog.ShowUnavailableWorkspace(null, 2, null), [create, exit]),
                (() => PaperNoticeDialog.ShowUnavailableWorkspace(null, 2, 3),
                    [create, (WorkspaceRecoveryChoice.Fallback, Strings.Format("WorkspaceRecoveryFallback", 3)), exit])
            };
            foreach (var scenario in dialogs)
            foreach (var choice in scenario.Actions.Select(action => action.Choice).Prepend(WorkspaceRecoveryChoice.Cancel))
            {
                Exception? failure = null;
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    var dialog = app.Windows.OfType<System.Windows.Window>().Single();
                    try
                    {
                        Require(dialog.Owner == null && dialog.ShowInTaskbar, "startup recovery dialog is not reachable");
                        var nodes = FailureDialogNodes(dialog).ToArray();
                        var buttons = nodes.OfType<System.Windows.Controls.Button>().ToArray();
                        Require(Equals(buttons.Single(button => button.IsDefault).Content, scenario.Actions[1].Label),
                            "recovery dialog default must restore, fall back or exit, never rebuild");
                        if (choice == WorkspaceRecoveryChoice.Restore)
                            Require(nodes.OfType<System.Windows.Controls.TextBlock>().Any(block => block.Text.Contains("2026-01-01")),
                                "recovery dialog omitted backup time");
                        if (choice == WorkspaceRecoveryChoice.Cancel) dialog.Close();
                        else buttons.Single(button => Equals(button.Content,
                            scenario.Actions.Single(action => action.Choice == choice).Label)).RaiseEvent(
                                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                }));
                var result = scenario.Show();
                if (failure != null) throw failure;
                Require(result == choice, "recovery dialog did not honor the selected action");
            }

            T Choose<T>(Func<T> show, Action<System.Windows.Window> respond)
            {
                Exception? failure = null;
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    var dialog = app.Windows.OfType<System.Windows.Window>().Single(window => window.IsVisible);
                    try { respond(dialog); }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                }));
                var result = show();
                if (failure != null) throw failure;
                return result;
            }

            void Escape(System.Windows.Window dialog) => dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(dialog),
                Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });

            // An unshown owner is common for a restored edge capsule. Only an explicit
            // destructive action may confirm deletion; Escape and Cancel must remain safe.
            var hiddenOwner = new System.Windows.Window { Topmost = true };
            foreach (var action in new[] { "escape", "CommonCancel", "MenuDelete" })
            {
                var confirmed = Choose(() => PaperNoticeDialog.ShowDeleteConfirmation(hiddenOwner), dialog =>
                {
                    Require(dialog.Owner == null && !dialog.ShowInTaskbar && dialog.Topmost,
                        "delete confirmation cannot attach to an unshown paper owner");
                    var buttons = FailureDialogNodes(dialog).OfType<System.Windows.Controls.Button>().ToArray();
                    Require(Equals(buttons.Single(button => button.IsDefault).Content, Strings.Get("CommonCancel")),
                        "delete confirmation must default to Cancel");
                    if (action == "escape") Escape(dialog);
                    else buttons.Single(button => Equals(button.Content, Strings.Get(action))).RaiseEvent(
                        new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                });
                Require(confirmed == (action == "MenuDelete"), "delete confirmation changed dismissal semantics");
            }
            hiddenOwner.Close();

            foreach (var action in new string?[] { null, "CommonOk", "SaveFailureIgnore" })
            {
                bool? selected = null;
                Choose(() =>
                {
                    PaperNoticeDialog.ShowSaveFailure("fixture", ignore => selected = ignore);
                    return selected;
                }, dialog =>
                {
                    Require(dialog.Owner == null && dialog.ShowInTaskbar && dialog.Topmost,
                        "save failure notice must remain independently reachable");
                    if (action == null)
                    {
                        Escape(dialog);
                        Require(dialog.IsVisible, "Escape must not acknowledge a save failure");
                        dialog.Close();
                    }
                    else FailureDialogNodes(dialog).OfType<System.Windows.Controls.Button>()
                        .Single(button => Equals(button.Content, Strings.Get(action))).RaiseEvent(
                            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                });
                bool? expected = action == null ? null : action == "SaveFailureIgnore";
                Require(selected == expected,
                    "save failure dismissal must not invoke either selection callback");
            }
            Console.WriteLine("PASS shared notices: recovery choices, delete cancellation and save acknowledgement");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }
}
