using System.IO;
using System.Windows;
using PaperTodo;

internal static partial class Program
{
    private static void CheckUserExitIoFailure(AppController controller, MarkdownTextBox editor)
    {
        var store = Field<StateStore>(controller, "_store");
        var writerField = typeof(StateStore).GetField("_atomicWriter", Private)!;
        var originalWriter = writerField.GetValue(store);
        var ignored = Field<bool>(controller, "_ignoreSaveFailures");
        var disk = File.ReadAllBytes(store.FilePath);
        editor.Text = "local edit survives failed ordinary exit";
        writerField.SetValue(store, new DurableAtomicFileWriter((stage, _) =>
        {
            if (stage == DurableAtomicWriteStage.AfterFlush) throw new IOException("injected exit write failure");
        }));
        typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, true);
        try
        {
            controller.Exit();
            Require(controller.IsRunning && !store.WriteLifetime.IsStopped &&
                editor.PersistentText == "local edit survives failed ordinary exit" &&
                File.ReadAllBytes(store.FilePath).SequenceEqual(disk),
                "failed ordinary exit discarded the editor or replaced primary data");
        }
        finally
        {
            writerField.SetValue(store, originalWriter);
            typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, ignored);
        }
        controller.SaveNow(sync: true);
        Require(Str(LivePaper(ReadLive(), "note-a")["content"]) == editor.PersistentText,
            "normal saving did not recover after the I/O fault was removed");
        Console.WriteLine("PASS actual ordinary Exit: I/O failure preserves editor and primary; subsequent save recovers");
    }

    private static int RunPassiveDisposeFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker))) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        byte[]? saved = null;
        var disposed = false;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var seed = new StateStore(); seed.SaveJsonSync(seed.SerializeState(NewState()), 1);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                var window = Field<Dictionary<string, PaperWindow>>(controller, "_windows")["note-a"];
                window.EnsureShellBuilt();
                controller.SaveNow(sync: true);
                saved = File.ReadAllBytes(seed.FilePath);
                Field<MarkdownTextBox>(window, "_noteBox").Text = "unaccepted transient content";
                controller.MarkDirty();
                controller.Dispose();
                disposed = true;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        controller?.Dispose();
        try
        {
            Require(disposed && saved != null && controller?.IsRunning == false &&
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data.json")).SequenceEqual(saved),
                "passive Dispose performed a fallback final save");
            Console.WriteLine("PASS passive Dispose stops persistence without writing transient state");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
