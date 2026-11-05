using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
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


    [McpServerTool(Name = "reload_data", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Explicitly replace PaperTodo's running state with the validated active workspace data file (query get_data_reload_status.DataFileName first). Requires full writes. Unsaved in-memory edits may be discarded; no file watcher or automatic merge. Check get_data_reload_status for UI reconciliation.")]
    public Task<JsonElement> ReloadData(CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("reload_data", new { }, cancellationToken);

    [McpServerTool(Name = "merge_data", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Explicit three-way merge of external edits to the active workspace data file. Requires the complete original JSON read before editing as baseline_json; requests without it are rejected. Conflicts follow the current ask/external/internal preference. No file watcher or autosave source tracking is installed. Check get_data_reload_status. Requires full writes.")]
    public Task<JsonElement> MergeData(
        [Description("Required complete original active-workspace JSON contents read before editing, as a JSON string. Preserve this snapshot even if PaperTodo saves again.")] string baseline_json,
        CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("merge_data", new { baseline_json }, cancellationToken);

    [McpServerTool(Name = "get_data_reload_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read WorkspaceNumber, DataFileName, FollowsMainSettings, the last explicit reload outcome and whether UI reconciliation is still in progress. Does not read or modify the data file. After a workspace switch, this query also acknowledges the current workspace so the internal MCP bridge can continue without restarting; old writes are never automatically retried.")]
    public Task<JsonElement> GetDataReloadStatus(CancellationToken cancellationToken = default) =>
        _client.InvokeAsync("get_data_reload_status", new { }, cancellationToken);

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
    [Description("List PaperTodo papers and compact metadata. Each result exposes its identifier as both id and paper_id; pass paper_id to tools that target a paper.")]
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
        [Description("Optional paper title. Limited by the current PaperTodo title.max_length setting (default 6, configurable from 2 to 20 full-width-equivalent characters; two half-width characters count as one). For normal short titles, do not query the setting first; if rejected, the error reports the active limit.")] string? title = null,
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
        [Description("Optional paper title. Limited by the current PaperTodo title.max_length setting (default 6, configurable from 2 to 20 full-width-equivalent characters; two half-width characters count as one). For normal short titles, do not query the setting first; if rejected, the error reports the active limit.")] string? title = null,
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
    [Description("Fill or replace todo text, change completion state, reorder the item, and/or link another PaperTodo paper. Filling blank text needs additive writes; replacing existing text/state, reordering, or changing a paper link needs full writes.")]
    public Task<JsonElement> UpdateTodo(
        [Description("Exact todo paper ID.")] string paper_id,
        [Description("Exact todo item ID.")] string todo_id,
        [Description("Replacement text. Omit to keep text unchanged.")] string? text = null,
        [Description("Replacement completion state. Omit to keep it unchanged.")] bool? done = null,
        [Description("Zero-based target position within the todo paper. Values outside the current range are clamped; PaperTodo's completed-item ordering rules still apply. Omit to keep the current order. Requires full writes.")] int? order = null,
        [Description("Paper ID to link from this todo, such as a Note containing longer details. Omit to keep the current link unchanged. Requires PaperTodo full writes.")] string? linked_paper_id = null,
        [Description("Set true to unlink the todo's linked Paper without deleting the Note. Mutually exclusive with linked_paper_id. Requires full writes.")] bool clear_linked_paper = false,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "update_todo",
            OptionalUpdateParameters(paper_id, todo_id, text, done, order, linked_paper_id, clear_linked_paper),
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
        Name = "insert_note_image",
        ReadOnly = false,
        Destructive = false,
        OpenWorld = false)]
    [Description("Append an image to an existing built-in Markdown note. Supply image bytes as raw Base64 (up to 4 MiB decoded, without a data URI), constructed by client code, never copied/generated by the model. Local agents can instead run PaperTodo.exe --import-note-image --paper-id <id> [--image-name <name>] with raw binary stdin, then close stdin. Both routes require MCP additive-write permission; file paths are not accepted. The image is stored in PaperTodo's LMDB store.")]
    public Task<JsonElement> InsertNoteImage(
        [Description("Exact Markdown note paper ID.")] string paper_id,
        [Description("Required raw Base64-encoded image bytes (up to 4 MiB decoded), not a file path or data URI.")] string image_base64,
        [Description("Optional original filename metadata.")] string? image_name = null,
        CancellationToken cancellationToken = default)
        => _client.InvokeAsync(
            "insert_note_image",
            new { paper_id, image_base64, image_name },
            cancellationToken);

    [McpServerTool(
        Name = "read_note_image",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Read one image from a built-in Markdown note as actual MCP image content for vision-capable clients. Get the image ID from the note's Markdown i: image reference via get_paper. Images owned by other notes are not accessible.")]
    public async Task<ContentBlock[]> ReadNoteImage(
        [Description("Exact note paper ID.")] string paper_id,
        [Description("Image ID from a Markdown image reference, e.g. i:001. Supply only the ID (001).")] string image_id,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.InvokeAsync(
            "read_note_image", new { paper_id, image_id }, cancellationToken);
        return
        [
            new TextContentBlock
            {
                Text = $"PaperTodo note {paper_id}, image {image_id}."
            },
            ImageContentBlock.FromBytes(
                Convert.FromBase64String(result.GetProperty("image_base64").GetString()!),
                result.GetProperty("mime").GetString()!)
        ];
    }

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
        int? order,
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
        if (order.HasValue)
        {
            parameters["order"] = order.Value;
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
    [Description("Whether the new todo starts completed.")]
    public bool Done { get; init; }

    [JsonPropertyName("reminder_at")]
    [Description("Optional ISO 8601 future reminder date/time with UTC offset for the new todo.")]
    public string? ReminderAt { get; init; }

    [JsonPropertyName("linked_paper_id")]
    [Description("Optional PaperTodo paper ID to link from the new todo, such as a Note containing longer details.")]
    public string? LinkedPaperId { get; init; }
}

