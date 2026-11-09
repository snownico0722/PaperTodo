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
        DeferredPluginEnablementDoesNotAttachEarly(c);
        StartingPluginRuntimeIsDisposedOnRemoval(c);
        ReadsAndNotificationsDoNotCommitOrReplaceBodies(c, owner, note, todo);
        await ExternalSavePreservesRuntimeCallbackDirtyState(c);
        VisualBroadcastSurvivesPaperCreation(c);
        PopupThemeNotificationsFollowAppearance(c);
        ExtendedComponentBoundaryBehavior(c);
        CreationOwnsInitialTodoFields(c, note);
        PartialReminderDeliveryKeepsUnsurfacedItemsPending(c);
        await InitialRuntimeFailureDoesNotRetry(c);
    }

    private static void DeferredPluginEnablementDoesNotAttachEarly(AppController c)
    {
        const string providerId = "tests.deferred-enable";
        var paper = new PaperData
        {
            Id = "deferred-enable-paper",
            Type = PaperTypes.Note,
            BodyProviderId = providerId,
            IsVisible = true,
            IsCollapsed = true
        };
        var window = new PaperWindow(paper, c, deferShellConstruction: true);
        try
        {
            var host = ReadField<PaperBodyHost>(window, "_paperBodyHost");
            Check(!window.IsShellBuilt && host.Current == null,
                "Deferred plugin fixture must start without a body session.");

            c.State.DisabledPluginIds.Add(providerId);
            window.RefreshPluginEnabledState(providerId);
            Check(!window.IsShellBuilt &&
                  host.Current == null &&
                  ReadField<bool>(window, "_bodyDisabled"),
                "Disabling a deferred plugin must update capsule state without attaching a body.");

            c.State.DisabledPluginIds.Remove(providerId);
            window.RefreshPluginEnabledState(providerId);
            Check(!window.IsShellBuilt &&
                  host.Current == null &&
                  !ReadField<bool>(window, "_bodyDisabled"),
                "Re-enabling a deferred plugin must restore capsule state without attaching a body.");

            window.EnsureShellBuilt();
            Check(window.IsShellBuilt && host.Current != null,
                "Normal shell construction must attach exactly the first body session after a deferred enablement change.");
        }
        finally
        {
            c.State.DisabledPluginIds.Remove(providerId);
            window.CloseForReal();
        }
    }

    private static void StartingPluginRuntimeIsDisposedOnRemoval(AppController c)
    {
        const string providerId = "tests.starting-runtime";
        var slotType = typeof(AppController).GetNestedType(
            "PluginRuntimeSlot",
            BindingFlags.NonPublic)!;
        var slot = Activator.CreateInstance(slotType, nonPublic: true)!;
        slotType.GetProperty("ProviderId")!.SetValue(slot, providerId);
        var stateProperty = slotType.GetProperty("State")!;
        stateProperty.SetValue(
            slot,
            Enum.Parse(stateProperty.PropertyType, "Starting"));

        var starting = new BoundaryDisposable();
        slotType.GetProperty("StartingRuntime")!.SetValue(slot, starting);
        var slots = ReadField<IDictionary>(c, "_pluginRuntimeSlots");
        slots[providerId] = slot;
        try
        {
            Invoke(c, "RemovePluginRuntimeSlot", providerId);
            Check(starting.Disposals == 1 && !slots.Contains(providerId),
                "Removing a starting plugin Runtime must immediately dispose its temporary runtime owner.");
        }
        finally
        {
            slots.Remove(providerId);
        }
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
            var oldAdditive = c.State.McpAllowBlankWrites;
            var oldFull = c.State.McpAllowFullWrites;
            c.State.McpEnabled = true;
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    _ = commands.ListPapers();
                    _ = commands.GetPaper(owner.Id);
                    _ = commands.ListTodos(includeBlank: true);
                    Check(commands.GetNote(owner.Id)!.Content == owner.Content,
                        "A clean note should read back its current content.");
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

                var noteEvents = new List<PaperTodoEvent>();
                using (c.PaperBodyPluginEvents.Subscribe(
                    Guid.NewGuid(),
                    "tests.boundary",
                    new PaperTodoEventFilter
                    {
                        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.NoteChanged },
                        ExcludeOwnOperations = true
                    },
                    noteEvents.Add))
                {
                    var persistedBeforeUnrelatedWrite = note.Content;
                    box.Text = "user unrelated edit";
                    Check(note.Content != box.PersistentText,
                        "Fixture must keep an unrelated Markdown edit pending.");

                    commands.AppendTodos(new AppendTodosRequest
                    {
                        PaperId = todo.Id,
                        Todos = [new TodoCreateItem { Text = "external while note pending" }]
                    }, PaperOperationContext.Plugin("tests.boundary"));

                    Check(note.Content == persistedBeforeUnrelatedWrite,
                        "An external write to another paper must not commit pending Markdown.");
                    Check(ReadField<bool>(c, "_hasPendingDirty"),
                        "The unrelated Markdown edit must remain pending after the external sync save.");
                    Check(ReadField<System.Windows.Threading.DispatcherTimer>(c, "_saveTimer").IsEnabled,
                        "The unrelated Markdown edit must keep its normal save timer.");

                    c.SaveNow(sync: true);
                    c.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
                    Check(noteEvents.OfType<NoteChangedEvent>().Any(value =>
                            value.Metadata.Origin == PaperTodoEventOrigin.User),
                        "The unrelated Markdown edit must be published as a user change.");
                }

                void ReplaceViaMcp(string text) => mcp.Execute(Json(new
                {
                    method = "write_note",
                    @params = new { paper_id = note.Id, mode = "replace", content = text }
                }));

                c.State.McpAllowFullWrites = c.State.McpAllowBlankWrites = false;
                var previousText = note.Content;
                box.Text = "unsaved edit";
                using var pluginReader = new PaperBodyPluginHostApi(c, commands, owner.Id, "tests.reader",
                    [PaperTodoPermissionNames.NotesRead], () => true, () => true);
                Check(pluginReader.GetNote(note.Id)!.Content == box.PersistentText && note.Content == previousText,
                    "Plugin reads must see pending Markdown without committing it.");
                Throws<McpApiException>(() => ReplaceViaMcp(previousText), "full_writes_disabled");
                ReplaceViaMcp(box.PersistentText);
                Check(box.PersistentText == "unsaved edit" && note.Content == previousText,
                    "Read-only replace must leave both the draft and its pending status intact.");

                box.Text = "";
                noteWindow.CommitPendingMarkdownContentForSave();
                box.Text = "draft in a previously empty note";
                c.State.McpAllowBlankWrites = true;
                Throws<McpApiException>(() => ReplaceViaMcp("replacement"), "full_writes_disabled");
                Check(note.Content == "" && box.PersistentText == "draft in a previously empty note",
                    "An additive grant cannot replace a live draft in a previously empty note.");
                box.Text = "";
                ReplaceViaMcp("filled blank note");
                Check(note.Content == "filled blank note", "Additive access can still fill a genuinely empty note.");

                box.Text = "user invalid write edit";
                var beforeInvalidWrite = note.Content;
                Throws<PaperCommandException>(() => commands.WriteNote(
                    new WriteNoteRequest
                    {
                        PaperId = note.Id,
                        Mode = NoteWriteMode.Append,
                        Content = new string('x', PaperWindow.NoteTextMaxLength + 1)
                    },
                    PaperOperationContext.Plugin("tests.boundary")), "content_too_long");
                Check(note.Content == beforeInvalidWrite &&
                      box.PersistentText == "user invalid write edit",
                    "An invalid note write must not commit the user's pending target text.");

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
            finally
            {
                c.State.McpEnabled = oldEnabled;
                c.State.McpAllowBlankWrites = oldAdditive;
                c.State.McpAllowFullWrites = oldFull;
            }

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

    private static async Task ExternalSavePreservesRuntimeCallbackDirtyState(AppController c)
    {
        // Settle pre-existing application edits before installing the temporary Runtime fixture.
        // Saving after the fixture is installed would itself reconcile and start that Runtime.
        c.SaveNow(sync: true);

        const string id = "tests.post-save-dirty";
        var registry = c.PaperBodyPlugins;
        var descriptors = ReadField<Dictionary<string, PaperBodyPluginDescriptor>>(registry, "_descriptors");
        var loaded = ReadField<IDictionary>(registry, "_loadedNativeByDirectory");
        var path = Path.Combine(AppContext.BaseDirectory, "plugins", id);
        var manifest = new PaperBodyPluginManifest
        {
            Id = id,
            Kind = "native",
            ApiVersion = "2.2",
            Capabilities = ["runtime"],
            DirectoryPath = path
        };
        var descriptor = new PaperBodyPluginDescriptor(
            id,
            "Post-save dirty test",
            "",
            new Version(1, 0),
            "2.2",
            1,
            PaperBodyPluginKind.Native,
            PaperBodyCapabilities.None,
            PaperTodoPermissionNames.None,
            path,
            path,
            "fixture",
            typeof(BoundaryRuntimePlugin),
            manifest);
        var loadedType = typeof(PaperBodyPluginRegistry).GetNestedType(
            "LoadedNativePlugin",
            BindingFlags.NonPublic)!;
        loaded[path] = Activator.CreateInstance(
            loadedType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [path, descriptor, null],
            null)!;
        descriptors[id] = descriptor;

        var removed = new PaperData
        {
            Id = "post-save-removed",
            Type = PaperTypes.Note,
            BodyProviderId = id,
            IsVisible = false
        };
        var survivor = new PaperData
        {
            Id = "post-save-survivor",
            Type = PaperTypes.Note,
            BodyProviderId = id,
            IsVisible = false
        };
        c.State.Papers.Add(removed);
        c.State.Papers.Add(survivor);

        try
        {
            BoundaryRuntimePlugin.Starts = 0;
            BoundaryRuntimePlugin.Fail = false;
            var removedCallbacks = 0;
            var addedCallbacks = 0;
            var added = new PaperData
            {
                Id = "post-save-added", Type = PaperTypes.Note, BodyProviderId = id, IsVisible = false
            };
            BoundaryRuntimePlugin.Configure = context =>
            {
                _ = context.Papers.Subscribe(value =>
                {
                    if (value.Kind == PaperPluginRuntimeEventKind.PaperAdded && value.PaperId == added.Id)
                        addedCallbacks++;
                    if (value.Kind == PaperPluginRuntimeEventKind.PaperRemoved &&
                        string.Equals(value.PaperId, removed.Id, StringComparison.Ordinal))
                    {
                        removedCallbacks++;
                        survivor.Title = "changed after save";
                        c.MarkDirty();
                    }
                });
            };

            c.ReconcilePluginRuntimes();
            var slots = ReadField<IDictionary>(c, "_pluginRuntimeSlots");
            var slot = slots[id]!;
            for (var attempt = 0;
                 attempt < 50 &&
                 slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() != "Running";
                 attempt++)
            {
                await Task.Delay(10);
            }
            Check(BoundaryRuntimePlugin.Starts == 1 &&
                  slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Running",
                "Post-save dirty fixture Runtime did not finish startup.");

            c.State.Papers.Add(added);
            c.TryFlushPendingPluginPaperStateDeletes();
            Check(addedCallbacks == 0,
                "Plugin file cleanup must not publish unrelated Runtime membership changes.");
            c.SaveNow(sync: true);
            Check(addedCallbacks == 1,
                "An ordinary successful save must still publish Runtime membership changes.");
            c.State.Papers.Remove(added);
            c.SaveNow(sync: true);

            _ = c.PaperCommands.DeletePaper(
                removed.Id,
                PaperOperationContext.Plugin("tests.boundary"));

            Check(removedCallbacks == 1 && survivor.Title == "changed after save",
                "Runtime PaperRemoved callback did not run exactly once.");
            Check(ReadField<bool>(c, "_hasPendingDirty"),
                "A Runtime mutation created after the external snapshot save lost its dirty state.");
            Check(ReadField<System.Windows.Threading.DispatcherTimer>(c, "_saveTimer").IsEnabled,
                "A Runtime mutation created after the external snapshot save lost its save timer.");

            c.SaveNow(sync: true);
        }
        finally
        {
            BoundaryRuntimePlugin.Configure = null;
            c.State.Papers.Remove(removed);
            c.State.Papers.Remove(survivor);
            c.State.Papers.RemoveAll(paper => paper.Id == "post-save-added");
            c.ReconcilePluginRuntimes();
            descriptors.Remove(id);
            loaded.Remove(path);
        }
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

    private static void TodoPasteUsesEventPayload()
    {
        const string eventText = "event A\nevent second";
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, eventText);

        var read = typeof(PaperWindow).GetMethod(
            "TryGetTodoPastingText",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Todo paste event reader was not found.");
        object?[] args = [data, null];
        var accepted = (bool)(read.Invoke(null, args) ?? false);
        Check(accepted && string.Equals(args[1] as string, eventText, StringComparison.Ordinal),
            "Todo paste preflight and execution must use the current event payload.");
    }

    private static void PartialReminderDeliveryKeepsUnsurfacedItemsPending(AppController c)
    {
        var previousEnabled = c.State.ExperimentalTodoReminders;
        var previousSound = c.State.ExperimentalTodoReminderSoundEnabled;
        var tray = ReadField<object?>(c, "_trayIcon");
        var now = DateTimeOffset.Now;
        var paper = new PaperData
        {
            Id = "tests.partial-reminder-delivery",
            Type = PaperTypes.Todo,
            IsVisible = false,
            Items =
            [
                new PaperItem { Id = "reminder-1", Text = "first", Order = 0, ReminderAt = now.AddSeconds(-2) },
                new PaperItem { Id = "reminder-2", Text = "second", Order = 1, ReminderAt = now.AddSeconds(-1) },
                new PaperItem { Id = "reminder-future", Text = "future", Order = 2, ReminderAt = now.AddSeconds(12) }
            ]
        };
        var cancelledPaper = new PaperData
        {
            Id = "tests.cancelled-before-display",
            Type = PaperTypes.Todo,
            IsVisible = false
        };
        var delegated = new PaperData
        {
            Id = "tests.suppressed-reminder-notification",
            Type = PaperTypes.Todo,
            IsVisible = false,
            Items = [new PaperItem { Id = "delegated", Text = "pet notification",
                ReminderAt = DateTimeOffset.Now.AddMinutes(1) }]
        };
        c.State.Papers.Add(paper);

        try
        {
            c.State.ExperimentalTodoReminders = true;
            c.State.ExperimentalTodoReminderSoundEnabled = false;
            Field(c, "_trayIcon", null!);
            c.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
            var events = new List<PaperTodoEvent>();
            using var subscription = c.PaperBodyPluginEvents.Subscribe(
                Guid.NewGuid(), "reminder-test", new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind>
                    {
                        PaperTodoEventKind.TodoChanged,
                        PaperTodoEventKind.PaperChanged
                    }
                }, events.Add);
            paper.Items[0].Text = "pending user edit";
            c.MarkDirty();

            _ = Invoke(c, "ProcessDueTodoReminders", now);
            c.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
            var presentationEvents = events.OfType<PaperChangedEvent>()
                .Where(e => e.After.Id == paper.Id &&
                    e.ChangedFields.HasFlag(PaperChangedFields.Visibility))
                .ToArray();
            Check(presentationEvents.Length == 1 &&
                presentationEvents[0].Metadata.Origin == PaperTodoEventOrigin.System &&
                !presentationEvents[0].Before.IsVisible && presentationEvents[0].After.IsVisible,
                "Automatic reminder presentation must emit exactly one system-origin visibility event.");
            Check(events.FindIndex(e => e is TodoChangedEvent change &&
                    change.After.Id == "reminder-1" &&
                    change.Metadata.Origin == PaperTodoEventOrigin.User) <
                  events.FindIndex(e => e is PaperChangedEvent changed &&
                    changed.After.Id == paper.Id &&
                    changed.Metadata.Origin == PaperTodoEventOrigin.System),
                "Pending user changes must be delivered before reminder presentation changes.");
            var changes = events.OfType<TodoChangedEvent>().ToArray();
            Check(changes.Any(e => e.After.Id == "reminder-1" &&
                e.ChangedFields == TodoChangedFields.Reminder &&
                e.Metadata.Origin == PaperTodoEventOrigin.System),
                "Delivered reminder state must be attributed to the system. Events: " +
                string.Join(", ", events.Select(e => e is TodoChangedEvent change
                    ? $"{change.After.Id}:{change.ChangedFields}:{change.Metadata.Origin}"
                    : e.Kind.ToString())));
            var reminderChanges = changes.Where(e => e.After.Id == "reminder-1").ToArray();
            Check(reminderChanges.Length == 2 &&
                reminderChanges[0].ChangedFields == TodoChangedFields.Text &&
                reminderChanges[0].Metadata.Origin == PaperTodoEventOrigin.User &&
                reminderChanges[1].Metadata.Origin == PaperTodoEventOrigin.System &&
                reminderChanges[0].After == reminderChanges[1].Before,
                "Pending user edits must precede the system transition with continuous snapshots.");
            Check(!events.OfType<TodoChangedEvent>().Any(e => e.After.Id == "reminder-2" && e.Metadata.Origin == PaperTodoEventOrigin.System && !e.Before.ReminderTriggered && e.After.ReminderTriggered),
                "An undelivered reminder must not emit an occurrence.");
            var retryTimer = ReadField<System.Windows.Threading.DispatcherTimer?>(c, "_todoReminderTimer");
            Check(retryTimer?.IsEnabled == true && retryTimer.Interval < TimeSpan.FromSeconds(25),
                "Partial delivery retry postponed a separately scheduled future reminder.");

            // Ordinary TodoChanged handlers run after presentation. Even if a listener
            // edits the reminder, it cannot retroactively veto the occurrence.
            var edited = paper.Items[1];
            var callbackSawTriggered = false;
            using (var changeSub = c.PaperBodyPluginEvents.Subscribe(
                Guid.NewGuid(), "post-edit-test", new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged }
                }, e =>
                {
                    if (e is TodoChangedEvent change && change.After.Id == edited.Id &&
                        change.Metadata.Origin == PaperTodoEventOrigin.User)
                    {
                        callbackSawTriggered = edited.ReminderTriggered;
                        edited.ReminderAt = null;
                    }
                }))
            {
                edited.Text = "edited before reminder";
                c.MarkDirty();
                _ = Invoke(c, "ProcessDueTodoReminders", now);
                Check(callbackSawTriggered && edited.ReminderTriggered &&
                    events.OfType<TodoChangedEvent>().Any(e => e.After.Id == edited.Id &&
                        e.Metadata.Origin == PaperTodoEventOrigin.System &&
                        !e.Before.ReminderTriggered && e.After.ReminderTriggered),
                    "Ordinary TodoChanged callback ran before host reminder delivery.");
            }

            // A just-created due Todo must still emit creation then system transition,
            // but its creation callback must not become an implicit pre-notification hook.
            paper.Items[0].Done = true;
            paper.Items[1].Done = true;
            c.State.Papers.Add(cancelledPaper);
            c.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
            var newDue = new PaperItem { Id = "reminder-cancelled", Text = "new reminder", ReminderAt = now.AddSeconds(-1) };
            var callbackRanAfterTrigger = false;
            using var createSub = c.PaperBodyPluginEvents.Subscribe(
                Guid.NewGuid(), "creation-test", new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoCreated }
                }, e =>
                {
                    if (e is TodoCreatedEvent created && created.Todo.Id == newDue.Id)
                    {
                        callbackRanAfterTrigger = newDue.ReminderTriggered;
                        newDue.ReminderAt = null;
                    }
                });
            cancelledPaper.Items.Add(newDue);
            _ = Invoke(c, "ProcessDueTodoReminders", now);
            Check(newDue.ReminderTriggered && callbackRanAfterTrigger && cancelledPaper.IsVisible,
                "Todo creation callback incorrectly prevented the host reminder.");

            Check(paper.Items[0].ReminderTriggered && paper.Items[1].ReminderTriggered,
                "An undelivered second reminder was not retried on the next delivery pass.");

            // Advance reminder notification is an explicit opt-in hook: the plugin may suppress
            // this one default presentation. No PaperTodo paper/balloon/sound is shown, but
            // the Todo is acknowledged and a normal System TodoChanged is still emitted.
            paper.Items[2].Done = true;
            c.State.Papers.Add(delegated);
            c.PaperBodyPluginEvents.ScanNow(PaperOperationContext.User());
            var upcoming = new List<TodoReminderUpcomingEvent>();
            using var preSub = c.PaperBodyPluginEvents.Subscribe(
                Guid.NewGuid(), "advance-test", new PaperTodoEventFilter
                {
                    Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoReminderUpcoming }
                }, e => upcoming.Add((TodoReminderUpcomingEvent)e));
            var delegatedAt = delegated.Items[0].ReminderAt!.Value;
            _ = Invoke(c, "PublishUpcomingTodoReminders", delegatedAt.AddSeconds(-5));
            Check(upcoming.Count == 1 && upcoming[0].Todo.Id == "delegated" &&
                c.SuppressDefaultReminderNotification(upcoming[0].NotificationId),
                "Advance notification did not supply a valid suppression token.");
            _ = Invoke(c, "ProcessDueTodoReminders", delegatedAt);
            Check(delegated.Items[0].ReminderTriggered && !delegated.IsVisible &&
                events.OfType<TodoChangedEvent>().Any(e =>
                    e.After.Id == "delegated" && e.Metadata.Origin == PaperTodoEventOrigin.System &&
                    !e.Before.ReminderTriggered && e.After.ReminderTriggered) &&
                !c.SuppressDefaultReminderNotification(upcoming[0].NotificationId),
                "A suppressed default notification was displayed, retried, or failed to publish TodoChanged.");
            var timer = ReadField<System.Windows.Threading.DispatcherTimer?>(c, "_todoReminderTimer");
            Check(timer?.IsEnabled == true,
                "A remaining future reminder must stay scheduled.");
        }
        finally
        {
            _ = Invoke(c, "StopTodoReminderTimer");
            Field(c, "_trayIcon", tray!);
            c.State.ExperimentalTodoReminders = previousEnabled;
            c.State.ExperimentalTodoReminderSoundEnabled = previousSound;
            if (c.State.Papers.Contains(cancelledPaper))
            {
                _ = c.PaperCommands.DeletePaper(
                    cancelledPaper.Id,
                    PaperOperationContext.User());
            }
            if (c.State.Papers.Contains(delegated))
            {
                _ = c.PaperCommands.DeletePaper(
                    delegated.Id,
                    PaperOperationContext.User());
            }
            if (c.State.Papers.Contains(paper))
            {
                _ = c.PaperCommands.DeletePaper(
                    paper.Id,
                    PaperOperationContext.User());
            }
        }
    }

    private static void ReviewArchiveUnreadableDataFailsClosed()
    {
        var type = typeof(PaperTodo.Plugin.ReviewArchive.ReviewArchivePlugin).Assembly
            .GetType("PaperTodo.Plugin.ReviewArchive.ReviewArchiveStore")!;
        var read = type.GetMethod(
            "ReadDocument",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var root = Path.Combine(
            Path.GetTempPath(),
            "PaperTodo.ArchiveReadChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "review-archive.json");
            const string original = "{broken review archive";
            File.WriteAllText(path, original);
            try
            {
                _ = read.Invoke(null, [path]);
                throw new InvalidOperationException(
                    "Unreadable review archive was treated as missing.");
            }
            catch (TargetInvocationException ex) when (
                ex.InnerException is JsonException or InvalidDataException)
            {
                Check(File.ReadAllText(path) == original,
                    "Unreadable review archive bytes must remain untouched.");
            }
        }
        finally
        {
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
            BoundaryRuntimePlugin.Configure = null;
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
            var before = BoundaryRuntimePlugin.Starts;
            Invoke(c, "RequestPluginRuntimeRestart", runtimeId, id);
            await Task.Delay(250);
            Check(BoundaryRuntimePlugin.Starts == before + 1 &&
                  slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Running",
                "A running runtime failure gets one immediate automatic rebuild.");

            var recoveredRuntimeId =
                (Guid)slot.GetType().GetProperty("RuntimeId")!.GetValue(slot)!;
            before = BoundaryRuntimePlugin.Starts;
            Invoke(c, "RequestPluginRuntimeRestart", recoveredRuntimeId, id);
            await Task.Delay(250);
            Check(BoundaryRuntimePlugin.Starts == before &&
                  slot.GetType().GetProperty("State")!.GetValue(slot)!.ToString() == "Failed",
                "A second running failure stops instead of entering another retry cycle.");
        }
        finally
        {
            c.State.Papers.Remove(paper);
            c.ReconcilePluginRuntimes();
            descriptors.Remove(id); loaded.Remove(path);
        }
    }

    private sealed class BoundaryDisposable : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
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
    public static Action<PaperPluginRuntimeContext>? Configure;
    public IPaperBodySession Create(PaperBodyContext context) => throw new NotSupportedException();
    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context)
    {
        Starts++;
        if (Fail) throw new InvalidOperationException("Test startup failure");
        Configure?.Invoke(context);
        return new Runtime();
    }
    private sealed class Runtime : IPaperPluginRuntime { public void Dispose() { } }
}

