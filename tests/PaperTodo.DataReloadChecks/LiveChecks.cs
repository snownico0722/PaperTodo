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

    private static void RunIsolatedLiveChecks(string fixtureArgument = "--live-fixture")
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
            start.ArgumentList.Add(fixtureArgument);
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
            await MergePolicyLiveChecks(controller);
            var stateIdentity = controller.State;
            var paperIdentity = controller.State.Papers.Single(p => p.Id == "note-a");
            var noteA = windows["note-a"]; var noteB = windows["note-b"];
            var editorA = Field<MarkdownTextBox>(noteA, "_noteBox");
            var editorB = Field<MarkdownTextBox>(noteB, "_noteBox");
            var original = ReadLive();
            editorB.Text = "local draft";
            editorB.CaretOffset = 3;
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
            Require(result.Applied && !noteA.IsClosed, "Native API did not return before surface teardown");
            await Until(() => !controller.IsDataReloading, "Native reload completion");
            Require(ReferenceEquals(stateIdentity, controller.State) && ReferenceEquals(paperIdentity, controller.State.Papers.Single(p => p.Id == "note-a")), "state/paper identity changed");
            Require(ReferenceEquals(editorA, Field<MarkdownTextBox>(windows["note-a"], "_noteBox")) && editorA.PersistentText == "external body", "changed note did not refresh in place");
            Require(ReferenceEquals(editorB, Field<MarkdownTextBox>(windows["note-b"], "_noteBox")) && editorB.PersistentText == "second", "explicit import did not discard divergent unsaved editor text");
            Require(windows.ContainsKey("added") && Math.Abs(noteA.Left - 180) < 2, "new paper/geometry did not apply");
            Require(controller.State.Papers.Single(p => p.Id == "todo").Items[0].Text == "external todo", "todo did not apply");
            Require(observed.Any(e => e.Metadata.Origin == PaperTodoEventOrigin.Plugin && e.Metadata.SourcePluginId == "fixture.native"), "reload event origin was lost");
            Console.WriteLine("PASS explicit Native import, in-place model/editor update, unsaved draft replacement, geometry and events");

            controller.SaveNow(sync: true);
            var externalFile = ReadLive();
            LivePaper(externalFile, "note-a")["title"] = "watch";
            WriteLive(externalFile);
            await Task.Delay(400);
            Require(controller.State.Papers.Single(p => p.Id == "note-a").Title != "watch",
                "data.json was imported without an explicit request");
            Require(Str(LivePaper(ReadLive(), "note-a")["title"]) == "watch",
                "idle host unexpectedly overwrote the external edit");
            Require(native.ReloadData().Applied, "explicit Native import rejected");
            await Until(() => !controller.IsDataReloading, "explicit import");
            Require(controller.State.Papers.Single(p => p.Id == "note-a").Title == "watch" &&
                ReferenceEquals(noteB, windows["note-b"]), "explicit import did not update title in place");
            Console.WriteLine("PASS no implicit file watching; explicit request updates existing windows");

            controller.SaveNow(sync: true);
            var web = ReadLive(); LivePaper(web, "note-a")["title"] = "web title"; WriteLive(web);
            var webResult = (DataReloadResult)WebPluginWorkspaceRequests.Execute(native, "data.reload", JsonSerializer.SerializeToElement(new { }))!;
            Require(webResult.Applied, "Web bridge failed");
            await Until(() => !controller.IsDataReloading, "Web reload");
            var status = (DataReloadStatus)WebPluginWorkspaceRequests.Execute(native, "data.status", JsonSerializer.SerializeToElement(new { }))!;
            Require(status.LastResult?.Applied == true && !status.IsReloading, "Web status disagreed");

            controller.SaveNow(sync: true);
            var remote = ReadLive(); remote["theme"] = "dark"; WriteLive(remote);
            var mcp = new McpCommandService(controller, controller.PaperCommands);
            var mcpResult = (DataReloadResult)mcp.Execute(JsonSerializer.SerializeToElement(new { method = "reload_data" }))!;
            Require(mcpResult.Applied, "MCP reload failed");
            await Until(() => !controller.IsDataReloading, "MCP reload");
            Require(controller.State.Theme == "dark" && ReferenceEquals(editorB, Field<MarkdownTextBox>(windows["note-b"], "_noteBox")), "settings refresh destroyed note editor");
            Require(mcp.Execute(JsonSerializer.SerializeToElement(new { method = "get_data_reload_status" })) is DataReloadStatus, "MCP status missing");
            Console.WriteLine("PASS shared Web/MCP routes and live theme refresh");

            // #349 selective integration: the extra explicit merge route is distinct from
            // authoritative reload_data and does not require a FileSystemWatcher.
            controller.SaveNow(sync: true);
            var mergedDisk = ReadLive();
            var mergedDiskBaseline = mergedDisk.ToJsonString();
            LivePaper(mergedDisk, "note-b")["content"] = "external independent change";
            WriteLive(mergedDisk);
            var localPaper = controller.State.Papers.Single(p => p.Id == "note-b");
            localPaper.Title = "LOCAL";
            var mergePipeName = "PaperTodo-merge-check-" + Guid.NewGuid().ToString("N");
            using var mergePipe = new McpApiHost(Dispatcher.CurrentDispatcher, mcp.Execute, mergePipeName);
            mergePipe.Start();
            var mergeTools = new McpTools(new McpPipeClient(mergePipeName));
            var mergeStart = (await mergeTools.MergeData(mergedDiskBaseline)).Deserialize<DataReloadResult>()!;
            Require(mergeStart.Outcome == "pending" && !mergeStart.Applied,
                "merge_data must return pending until the atomic commit is complete");
            await Until(() => !controller.IsDataReloading, "MCP merge apply");
            Require(localPaper.Title == "LOCAL" && localPaper.Content == "external independent change",
                "merge_data lost independent changes");
            var mergedStatus = controller.PaperCommands.GetDataReloadStatus();
            Require(mergedStatus.LastResult?.Applied == true &&
                    mergedStatus.LastResult.Outcome == "applied", "merge status did not reach applied");
            Console.WriteLine("PASS explicit MCP three-way merge retains independent local and disk edits");

            controller.SaveNow(sync: true);
            editorB.Text = "local conflicting body";
            var conflicting = ReadLive();
            var conflictingBaseline = conflicting.ToJsonString();
            LivePaper(conflicting, "note-b")["content"] = "external conflicting body";
            WriteLive(conflicting);
            var driver = AppController.ConflictDialogDriver;
            var dialogueCalls = 0;
            AppController.ConflictDialogDriver = (_, bucket) =>
            {
                dialogueCalls++;
                return bucket is PaperBucket
                    ? PaperConflictChoice.KeepExternal
                    : ConflictChoice.KeepExternal;
            };
            try
            {
                var started = (DataReloadResult)mcp.Execute(
                    JsonSerializer.SerializeToElement(new { method = "merge_data", @params = new { baseline_json = conflictingBaseline } }))!;
                Require(started.Outcome == "pending" && started.ConflictCount >= 1,
                    "competing field edits were not flagged");
                await Until(() => !controller.IsDataReloading, "MCP conflict apply");
                var finished = controller.PaperCommands.GetDataReloadStatus().LastResult;
                Require(finished?.Applied == true && finished.Outcome == "conflict",
                    "conflict choice not committed");
                Require(localPaper.Content == "external conflicting body",
                    "explicit KeepExternal did not replace conflicting value");
                Require(dialogueCalls == 1, "single paper should prompt only once");
                Require(!string.IsNullOrWhiteSpace(finished.ConflictFile) &&
                    File.Exists(Path.Combine(AppContext.BaseDirectory, finished.ConflictFile)),
                    "conflicting external data was not archived");
            }
            finally { AppController.ConflictDialogDriver = driver; }
            Console.WriteLine("PASS explicit conflict dialog choice, paper grouping and recovery archive");

            foreach (var choice in new[] { ConflictChoice.KeepMemory, ConflictChoice.KeepExternal })
            {
                controller.SaveNow(sync: true);
                var noBaseline = ReadLive();
                localPaper.Title = "LOCAL";
                controller.SaveNow(sync: true);
                LivePaper(noBaseline, "note-b")["title"] = "REMOTE";
                WriteLive(noBaseline);
                var previousDriver = AppController.ConflictDialogDriver;
                var calls = 0;
                AppController.ConflictDialogDriver = (_, bucket) =>
                {
                    Require(bucket is UnknownBaselineBucket, "missing baseline guessed field-level changes");
                    calls++;
                    return choice;
                };
                try
                {
                    var started = choice == ConflictChoice.KeepMemory
                        ? native.MergeData()
                        : (DataReloadResult)mcp.Execute(JsonSerializer.SerializeToElement(new { method = "merge_data" }))!;
                    Require(started.Outcome == "pending" && started.ConflictCount == 1,
                        "baseline-free merge did not request a whole-state choice");
                    await Until(() => !controller.IsDataReloading, "whole-state choice");
                    var expectedTitle = choice == ConflictChoice.KeepMemory
                        ? "LOCAL" : "REMOTE";
                    Require(calls == 1 && localPaper.Title == expectedTitle &&
                        Str(LivePaper(ReadLive(), "note-b")["title"]) == expectedTitle,
                        "whole-state choice did not reach memory and disk");
                }
                finally { AppController.ConflictDialogDriver = previousDriver; }
            }
            Require(native.MergeData("[]").Outcome == "invalid" &&
                ((DataReloadResult)WebPluginWorkspaceRequests.Execute(native, "data.merge",
                    JsonSerializer.SerializeToElement(new { baselineJson = "[]" }))!).Outcome == "invalid" &&
                ((DataReloadResult)mcp.Execute(JsonSerializer.SerializeToElement(new {
                    method = "merge_data", @params = new { baseline_json = "[]" } }))!).Outcome == "invalid",
                "an adapter dropped the invalid supplied baseline");
            Require(Field<StateStore>(controller, "_store").OrdinaryWritesPaused,
                "invalid baseline failed to retain the write hold");
            Require(native.ReloadData().Applied, "explicit recovery after invalid baseline failed");
            await Until(() => !controller.IsDataReloading, "accept disk after invalid baseline");
            Console.WriteLine("PASS missing-baseline choices, invalid-baseline hold and explicit recovery");

            // KeepExternal must also resolve the absence of a whole paper, in either direction.
            foreach (var externalDeletes in new[] { true, false })
            {
                var conflictPaper = controller.CreatePaper(PaperTypes.Note, show: true)!;
                var conflictId = conflictPaper.Id;
                var conflictWindow = windows[conflictId];
                conflictWindow.EnsureShellBuilt();
                controller.SaveNow(sync: true);
                var conflictDisk = ReadLive();
                var conflictDiskBaseline = conflictDisk.ToJsonString();
                if (externalDeletes)
                {
                    Field<MarkdownTextBox>(conflictWindow, "_noteBox").Text = "local edited before deletion";
                    conflictDisk["papers"]!.AsArray().Remove(LivePaper(conflictDisk, conflictId));
                }
                else
                {
                    // Stage an unsaved deletion without DeletePaper's immediate persistence.
                    conflictWindow.CloseForReal();
                    windows.Remove(conflictId);
                    controller.State.Papers.Remove(conflictPaper);
                    LivePaper(conflictDisk, conflictId)["content"] = "external restored body";
                }
                WriteLive(conflictDisk);
                var oldDriver = AppController.ConflictDialogDriver;
                var calls = 0;
                AppController.ConflictDialogDriver = (_, bucket) =>
                {
                    Require(bucket is PaperBucket paper && paper.PaperId == conflictId,
                        "delete/edit conflict was not dispatched to its paper dialog");
                    calls++;
                    return PaperConflictChoice.KeepExternal;
                };
                try
                {
                    var started = controller.PaperCommands.MergeData(PaperOperationContext.Mcp(), conflictDiskBaseline);
                    Require(started.Outcome == "pending" && started.ConflictCount == 1,
                        "whole-paper delete/edit conflict was not detected");
                    await Until(() => !controller.IsDataReloading, "delete/edit conflict apply");
                    var finished = controller.PaperCommands.GetDataReloadStatus().LastResult;
                    Require(finished?.Applied == true && finished.Outcome == "conflict" && calls == 1,
                        "whole-paper conflict choice was not committed");
                    Require(controller.State.Papers.Any(p => p.Id == conflictId) == !externalDeletes &&
                        windows.ContainsKey(conflictId) == !externalDeletes &&
                        ReadLive()["papers"]!.AsArray().Any(p => Str(p!["id"]) == conflictId) == !externalDeletes,
                        "disk, model and window disagree after delete/edit choice");
                    Require(conflictWindow.IsClosed, "deleted or replaced paper window survived");
                    if (!externalDeletes)
                        Require(controller.State.Papers.Single(p => p.Id == conflictId).Content == "external restored body" &&
                            !ReferenceEquals(conflictWindow, windows[conflictId]),
                            "restored paper did not get its external contents and a new window");
                }
                finally { AppController.ConflictDialogDriver = oldDriver; }
            }
            Console.WriteLine("PASS delete/edit conflict choices synchronize disk, model and window lifetimes");

            // The nonconflicting rename/delete cases from #349 must work without revision
            // metadata, a watcher or a full-state overwrite.
            var newPaper = controller.CreatePaper(PaperTypes.Note, show: true);
            Require(newPaper != null, "cannot create rename fixture");
            var oldId = newPaper.Id;
            controller.SaveNow(sync: true);
            var renamed = ReadLive();
            var renamedBaseline = renamed.ToJsonString();
            LivePaper(renamed, oldId)["id"] = "merge-renamed-window";
            WriteLive(renamed);
            Require(native.MergeData(renamedBaseline).Outcome == "pending",
                "rename merge was not queued");
            await Until(() => !controller.IsDataReloading, "rename merge apply");
            Require(!windows.ContainsKey(oldId) && windows.ContainsKey("merge-renamed-window"),
                "old paper surface was not replaced after external ID rename");
            controller.SaveNow(sync: true);
            var removed = ReadLive();
            var removedBaseline = removed.ToJsonString();
            removed["papers"]!.AsArray().Remove(LivePaper(removed, "merge-renamed-window"));
            WriteLive(removed);
            Require(((DataReloadResult)WebPluginWorkspaceRequests.Execute(native, "data.merge",
                JsonSerializer.SerializeToElement(new { baselineJson = removedBaseline }))!).Outcome == "pending",
                "delete merge was not queued");
            await Until(() => !controller.IsDataReloading, "delete merge apply");
            Require(!windows.ContainsKey("merge-renamed-window"),
                "deleted paper surface survived merge");
            Console.WriteLine("PASS external paper ID rename and delete close old windows");


            using (var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "fixture.runtime", [], () => true))
            {
                controller.SaveNow(sync: true);
                var runtimeEdit = ReadLive();
                var runtimeBaseline = runtimeEdit.ToJsonString();
                LivePaper(runtimeEdit, "note-a")["title"] = "RUN"; WriteLive(runtimeEdit);
                Require((await Task.Run(() => runtime.MergeData(runtimeBaseline))).Outcome == "pending",
                    "Runtime merge API was not marshaled to UI");
                await Until(() => !controller.IsDataReloading, "Runtime merge");
                Require(controller.State.Papers.Single(p => p.Id == "note-a").Title == "RUN",
                    "Runtime merge baseline was not forwarded");
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
        }
    }
}
