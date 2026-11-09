using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class WebPluginWorkspaceRequests
{
    public static object? Execute(
        IPaperTodoHostApi host,
        string method,
        JsonElement parameters,
        JsonSerializerOptions? jsonOptions = null) => method switch
    {
        "appSettings.list" or "appSettings.get" or "appSettings.set" =>
            WebPluginSettingsRequests.Execute(host, method, parameters),
        "papers.show" => WorkspacePresentation(host).ShowPaper(RequiredString(parameters, "paperId"), OptionalBoolean(parameters, "activate") ?? true),
        "papers.hide" => WorkspacePresentation(host).HidePaper(RequiredString(parameters, "paperId")),
        "papers.toggle" => WorkspacePresentation(host).TogglePaperVisibility(RequiredString(parameters, "paperId"), OptionalBoolean(parameters, "activate") ?? true),
        "papers.expand" => WorkspacePresentation(host).ExpandPaper(RequiredString(parameters, "paperId"), OptionalBoolean(parameters, "activate") ?? true),
        "papers.collapse" => WorkspacePresentation(host).CollapsePaper(RequiredString(parameters, "paperId")),
        "papers.toggleCollapsed" => WorkspacePresentation(host).TogglePaperCollapsed(RequiredString(parameters, "paperId"), OptionalBoolean(parameters, "activate") ?? true),
        "papers.activate" => WorkspacePresentation(host).ActivatePaper(RequiredString(parameters, "paperId")),
        "data.reload" => host.ReloadData(),
        "data.merge" => host.MergeData(RequiredString(parameters, "baselineJson")),
        "data.status" => host.GetDataReloadStatus(),
        "papers.list" => host.ListPapers(OptionalString(parameters, "type")),
        "papers.get" => host.GetPaper(RequiredString(parameters, "paperId")),
        "todos.list" => host.ListTodos(
            OptionalString(parameters, "paperId"),
            OptionalBoolean(parameters, "includeBlank") ?? false),
        "notes.get" => host.GetNote(RequiredString(parameters, "paperId")),
        "noteAssets.readImage" => (host as IPaperNoteAssetsApi
            ?? throw new PaperTodoPluginException("note_assets_unavailable", "NoteAssets is unavailable."))
            .ReadImage(RequiredString(parameters, "paperId"), RequiredString(parameters, "imageId")),
        "papers.create" => host.CreatePaper(Deserialize<CreatePaperRequest>(parameters, jsonOptions)),
        "todos.append" => host.AppendTodos(Deserialize<AppendTodosRequest>(parameters, jsonOptions)),
        "todos.update" => host.UpdateTodo(Deserialize<UpdateTodoRequest>(parameters, jsonOptions)),
        "todos.setReminder" => host.SetTodoReminder(
            Deserialize<SetTodoReminderRequest>(parameters, jsonOptions)),
        "reminders.suppressDefault" => ReminderControl(host).SuppressDefaultReminderNotification(
            RequiredGuid(parameters, "notificationId")),
        "notes.write" => host.WriteNote(Deserialize<WriteNoteRequest>(parameters, jsonOptions)),
        "todos.delete" => host.DeleteTodo(Deserialize<DeleteTodoRequest>(parameters, jsonOptions)),
        "papers.delete" => host.DeletePaper(RequiredString(parameters, "paperId")),
        _ => throw new PaperTodoPluginException(
            "method_not_found",
            $"Unknown PaperTodo workspace method: {method}")
    };

    private static IPaperReminderNotificationControl ReminderControl(IPaperTodoHostApi host) =>
        host as IPaperReminderNotificationControl
        ?? throw new PaperTodoPluginException("reminder_control_unavailable", "Reminder notification controls are unavailable.");

    private static Guid RequiredGuid(JsonElement parameters, string name)
    {
        if (!Guid.TryParse(RequiredString(parameters, name), out var value) || value == Guid.Empty)
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a non-empty UUID.");
        return value;
    }

    private static IPaperWorkspacePresentationApi WorkspacePresentation(IPaperTodoHostApi host) =>
        host as IPaperWorkspacePresentationApi
        ?? throw new PaperTodoPluginException("presentation_unavailable", "Cross-paper presentation is unavailable.");

    private static T Deserialize<T>(JsonElement payload, JsonSerializerOptions? options)
    {
        try
        {
            return payload.Deserialize<T>(options ?? WebPluginRuntimeInfrastructure.JsonOptions)
                ?? throw new JsonException("Payload deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                ex.GetBaseException().Message);
        }
    }

    private static string RequiredString(JsonElement payload, string name)
    {
        var value = OptionalString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} is required.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a string.");
        }
        return value.GetString();
    }

    private static bool? OptionalBoolean(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a boolean.");
        }
        return value.GetBoolean();
    }
}

internal static class WebPluginSettingsRequests
{
    internal static object Execute(IPaperTodoHostApi host, string method, JsonElement parameters)
    {
        var api = host as IPaperSettingsApi
            ?? throw new PaperTodoPluginException("settings_unavailable", "Application settings are unavailable.");
        string? Text(string name, bool required = true)
        {
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && (!required || !string.IsNullOrEmpty(v.GetString())))
                    return v.GetString();
                if (!required && v.ValueKind == JsonValueKind.Null) return null;
            }
            else if (!required) return null;
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a string.");
        }
        switch (method)
        {
            case "appSettings.list": return api.List(Text("category", required: false));
            case "appSettings.get": return api.Get(Text("id")!);
            case "appSettings.set":
                var id = Text("id")!;
                if (!parameters.TryGetProperty("value", out var value))
                    throw new PaperTodoPluginException("invalid_params", "value is required.");
                return api.Set(id, value);
            default: throw new PaperTodoPluginException("method_not_found", "Unknown application settings method.");
        }
    }
}
