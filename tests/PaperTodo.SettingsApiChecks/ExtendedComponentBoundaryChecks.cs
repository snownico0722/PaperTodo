using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void ExtendedComponentBoundaryBehavior(AppController controller)
    {
        ExternalMutationsRevalidateAfterCallbacks(controller);
        WebBodyDocumentOwnsResourceReset(controller);
    }

    private static void ExternalMutationsRevalidateAfterCallbacks(AppController controller)
    {
        var commands = controller.PaperCommands;
        var origin = PaperOperationContext.Plugin("tests.boundary-command");
        var signalId = commands.CreatePaper(new CreatePaperRequest
        {
            Type = PaperTypes.Todo, Show = false,
            Todos = [new TodoCreateItem { Text = "callback signal" }]
        }, origin).PaperId;
        var signal = controller.State.Papers.Single(p => p.Id == signalId);
        var oldReminders = controller.State.ExperimentalTodoReminders;
        controller.State.ExperimentalTodoReminders = true;
        var allRejected = true;
        try
        {
            foreach (var operation in new[] { "append", "update", "reminder", "note", "deleteTodo", "deletePaper", "title" })
            {
                var note = operation is "note" or "title";
                var targetId = commands.CreatePaper(new CreatePaperRequest
                {
                    Type = note ? PaperTypes.Note : PaperTypes.Todo, Show = false,
                    Content = note ? "original note" : "",
                    Todos = note ? [] : [new TodoCreateItem { Text = "original todo" }]
                }, origin).PaperId;
                var target = controller.State.Papers.Single(p => p.Id == targetId);
                var itemId = note ? "" : target.Items[0].Id;
                var beforeItems = target.Items.ToArray();
                var beforeTitle = target.Title;
                var fired = false;
                Exception? callbackFailure = null;
                using var subscription = controller.PaperBodyPluginEvents.Subscribe(
                    Guid.NewGuid(), "tests.boundary-deleter", new PaperTodoEventFilter
                    {
                        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged },
                        PaperIds = new HashSet<string> { signalId }
                    }, value =>
                    {
                        if (fired || value.Metadata.Origin != PaperTodoEventOrigin.User) return;
                        fired = true;
                        try { commands.DeletePaper(targetId, PaperOperationContext.Plugin("tests.boundary-deleter")); }
                        catch (Exception ex) { callbackFailure = ex; }
                    });
                signal.Items[0].Text += ".";
                controller.MarkDirty();
                Exception? failure = null;
                try
                {
                    switch (operation)
                    {
                        case "append":
                            commands.AppendTodos(new AppendTodosRequest { PaperId = targetId,
                                Todos = [new TodoCreateItem { Text = "must not be appended" }] }, origin);
                            break;
                        case "update":
                            commands.UpdateTodo(new UpdateTodoRequest { PaperId = targetId, TodoId = itemId,
                                Text = "must not be written" }, origin);
                            break;
                        case "reminder":
                            commands.SetTodoReminder(new SetTodoReminderRequest { PaperId = targetId, TodoId = itemId,
                                ReminderAt = DateTimeOffset.Now.AddHours(2) }, origin);
                            break;
                        case "note":
                            commands.WriteNote(new WriteNoteRequest { PaperId = targetId,
                                Content = "must not be written", Mode = NoteWriteMode.Replace }, origin);
                            break;
                        case "deleteTodo":
                            commands.DeleteTodo(new DeleteTodoRequest { PaperId = targetId, TodoId = itemId }, origin);
                            break;
                        case "deletePaper": commands.DeletePaper(targetId, origin); break;
                        case "title":
                            controller.UpdatePaperTitleFromPlugin(target, "must not change", target.BodyProviderId);
                            break;
                    }
                }
                catch (Exception ex) { failure = ex; }
                Check(fired && callbackFailure == null && !controller.State.Papers.Contains(target),
                    "Fixture must delete the actual target during a synchronous user-event callback.");
                var rejected = operation == "title"
                    ? failure is PaperTodoPluginException { Code: "paper_not_owned" }
                    : failure is PaperCommandException { Code: "paper_not_found" };
                Console.WriteLine($"TARGET_CALLBACK operation={operation} rejected={rejected} error={failure?.GetType().Name ?? "none"}");
                allRejected &= rejected;
                if (rejected)
                    Check(target.Title == beforeTitle && target.Items.SequenceEqual(beforeItems) &&
                          (note ? target.Content == "original note" :
                              target.Items[0].Text == "original todo" && target.Items[0].ReminderAt == null),
                        "Rejected outer mutation must not modify the detached target.");
                Check(!ReadField<StateStore>(controller, "_store").Load().Papers.Any(p => p.Id == targetId),
                    "The callback's committed deletion must survive the rejected outer operation.");
            }
            Check(allRejected, "External mutation must revalidate its target after plugin callbacks.");
        }
        finally
        {
            controller.State.ExperimentalTodoReminders = oldReminders;
            commands.DeletePaper(signalId, origin);
        }
    }

    private static async Task ArchiveNotificationsCannotBlockPersistence()
    {
        var assembly = typeof(PaperTodo.Plugin.ReviewArchive.ReviewArchivePlugin).Assembly;
        var type = assembly.GetType("PaperTodo.Plugin.ReviewArchive.ReviewArchiveStore")!;
        var pathField = type.GetField("_path", BindingFlags.Static | BindingFlags.NonPublic)!;
        var docField = type.GetField("_document", BindingFlags.Static | BindingFlags.NonPublic)!;
        var changedField = type.GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldPath = pathField.GetValue(null);
        var oldDoc = docField.GetValue(null);
        var oldChanged = changedField.GetValue(null);
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo-ArchiveObserver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "review-archive.json");
        var laterNotifications = 0;
        try
        {
            pathField.SetValue(null, path);
            docField.SetValue(null, Activator.CreateInstance(docField.FieldType, nonPublic: true));
            Action brokenView = () => throw new InvalidOperationException("fixture view failed");
            Action laterView = () => laterNotifications++;
            changedField.SetValue(null, brokenView + laterView);
            var reader = assembly.GetType("PaperTodo.Plugin.ReviewArchive.ReviewArchiveSettingsReader")!;
            var settings = reader.GetMethod("ReadSettings", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["{}"]);
            Exception? notificationFailure = null;
            try { type.GetMethod("Clear")!.Invoke(null, [settings]); }
            catch (TargetInvocationException ex) { notificationFailure = ex.InnerException; }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(File.Exists(path), "Archive persistence must not depend on view notification success.");
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Check(saved.RootElement.GetProperty("storageVersion").GetInt32() == 3 &&
                  !saved.RootElement.GetProperty("records").EnumerateObject().Any(),
                "The mutation must reach the archive's existing asynchronous persistence path.");
            Check(notificationFailure == null && laterNotifications == 1,
                "One broken archive observer must not stop the remaining observers or escape the mutation.");
        }
        finally
        {
            // Settle only this fixture's pending write before restoring the original store.
            type.GetMethod("Flush")!.Invoke(null, null);
            changedField.SetValue(null, oldChanged);
            pathField.SetValue(null, oldPath);
            docField.SetValue(null, oldDoc);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WebBodyDocumentOwnsResourceReset(AppController controller)
    {
        using var api = new PaperBodyPluginHostApi(controller, controller.PaperCommands,
            null, "tests.document-resources", [], () => true, () => true);
        var content = new DocumentBoundaryPopup();
        var popup = api.Open(new PaperPopupPosition(100, 100),
            new PaperPluginPopupOptions { Width = 160, Height = 100 }, _ => content);
        // The cleanup methods do not need a browser process. Use the real host/popup owner
        // and real disposable leases without constructing a WebView2 renderer.
        var session = (WebPaperBodySession)RuntimeHelpers.GetUninitializedObject(typeof(WebPaperBodySession));
        var context = (PaperBodyContext)RuntimeHelpers.GetUninitializedObject(typeof(PaperBodyContext));
        Field(context, "<Workspace>k__BackingField", api);
        Field(session, "_context", context);
        var first = new BoundaryDisposable();
        var subscriptions = new Dictionary<string, IDisposable> { ["first"] = first };
        Field(session, "_hostSubscriptions", subscriptions);
        Invoke(session, "ClearHostSubscriptions");
        Check(first.Disposals == 1 && subscriptions.Count == 0 && popup.IsOpen,
            "Clearing event subscriptions must not revoke an unrelated plugin popup.");
        var second = new BoundaryDisposable();
        subscriptions["second"] = second;
        Invoke(session, "ResetDocumentResources");
        Check(second.Disposals == 1 && subscriptions.Count == 0 && !popup.IsOpen && content.Disposals == 1,
            "Document teardown must explicitly release both subscriptions and popup content.");
        Invoke(session, "ResetDocumentResources");
        Check(second.Disposals == 1 && content.Disposals == 1,
            "Repeated document teardown must remain idempotent.");
    }

    private sealed class DocumentBoundaryPopup : IPaperPluginPopupContent
    {
        public FrameworkElement View { get; } = new TextBlock { Text = "boundary fixture" };
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
