import base64, hashlib, json, lzma, os, pathlib, subprocess
data=lzma.decompress(base64.b64decode("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",validate=True))
assert hashlib.sha256(data).hexdigest()=="17855dd2e8cc001de92ec25e1a099137c7fe52f0c2fe5a89a89bb0a76f13f81e"
patch=pathlib.Path(os.environ['RUNNER_TEMP'])/'safety-followup.patch'
patch.write_text(json.loads(data)[os.environ['REVIEW_PR']],encoding='utf-8',newline='')
subprocess.run(['git','apply','--check',str(patch)],check=True)
subprocess.run(['git','apply',str(patch)],check=True)
from pathlib import Path

def edit(name, old, new):
    p=Path(name);s=p.read_text(encoding='utf-8-sig')
    assert s.count(old)==1,(name,s.count(old))
    p.write_text(s.replace(old,new),encoding='utf-8',newline='')

edit('tests/PaperTodo.DataReloadChecks/LiveChecks.cs',
'''            Console.WriteLine("PASS missing-baseline whole-state choices and invalid baseline propagation");''',
'''            Require(Field<StateStore>(controller, "_store").OrdinaryWritesPaused,
                "invalid baseline failed to retain the write hold");
            Require(native.ReloadData().Applied, "explicit recovery after invalid baseline failed");
            await Until(() => !controller.IsDataReloading, "accept disk after invalid baseline");
            Console.WriteLine("PASS missing-baseline choices, invalid-baseline hold and explicit recovery");''')

edit('tests/PaperTodo.DataReloadChecks/PolicyLiveChecks.cs',
'''        // Finish the normal save explicitly before the rest of the fixture; no sleep/retry.''',
'''        CheckUserExitIoFailure(controller, editor);
        // Finish the normal save explicitly before the rest of the fixture; no sleep/retry.''')

Path('tests/PaperTodo.DataReloadChecks/TerminationOwnerChecks.cs').write_text('''using System.IO;
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
''',encoding='utf-8',newline='')
edit('tests/PaperTodo.DataReloadChecks/Program.cs',
'''        if (args.Contains("--exit-fixture")) return RunDeferredExitFixture();''',
'''        if (args.Contains("--exit-fixture")) return RunDeferredExitFixture();
        if (args.Contains("--passive-dispose")) return RunPassiveDisposeFixture();''')
edit('tests/PaperTodo.DataReloadChecks/Program.cs',
'''            RunIsolatedLiveChecks("--exit-fixture");''',
'''            RunIsolatedLiveChecks("--exit-fixture");
            RunIsolatedLiveChecks("--passive-dispose");''')
