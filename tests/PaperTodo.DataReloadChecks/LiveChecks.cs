using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string LiveMarker = ".papertodo-data-reload-fixture";

    private static void RunIsolatedLiveChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.DataReloadChecks.Live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
                if (relative.StartsWith("plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(file.EndsWith(".exe") || file.EndsWith(".dll") || file.EndsWith(".pdb") ||
                      file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))) continue;
                var target = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            File.WriteAllText(Path.Combine(directory, LiveMarker), "isolated fixture");
            var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.DataReloadChecks.exe"))
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add("--live-fixture");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000))
            {
                process.Kill(entireProcessTree: true); process.WaitForExit();
                Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult());
                throw new TimeoutException("Live data reload fixture timed out.");
            }
            Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult());
            Require(process.ExitCode == 0, "Live data reload fixture failed.");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static int RunLiveFixture()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LiveMarker)))
            throw new InvalidOperationException("Refusing to use a non-fixture data directory.");
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await LiveChecks(); }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static T Field<T>(object instance, string name)
    {
        var type = instance.GetType();
        if (type.GetField(name, Private) is { } field) return (T)field.GetValue(instance)!;
        if (type.GetProperty(name, Private) is { } property) return (T)property.GetValue(instance)!;
        throw new MissingMemberException(type.FullName, name);
    }
    private static async Task Until(Func<bool> condition, string label)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(label);
            await Task.Delay(20);
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
    private static JsonObject ReadLive() => J(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.json")));
    private static void WriteLive(JsonObject value) =>
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "data.json"), value.ToJsonString());
    private static JsonNode LivePaper(JsonObject document, string id) =>
        document["papers"]!.AsArray().Single(p => Str(p!["id"]) == id)!;

    private static async Task LiveChecks()
    {
        var seed = NewState();
        seed.Papers.Add(new() { Id = "note-b", Type = PaperTypes.Note, Content = "second", X = 450, Y = 120 });
        seed.McpEnabled = true; seed.McpAllowFullWrites = true; seed.McpAllowDeletes = true;
        var seedStore = new StateStore(); seedStore.SaveJsonSync(seedStore.SerializeState(seed), 1);
        var controller = new AppController();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            var windows = Field<Dictionary<string, PaperWindow>>(controller, "_windows");
            foreach (var window in windows.Values) window.EnsureShellBuilt();
            await Dispatcher.Yield(DispatcherPriority.Background);
            controller.SaveNow(sync: true);
            var stateIdentity = controller.State;
            var paperIdentity = controller.State.Papers.Single(p => p.Id == "note-a");
            var noteA = windows["note-a"]; var noteB = windows["note-b"];
            var editorA = Field<MarkdownTextBox>(noteA, "_noteBox");
            var editorB = Field<MarkdownTextBox>(noteB, "_noteBox");
            var original = ReadLive();
            editorB.Text = "local draft";
            editorB.CaretOffset = 3;
            var canUndo = editorB.Document.UndoStack.CanUndo;
            using var native = new PaperBodyPluginHostApi(controller, controller.PaperCommands, "note-a", "fixture.native", [], () => !noteA.IsClosed, () => true);
            using var observer = new PaperBodyPluginHostApi(controller, controller.PaperCommands, null, "fixture.observer", PaperTodoPermissionNames.All, () => true, () => true);
            var observed = new List<PaperTodoEvent>();
            using var subscription = observer.Subscribe(new() { ExcludeOwnOperations = false }, observed.Add);
            LivePaper(original, "note-a")["content"] = "external body";
            LivePaper(original, "note-a")["title"] = "external title";
            LivePaper(original, "note-a")["x"] = 180;
            LivePaper(original, "todo")["items"]![0]!["text"] = "external todo";
            original["papers"]!.AsArray().Add(new JsonObject { ["id"] = "added", ["type"] = "note", ["content"] = "new paper", ["x"] = 250, ["y"] = 350 });
            WriteLive(original);
            var result = native.ReloadData();
            Require(result.Applied && result.ConflictCount == 0 && !noteA.IsClosed, "Native API did not commit/return before surface teardown");
            await Until(() => !controller.IsDataReloading, "Native reload completion");
            Require(ReferenceEquals(stateIdentity, controller.State) && ReferenceEquals(paperIdentity, controller.State.Papers.Single(p => p.Id == "note-a")), "state/paper identity changed");
            Require(ReferenceEquals(editorA, Field<MarkdownTextBox>(windows["note-a"], "_noteBox")) && editorA.PersistentText == "external body", "changed note did not refresh in place");
            Require(ReferenceEquals(editorB, Field<MarkdownTextBox>(windows["note-b"], "_noteBox")) && editorB.PersistentText == "local draft" && editorB.CaretOffset == 3 && editorB.Document.UndoStack.CanUndo == canUndo, "unrelated note/editor/undo state changed");
            Require(windows.ContainsKey("added") && Math.Abs(noteA.Left - 180) < 2, "new paper/geometry did not apply");
            Require(controller.State.Papers.Single(p => p.Id == "todo").Items[0].Text == "external todo", "todo did not apply");
            Require(observed.Any(e => e.Metadata.Origin == PaperTodoEventOrigin.Plugin && e.Metadata.SourcePluginId == "fixture.native"), "reload event origin was lost");
            Console.WriteLine("PASS live native merge, model/editor identity, local pending edit, undo, geometry, add and events");

            controller.SaveNow(sync: true);
            var watched = ReadLive(); LivePaper(watched, "note-a")["title"] = "watch";
            var activeWatcher = Field<FileSystemWatcher?>(controller, "_dataWatcher");
            Require(activeWatcher is { EnableRaisingEvents: true }, "watcher was not active");
            WriteLive(watched);
            await Until(() => controller.State.Papers.Single(p => p.Id == "note-a").Title == "watch" && !controller.IsDataReloading, "watcher reload");
            Require(ReferenceEquals(noteB, windows["note-b"]), "watcher rebuilt an unrelated window");
            Console.WriteLine("PASS live FileSystemWatcher and self-write suppression");

            controller.SaveNow(sync: true);
            var web = ReadLive(); LivePaper(web, "note-a")["title"] = "web title"; WriteLive(web);
            var webResult = (DataReloadResult)WebPluginWorkspaceRequests.Execute(native, "data.reload", JsonSerializer.SerializeToElement(new { }))!;
            Require(webResult.Applied, "Web bridge failed");
            await Until(() => !controller.IsDataReloading, "Web reload");
            var status = (DataReloadStatus)WebPluginWorkspaceRequests.Execute(native, "data.status", JsonSerializer.SerializeToElement(new { }))!;
            Require(status.LastResult?.Applied == true && !status.PendingExternalChange, "Web status disagreed");

            controller.SaveNow(sync: true);
            var remote = ReadLive(); remote["theme"] = "dark"; WriteLive(remote);
            var mcp = new McpCommandService(controller, controller.PaperCommands);
            var mcpResult = (DataReloadResult)mcp.Execute(JsonSerializer.SerializeToElement(new { method = "reload_data" }))!;
            Require(mcpResult.Applied, "MCP reload failed");
            await Until(() => !controller.IsDataReloading, "MCP reload");
            Require(controller.State.Theme == "dark" && ReferenceEquals(editorB, Field<MarkdownTextBox>(windows["note-b"], "_noteBox")), "settings refresh destroyed note editor");
            Require(mcp.Execute(JsonSerializer.SerializeToElement(new { method = "get_data_reload_status" })) is DataReloadStatus, "MCP status missing");
            Console.WriteLine("PASS shared Web/MCP routes and live theme refresh");

            using (var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "fixture.runtime", [], () => true))
            {
                controller.SaveNow(sync: true);
                var runtimeEdit = ReadLive(); LivePaper(runtimeEdit, "note-a")["title"] = "runtime title"; WriteLive(runtimeEdit);
                Require((await Task.Run(runtime.ReloadData)).Applied, "Runtime API was not marshaled to UI");
                await Until(() => !controller.IsDataReloading, "Runtime reload");
            }
            Console.WriteLine("PASS Native runtime thread dispatch without new plugin permissions");

            controller.SaveNow(sync: true);
            var deletion = ReadLive(); deletion["papers"]!.AsArray().Remove(LivePaper(deletion, "note-a")); WriteLive(deletion);
            Require(native.ReloadData().Applied && !noteA.IsClosed, "originating session closed before API returned");
            await Until(() => !controller.IsDataReloading, "self-delete completion");
            Require(noteA.IsClosed && !windows.ContainsKey("note-a"), "removed paper surface survived");
            Throws<PaperTodoPluginException>(() => native.ReloadData());
            Console.WriteLine("PASS source-paper deletion returns before teardown and invalidates its lease");

            controller.SaveNow(sync: true);
            var capsule = ReadLive(); capsule["useCapsuleMode"] = true; capsule["useDeepCapsuleMode"] = true;
            LivePaper(capsule, "note-b")["isCollapsed"] = true; WriteLive(capsule);
            Require(controller.PaperCommands.ReloadData(PaperOperationContext.Mcp()).Applied, "capsule update rejected");
            await Until(() => !controller.IsDataReloading, "capsule reload");
            Require(controller.State.Papers.Single(p => p.Id == "note-b").IsCollapsed && windows["note-b"].HasVisibleSurface, "collapsed paper lost its visible surface");
            controller.SaveNow(sync: true);
            var expanded = ReadLive(); LivePaper(expanded, "note-b")["isCollapsed"] = false; WriteLive(expanded);
            Require(controller.PaperCommands.ReloadData(PaperOperationContext.Mcp()).Applied, "expand rejected");
            await Until(() => !controller.IsDataReloading, "expand reload");
            Require(windows["note-b"].HasExpandedPaperSurface, "expanded paper not displayed");
            Console.WriteLine("PASS existing capsule/form presentation path");
        }
        finally
        {
            controller.Dispose();
            Require(Field<FileSystemWatcher?>(controller, "_dataWatcher") == null, "watcher remained after dispose");
        }
    }
}
