using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace PaperTodo;

[McpServerToolType]
internal sealed partial class McpTools
{
    private readonly McpPipeClient _client;

    public McpTools(McpPipeClient client)
    {
        _client = client;
    }


    [McpServerTool(Name = "list_settings", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List public PaperTodo application settings, stable IDs, current values, types, constraints and this caller's writability. Plugin-private settings and internal state are not included.")]
    public Task<JsonElement> ListSettings(
        [Description("Optional category from a previous listing.")] string? category = null,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync("list_settings", new { category }, cancellationToken);

    [McpServerTool(Name = "get_setting", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read one public application setting and its schema. Before creating a Note to link from a todo, check todo.paper_links; do not create an orphan Note when linking is disabled.")]
    public Task<JsonElement> GetSetting(
        [Description("Exact public setting ID returned by list_settings.")] string id,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync("get_setting", new { id }, cancellationToken);

    [McpServerTool(Name = "set_setting", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Set one public application setting to a specific value, not a toggle. Requires full writes. Change settings only when the user's request authorizes it; a language change requires restart but does not restart the app.")]
    public Task<JsonElement> SetSetting(
        [Description("Exact public setting ID returned by list_settings.")] string id,
        [Description("JSON value matching the type/constraints returned by get_setting.")] JsonElement value,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync("set_setting", new { id, value }, cancellationToken);

    [McpServerTool(
        Name = "list_papers",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("List PaperTodo papers and compact metadata. Use this first to discover paper IDs.")]
    public Task<JsonElement> ListPapers(
        [Description("Optional filter: 'todo' or 'note'.")] string? type = null,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "list_papers",
            new { type },
            cancellationToken);

    [McpServerTool(
        Name = "get_paper",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Read one PaperTodo paper in full, including ordered todos or note content.")]
    public Task<JsonElement> GetPaper(
        [Description("Exact paper ID returned by list_papers.")] string paper_id,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "get_paper",
            new { paper_id },
            cancellationToken);

    [McpServerTool(
        Name = "create_todo_paper",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description("Create a new todo paper. Requires PaperTodo blank/additive writes.")]
    public Task<JsonElement> CreateTodoPaper(
        [Description("Optional paper title.")] string? title = null,
        [Description("Optional ordered todo steps.")] IReadOnlyList<McpTodoInput>? todos = null,
        [Description("Show the new paper immediately.")] bool show = true,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "create_todo_paper",
            new { title, todos, show },
            cancellationToken);

    [McpServerTool(
        Name = "create_note",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description("Create a new note with optional Markdown content. Requires PaperTodo blank/additive writes.")]
    public Task<JsonElement> CreateNote(
        [Description("Optional paper title.")] string? title = null,
        [Description("Initial note content.")] string content = "",
        [Description("Show the new paper immediately.")] bool show = true,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "create_note",
            new { title, content, show },
            cancellationToken);

    [McpServerTool(
        Name = "add_todos",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description("Append new todo steps to an existing todo paper. Requires PaperTodo blank/additive writes.")]
    public Task<JsonElement> AddTodos(
        [Description("Exact todo paper ID.")] string paper_id,
        [Description("One or more ordered todo steps.")] IReadOnlyList<McpTodoInput> todos,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "add_todos",
            new { paper_id, todos },
            cancellationToken);

    [McpServerTool(
        Name = "update_todo",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Fill or replace todo text, change completion state, and/or link another PaperTodo paper. Filling blank text needs additive writes; replacing existing text/state or changing a paper link needs full writes.")]
    public Task<JsonElement> UpdateTodo(
        [Description("Exact todo paper ID.")] string paper_id,
        [Description("Exact todo item ID.")] string todo_id,
        [Description("Replacement text. Omit to keep text unchanged.")] string? text = null,
        [Description("Replacement completion state. Omit to keep it unchanged.")] bool? done = null,
        [Description("Paper ID to link from this todo, such as a Note containing longer details. Omit to keep the current link unchanged. Requires PaperTodo full writes.")] string? linked_paper_id = null,
        [Description("Set true to unlink the todo's linked Paper without deleting the Note. Mutually exclusive with linked_paper_id. Requires full writes.")] bool clear_linked_paper = false,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "update_todo",
            OptionalUpdateParameters(paper_id, todo_id, text, done, linked_paper_id, clear_linked_paper),
            cancellationToken);

    [McpServerTool(
        Name = "set_todo_reminder",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Set, replace, or cancel a lightweight todo reminder. Requires reminders and full writes in PaperTodo.")]
    public Task<JsonElement> SetTodoReminder(
        [Description("Exact todo paper ID.")] string paper_id,
        [Description("Exact todo item ID.")] string todo_id,
        [Description("ISO 8601 future date/time with UTC offset, or null to cancel.")] string? reminder_at,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "set_todo_reminder",
            new { paper_id, todo_id, reminder_at },
            cancellationToken);

    [McpServerTool(
        Name = "write_note",
        ReadOnly = false,
        Destructive = true,
        OpenWorld = false)]
    [Description("Fill, append, or replace a note. Fill/append needs additive writes; replacing existing content needs full writes.")]
    public Task<JsonElement> WriteNote(
        [Description("Exact note paper ID.")] string paper_id,
        [Description("Text to fill or append.")] string content,
        [Description("Mode: 'fill_blank', 'append', or 'replace'.")] string mode = "fill_blank",
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "write_note",
            new { paper_id, content, mode },
            cancellationToken);

    [McpServerTool(
        Name = "delete_paper",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Delete a paper. Requires PaperTodo's direct-delete permission.")]
    public Task<JsonElement> DeletePaper(
        [Description("Exact paper ID.")] string paper_id,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "delete_paper",
            new { paper_id },
            cancellationToken);

    [McpServerTool(
        Name = "delete_todo",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Delete a todo. Requires PaperTodo's direct-delete permission.")]
    public Task<JsonElement> DeleteTodo(
        [Description("Exact todo paper ID.")] string paper_id,
        [Description("Exact todo item ID.")] string todo_id,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "delete_todo",
            new { paper_id, todo_id },
            cancellationToken);

    private static Dictionary<string, object?> OptionalUpdateParameters(
        string paperId,
        string todoId,
        string? text,
        bool? done,
        string? linkedPaperId,
        bool clearLinkedPaper = false)
    {
        if (clearLinkedPaper && linkedPaperId != null)
            throw new ModelContextProtocol.McpException("linked_paper_id and clear_linked_paper cannot be used together.");
        var parameters = new Dictionary<string, object?>
        {
            ["paper_id"] = paperId,
            ["todo_id"] = todoId
        };
        if (text != null)
        {
            parameters["text"] = text;
        }
        if (done.HasValue)
        {
            parameters["done"] = done.Value;
        }
        if (linkedPaperId != null || clearLinkedPaper)
        {
            // Preserve an explicit null at the internal JSON boundary; omitted still means no change.
            parameters["linked_paper_id"] = linkedPaperId;
        }
        return parameters;
    }
}

internal sealed record McpTodoInput
{
    [JsonPropertyName("text")]
    [Description("Todo text. Uses the same length limit as PaperTodo's normal editor.")]
    public required string Text { get; init; }

    [JsonPropertyName("done")]
    [Description("Whether the todo starts completed. Setting true requires PaperTodo full writes.")]
    public bool Done { get; init; }

    [JsonPropertyName("reminder_at")]
    [Description("Optional ISO 8601 future reminder date/time with UTC offset. Requires PaperTodo full writes.")]
    public string? ReminderAt { get; init; }

    [JsonPropertyName("linked_paper_id")]
    [Description("Optional PaperTodo paper ID to link from this todo, such as a Note containing longer details. Requires PaperTodo full writes.")]
    public string? LinkedPaperId { get; init; }
}
