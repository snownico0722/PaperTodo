using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static IEnumerable<DependencyObject> FailureDialogNodes(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FailureDialogNodes(child)) yield return descendant;
    }

    private static void ClickFailureAction(Window dialog, string key) =>
        FailureDialogNodes(dialog).OfType<Button>().Single(button => Equals(button.Content, Strings.Get(key)))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<bool> WithWorkspaceFailureDialog(
        Func<Task<bool>> operation, Action<Window> respond)
    {
        var response = RespondAsync();
        var result = await operation();
        await response;
        return result;

        async Task RespondAsync()
        {
            Window? dialog = null;
            await Until(() => (dialog = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(window => window.IsVisible && window.Title == Strings.Get("WorkspaceSwitchFailed"))) != null,
                "workspace failure dialog did not appear");
            try { respond(dialog!); }
            finally { if (dialog!.IsVisible) dialog.Close(); }
        }
    }

    private static void WorkspaceFailureMessageChecks()
    {
        var originalLanguage = UiLanguages.EffectiveUiCulture.Name;
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP", "ko-KR" })
            {
                UiLanguages.ApplyWorkspacePreference(language);
                var cases = new (Exception Error, string Key)[]
                {
                    (new UnauthorizedAccessException("raw"), "WorkspaceFailureAccessDenied"),
                    (new FileNotFoundException("raw"), "WorkspaceFailureMissing"),
                    (new InvalidDataException("raw"), "WorkspaceFailureInvalidData"),
                    (new IOException("raw", unchecked((int)0x80070020)), "WorkspaceFailureBusy"),
                    (new IOException("raw", unchecked((int)0x80070021)), "WorkspaceFailureBusy"),
                    (new IOException("raw", unchecked((int)0x80070070)), "WorkspaceFailureDiskFull"),
                    (new IOException("raw"), "WorkspaceFailureIo"),
                    (new InvalidOperationException("raw"), "WorkspaceFailureUnknown"),
                    (new StateRecoveryException("WorkspaceFailureNoRecovery"), "WorkspaceFailureNoRecovery"),
                    (new StateRecoveryException("WorkspaceFailureRecoveryChanged"), "WorkspaceFailureRecoveryChanged")
                };
                foreach (var (error, key) in cases)
                {
                    var message = WorkspaceFailureMessage.For(error);
                    Require(message == Strings.Get(key) && message != key && !message.Contains("raw"),
                        "workspace failure did not use a localized known reason: " + language + "/" + key);
                }
                Require(Strings.Get("WorkspaceFailureRetry") != "WorkspaceFailureRetry" &&
                        Strings.Get("WorkspaceFailureExit") != "WorkspaceFailureExit", "failure actions lack translations");
            }
            UiLanguages.ApplyWorkspacePreference("en-US");
            var beforeRollback = new StateRecoveryException("WorkspaceFailurePrimaryChanged");
            UiLanguages.ApplyWorkspacePreference("zh-CN");
            Require(WorkspaceFailureMessage.For(beforeRollback) == Strings.Get("WorkspaceFailurePrimaryChanged") &&
                    WorkspaceFailureMessage.For(beforeRollback) != beforeRollback.Message,
                "rollback kept the failed target's language in the retry dialog");
        }
        finally { UiLanguages.ApplyWorkspacePreference(originalLanguage); }
        Console.WriteLine("PASS workspace failure messages: typed recovery, I/O reasons and four languages");
    }

    private static int RunWorkspaceFailureFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker))) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
                var second = new StateStore(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared, 2);
                second.SaveJsonSync(second.SerializeState(NewState()), 1);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);

                // Release the real Windows lock while the prompt is open. Retry
                // must reread the target and actually complete the switch.
                using (var busy = new FileStream(second.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Require(await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                    {
                        Require(controller.WorkspaceNumber == 1 && controller.IsWorkspaceSwitching,
                            "retry prompt did not retain the old workspace and transition gate");
                        Require(FailureDialogNodes(dialog).OfType<TextBlock>().Any(block =>
                            block.Text == Strings.Get("WorkspaceFailureBusy")), "real file lock was not explained");
                        busy.Dispose();
                        var changed = NewState(); changed.Papers[0].Content = "freshly reread target";
                        File.WriteAllText(second.FilePath, second.SerializeState(changed));
                        ClickFailureAction(dialog, "WorkspaceFailureRetry");
                    }), "retry did not complete after unlocking target");
                }
                Require(controller.WorkspaceNumber == 2 && controller.State.Papers[0].Content == "freshly reread target",
                    "retry reused stale state instead of reopening the workspace");
                Require(await controller.SwitchWorkspaceAsync(1), "return before failed-save fixture failed");

                // A failed recovery save must produce ONE actionable dialog,
                // then retry from the current files after the old UI is restored.
                Require(second.TryRefreshBackupFromPrimary(), "recovery backup fixture failed");
                File.WriteAllText(second.FilePath, "{broken");
                var catalog = Field<WorkspaceCatalog>(controller, "_workspaceCatalog");
                var cachedSecond = catalog.StoreFor(2);
                var failSave = true;
                typeof(StateStore).GetField("_atomicWriter", Private)!.SetValue(cachedSecond,
                    new DurableAtomicFileWriter((stage, path) =>
                    {
                        if (failSave && path == second.FilePath && stage == DurableAtomicWriteStage.BeforeTempOpen)
                            throw new IOException("disk full", unchecked((int)0x80070070));
                    }));
                Require(await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                {
                    Require(controller.WorkspaceNumber == 1 && !Field<bool>(controller, "_hasShownSaveFailure") &&
                            !Application.Current.Windows.OfType<Window>().Any(window => window.Title == Strings.Get("SaveFailureTitle")),
                        "failed target save queued a second save-failure dialog");
                    Require(FailureDialogNodes(dialog).OfType<TextBlock>().Any(block =>
                        block.Text == Strings.Get("WorkspaceFailureDiskFull")), "disk-full cause was lost during rollback");
                    failSave = false;
                    ClickFailureAction(dialog, "WorkspaceFailureRetry");
                }), "failed recovery save could not be retried");
                Require(await controller.SwitchWorkspaceAsync(1), "return before dismissal fixture failed");

                File.WriteAllText(second.FilePath, "{broken");
                File.Delete(second.BackupPath);
                Require(!await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog => dialog.Close()) &&
                        controller.IsRunning && controller.WorkspaceNumber == 1 && !controller.IsWorkspaceSwitching,
                    "closing a failure dialog implicitly exited or switched the workspace");
                controller.State.Papers[0].Content = "edit retained on normal exit";
                controller.MarkDirty();
                Require(!await WithWorkspaceFailureDialog(() => controller.SwitchWorkspaceAsync(2), dialog =>
                    ClickFailureAction(dialog, "WorkspaceFailureExit")) && !controller.IsRunning,
                    "exit action did not use the normal exit path");
                Require(new StateStore().Load().Papers[0].Content == "edit retained on normal exit" &&
                        File.ReadAllText(second.FilePath) == "{broken",
                    "exit lost current edits or overwrote the failed target");
                Console.WriteLine("PASS workspace failure dialog: actual retry, one save prompt, dismissal and normal exit");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { if (controller?.IsRunning == true) controller.Dispose(); app.Shutdown(); }
        });
        app.Run();
        return result;
    }
}
