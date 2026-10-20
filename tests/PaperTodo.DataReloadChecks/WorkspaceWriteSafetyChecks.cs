using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void WorkspaceWriteSafetyChecks()
    {
        using var s = new Scope();
        var catalog = new WorkspaceCatalog(s.DirectoryPath, DurableAtomicFileWriter.Shared);
        var main = catalog.OpenInitial();
        var secondary = catalog.StoreFor(2);
        var state = secondary.Load();
        secondary.SaveJsonSync(secondary.SerializeState(state), 1);
        var mainBytes = File.ReadAllBytes(main.Store.FilePath);
        var secondaryBytes = File.ReadAllBytes(secondary.FilePath);
        main.Store.StopWritesForTermination();
        Throws<OperationCanceledException>(() => secondary.SaveJsonAsync(secondary.SerializeState(state), 2).GetAwaiter().GetResult());
        Throws<OperationCanceledException>(() => catalog.SaveOptions(3));
        var unopened = catalog.StoreFor(3);
        Throws<OperationCanceledException>(() => unopened.SaveJsonSync(unopened.SerializeState(NewState()), 4));
        Require(File.ReadAllBytes(main.Store.FilePath).SequenceEqual(mainBytes) &&
            File.ReadAllBytes(secondary.FilePath).SequenceEqual(secondaryBytes) && !File.Exists(unopened.FilePath),
            "shutdown fence did not cover inactive/new workspace stores and catalog");
        Console.WriteLine("PASS workspace: termination closes current, inactive, future and catalog writers");
    }

    private static int RunWorkspaceShutdownFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker))) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        byte[]? targetBytes = null;
        var interrupted = false;
        var directory = AppContext.BaseDirectory;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var main = NewState();
                main.Workspaces = new() { Enabled = true, Count = 2, Active = 1 };
                var seed = new StateStore(); seed.SaveJsonSync(seed.SerializeState(main), 1);
                var target = NewState(); target.Papers[0].Content = "saved secondary";
                var targetStore = new StateStore(directory, DurableAtomicFileWriter.Shared, 2);
                targetStore.SaveJsonSync(targetStore.SerializeState(target), 1);
                targetBytes = File.ReadAllBytes(targetStore.FilePath);
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                var catalog = Field<WorkspaceCatalog>(controller, "_workspaceCatalog");
                var candidateStore = catalog.StoreFor(2);
                typeof(StateStore).GetField("_atomicWriter", Private)!.SetValue(candidateStore,
                    new DurableAtomicFileWriter((stage, path) =>
                    {
                        if (stage != DurableAtomicWriteStage.BeforeTempOpen || path != targetStore.FilePath) return;
                        Require(controller.WorkspaceNumber == 2, "shutdown did not reach installed target");
                        interrupted = true;
                        controller.ExitForSystemShutdown();
                    }));
                _ = await controller.SwitchWorkspaceAsync(2);
                if (!interrupted) throw new InvalidOperationException("workspace shutdown boundary not exercised");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(); }
        });
        app.Run();
        controller?.Dispose();
        try
        {
            Require(interrupted && targetBytes != null && controller?.IsRunning == false, "workspace shutdown not completed");
            Require(File.ReadAllBytes(Path.Combine(directory, "data2.json")).SequenceEqual(targetBytes!),
                "target save continued after shutdown");
            Require(J(File.ReadAllText(Path.Combine(directory, "data.json")))["workspaces"]!["active"]!.GetValue<int>() == 1,
                "shutdown published unfinished workspace catalog");
            Require(!File.Exists(Path.Combine(directory, "data2.json.tmp")), "shutdown started target temp write");
            Console.WriteLine("PASS actual workspace shutdown after target install: target and active catalog retained");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
