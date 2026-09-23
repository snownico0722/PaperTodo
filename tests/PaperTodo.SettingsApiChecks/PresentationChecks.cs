using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private const string PresentationMarker = ".papertodo-presentation-fixture";

    private static void IsolatedPresentationBehavior()
    {
        foreach (var mode in new[] { "normal", "edge" })
        {
            var root = Path.Combine(Path.GetTempPath(), "PaperTodo.PresentationChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // A real controller saves beside its executable. Copy binaries only, never user
                // data, installed plugins or preferences. The child refuses an unmarked directory.
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
                    if (relative.Split(Path.DirectorySeparatorChar)[0] is "plugins" or "data") continue;
                    if (Path.GetExtension(file) is not (".exe" or ".dll" or ".pdb") &&
                        !file.EndsWith(".deps.json") && !file.EndsWith(".runtimeconfig.json")) continue;
                    var target = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                File.WriteAllText(Path.Combine(root, PresentationMarker), "isolated");
                var start = new ProcessStartInfo(Path.Combine(root, "PaperTodo.SettingsApiChecks.exe"))
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("--presentation-fixture");
                start.ArgumentList.Add(mode);
                using var child = Process.Start(start)!;
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(90_000))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                    throw new TimeoutException("Presentation fixture timed out: " + mode);
                }
                Console.Write(stdout.GetAwaiter().GetResult());
                Console.Error.Write(stderr.GetAwaiter().GetResult());
                Check(child.ExitCode == 0, "Real presentation fixture failed: " + mode);
            }
            finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
        }
    }

    private static int RunPresentationFixture(string mode)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, PresentationMarker)))
            throw new InvalidOperationException("Refusing to open non-fixture PaperTodo data.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await PresentationBehavior(mode == "edge"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }

    private static async Task PresentationSettle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task PresentationBehavior(bool edge)
    {
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = true,
            UseDeepCapsuleMode = edge, UsePersistentPowerShellProcess = false, McpEnabled = false,
            HidePapersFromTaskbar = false, HidePapersFromWindowSwitcher = false
        };
        state.Papers.Add(new PaperData { Id = "owner", Type = PaperTypes.Note, Content = "keep owner",
            IsVisible = true, IsCollapsed = false, X = 80, Y = 80, Width = 360, Height = 260 });
        state.Papers.Add(new PaperData { Id = "note", Type = PaperTypes.Note, Content = "keep note",
            IsVisible = false, IsCollapsed = false, X = 500, Y = 80, Width = 360, Height = 260 });
        state.Papers.Add(new PaperData { Id = "todo", Type = PaperTypes.Todo, IsVisible = false, IsCollapsed = false,
            X = 500, Y = 400, Width = 360, Height = 260, Items = [new PaperItem { Text = "keep task", Order = 0 }] });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var c = new AppController();
        await c.StartAsync(createDefaultPaper: false);
        await PresentationSettle();
        var commands = new PaperCommandService(c);
        var papers = c.State.Papers;
        var note = papers.Single(p => p.Id == "note");
        var todo = papers.Single(p => p.Id == "todo");
        var owner = papers.Single(p => p.Id == "owner");
        var current = true;
        PaperBodyPluginHostApi Api(params string[] permissions) =>
            new(c, commands, "owner", "tests.presentation", permissions, () => current, () => current);
        using var host = Api(PaperTodoPermissionNames.PapersObserve);
        var api = (IPaperWorkspacePresentationApi)host;
        using var runtime = new PaperPluginRuntimeWorkspaceApi(c, "tests.presentation", [], () => current);
        var runtimeApi = (IPaperWorkspacePresentationApi)runtime;
        var events = new List<PaperTodoEvent>();
        using var subscription = host.Subscribe(new PaperTodoEventFilter
        {
            Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.PaperChanged }, ExcludeOwnOperations = false
        }, events.Add);

        var shown = api.ShowPaper("note", activate: false);
        Check(shown.PaperId == "note" && shown.IsVisible && !shown.IsCollapsed, "Native can show another note without unfolding implicitly.");
        await PresentationSettle();
        Check(events.OfType<PaperChangedEvent>().Any(e => e.After.Id == "note" &&
            e.Metadata.Origin == PaperTodoEventOrigin.Plugin && e.Metadata.SourcePluginId == "tests.presentation"),
            "Presentation events preserve plugin attribution.");
        api.CollapsePaper("note");
        Check(note.IsVisible && note.IsCollapsed, "Collapse retains visible state.");
        api.HidePaper("note");
        Check(!note.IsVisible && note.IsCollapsed && note.Content == "keep note", "Hide preserves fold and content.");
        var folded = api.ShowPaper("note", activate: false);
        Check(folded.IsVisible && folded.IsCollapsed, "Show does not mean Expand.");
        api.HidePaper("note");
        api.ExpandPaper("note", activate: false);
        Check(note.IsVisible && !note.IsCollapsed, "Expand also reveals a hidden paper.");
        api.TogglePaperVisibility("note", activate: false);
        Check(!note.IsVisible, "Visibility toggle hides.");
        api.TogglePaperVisibility("note", activate: false);
        Check(note.IsVisible, "Visibility toggle restores.");
        api.TogglePaperCollapsed("note", activate: false);
        Check(note.IsCollapsed, "Fold toggle collapses.");
        api.TogglePaperCollapsed("note", activate: false);
        Check(!note.IsCollapsed && note.IsVisible, "Fold toggle expands.");
        api.HidePaper("todo");
        api.CollapsePaper("todo");
        Check(!todo.IsVisible && todo.IsCollapsed, "Collapsing a hidden todo does not reveal it.");
        var activated = api.ActivatePaper("todo");
        Check(activated.IsVisible && activated.IsCollapsed, "Activate reveals without forcing expansion.");
        api.ExpandPaper("todo", activate: false);
        Check(todo.IsVisible && !todo.IsCollapsed && todo.Items.Single().Text == "keep task", "Cross-paper controls support Todo without modifying its items.");
        Throws<PaperTodoPluginException>(() => api.ShowPaper("missing"), "paper_not_found");
        Throws<PaperTodoPluginException>(() => api.ShowPaper(" "), "invalid_params");
        Check(papers.Count == 3, "Missing IDs never create papers.");
        c.State.UseCapsuleMode = false;
        Throws<PaperTodoPluginException>(() => api.CollapsePaper("note"), "presentation_unavailable");
        Check(!note.IsCollapsed && !c.State.UseCapsuleMode, "Unavailable presentation neither mutates the paper nor enables a feature.");
        c.State.UseCapsuleMode = true;

        // The session-scoped 2.1 API still works without the new permission, and still targets owner.
        using (var own = new PaperBodyPluginHostApi(c, commands, "owner", owner.BodyProviderId, [], () => true, () => true))
        {
            own.Hide();
            await PresentationSettle();
            Check(!owner.IsVisible && note.IsVisible, "Own-paper control remains scoped and permission-free.");
            own.Show(activate: false);
            await PresentationSettle();
            Check(owner.IsVisible, "Legacy own-paper Show still works.");
        }
        await Task.Run(() => runtimeApi.HidePaper("note"));
        Check(!note.IsVisible, "Background Runtime marshals requests onto the actual UI dispatcher.");
        await Task.Run(() => runtimeApi.ShowPaper("note", activate: false));
        Check(note.IsVisible, "Runtime can reopen a paper without a body callback.");

        object? Web(string method, object parameters) => WebPluginWorkspaceRequests.Execute(host, method, Json(parameters));
        Web("papers.hide", new { paperId = "note" });
        Web("papers.show", new { paperId = "note", activate = false });
        Web("papers.collapse", new { paperId = "note" });
        Web("papers.expand", new { paperId = "note", activate = false });
        Web("papers.toggle", new { paperId = "note", activate = false });
        Web("papers.toggleCollapsed", new { paperId = "note", activate = false });
        Web("papers.activate", new { paperId = "note" });
        Check(note.IsVisible && note.IsCollapsed, "All seven Web Workspace methods route through the real host.");
        Throws<PaperTodoPluginException>(() => Web("papers.show", new { paperId = "note", activate = "false" }), "invalid_params");
        Throws<PaperTodoPluginException>(() => Web("papers.hide", new { }), "invalid_params");

        // Test the actual Body dispatcher separately: it has its own switch in addition to the
        // common Workspace router used by Mini and Runtime. No live WebView is claimed here.
        var bodyContext = (PaperBodyContext)RuntimeHelpers.GetUninitializedObject(typeof(PaperBodyContext));
        Field(bodyContext, "<Workspace>k__BackingField", host);
        var body = (WebPaperBodySession)RuntimeHelpers.GetUninitializedObject(typeof(WebPaperBodySession));
        Field(body, "_context", bodyContext);
        var bodyMethod = typeof(WebPaperBodySession).GetMethod("ExecuteHostRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var method in new[] { "papers.show", "papers.hide", "papers.toggle", "papers.expand", "papers.collapse", "papers.toggleCollapsed", "papers.activate" })
        {
            var value = bodyMethod.Invoke(body, [method, Json(new { paperId = "note", activate = false })]);
            Check(value is PaperPresentationResult, "Body dispatcher: " + method);
        }

        c.State.McpEnabled = true;
        var mcp = new McpCommandService(c, commands);
        object? Mcp(string method, object parameters) => mcp.Execute(Json(new { method, @params = parameters }));
        var mcpMethods = new[] { "show_paper", "hide_paper", "toggle_paper_visibility", "expand_paper", "collapse_paper", "toggle_paper_collapsed", "activate_paper" };
        c.State.McpAllowFullWrites = false;
        foreach (var method in mcpMethods)
            Check(Mcp(method, new { paper_id = "note", activate = false }) is PaperPresentationResult, "MCP presentation does not require full writes: " + method);
        Mcp("expand_paper", new { paper_id = "note", activate = false });
        Mcp("hide_paper", new { paper_id = "note" });
        await PresentationSettle();
        Check(events.OfType<PaperChangedEvent>().Any(e => e.After.Id == "note" && e.Metadata.Origin == PaperTodoEventOrigin.Mcp), "MCP event attribution survives shared UI dispatch.");
        var details = Json(Mcp("get_paper", new { paper_id = "note" }));
        Check(!details.GetProperty("is_visible").GetBoolean() && !details.GetProperty("is_collapsed").GetBoolean(), "MCP details expose both logical states.");
        var listed = Json(Mcp("list_papers", new { })).GetProperty("papers");
        Check(listed.EnumerateArray().All(p => p.TryGetProperty("is_collapsed", out _)), "MCP list includes collapsed state.");
        Throws<McpApiException>(() => Mcp("show_paper", new { paper_id = "missing" }), "paper_not_found");
        Throws<McpApiException>(() => Mcp("show_paper", new { paper_id = "note", activate = "yes" }), "invalid_params");
        c.State.McpEnabled = false;
        Throws<McpApiException>(() => Mcp("show_paper", new { paper_id = "note" }), "mcp_disabled");
        c.State.McpEnabled = true;

        api.ExpandPaper("note", activate: false);
        c.State.EnableAnimations = true;
        Mcp("hide_paper", new { paper_id = "note" });
        Mcp("show_paper", new { paper_id = "note", activate = false });
        await Task.Delay(700);
        await PresentationSettle();
        var windows = ReadField<Dictionary<string, PaperWindow>>(c, "_windows");
        Check(note.IsVisible && windows["note"].IsVisible && !windows["note"].IsClosed,
            "A stale hide animation cannot withdraw a later MCP show request.");
        Check(note.Content == "keep note" && todo.Items.Single().Text == "keep task" && papers.Count == 3,
            "Presentation operations leave all content and paper identities intact.");
        current = false;
        Throws<PaperTodoPluginException>(() => api.HidePaper("note"), "session_closed");
        Throws<PaperTodoPluginException>(() => runtimeApi.HidePaper("note"), "runtime_closed");
        current = true;
        await LinkedTitleTruncationBehavior(c, note, todo);
        await PluginBoundaryBehavior(c, owner, note, todo);
        await PresentationPipeBehavior();
        Console.WriteLine($"Presentation ({(edge ? "edge" : "normal")}): {_checks} behavior checks passed.");
    }

    private static async Task LinkedTitleTruncationBehavior(AppController c, PaperData note, PaperData todo)
    {
        c.State.EnableAnimations = false;
        c.State.ShowLinkedPaperName = true;
        c.State.AllowLongLinkedPaperTitles = true;
        c.State.EnableTodoPaperLinks = true;
        c.State.MaxTitleLength = 30;
        note.Title = "LinkedABC";
        todo.Items[0].LinkPaper(note.Id);
        c.ApplyPaperPresentation(todo, PaperPresentationAction.Expand, activate: false);
        var window = ReadField<Dictionary<string, PaperWindow>>(c, "_windows")[todo.Id];
        window.RefreshTodoRowsForExternalChange();
        await PresentationSettle();
        static IEnumerable<System.Windows.Controls.TextBlock> Labels(DependencyObject root)
        {
            if (root is System.Windows.Controls.TextBlock label) yield return label;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Labels(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                    yield return child;
        }
        c.PublicSettings.Set("title.max_length", Json(4));
        await PresentationSettle();
        var visibleLabels = Labels(window).Select(label => label.Text).ToArray();
        Check(note.Title == "Link" && visibleLabels.Any(text => text.Contains("Link", StringComparison.Ordinal)) &&
            visibleLabels.All(text => !text.Contains("LinkedABC", StringComparison.Ordinal)),
            "Title truncation updates the actual linked-paper label immediately.");
        // Verify the UI entry point uses the same post-commit title notification.
        Invoke(c, "SetMaxTitleLength", 2);
        Check(note.Title == "Li" && Labels(window).Any(label => label.Text.Contains("Li", StringComparison.Ordinal)) &&
            Labels(window).All(label => !label.Text.Contains("LinkedABC", StringComparison.Ordinal)),
            "UI title truncation refreshes the visible linked label.");
    }

    private static async Task PresentationPipeBehavior()
    {
        var requests = new List<JsonElement>();
        var name = "PaperTodo.PresentationChecks." + Guid.NewGuid().ToString("N");
        using var pipe = new McpApiHost(Dispatcher.CurrentDispatcher, request =>
        {
            requests.Add(request.Clone());
            return new PaperPresentationResult("note", true, false);
        }, name);
        pipe.Start();
        var tools = new McpTools(new McpPipeClient(name));
        await tools.ShowPaper("note", activate: false);
        await tools.HidePaper("note");
        await tools.TogglePaperVisibility("note", activate: false);
        await tools.ExpandPaper("note", activate: false);
        await tools.CollapsePaper("note");
        await tools.TogglePaperCollapsed("note", activate: false);
        await tools.ActivatePaper("note");
        var expected = new[] { "show_paper", "hide_paper", "toggle_paper_visibility", "expand_paper", "collapse_paper", "toggle_paper_collapsed", "activate_paper" };
        Check(requests.Count == expected.Length, "All seven actual MCP tool methods reached the named pipe.");
        for (var i = 0; i < expected.Length; i++)
        {
            Check(requests[i].GetProperty("method").GetString() == expected[i], "Tool method survives serialization: " + expected[i]);
            var p = requests[i].GetProperty("params");
            Check(p.GetProperty("paper_id").GetString() == "note", "Exact target ID reaches the GUI bridge.");
            if (i is 0 or 2 or 3 or 5)
                Check(!p.GetProperty("activate").GetBoolean(), "activate=false survives tool serialization.");
        }
    }

}
