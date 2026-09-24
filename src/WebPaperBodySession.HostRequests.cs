using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class WebPluginWorkspaceRequests
{
    public static object? Execute(
        IPaperTodoHostApi host,
        string method,
        JsonElement parameters) => method switch
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
        "papers.list" => host.ListPapers(OptionalString(parameters, "type")),
        "papers.get" => host.GetPaper(RequiredString(parameters, "paperId")),
        "todos.list" => host.ListTodos(
            OptionalString(parameters, "paperId"),
            OptionalBoolean(parameters, "includeBlank") ?? false),
        "notes.get" => host.GetNote(RequiredString(parameters, "paperId")),
        "noteAssets.readImage" => (host as IPaperNoteAssetsApi
            ?? throw new PaperTodoPluginException("note_assets_unavailable", "NoteAssets is unavailable."))
            .ReadImage(RequiredString(parameters, "paperId"), RequiredString(parameters, "imageId")),
        "papers.create" => host.CreatePaper(Deserialize<CreatePaperRequest>(parameters)),
        "todos.append" => host.AppendTodos(Deserialize<AppendTodosRequest>(parameters)),
        "todos.update" => host.UpdateTodo(Deserialize<UpdateTodoRequest>(parameters)),
        "todos.setReminder" => host.SetTodoReminder(
            Deserialize<SetTodoReminderRequest>(parameters)),
        "notes.write" => host.WriteNote(Deserialize<WriteNoteRequest>(parameters)),
        "todos.delete" => host.DeleteTodo(Deserialize<DeleteTodoRequest>(parameters)),
        "papers.delete" => host.DeletePaper(RequiredString(parameters, "paperId")),
        _ => throw new PaperTodoPluginException(
            "method_not_found",
            $"Unknown PaperTodo workspace method: {method}")
    };

    private static IPaperWorkspacePresentationApi WorkspacePresentation(IPaperTodoHostApi host) =>
        host as IPaperWorkspacePresentationApi
        ?? throw new PaperTodoPluginException("presentation_unavailable", "Cross-paper presentation is unavailable.");

    private static T Deserialize<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(WebPluginRuntimeInfrastructure.JsonOptions)
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
