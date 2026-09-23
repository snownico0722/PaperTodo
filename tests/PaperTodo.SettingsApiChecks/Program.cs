using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ModelContextProtocol;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static int _checks;
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        _checks++;
    }
    private static void Throws<T>(Action action, string? code = null) where T : Exception
    {
        try { action(); }
        catch (T ex)
        {
            if (code != null)
            {
                var actual = ex is PaperSettingsException a ? a.Code : ex is PaperTodoPluginException b ? b.Code :
                    ex is McpApiException c ? c.Code : ex is PaperCommandException d ? d.Code : "";
                Check(actual == code, $"Expected {code}, got {actual}.");
            }
            else _checks++;
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Field(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static AppController Controller()
    {
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        Field(controller, "<State>k__BackingField", new AppState());
        Field(controller, "_windows", new Dictionary<string, PaperWindow>());
        return controller;
    }
    private static PaperSettingDefinition Boolean(string id, Func<bool> read, Action<bool> write, Action publish) => new()
    {
        Metadata = new PaperSettingSnapshot { Id = id, Category = id.Split('.')[0], Title = id,
            Type = "boolean", Value = Json(false), Writable = true },
        Read = () => Json(read()),
        Validate = value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.Clone() :
            throw PaperSettingsService.Error("invalid_setting_value", "bool required"),
        Begin = value =>
        {
            var previous = read();
            return new PaperSettingChange(() => write(value.GetBoolean()), () => write(previous), publish);
        }
    };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--presentation-fixture") return RunPresentationFixture(args[1]);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            ServiceBehavior();
            CatalogBehavior();
            TodoMoveBehavior();
            AdapterBehavior();
            SharedUiSettingBehavior();
            SettingsEditorBehavior();
            PostCommitFailureDoesNotReplay();
            Pump(PipeAndUnlinkBehavior());
            WebBridges();
            IsolatedPresentationBehavior();
            Console.WriteLine($"Settings API: {_checks} behavior checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void PostCommitFailureDoesNotReplay()
    {
        var c = Controller();
        var calls = 0;
        c.RunMcpPostCommitUi(() => { calls++; throw new InvalidOperationException("UI refresh failed"); });
        DrainSettingsUi();
        Check(calls == 1, "A failed post-commit UI update must not replay its partial side effects.");
    }

    private static void ServiceBehavior()
    {
        var value = false;
        var success = true;
        var saves = 0;
        var publications = 0;
        var running = true;
        var definition = Boolean("todo.paper_links", () => value, v => value = v, () => publications++);
        var service = new PaperSettingsService([definition], () => running, () => { saves++; return success; }, a => a());
        Check(!service.Get("todo.paper_links").Value.GetBoolean(), "Get returns live state.");
        Check(service.List("todo").Count == 1 && service.List("missing").Count == 0, "Category filtering.");
        Throws<PaperSettingsException>(() => service.Get("EnableTodoPaperLinks"), "setting_not_found");
        Throws<PaperSettingsException>(() => service.Set("Papers", Json(true)), "setting_not_found");
        Throws<PaperSettingsException>(() => service.Set("todo.paper_links", Json("true")), "invalid_setting_value");
        Check(saves == 0, "Invalid inputs do not persist.");
        Check(!service.Set("todo.paper_links", Json(false)).Changed && saves == 0, "No-op is side-effect free.");
        var changed = service.Set("todo.paper_links", Json(true));
        Check(changed.Changed && !changed.PreviousValue.GetBoolean() && value && saves == 1 && publications == 1, "Set commits then publishes.");
        success = false;
        Throws<PaperSettingsException>(() => service.Set("todo.paper_links", Json(false)), "save_failed");
        Check(value && publications == 1, "Persistence failure rolls back and does not publish.");
        running = false;
        Throws<PaperSettingsException>(() => service.List(), "app_exiting");
        Throws<PaperSettingsException>(() => service.Set("todo.paper_links", Json(false)), "app_exiting");
        running = true;
        success = true;
        // Reentry is tested with the actual in-flight service rather than an independent instance.
        PaperSettingsService? reentrant = null;
        reentrant = new PaperSettingsService([definition], () => true,
            () => { Throws<PaperSettingsException>(() => reentrant!.Set("todo.paper_links", Json(false)), "settings_busy"); return true; }, a => a());
        reentrant.Set("todo.paper_links", Json(false));
        Check(!PaperSettingsService.WithAccess(definition.Snapshot(), update: false).Writable, "Read-only access is not writable.");
        Check(PaperSettingsService.WithAccess(definition.Snapshot(), update: true).Writable, "Update permission makes a writable setting writable.");
    }

    private static void CatalogBehavior()
    {
        var c = Controller();
        var catalog = (IEnumerable<PaperSettingDefinition>)typeof(AppController).GetMethod("CreatePublicSettingsCatalog",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null)!;
        var definitions = catalog.ToArray();
        Check(definitions.Length >= 120, "Catalog must cover all UI preference groups, not two selected features.");
        Check(definitions.Select(d => d.Metadata.Id).Distinct().Count() == definitions.Length, "Public IDs are unique.");
        foreach (var d in definitions)
        {
            Check(!string.IsNullOrEmpty(d.Metadata.Title), $"{d.Metadata.Id} has a title.");
            var current = d.Read();
            try { Check(JsonElement.DeepEquals(d.Validate(current), current), $"Current value matches schema: {d.Metadata.Id}"); }
            catch (Exception ex) { throw new InvalidOperationException($"Invalid catalog default: {d.Metadata.Id}={current}", ex); }
        }
        Check(definitions.All(d => d.Metadata.Id is not ("Papers" or "CapsuleCollapseAllActiveQueues" or "data.json")), "No internal state is a public setting.");
        var rendering = definitions.Single(d => d.Metadata.Id == "appearance.text_rendering");
        foreach (var token in new[] { TextRenderingProfiles.Standard, TextRenderingProfiles.Soft, TextRenderingProfiles.Sharp })
            Check(rendering.Validate(Json(token)).GetString() == token, "All existing rendering tokens remain valid.");
        var colorScheme = definitions.Single(d => d.Metadata.Id == "appearance.color_scheme");
        foreach (var token in ColorSchemes.All)
            Check(colorScheme.Validate(Json(token)).GetString() == token, "All visible color-scheme tokens remain valid.");
        var paperSkin = definitions.Single(d => d.Metadata.Id == "appearance.paper_skin");
        foreach (var token in PaperSkins.All)
            Check(paperSkin.Validate(Json(token)).GetString() == token, "All visible paper-skin tokens remain valid.");
        var extension = definitions.Single(d => d.Metadata.Id == "note.external_extension");
        Check(extension.Validate(Json("*.MD")).GetString() == ".md", "Filename extension uses UI normalization.");
        var bottomBar = definitions.Single(d => d.Metadata.Id == "todo.bottom_bar");
        Check(bottomBar.Read().GetBoolean(), "Todo bottom bar is enabled by default.");
        Check(extension.Validate(Json("笔记")).GetString() == ".笔记", "Valid Unicode extensions are not needlessly rejected.");
        var saves = 0;
        var success = true;
        var service = new PaperSettingsService(definitions, () => true, () => { saves++; return success; }, _ => { });
        Check(service.Get("general.language").RequiresRestart, "Language is restart-based; API must not force exit.");
        Check(service.Get("appearance.paper_skin").Options.Count == PaperSkins.All.Length,
            "Public settings exposes every paper skin.");
        Check(service.Get("appearance.match_auxiliary_material").Type == "boolean" &&
            service.Get("appearance.material_transparency").Type == "string" &&
            service.Get("appearance.material_transparency").Options.Count == 5 &&
            service.Get("appearance.native_material_always_active").Type == "boolean",
            "All visible material toggles share the public settings catalog.");
        Throws<PaperSettingsException>(() => service.Set("appearance.font_scale", Json(1.3)), "invalid_setting_value");
        Throws<PaperSettingsException>(() => service.Set("appearance.font_scale", Json(1.01)), "invalid_setting_value");
        Throws<PaperSettingsException>(() => service.Set("appearance.theme", Json("unknown")), "invalid_setting_value");
        Throws<PaperSettingsException>(() => service.Set("appearance.paper_skin", Json("unknown")), "invalid_setting_value");
        service.Set("appearance.color_scheme", Json(ColorSchemes.Neutral));
        Check(c.State.ColorScheme == ColorSchemes.Neutral, "Neutral palette remains selectable through the shared settings path.");
        Throws<PaperSettingsException>(() => service.Set("note.external_extension", Json("../../tmp")), "invalid_setting_value");
        Throws<PaperSettingsException>(() => service.Set("window.hide_from_taskbar", Json(false)), "setting_dependency");
        var savesBeforeTodoLink = saves;
        service.Set("todo.paper_links", Json(false));
        Check(!c.State.EnableTodoPaperLinks && saves == savesBeforeTodoLink + 1,
            "Catalog setter changes the exact backing feature and commits once.");
        service.Set("todo.bottom_bar", Json(false));
        Check(!c.State.ShowTodoBottomBar && saves == savesBeforeTodoLink + 2,
            "Todo bottom-bar setting changes the live preference.");
        success = false;
        Throws<PaperSettingsException>(() => service.Set("todo.paper_links", Json(true)), "save_failed");
        Check(!c.State.EnableTodoPaperLinks, "Real catalog rollback restores the preference.");
        c.State.PaperSkin = null;
        c.State.ColorScheme = ColorSchemes.Mica;
        c.State.MicaBackdropType = MicaBackdropTypes.ClearAcrylic;
        Throws<PaperSettingsException>(() => service.Set("appearance.paper_skin", Json(PaperSkins.Acrylic)), "save_failed");
        Check(c.State.PaperSkin == null && c.State.MicaBackdropType == MicaBackdropTypes.ClearAcrylic,
            "Failed skin save restores both the legacy null sentinel and prior native recipe.");
        success = true;
        service.Set("appearance.paper_skin", Json(PaperSkins.Mica));
        Check(c.State.PaperSkin == PaperSkins.Mica && c.State.MicaBackdropType == MicaBackdropTypes.Mica,
            "Successful system skin keeps the compatibility native recipe in the same transaction.");
        service.Set("appearance.match_auxiliary_material", Json(true));
        service.Set("appearance.material_transparency", Json(MaterialTransparencyLevels.High));
        service.Set("appearance.native_material_always_active", Json(true));
        Check(c.State.MatchAuxiliaryMaterialStrength &&
            c.State.MaterialTransparency == MaterialTransparencyLevels.High &&
            c.State.MicaAlwaysActive,
            "Material preferences mutate through the shared catalog.");
        Throws<PaperSettingsException>(
            () => service.Set("appearance.material_transparency", Json("extreme")),
            "invalid_setting_value");
        service.Set("window.hide_from_switcher", Json(false));
        service.Set("window.hide_from_taskbar", Json(false));
        success = false;
        Throws<PaperSettingsException>(() => service.Set("window.hide_from_switcher", Json(true)), "save_failed");
        Check(!c.State.HidePapersFromWindowSwitcher && !c.State.HidePapersFromTaskbar, "Coupled taskbar preference is rolled back.");
        c.State.CapsuleCollapseAllActiveQueues["saved"] = true;
        c.State.DeepCapsuleQueueStartTopMargins["saved"] = 25;
        Throws<PaperSettingsException>(() => service.Set("capsule.enabled", Json(false)), "save_failed");
        Check(c.State.UseCapsuleMode && c.State.UseDeepCapsuleMode && c.State.CapsuleCollapseAllActiveQueues["saved"] &&
            c.State.DeepCapsuleQueueStartTopMargins["saved"] == 25, "Capsule dependency and queue rollback.");
        success = true;
        service.Set("capsule.enabled", Json(false));
        Check(!c.State.UseCapsuleMode && c.State.DeepCapsuleQueueStartTopMargins["saved"] == 25,
            "Disabling capsule mode preserves remembered per-queue layout.");
        success = false;
        var paper = new PaperData { Type = PaperTypes.Todo, Title = "abcdef", Items = [new PaperItem { Text = "done", Done = true, Order = 0 }, new PaperItem { Text = "open", Order = 1 }] };
        c.State.Papers.Add(paper);
        Throws<PaperSettingsException>(() => service.Set("title.max_length", Json(2)), "save_failed");
        Check(paper.Title == "abcdef" && c.State.MaxTitleLength == 6, "Failed title limit changes do not truncate user data.");
        Throws<PaperSettingsException>(() => service.Set("todo.move_completed_to_bottom", Json(true)), "save_failed");
        Check(paper.Items[0].Done && paper.Items[0].Order == 0 && !c.State.AutoMoveCompletedTodosToBottom, "Failed reorder restores items and order.");
        success = true;
        service.Set("todo.move_completed_to_bottom", Json(true));
        Check(!paper.Items[0].Done && paper.Items[0].Order == 0 && paper.Items[1].Order == 1, "Completed group is reordered just like Settings UI.");
        service.Set("title.max_length", Json(2));
        Check(paper.Title == "ab", "Successful max length applies existing title semantics.");
    }

    private static void TodoMoveBehavior()
    {
        static PaperItem Item(string id, int order) => new() { Id = id, Text = id, Order = order };

        var items = new List<PaperItem>
        {
            Item("a", 0),
            Item("b", 1),
            Item("c", 2),
            Item("d", 3),
            Item("e", 4)
        };

        Check(
            TodoRules.TryCreateMovedOrder(
                items,
                new[] { "b", "d" },
                "e",
                insertAfter: true,
                out var movedAfter),
            "Non-contiguous selected todos can move as one group.");
        Check(
            movedAfter.Select(item => item.Id).SequenceEqual(new[] { "a", "c", "e", "b", "d" }),
            "Group drag preserves the selected todos' relative order.");
        Check(
            items.Select(item => item.Id).SequenceEqual(new[] { "a", "b", "c", "d", "e" }),
            "Planning a group move does not mutate the current paper before undo is captured.");

        Check(
            TodoRules.TryCreateMovedOrder(
                items,
                new[] { "b", "d" },
                "a",
                insertAfter: false,
                out var movedBefore),
            "A selected group can move before an existing todo.");
        Check(
            movedBefore.Select(item => item.Id).SequenceEqual(new[] { "b", "d", "a", "c", "e" }),
            "Moving before a target keeps group order stable.");

        Check(
            !TodoRules.TryCreateMovedOrder(
                items,
                new[] { "b", "d" },
                "d",
                insertAfter: true,
                out _),
            "A selected todo is never a valid drop target for its own group.");
    }

    private static void AdapterBehavior()
    {
        var c = Controller();
        c.State.McpEnabled = true;
        var defs = new[] {
            Boolean("todo.paper_links", () => c.State.EnableTodoPaperLinks, v => c.State.EnableTodoPaperLinks = v, () => { })
        };
        var service = new PaperSettingsService(defs, () => true, () => true, a => a());
        Field(c, "_publicSettings", service);
        var current = true;
        PaperBodyPluginHostApi Api(params string[] permissions) => new(c, new PaperCommandService(c), null, "sample.settings", permissions, () => current, () => current);
        var none = Api();
        Throws<PaperTodoPluginException>(() => ((IPaperSettingsApi)none).List(), "permission_denied");
        var reader = Api(PaperTodoPermissionNames.SettingsRead);
        Check(!((IPaperSettingsApi)reader).Get("todo.paper_links").Writable, "Read-only caller gets truthful writability.");
        Throws<PaperTodoPluginException>(() => ((IPaperSettingsApi)reader).Set("todo.paper_links", false), "permission_denied");
        var writer = Api(PaperTodoPermissionNames.SettingsRead, PaperTodoPermissionNames.SettingsUpdate);
        ((IPaperSettingsApi)writer).Set("todo.paper_links", false);
        Check(!c.State.EnableTodoPaperLinks, "Native API uses the shared service.");
        WebPluginWorkspaceRequests.Execute(writer, "appSettings.set", Json(new { id = "todo.paper_links", value = true }));
        Check(c.State.EnableTodoPaperLinks, "Web routing preserves JSON booleans and uses Native permission gate.");

        // Exercise the actual Web Body dispatcher. The JS bridge and shared workspace adapter can
        // both be correct while this middle switch forgets to forward an appSettings.* method.
        var bodyContext = (PaperBodyContext)RuntimeHelpers.GetUninitializedObject(typeof(PaperBodyContext));
        Field(bodyContext, "<Workspace>k__BackingField", writer);
        var bodySession = (WebPaperBodySession)RuntimeHelpers.GetUninitializedObject(typeof(WebPaperBodySession));
        Field(bodySession, "_context", bodyContext);
        var bodyDispatcher = typeof(WebPaperBodySession).GetMethod(
            "ExecuteHostRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bodyGet = (PaperSettingSnapshot)bodyDispatcher.Invoke(
            bodySession, ["appSettings.get", Json(new { id = "todo.paper_links" })])!;
        Check(bodyGet.Value.GetBoolean(),
            "Web Body dispatcher routes settingsApi.get through the shared settings adapter.");
        var bodyList = ((IEnumerable<PaperSettingSnapshot>)bodyDispatcher.Invoke(
            bodySession, ["appSettings.list", Json(new { category = "todo" })])!).ToArray();
        Check(bodyList.Length == 1 && bodyList[0].Id == "todo.paper_links",
            "Web Body dispatcher routes settingsApi.list through the shared settings adapter.");
        _ = bodyDispatcher.Invoke(
            bodySession, ["appSettings.set", Json(new { id = "todo.paper_links", value = false })]);
        Check(!c.State.EnableTodoPaperLinks,
            "Web Body dispatcher routes settingsApi.set through the shared settings adapter.");
        WebPluginWorkspaceRequests.Execute(writer, "appSettings.set",
            Json(new { id = "todo.paper_links", value = true }));
        Check(c.State.EnableTodoPaperLinks,
            "Direct Web workspace adapter still shares state after Body-dispatch coverage.");

        Throws<PaperTodoPluginException>(() => WebPluginWorkspaceRequests.Execute(reader, "appSettings.set", Json(new { id = "todo.paper_links", value = false })), "permission_denied");
        Throws<PaperTodoPluginException>(() => WebPluginWorkspaceRequests.Execute(writer, "appSettings.set", Json(new { id = "todo.paper_links" })), "invalid_params");
        Throws<PaperTodoPluginException>(() => ((IPaperSettingsApi)writer).Get("State.Papers"), "setting_not_found");
        var runtime = new PaperPluginRuntimeWorkspaceApi(c, "sample.settings", [PaperTodoPermissionNames.SettingsRead], () => current);
        Check(((IPaperSettingsApi)runtime).List().Count == 1, "Runtime optional facade exposes the same catalog.");
        current = false;
        Throws<PaperTodoPluginException>(() => ((IPaperSettingsApi)writer).Get("todo.paper_links"), "session_closed");
        Throws<PaperTodoPluginException>(() => ((IPaperSettingsApi)runtime).List(), "runtime_closed");
        var mcp = new McpCommandService(c, new PaperCommandService(c));
        object? Call(string method, object param) => mcp.Execute(Json(new { method, @params = param }));
        var metadata = (PaperSettingSnapshot)Call("get_setting", new { id = "todo.paper_links" })!;
        Check(!metadata.Writable, "MCP read-only does not advertise writes.");
        Throws<McpApiException>(() => Call("set_setting", new { id = "todo.paper_links", value = false }), "full_writes_disabled");
        c.State.McpAllowFullWrites = true;
        Call("set_setting", new { id = "todo.paper_links", value = false });
        Check(!c.State.EnableTodoPaperLinks, "MCP ordinary writes share actual state with Native/Web.");
        c.State.McpEnabled = false;
        Throws<McpApiException>(() => Call("get_setting", new { id = "todo.paper_links" }), "mcp_disabled");
    }

    private static async Task PipeAndUnlinkBehavior()
    {
        var name = "PaperTodo.SettingsChecks." + Guid.NewGuid().ToString("N");
        var requests = new List<JsonElement>();
        McpApiHost? host = null;
        host = new McpApiHost(Dispatcher.CurrentDispatcher, request =>
        {
            requests.Add(request.Clone());
            if (request.GetProperty("method").GetString() == "set_setting") host!.StopAfterResponse();
            return new { received = true };
        }, name);
        using (host)
        {
            host.Start();
            var tools = new McpTools(new McpPipeClient(name));
            await tools.UpdateTodo("paper", "todo", text: "short");
            Check(!requests[^1].GetProperty("params").TryGetProperty("linked_paper_id", out _), "Omitted link is omitted on the real pipe.");
            await tools.UpdateTodo("paper", "todo", linked_paper_id: "note");
            Check(requests[^1].GetProperty("params").GetProperty("linked_paper_id").GetString() == "note", "Binding survives actual tool serialization.");
            await tools.UpdateTodo("paper", "todo", clear_linked_paper: true);
            Check(requests[^1].GetProperty("params").GetProperty("linked_paper_id").ValueKind == JsonValueKind.Null, "Unlink survives as explicit null on the real pipe.");
            var count = requests.Count;
            Throws<McpException>(() => tools.UpdateTodo("paper", "todo", linked_paper_id: "note", clear_linked_paper: true));
            Check(requests.Count == count, "Conflicting link arguments cause no request or mutation.");
            await tools.ListSettings("todo");
            Check(requests[^1].GetProperty("params").GetProperty("category").GetString() == "todo", "MCP list category forwarded.");
            await tools.GetSetting("todo.paper_links");
            Check(requests[^1].GetProperty("params").GetProperty("id").GetString() == "todo.paper_links", "MCP stable ID forwarded.");
            var response = await tools.SetSetting("mcp.enabled", Json(false));
            Check(response.GetProperty("received").GetBoolean(), "Disabling the listener does not lose its committed response.");
            var listener = (Task)typeof(McpApiHost).GetField("_listenerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            await listener.WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.IsStopping, "Graceful stop has completed and no longer accepts new calls.");
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void WebBridges()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperTodo.SettingsJs." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var body = typeof(WebPaperBodySession).GetMethod("BuildBridgeScript", BindingFlags.NonPublic | BindingFlags.Static)!;
            var runtime = typeof(WebPluginRuntime).GetMethod("BuildBridgeScript", BindingFlags.NonPublic | BindingFlags.Static)!;
            var miniType = typeof(WebPaperBodySession).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.GetMethod("BuildMiniBridgeScript", BindingFlags.NonPublic | BindingFlags.Static) != null);
            var mini = miniType.GetMethod("BuildMiniBridgeScript", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var (name, script) in new[] {
                ("body", (string)body.Invoke(null, ["https://settings.test", true])!),
                ("runtime", (string)runtime.Invoke(null, ["https://settings.test"])!),
                ("mini", (string)mini.Invoke(null, ["https://settings.test", true])!) })
            {
                var path = Path.Combine(root, name + ".js");
                File.WriteAllText(path, script);
                var start = new ProcessStartInfo("node") { UseShellExecute = false };
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bridge-check.cjs"));
                start.ArgumentList.Add(path); start.ArgumentList.Add(name);
                using var process = Process.Start(start)!;
                Check(process.WaitForExit(15000) && process.ExitCode == 0, "Actual " + name + " bridge sends settings API requests and returns results.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
