using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static async Task PluginBoundaryBehavior(AppController c, PaperData owner, PaperData note, PaperData todo)
    {
        ReadsAndNotificationsDoNotCommitOrReplaceBodies(c, owner, note, todo);
        CreationOwnsInitialTodoFields(c, note);
        MultilinePluginTooltips();
        ReviewArchiveSavesWithoutBackup();
        await InitialRuntimeFailureDoesNotRetry(c);
    }

    private static void ReadsAndNotificationsDoNotCommitOrReplaceBodies(
        AppController c, PaperData owner, PaperData note, PaperData todo)
    {
        var window = ReadField<Dictionary<string, PaperWindow>>(c, "_windows")[owner.Id];
        var host = ReadField<PaperBodyHost>(window, "_paperBodyHost");
        var original = host.Current!;
        var view = ReadField<UIElement>(window, "_bodyElement");
        var probe = new BoundaryBodyProbe();
        Field(host, "<Current>k__BackingField", probe);
        try
        {
            var commands = c.PaperCommands;
            var mcp = new McpCommandService(c, commands);
            var oldEnabled = c.State.McpEnabled;
            c.State.McpEnabled = true;
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    _ = commands.ListPapers();
                    _ = commands.GetPaper(owner.Id);
                    _ = commands.ListTodos(includeBlank: true);
                    Check(commands.GetNote(owner.Id)!.Content == owner.Content,
                        "Note reads use the existing model, without synchronizing the editor.");
                    _ = mcp.Execute(Json(new { method = "list_papers", @params = new { } }));
                }
                Throws<PaperCommandException>(() => commands.ReadNoteImage(note.Id, "missing"), "asset_not_found");
                Check(probe.Commits == 0, "Paper/todo/note/image reads must never commit an unrelated live body.");

                commands.AppendTodos(new AppendTodosRequest
                {
                    PaperId = todo.Id,
                    Todos = [new TodoCreateItem { Text = "external target" }]
                }, PaperOperationContext.Plugin("tests.boundary"));
                Check(probe.Commits == 0,
                    "An external write to another paper must not Commit an unrelated body.");

                var noteWindow = ReadField<Dictionary<string, PaperWindow>>(c, "_windows")[note.Id];
                var markdown = ReadField<MarkdownPaperBodySession>(noteWindow, "_markdownBodySession");
                var box = markdown.NoteBox ?? throw new InvalidOperationException("Markdown editor is not available.");
                box.Text = "user target edit";
                Check(note.Content != box.PersistentText,
                    "Fixture must keep the target Markdown edit pending before the external write.");
                commands.WriteNote(new WriteNoteRequest
                {
                    PaperId = note.Id,
                    Mode = NoteWriteMode.Append,
                    Content = "plugin append"
                }, PaperOperationContext.Plugin("tests.boundary"));
                Check(note.Content == "user target edit" + Environment.NewLine + "plugin append",
                    "A write to the same Markdown paper commits the user's pending text before the plugin write.");
                Check(probe.Commits == 0,
                    "Target Markdown ordering must not invoke Commit on an unrelated body.");
            }
            finally { c.State.McpEnabled = oldEnabled; }

            window.NotifyCurrentPaperBodyActivated();
            window.NotifyCurrentPaperBodyDeactivated();
            window.NotifyCurrentPaperBodyThemeChanged();
            window.NotifyCurrentPaperBodyTypographyChanged();
            window.NotifyCurrentPaperBodyDpiChanged();
            window.CancelCurrentPaperBodyInteractions();
            window.RefreshCurrentPaperBodyFromModel();
            Check(probe.Notifications == 7 && probe.Disposals == 0 && probe.Commits == 0,
                "A failed ordinary notification must not dispose or commit the current body.");
            Check(ReferenceEquals(host.Current, probe) &&
                  ReferenceEquals(view, ReadField<UIElement>(window, "_bodyElement")),
                "Ordinary callback errors leave the existing body session and visual in place.");
        }
        finally { Field(host, "<Current>k__BackingField", original); }
    }

    private static void CreationOwnsInitialTodoFields(AppController c, PaperData linked)
    {
        c.State.EnableTodoPaperLinks = true;
        c.State.ExperimentalTodoReminders = true;
        c.State.AutoClearCompletedTodos = false;
        c.State.McpEnabled = true;
        c.State.McpAllowBlankWrites = true;
        c.State.McpAllowFullWrites = false;
        c.State.McpAllowDeletes = false;
        var reminder = DateTimeOffset.Now.AddHours(1);
        TodoCreateItem[] items =
        [
            new() { Text = "already done", Done = true },
            new() { Text = "remind and link", ReminderAt = reminder, LinkedPaperId = linked.Id }
        ];
        using var api = new PaperBodyPluginHostApi(c, c.PaperCommands, null, "tests.creation",
            [PaperTodoPermissionNames.PapersCreate, PaperTodoPermissionNames.TodosAppend], () => true, () => true);
        var created = api.CreatePaper(new CreatePaperRequest { Type = "todo", Show = false, Todos = items });
        var paper = c.State.Papers.Single(p => p.Id == created.PaperId);
        Check(paper.Items.Any(t => t.Done) && paper.Items.Any(t => t.ReminderAt == reminder && t.LinkedPaperId == linked.Id),
            "Native creation accepts initial completion, reminder and link under create/append permission.");
        var appended = api.AppendTodos(new AppendTodosRequest { PaperId = paper.Id, Todos = items });
        Check(appended.TodoIds.Count == 2, "Native append also owns the new records' initial fields.");
        Throws<PaperTodoPluginException>(() => api.UpdateTodo(new UpdateTodoRequest
            { PaperId = paper.Id, TodoId = appended.TodoIds[0], Text = "replace" }), "permission_denied");
        Throws<PaperTodoPluginException>(() => api.DeletePaper(paper.Id), "permission_denied");

        var mcp = new McpCommandService(c, c.PaperCommands);
        object? Call(string method, object parameters) => mcp.Execute(Json(new { method, @params = parameters }));
        var inputs = new object[]
        {
            new { text = "done at creation", done = true },
            new { text = "new reminder and link", reminder_at = reminder, linked_paper_id = linked.Id }
        };
        var result = Json(Call("create_todo_paper", new { show = false, todos = inputs }));
        var mcpPaper = c.State.Papers.Single(p => p.Id == result.GetProperty("id").GetString());
        Check(mcpPaper.Items.Any(t => t.Done) && mcpPaper.Items.Any(t => t.LinkedPaperId == linked.Id && t.ReminderAt == reminder),
            "MCP additive creation does not require full writes for initial fields.");
        Call("add_todos", new { paper_id = mcpPaper.Id, todos = inputs });
        Check(mcpPaper.Items.Count == 4, "MCP additive append accepts the same initial fields.");
        Throws<McpApiException>(() => Call("update_todo", new
            { paper_id = mcpPaper.Id, todo_id = mcpPaper.Items[0].Id, text = "replace existing" }), "full_writes_disabled");
        Throws<McpApiException>(() => Call("set_todo_reminder", new
            { paper_id = mcpPaper.Id, todo_id = mcpPaper.Items[1].Id, reminder_at = reminder.AddHours(1) }), "full_writes_disabled");
        c.State.EnableTodoPaperLinks = false;
        Throws<PaperTodoPluginException>(() => api.AppendTodos(new AppendTodosRequest
            { PaperId = paper.Id, Todos = [items[1]] }), "paper_links_disabled");
        Check(!c.State.EnableTodoPaperLinks, "Creation must not silently turn on the link feature.");
        c.State.EnableTodoPaperLinks = true;
    }

    private static void MultilinePluginTooltips()
    {
        const string text = "Completed: 3\r\nOpen: 5";
        var actions = PluginContributionPolicy.NormalizeTodoActions(
            [new() { Id = "summary", Text = "Summary", ToolTip = text,
                     Icon = PaperTopBarIcon.Character("i"), Placement = PaperTodoActionPlacement.Inline }]);
        Check(actions[0].ToolTip == text, "Todo action tooltips preserve line breaks.");
        var labels = PluginContributionPolicy.NormalizeTopBarLabels([new() { Text = "Summary", ToolTip = text }]);
        Check(labels[0].ToolTip == text, "Top-bar label tooltips preserve line breaks.");
        Throws<PaperTodoPluginException>(() => PluginContributionPolicy.NormalizeTopBarLabels(
            [new() { Text = "Summary", ToolTip = new string('x', 161) }]), "invalid_topbar_label_tooltip");
        Throws<PaperTodoPluginException>(() => PluginContributionPolicy.NormalizeTopBarLabels(
            [new() { Text = "Bad\nlabel" }]), "invalid_topbar_label_text");
    }

    private static void ReviewArchiveSavesWithoutBackup()
    {
        var type = typeof(PaperTodo.Plugin.ReviewArchive.ReviewArchivePlugin).Assembly
            .GetType("PaperTodo.Plugin.ReviewArchive.ReviewArchiveStore")!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var pathField = type.GetField("_path", flags)!;
        var docField = type.GetField("_document", flags)!;
        var oldPath = pathField.GetValue(null);
        var oldDoc = docField.GetValue(null);
        var root = Path.Combine(Path.GetTempPath(), "PaperTodo.ArchiveChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "review-archive.json");
            File.WriteAllText(path, "old data");
            // An existing directory at the old backup path makes a backup copy impossible.
            Directory.CreateDirectory(path + ".bak");
            pathField.SetValue(null, path);
            docField.SetValue(null, Activator.CreateInstance(docField.FieldType, nonPublic: true));
            type.GetMethod("Flush")!.Invoke(null, null);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Check(saved.RootElement.GetProperty("storageVersion").GetInt32() == 3 && Directory.Exists(path + ".bak"),
                "The archive saves normally without accessing the obsolete backup path.");
            Check((string)type.GetProperty("LastSaveError")!.GetValue(null)! == "", "Normal archive save succeeded.");
        }
        finally
        {
            pathField.SetValue(null, oldPath); docField.SetValue(null, oldDoc);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InitialRuntimeFailureDoesNotRetry(AppController c)
    {
        const string id = "tests.initial-failure";
        var registry = c.PaperBodyPlugins;
        var descriptors = ReadField<Dictionary<string, PaperBodyPluginDescriptor>>(registry, "_descriptors");
        var loaded = ReadField<IDictionary>(registry, "_loadedNativeByDirectory");
        var path = Path.Combine(AppContext.BaseDirectory, "plugins", id);
        var manifest = new PaperBodyPluginManifest { Id = id, Kind = "native", ApiVersion = "2.2",
            Capabilities = ["runtime"], DirectoryPath = path };
        var descriptor = new PaperBodyPluginDescriptor(id, "Test", "", new Version(1, 0), "2.2", 1,
            PaperBodyPluginKind.Native, PaperBodyCapabilities.None, PaperTodoPermissionNames.None,
            path, path, "fixture", typeof(BoundaryRuntimePlugin), manifest);
        var loadedType = typeof(PaperBodyPluginRegistry).GetNestedType("LoadedNativePlugin", BindingFlags.NonPublic)!;
        loaded[path] = Activator.CreateInstance(loadedType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [path, descriptor, null], null)!;
        descriptors[id] = descriptor;
        var paper = new PaperData { Id = "failure-owner", Type = PaperTypes.Note, BodyProviderId = id, IsVisible = false };
        c.State.Papers.Add(paper);
        try
        {
            BoundaryRuntimePlugin.Starts = 0; BoundaryRuntimePlugin.Fail = true;
            c.ReconcilePluginRuntimes();
            await Task.Delay(1300);
            var slots = ReadField<IDictionary>(c, "_pluginRuntimeSlots");
            var slot = slots[id]!;
            Check(BoundaryRuntimePlugin.Starts == 1 && slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Failed",
                "A first startup failure is reported once without automatic retry.");

            BoundaryRuntimePlugin.Fail = false;
            Invoke(c, "RetryFailedPluginRuntimeAfterSettingsChanged", id);
            Check(slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Running",
                "An explicit settings change can start a previously failed runtime.");
            var runtimeId = (Guid)slot.GetType().GetProperty("RuntimeId")!.GetValue(slot)!;
            BoundaryRuntimePlugin.Fail = true;
            var before = BoundaryRuntimePlugin.Starts;
            Invoke(c, "RequestPluginRuntimeRestart", runtimeId, id);
            await Task.Delay(1300);
            Check(BoundaryRuntimePlugin.Starts == before + 1 &&
                  slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Backoff",
                "Recovery requested after successful running still uses the existing bounded retries.");
        }
        finally
        {
            c.State.Papers.Remove(paper);
            c.ReconcilePluginRuntimes();
            descriptors.Remove(id); loaded.Remove(path);
        }
    }

    private sealed class BoundaryBodyProbe : IPaperBodySession
    {
        public FrameworkElement View { get; } = new TextBlock();
        public int Commits, Disposals, Notifications;
        public void Commit() => Commits++;
        public void Dispose() => Disposals++;
        private void Fail() { Notifications++; throw new InvalidOperationException("Test notification failure"); }
        public void OnActivated() => Fail();
        public void OnDeactivated() => Fail();
        public void OnThemeChanged(PaperBodyTheme theme) => Fail();
        public void OnTypographyChanged(PaperBodyTheme theme) => Fail();
        public void OnDpiChanged() => Fail();
        public void CancelInteractions() => Fail();
        public void RefreshFromModel() => Fail();
    }
}

public sealed class BoundaryRuntimePlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public static bool Fail;
    public static int Starts;
    public IPaperBodySession Create(PaperBodyContext context) => throw new NotSupportedException();
    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context)
    {
        Starts++;
        if (Fail) throw new InvalidOperationException("Test startup failure");
        return new Runtime();
    }
    private sealed class Runtime : IPaperPluginRuntime { public void Dispose() { } }
}
