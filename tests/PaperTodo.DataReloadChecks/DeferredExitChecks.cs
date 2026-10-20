using System.IO;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static int RunDeferredExitFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing to use a non-fixture data directory.");
        var result = 1;
        var reviewed = false;
        var idleProcessed = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        app.Exit += (_, _) =>
        {
            try
            {
                Require(reviewed && idleProcessed && controller != null,
                    "exit did not wait for the merge or starved lower-priority dispatcher work");
                Require(controller.PaperCommands.GetDataReloadStatus().LastResult?.Applied == true &&
                    Str(LivePaper(ReadLive(), "note-a")["content"]) == "external before exit",
                    "exit ran before accepting and saving the selected merge");
                result = 0;
                Console.WriteLine("PASS exit intent is coalesced, yields to idle review work and resumes after committed merge");
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var store = new StateStore();
                store.SaveJsonSync(store.SerializeState(NewState()), 1);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                foreach (var window in Field<Dictionary<string, PaperWindow>>(controller, "_windows").Values)
                    window.EnsureShellBuilt();
                controller.SaveNow(sync: true);
                var external = ReadLive();
                var baseline = external.ToJsonString();
                var editor = Field<MarkdownTextBox>(Field<Dictionary<string, PaperWindow>>(controller, "_windows")["note-a"], "_noteBox");
                editor.Text = "local before exit";
                LivePaper(external, "note-a")["content"] = "external before exit";
                WriteLive(external);
                AppController.ConflictDialogDriver = (_, _) =>
                {
                    controller.Exit();
                    controller.Exit();
                    var frame = new DispatcherFrame();
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        idleProcessed = true;
                        frame.Continue = false;
                    }), DispatcherPriority.ApplicationIdle);
                    // Like a modal review, run a nested dispatcher. A continuously reposted
                    // Background Exit would starve the idle callback; no timing threshold is used.
                    Dispatcher.PushFrame(frame);
                    reviewed = true;
                    return PaperConflictChoice.KeepExternal;
                };
                Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), baseline).Outcome == "pending",
                    "deferred-exit merge did not start");
                // The merge owner must trigger Exit; this fixture must not call Shutdown on success.
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(); }
        });
        app.Run();
        AppController.ConflictDialogDriver = null;
        controller?.Dispose();
        return result;
    }
}
