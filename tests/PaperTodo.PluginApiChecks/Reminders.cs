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
        Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoReminderTriggered },
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
        using var observe = BodyApi(controller, [PaperTodoPermissionNames.TodosObserve]);
        using var read = BodyApi(controller, [PaperTodoPermissionNames.TodosRead, PaperTodoPermissionNames.TodosObserve, PaperTodoPermissionNames.PapersRead]);
        using var runtime = new PaperPluginRuntimeWorkspaceApi(controller, "runtime.plugin",
            [PaperTodoPermissionNames.TodosObserve, PaperTodoPermissionNames.TodosRead], () => true);
        var redacted = new List<TodoReminderTriggeredEvent>();
        var full = new List<TodoReminderTriggeredEvent>();
        var runtimeEvents = new List<TodoReminderTriggeredEvent>();
        using var observeSub = observe.Subscribe(ReminderFilter(), e => redacted.Add((TodoReminderTriggeredEvent)e));
        using var fullSub = read.Subscribe(ReminderFilter(paper.Id), e => full.Add((TodoReminderTriggeredEvent)e));
        using var runtimeSub = runtime.Subscribe(ReminderFilter(), e => runtimeEvents.Add((TodoReminderTriggeredEvent)e));
        var wrongPaperEvents = 0;
        using var wrongPaper = read.Subscribe(ReminderFilter("other"), _ => wrongPaperEvents++);
        var oldDefaultEvents = new List<PaperTodoEvent>();
        using var defaultSub = observe.Subscribe(new(), oldDefaultEvents.Add);
        var before = read.ListTodos().Single();
        Assert(before.ReminderAt == item.ReminderAt && !before.ReminderTriggered, "Pending reminder query was wrong.");
        item.ReminderTriggered = true;
        var snapshot = controller.CaptureTodoSnapshot(paper, item);
        var now = DateTimeOffset.UtcNow;
        hub.PublishReminderTriggered([snapshot], now);
        Assert(full.Count == 1 && full[0].Todo == snapshot && full[0].Metadata.Origin == PaperTodoEventOrigin.System &&
            full[0].Metadata.OccurredAt == now && full[0].Metadata.SourcePluginId == null, "Trigger payload or origin was lost.");
        Assert(redacted.Count == 1 && redacted[0].Todo.Text == "" && redacted[0].Todo.PaperTitle == "" &&
            redacted[0].Todo.ReminderAt == null && !redacted[0].Todo.ReminderTriggered, "Observe-only listener leaked reminder content.");
        Assert(runtimeEvents.Count == 1 && runtimeEvents[0].Todo.Text == item.Text && runtimeEvents[0].Todo.PaperTitle == "",
            "Runtime failed to receive the event or paper title was not redacted.");
        Assert(oldDefaultEvents.Count == 0, "A new occurrence kind reached legacy default subscriptions.");
        Assert(wrongPaperEvents == 0, "A paper filter received another paper's reminder.");
        Assert(read.ListTodos().Single().ReminderTriggered, "Triggered state was not queryable.");
        var lateEvents = 0;
        using var late = read.Subscribe(ReminderFilter(), _ => lateEvents++);
        Assert(lateEvents == 0, "A late subscriber replayed a prior occurrence.");
        fullSub.Dispose();
        runtime.Dispose();
        hub.PublishReminderTriggered([snapshot], now);
        Assert(full.Count == 1 && runtimeEvents.Count == 1, "Released subscriptions received events.");
        Assert(!before.ReminderTriggered, "A previous snapshot was mutated.");
    }

    private static void ReminderReentrancy()
    {
        var (controller, paper, item) = ReminderController();
        using var hub = controller.PaperBodyPluginEvents;
        using var api = BodyApi(controller, [PaperTodoPermissionNames.TodosRead, PaperTodoPermissionNames.TodosObserve]);
        var cancelled = 0;
        IDisposable? cancelledSub = null;
        var received = new List<TodoReminderTriggeredEvent>();
        using var mutating = api.Subscribe(ReminderFilter(), _ =>
        {
            item.Text = "Changed by listener";
            item.ReminderAt = null;
            item.ReminderTriggered = false;
            cancelledSub?.Dispose();
            throw new Exception("Injected listener failure");
        });
        cancelledSub = api.Subscribe(ReminderFilter(), _ => cancelled++);
        using var listener = api.Subscribe(ReminderFilter(), e => received.Add((TodoReminderTriggeredEvent)e));
        var first = controller.CaptureTodoSnapshot(paper, item) with { ReminderTriggered = true };
        var second = first with { Id = "second", Text = "Second reminder" };
        hub.PublishReminderTriggered([first, second], DateTimeOffset.UtcNow);
        Assert(received.Count == 2 && received[0].Todo.Text == "Reminder text" && received[1].Todo.Text == "Second reminder",
            "A reentrant/throwing listener changed the frozen batch or blocked another subscriber.");
        Assert(cancelled == 0 && received[0].Metadata.OperationId == received[1].Metadata.OperationId &&
            received[0].Metadata.EventId != received[1].Metadata.EventId, "Batch identity or in-flight unsubscribe was wrong.");
        hub.PublishReminderTriggered([], DateTimeOffset.UtcNow);
        Assert(received.Count == 2, "Empty batch emitted an event.");
        var changes = new List<TodoChangedEvent>();
        using var changed = api.Subscribe(new() { Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.TodoChanged } },
            e => changes.Add((TodoChangedEvent)e));
        hub.ResetBaseline();
        item.ReminderTriggered = true;
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 1 && changes[0].ChangedFields == TodoChangedFields.Reminder && changes[0].After.ReminderTriggered,
            "Reminder state changes were not observable.");
        hub.ScanNow(PaperOperationContext.User());
        Assert(changes.Count == 1 && received.Count == 2, "State scans replayed an occurrence or repeated a change.");
    }

    private static void ReminderWebRuntime()
    {
        var (controller, paper, item) = ReminderController();
        using var hub = controller.PaperBodyPluginEvents;
        using var workspace = new PaperPluginRuntimeWorkspaceApi(controller, "web.plugin",
            [PaperTodoPermissionNames.TodosObserve, PaperTodoPermissionNames.TodosRead], () => true);
        var runtime = (WebPluginRuntime)RuntimeHelpers.GetUninitializedObject(typeof(WebPluginRuntime));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WebPluginRuntime).GetField("_workspace", flags)!.SetValue(runtime, workspace);
        typeof(WebPluginRuntime).GetField("_hostSubscriptions", flags)!.SetValue(runtime, new Dictionary<string, IDisposable>());
        typeof(WebPluginRuntime).GetField("_extensionDocumentToken", flags)!.SetValue(runtime, "current");
        var execute = typeof(WebPluginRuntime).GetMethod("ExecuteHostRequest", flags)!;
        var payload = JsonSerializer.SerializeToElement(new { method = "events.subscribe", @params = new {
            subscriptionId = "s1", types = new[] { "todo.reminderTriggered" }, paperIds = new[] { paper.Id } } });
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
        var kinds = WebPaperBodySession.ReadEventKinds(JsonSerializer.SerializeToElement(new { types = new[] { "todo.reminderTriggered" } }));
        Assert(kinds.SetEquals(new[] { PaperTodoEventKind.TodoReminderTriggered }), "Web Body event parser differs from Runtime.");
        Error("invalid_params", () => WebPaperBodySession.ReadEventKinds(JsonSerializer.SerializeToElement(new { types = new[] { "unknown" } })));
        using var denied = BodyApi(controller, []);
        typeof(WebPluginRuntime).GetField("_workspace", flags)!.SetValue(runtime, denied);
        try
        {
            execute.Invoke(runtime, [payload]);
            throw new Exception("Web Runtime bypassed reminder observe permission.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is PaperTodoPluginException { Code: "permission_denied" }) { }
        var serialized = JsonSerializer.SerializeToElement(new TodoReminderTriggeredEvent(
            controller.CaptureTodoSnapshot(paper, item) with { ReminderTriggered = true },
            new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, PaperTodoEventOrigin.System, null)),
            WebPluginRuntimeInfrastructure.JsonOptions);
        Assert(serialized.GetProperty("kind").GetString() == "todoReminderTriggered" &&
            serialized.GetProperty("todo").GetProperty("reminderTriggered").GetBoolean(), "Web payload shape is incorrect.");
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
