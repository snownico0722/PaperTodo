using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class Program
{
    private static void ReminderPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        var due = new PaperItem { ReminderAt = now };
        Assert(TodoReminderPolicy.IsDue(due, now), "Reminder due at the boundary was excluded.");
        due.ReminderAt = now.AddHours(-12);
        Assert(TodoReminderPolicy.IsDue(due, now), "Overdue reminder after startup/resume was excluded.");
        due.Done = true;
        Assert(!TodoReminderPolicy.IsDue(due, now), "Completed todo was due.");
        due.Done = false;
        due.ReminderTriggered = true;
        Assert(!TodoReminderPolicy.IsPending(due), "Already triggered reminder was scheduled again.");
        due.ReminderTriggered = false;
        due.ReminderAt = null;
        Assert(!TodoReminderPolicy.IsDue(due, now), "Cancelled reminder was due.");
        due.ReminderAt = now.AddMinutes(1);
        Assert(TodoReminderPolicy.IsPending(due) && !TodoReminderPolicy.IsDue(due, now), "Rescheduled reminder fired early.");
        Assert(TodoReminderPolicy.DeliveredCount(3, false, false) == 0 &&
            TodoReminderPolicy.DeliveredCount(3, true, false) == 1 &&
            TodoReminderPolicy.DeliveredCount(3, false, true) == 3 &&
            TodoReminderPolicy.DeliveredCount(3, true, true) == 3 &&
            TodoReminderPolicy.DeliveredCount(0, true, false) == 0,
            "Failed/partial/batch delivery acknowledgement changed.");
        var (controller, paper, item) = ReminderController();
        using var hub = controller.PaperBodyPluginEvents;
        using var api = BodyApi(controller, [PaperTodoPermissionNames.TodosObserve]);
        var events = 0;
        using var subscription = api.Subscribe(ReminderFilter(), _ => events++);
        controller.State.ExperimentalTodoReminders = false;
        typeof(AppController).GetMethod("ProcessDueTodoReminders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [now]);
        Assert(events == 0 && !item.ReminderTriggered, "Disabled reminders emitted a trigger.");
    }

    private static PaperTodoEventFilter ReminderFilter(string? paperId = null) => new()
    {
        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged },
        PaperIds = paperId == null ? null : new HashSet<string> { paperId }
    };

    private static (AppController Controller, PaperData Paper, PaperItem Item) ReminderController()
    {
        var item = new PaperItem { Id = "item", Text = "Reminder text", ReminderAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var paper = new PaperData { Id = "todo", Title = "Private title", Type = PaperTypes.Todo, Items = [item] };
        return (Controller(new AppState { ExperimentalTodoReminders = true, Papers = [paper] }), paper, item);
    }

    private static void ReminderEvents()
    {
        var (controller, paper, item) = ReminderController();
        using var hub = controller.PaperBodyPluginEvents;
        using var denied = BodyApi(controller, []);
        Error("permission_denied", () => denied.Subscribe(ReminderFilter(), _ => { }));
        using var legacy = BodyApi(controller, [PaperTodoPermissionNames.TodosObserve]);
        Error("permission_denied", () => legacy.ListTodos());
        using var reader = BodyApi(controller, [PaperTodoPermissionNames.TodosRead, PaperTodoPermissionNames.PapersRead]);
        using var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "runtime.plugin",
            [PaperTodoPermissionNames.TodosRead], () => true);
        using var paperReader = BodyApi(controller, [PaperTodoPermissionNames.PapersRead]);
        using var paperSub = paperReader.Subscribe(new()
        { Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.PaperChanged } }, _ => { });
        using var noteReader = BodyApi(controller, [PaperTodoPermissionNames.NotesRead]);
        using var noteSub = noteReader.Subscribe(new()
        { Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.NoteChanged } }, _ => { });
        var redacted = new List<TodoChangedEvent>();
        var full = new List<TodoChangedEvent>();
        var runtimeEvents = new List<TodoChangedEvent>();
        using var observeSub = legacy.Subscribe(ReminderFilter(), e => redacted.Add((TodoChangedEvent)e));
        using var fullSub = reader.Subscribe(ReminderFilter(paper.Id), e => full.Add((TodoChangedEvent)e));
        using var runtimeSub = runtime.Subscribe(ReminderFilter(), e => runtimeEvents.Add((TodoChangedEvent)e));
        var other = 0;
        using var wrongPaper = reader.Subscribe(ReminderFilter("other"), _ => other++);
        var defaults = new List<PaperTodoEvent>();
        using var defaultSub = legacy.Subscribe(new(), defaults.Add);
        var before = reader.ListTodos().Single();
        Assert(before.ReminderAt == item.ReminderAt && !before.ReminderTriggered, "Pending reminder query was wrong.");
        item.ReminderTriggered = true;
        var after = controller.CaptureTodoSnapshot(paper, item);
        var now = DateTimeOffset.UtcNow;
        hub.CaptureTriggeredReminderChanges([before], [after], now)();
        Assert(full.Count == 1 && full[0].Before == before && full[0].After == after &&
            full[0].ChangedFields == TodoChangedFields.Reminder &&
            full[0].Metadata.Origin == PaperTodoEventOrigin.System &&
            full[0].Metadata.OccurredAt == now && full[0].Metadata.SourcePluginId == null,
            "TodoChanged did not describe the reminder transition.");
        Assert(redacted.Count == 1 && redacted[0].Before.Text == "" && redacted[0].After.Text == "" &&
            redacted[0].After.PaperTitle == "" && redacted[0].After.ReminderAt == null &&
            !redacted[0].After.ReminderTriggered, "Legacy observe-only listener leaked reminder content.");
        Assert(runtimeEvents.Count == 1 && runtimeEvents[0].After.Text == item.Text &&
            runtimeEvents[0].After.PaperTitle == "", "Read-only Runtime failed to receive a redacted event.");
        Assert(defaults.Count == 1 && defaults[0] is TodoChangedEvent,
            "Existing default subscriptions stopped receiving reminder changes.");
        Assert(other == 0, "Paper filter received a different paper's reminder.");
        Assert(reader.ListTodos().Single().ReminderTriggered, "Triggered state was not queryable.");
        var late = 0;
        using var lateSub = reader.Subscribe(ReminderFilter(), _ => late++);
        Assert(late == 0, "Late subscription replayed an earlier change.");
        fullSub.Dispose();
        runtime.Dispose();
        item.ReminderTriggered = false;
        hub.ResetBaseline();
        item.ReminderTriggered = true;
        hub.CaptureTriggeredReminderChanges([before], [after], now)();
        Assert(full.Count == 1 && runtimeEvents.Count == 1, "Released subscribers received events.");
        Assert(!before.ReminderTriggered, "Previous snapshot was mutated.");
    }

    private static void ReminderReentrancy()
    {
        var (controller, paper, item) = ReminderController();
        var secondItem = new PaperItem { Id = "second", Text = "Second reminder", ReminderAt = item.ReminderAt };
        paper.Items.Add(secondItem);
        using var hub = controller.PaperBodyPluginEvents;
        using var api = BodyApi(controller, [PaperTodoPermissionNames.TodosRead]);
        var cancelled = 0;
        IDisposable? cancelledSub = null;
        var received = new List<TodoChangedEvent>();
        using var mutating = api.Subscribe(ReminderFilter(), _ =>
        {
            item.Text = "Changed by listener";
            item.ReminderAt = null;
            item.ReminderTriggered = false;
            cancelledSub?.Dispose();
            throw new Exception("Injected listener failure");
        });
        cancelledSub = api.Subscribe(ReminderFilter(), _ => cancelled++);
        using var listener = api.Subscribe(ReminderFilter(), e => received.Add((TodoChangedEvent)e));
        var beforeFirst = controller.CaptureTodoSnapshot(paper, item);
        var beforeSecond = controller.CaptureTodoSnapshot(paper, secondItem);
        var first = beforeFirst with { ReminderTriggered = true };
        var second = beforeSecond with { ReminderTriggered = true };
        hub.CaptureTriggeredReminderChanges([beforeFirst, beforeSecond], [first, second], DateTimeOffset.UtcNow)();
        Assert(received.Count == 2 && received[0].After.Text == "Reminder text" &&
            received[1].After.Text == "Second reminder",
            "A reentrant/throwing listener changed the frozen batch or blocked another subscriber.");
        Assert(cancelled == 0 && received[0].Metadata.OperationId == received[1].Metadata.OperationId &&
            received[0].Metadata.EventId != received[1].Metadata.EventId,
            "Batch identity or in-flight unsubscribe was wrong.");
        hub.CaptureTriggeredReminderChanges([], [], DateTimeOffset.UtcNow)();
        Assert(received.Count == 2, "Empty batch emitted an event.");
        // These callbacks tested reentrancy during the frozen batch only. The following
        // assertions test regular TodoChanged scans without injecting another mutation.
        mutating.Dispose();
        listener.Dispose();
        var changes = new List<TodoChangedEvent>();
        using var changed = api.Subscribe(new() { Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged } },
            e => changes.Add((TodoChangedEvent)e));
        hub.ResetBaseline();
        item.ReminderTriggered = true;
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 1 && changes[0].ChangedFields == TodoChangedFields.Reminder && changes[0].After.ReminderTriggered,
            "Reminder state changes were not observable.");
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 1, "State scans repeated an unchanged TodoChanged transition.");

        item.ReminderAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        item.ReminderTriggered = false;
        item.Text = "Before edit";
        hub.ResetBaseline();
        changes.Clear();
        item.Text = "Pending user edit";
        var prior = controller.CaptureTodoSnapshot(paper, item);
        hub.PrepareTriggeredReminderChanges([prior]);
        item.ReminderTriggered = true;
        var after = controller.CaptureTodoSnapshot(paper, item);
        var publish = hub.CaptureTriggeredReminderChanges([prior], [after], DateTimeOffset.UtcNow);
        publish();
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 2 &&
            changes[0].Metadata.Origin == PaperTodoEventOrigin.User &&
            changes[0].ChangedFields == TodoChangedFields.Text &&
            changes[0].Before.Text == "Before edit" && changes[0].After.Text == "Pending user edit" &&
            changes[1].Metadata.Origin == PaperTodoEventOrigin.System &&
            changes[1].ChangedFields == TodoChangedFields.Reminder &&
            changes[0].After == changes[1].Before && changes[1].After.ReminderTriggered,
            "System reminder transition and pending user edit were not separated.");
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 2, "Reminder transition was emitted twice.");

        item.ReminderTriggered = false;
        hub.ResetBaseline();
        changes.Clear();
        item.ReminderAt = item.ReminderAt!.Value.AddSeconds(1);
        prior = controller.CaptureTodoSnapshot(paper, item);
        item.ReminderTriggered = true;
        after = controller.CaptureTodoSnapshot(paper, item);
        hub.CaptureTriggeredReminderChanges([prior], [after], DateTimeOffset.UtcNow)();
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 2 && changes[0].Metadata.Origin == PaperTodoEventOrigin.User &&
            changes[0].After.ReminderAt == prior.ReminderAt && !changes[0].After.ReminderTriggered &&
            changes[1].Metadata.Origin == PaperTodoEventOrigin.System && changes[1].After.ReminderTriggered,
            "A pending reminder reschedule was not separated from its system trigger.");

        var fresh = new PaperItem { Id = "fresh", Text = "New reminder", ReminderAt = DateTimeOffset.UtcNow };
        var lifecycle = new List<PaperTodoEvent>();
        using var lifecycleSub = api.Subscribe(new() { Kinds = new HashSet<PaperTodoEventKind>
            { PaperTodoEventKind.TodoCreated, PaperTodoEventKind.TodoChanged } }, lifecycle.Add);
        paper.Items.Add(fresh);
        var freshBefore = controller.CaptureTodoSnapshot(paper, fresh);
        hub.PrepareTriggeredReminderChanges([freshBefore]);
        fresh.ReminderTriggered = true;
        var freshAfter = controller.CaptureTodoSnapshot(paper, fresh);
        hub.CaptureTriggeredReminderChanges([freshBefore], [freshAfter], DateTimeOffset.UtcNow)();
        hub.ScanNow(PaperOperationContext.User());
        Assert(lifecycle.Count == 2 && lifecycle[0] is TodoCreatedEvent created &&
            !created.Todo.ReminderTriggered && lifecycle[1] is TodoChangedEvent triggered &&
            triggered.Metadata.Origin == PaperTodoEventOrigin.System && triggered.After.ReminderTriggered,
            "A new pending todo did not emit creation followed by a system trigger.");
    }

    private static void ReminderWebRuntime()
    {
        var (controller, paper, item) = ReminderController();
        using var hub = controller.PaperBodyPluginEvents;
        using var workspace = new PaperPluginRuntimeWorkspaceApi(controller, "web.plugin",
            [PaperTodoPermissionNames.TodosRead], () => true);
        var runtime = (WebPluginRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WebPluginRuntime));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WebPluginRuntime).GetField("_workspace", flags)!.SetValue(runtime, workspace);
        typeof(WebPluginRuntime).GetField("_hostSubscriptions", flags)!.SetValue(runtime, new Dictionary<string, IDisposable>());
        typeof(WebPluginRuntime).GetField("_extensionDocumentToken", flags)!.SetValue(runtime, "current");
        var execute = typeof(WebPluginRuntime).GetMethod("ExecuteHostRequest", flags)!;
        var payload = JsonSerializer.SerializeToElement(new { method = "events.subscribe", @params = new {
            subscriptionId = "s1", types = new[] { "todo.changed" }, paperIds = new[] { paper.Id } } });
        Assert(!WebPluginRuntime.AcceptsExtensionRequest(JsonSerializer.SerializeToElement(new { uiToken = "old" }), payload, "current") &&
            WebPluginRuntime.AcceptsExtensionRequest(JsonSerializer.SerializeToElement(new { uiToken = "current" }), payload, "current"),
            "Runtime accepted a stale document subscription.");
        execute.Invoke(runtime, [payload]);
        var subscriptions = (Dictionary<string, IDisposable>)typeof(WebPluginRuntime).GetField("_hostSubscriptions", flags)!.GetValue(runtime)!;
        Assert(subscriptions.Count == 1, "Web Runtime event request did not reach Workspace.");
        execute.Invoke(runtime, [payload]);
        Assert(subscriptions.Count == 1, "Replacing a subscription leaked registrations.");
        execute.Invoke(runtime, [JsonSerializer.SerializeToElement(new { method = "events.unsubscribe", @params = new { subscriptionId = "s1" } })]);
        Assert(subscriptions.Count == 0, "Web Runtime unsubscribe failed.");
        execute.Invoke(runtime, [payload]);
        typeof(WebPluginRuntime).GetMethod("TryResetExtensionUi", flags)!.Invoke(runtime, null);
        Assert(subscriptions.Count == 0, "Navigation/failure cleanup retained subscriptions.");
        var kinds = WebPaperBodySession.ReadEventKinds(JsonSerializer.SerializeToElement(new { types = new[] { "todo.changed" } }));
        Assert(kinds.SetEquals(new[] { PaperTodoEventKind.TodoChanged }), "Web Body event parser differs from Runtime.");
        Error("invalid_params", () => WebPaperBodySession.ReadEventKinds(JsonSerializer.SerializeToElement(new { types = new[] { "todo.reminderTriggered" } })));
        Error("invalid_params", () => WebPaperBodySession.ReadEventKinds(JsonSerializer.SerializeToElement(new { types = new[] { "unknown" } })));
        using var denied = BodyApi(controller, []);
        typeof(WebPluginRuntime).GetField("_workspace", flags)!.SetValue(runtime, denied);
        try
        {
            execute.Invoke(runtime, [payload]);
            throw new Exception("Web Runtime bypassed reminder read permission.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is PaperTodoPluginException { Code: "permission_denied" }) { }
        var before = controller.CaptureTodoSnapshot(paper, item) with { ReminderTriggered = false };
        var after = before with { ReminderTriggered = true };
        var serialized = JsonSerializer.SerializeToElement(new TodoChangedEvent(
            before, after, TodoChangedFields.Reminder,
            new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, PaperTodoEventOrigin.System, null)),
            WebPluginRuntimeInfrastructure.JsonOptions);
        Assert(serialized.GetProperty("kind").GetString() == "todoChanged" &&
            !serialized.GetProperty("before").GetProperty("reminderTriggered").GetBoolean() &&
            serialized.GetProperty("after").GetProperty("reminderTriggered").GetBoolean(),
            "Web reminder transition payload shape is incorrect.");
    }

    private static void ReminderWebBridge()
    {
        using var temp = new Temp();
        var build = typeof(WebPluginRuntime).GetMethod("BuildBridgeScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bridge = System.IO.Path.Combine(temp.Path, "runtime.js");
        System.IO.File.WriteAllText(bridge, (string)build.Invoke(null, ["https://reminder.test"])!, new UTF8Encoding(false));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "reminder-bridge-check.cjs"));
        start.ArgumentList.Add(bridge);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new TimeoutException("Reminder bridge check timed out."); }
        Assert(process.ExitCode == 0, stderr.GetAwaiter().GetResult());
        _ = stdout.GetAwaiter().GetResult();
    }
}
