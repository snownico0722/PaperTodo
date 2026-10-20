using System.IO;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static int RunShutdownMergeFixture(bool unknownBaseline)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker))) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        byte[]? beforeShutdown = null;
        var result = 1;
        var shown = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var seed = new StateStore(); seed.SaveJsonSync(seed.SerializeState(NewState()), 1);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                foreach (var window in Field<Dictionary<string, PaperWindow>>(controller, "_windows").Values)
                    window.EnsureShellBuilt();
                controller.SaveNow(sync: true);
                var external = ReadLive();
                var baseline = external.ToJsonString();
                var editor = Field<MarkdownTextBox>(Field<Dictionary<string, PaperWindow>>(controller, "_windows")["note-a"], "_noteBox");
                editor.Text = "local awaiting decision";
                LivePaper(external, "note-a")["content"] = "external must remain";
                WriteLive(external);
                beforeShutdown = File.ReadAllBytes(seed.FilePath);
                AppController.ConflictDialogDriver = (owner, bucket) =>
                {
                    shown++;
                    app.Dispatcher.BeginInvoke(new Action(controller.ExitForSystemShutdown), DispatcherPriority.ApplicationIdle);
                    // Exercise REAL modal ShowDialog + production shutdown Hide/Close, not
                    // a fake choice that skips WPF's nested dispatcher unwinding.
                    return bucket switch
                    {
                        PaperBucket paper => DataReloadPaperConflictDialog.Show(owner, paper),
                        UnknownBaselineBucket unknown => DataReloadConflictDialog.Show(owner, unknown.Conflict),
                        _ => throw new InvalidOperationException("unexpected fixture group")
                    };
                };
                Require(controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), unknownBaseline ? null : baseline).Outcome == "pending",
                    "shutdown merge did not start");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(); }
        });
        app.Run();
        AppController.ConflictDialogDriver = null;
        controller?.Dispose();
        try
        {
            Require(shown == 1 && beforeShutdown != null && controller?.IsRunning == false,
                "shutdown fixture did not close a live review");
            Require(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data.json")).SequenceEqual(beforeShutdown!),
                "modal unwind committed a default choice during shutdown");
            Require(!Directory.GetFiles(AppContext.BaseDirectory, "dataconflict-*").Any(),
                "shutdown initiated conflict archive writes");
            Require(!File.Exists(Path.Combine(AppContext.BaseDirectory, "data.json.tmp")),
                "shutdown started a core temp write");
            result = 0;
            Console.WriteLine($"PASS real modal shutdown: unknownBaseline={unknownBaseline}; no main/archive/temp write");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        return result;
    }
}
