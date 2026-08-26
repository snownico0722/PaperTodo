using System.Globalization;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// MCP transport adapter. JSON parsing and MCP authorization stay here; all PaperTodo reads,
/// validation, persistence, rollback and UI reconciliation are delegated to PaperCommandService.
/// </summary>
internal sealed class McpCommandService
{
    private readonly AppController _controller;
    private readonly PaperCommandService _commands;

    public McpCommandService(AppController controller, PaperCommandService commands)
    {
        _controller = controller;
        _commands = commands;
    }

    public object? Execute(JsonElement request)
    {
        if (!_controller.IsRunning)
        {
            throw new McpApiException("app_exiting", "PaperTodo is exiting.");
        }
        if (!_controller.State.McpEnabled)
        {
            throw new McpApiException(
                "mcp_disabled",
                "PaperTodo's MCP interface is disabled.");
        }

        var method = RequiredString(request, "method", 80);
        var parameters = request.TryGetProperty("params", out var value)
            ? RequireObject(value, "params")
            : JsonSerializer.SerializeToElement(new { });

        try
        {
            return method switch
            {
                "list_papers" => ListPapers(parameters),
                "get_paper" => GetPaper(parameters),
                "create_todo_paper" => CreateTodoPaper(parameters),
                "create_note" => CreateNote(parameters),
                "add_todos" => AddTodos(parameters),
                "update_todo" => UpdateTodo(parameters),
                "set_todo_reminder" => SetTodoReminder(parameters),
                "write_note" => WriteNote(parameters),
                "delete_paper" => DeletePaper(parameters),
                "delete_todo" => DeleteTodo(parameters),
                _ => throw new McpApiException(
                    "method_not_found",
                    $"Unknown PaperTodo method: {method}")
            };
        }
        catch (PaperCommandException ex)
        {
            throw new McpApiException(ex.Code, ex.Message);
        }
    }

    private object ListPapers(JsonElement parameters)
    {
        var type = OptionalString(parameters, "type", 20);
        var papers = _commands.ListPapers(type)
            .Select(paper =>
            {
                var todos = paper.Type == PaperTypes.Todo
                    ? _commands.ListTodos(paper.Id, includeBlank: false)
                    : [];
                var note = paper.Type == PaperTypes.Note
                    ? _commands.GetNote(paper.Id)
                    : null;
                return new
                {
                    id = paper.Id,
                    type = paper.Type,
                    title = paper.Title,
                    is_visible = paper.IsVisible,
                    item_count = paper.Type == PaperTypes.Todo
                        ? _commands.ListTodos(paper.Id, includeBlank: true).Count
                        : 0,
                    open_item_count = todos.Count(item => !item.Done),
                    content_length = note?.ContentAvailable == true
                        ? note.Content.Length
                        : 0
                };
            })
            .ToArray();
        return new { papers };
    }

    private object GetPaper(JsonElement parameters)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var paper = _commands.GetPaper(paperId)
            ?? throw new McpApiException(
                "paper_not_found",
                "The requested paper does not exist.");
        return PaperDetails(paper);
    }

    private object CreateTodoPaper(JsonElement parameters)
    {
        RequireAdditiveWrites();
        var title = OptionalString(
            parameters,
            "title",
            _controller.State.MaxTitleLength);
        var show = OptionalBoolean(parameters, "show") ?? true;
        var todos = ReadTodoInputs(parameters, required: false);
        RequireFullWritesForTodoMetadata(todos);

        var result = _commands.CreatePaper(
            new CreatePaperRequest
            {
                Type = PaperTypes.Todo,
                Title = title,
                Show = show,
                Todos = todos
            },
            PaperOperationContext.Mcp());
        return PaperDetails(RequirePaperSnapshot(result.PaperId));
    }

    private object CreateNote(JsonElement parameters)
    {
        RequireAdditiveWrites();
        var title = OptionalString(
            parameters,
            "title",
            _controller.State.MaxTitleLength);
        var content = OptionalString(
            parameters,
            "content",
            PaperWindow.NoteTextMaxLength,
            allowEmpty: true) ?? "";
        var show = OptionalBoolean(parameters, "show") ?? true;

        var result = _commands.CreatePaper(
            new CreatePaperRequest
            {
                Type = PaperTypes.Note,
                Title = title,
                Show = show,
                Content = content
            },
            PaperOperationContext.Mcp());
        return PaperDetails(RequirePaperSnapshot(result.PaperId));
    }

    private object AddTodos(JsonElement parameters)
    {
        RequireAdditiveWrites();
        var paperId = RequiredString(parameters, "paper_id", 64);
        var todos = ReadTodoInputs(parameters, required: true);
        RequireFullWritesForTodoMetadata(todos);
        var result = _commands.AppendTodos(
            new AppendTodosRequest
            {
                PaperId = paperId,
                Todos = todos
            },
            PaperOperationContext.Mcp());
        var added = _commands.ListTodos(paperId, includeBlank: true)
            .Where(item => result.TodoIds.Contains(item.Id, StringComparer.Ordinal))
            .Select(TodoDetails)
            .ToArray();
        return new { paper_id = paperId, added };
    }

    private object UpdateTodo(JsonElement parameters)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var todoId = RequiredString(parameters, "todo_id", 64);
        var before = RequireTodo(paperId, todoId);

        var hasText = parameters.TryGetProperty("text", out var textValue);
        var hasDone = parameters.TryGetProperty("done", out var doneValue);
        var hasOrder = parameters.TryGetProperty("order", out var orderValue);
        var hasLinkedPaper = parameters.TryGetProperty(
            "linked_paper_id",
            out var linkedPaperValue);
        if (!hasText && !hasDone && !hasOrder && !hasLinkedPaper)
        {
            throw new McpApiException(
                "invalid_params",
                "Provide text, done, order and/or linked_paper_id.");
        }

        string? text = null;
        if (hasText)
        {
            text = RequiredStringValue(
                textValue,
                "text",
                PaperWindow.TodoTextMaxLength,
                allowEmpty: true);
            if (string.IsNullOrWhiteSpace(before.Text))
            {
                RequireAdditiveWrites();
            }
            else if (!string.Equals(before.Text, text, StringComparison.Ordinal))
            {
                RequireFullWrites();
            }
        }

        bool? done = null;
        if (hasDone)
        {
            done = RequiredBooleanValue(doneValue, "done");
            if (done != before.Done)
            {
                RequireFullWrites();
            }
        }

        int? order = null;
        if (hasOrder)
        {
            order = RequiredIntegerValue(orderValue, "order");
            if (order != before.Order)
            {
                RequireFullWrites();
            }
        }

        string? linkedPaperId = null;
        if (hasLinkedPaper)
        {
            RequireFullWrites();
            linkedPaperId = linkedPaperValue.ValueKind == JsonValueKind.Null
                ? null
                : RequiredStringValue(linkedPaperValue, "linked_paper_id", 64);
        }

        _commands.UpdateTodo(
            new UpdateTodoRequest
            {
                PaperId = paperId,
                TodoId = todoId,
                Text = hasText ? text : null,
                Done = done,
                Order = order,
                UpdateLinkedPaper = hasLinkedPaper,
                LinkedPaperId = linkedPaperId
            },
            PaperOperationContext.Mcp());

        if (done == true &&
            !before.Done &&
            _controller.State.AutoClearCompletedTodos)
        {
            return new
            {
                paper_id = paperId,
                todo_id = todoId,
                deleted = true
            };
        }

        return TodoDetails(RequireTodo(paperId, todoId));
    }

    private object SetTodoReminder(JsonElement parameters)
    {
        RequireFullWrites();
        var paperId = RequiredString(parameters, "paper_id", 64);
        var todoId = RequiredString(parameters, "todo_id", 64);
        if (!parameters.TryGetProperty("reminder_at", out var reminderValue))
        {
            throw new McpApiException(
                "invalid_params",
                "reminder_at is required; use null to cancel.");
        }
        var reminderAt = reminderValue.ValueKind == JsonValueKind.Null
            ? (DateTimeOffset?)null
            : ParseReminderAt(RequiredStringValue(
                reminderValue,
                "reminder_at",
                80));
        _commands.SetTodoReminder(
            new SetTodoReminderRequest
            {
                PaperId = paperId,
                TodoId = todoId,
                ReminderAt = reminderAt
            },
            PaperOperationContext.Mcp());
        return TodoDetails(RequireTodo(paperId, todoId));
    }

    private object WriteNote(JsonElement parameters)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var content = RequiredString(
            parameters,
            "content",
            PaperWindow.NoteTextMaxLength,
            allowEmpty: true);
        var modeText = OptionalString(parameters, "mode", 20) ?? "fill_blank";
        var mode = modeText switch
        {
            "fill_blank" => NoteWriteMode.FillBlank,
            "append" => NoteWriteMode.Append,
            "replace" => NoteWriteMode.Replace,
            _ => throw new McpApiException(
                "invalid_params",
                "mode must be 'fill_blank', 'append', or 'replace'.")
        };
        if (mode == NoteWriteMode.Replace)
        {
            var note = _commands.GetNote(paperId)
                ?? throw new McpApiException(
                    "paper_not_found",
                    "The requested paper does not exist.");
            if (note.Content.Length == 0)
            {
                RequireAdditiveWrites();
            }
            else if (!string.Equals(note.Content, content, StringComparison.Ordinal))
            {
                RequireFullWrites();
            }
        }
        else
        {
            RequireAdditiveWrites();
        }

        _commands.WriteNote(
            new WriteNoteRequest
            {
                PaperId = paperId,
                Content = content,
                Mode = mode
            },
            PaperOperationContext.Mcp());
        return PaperDetails(RequirePaperSnapshot(paperId));
    }

    private object DeletePaper(JsonElement parameters)
    {
        RequireDeletes();
        var paperId = RequiredString(parameters, "paper_id", 64);
        var replacementCreated = _controller.State.Papers.Count == 1;
        _commands.DeletePaper(paperId, PaperOperationContext.Mcp());
        return new
        {
            deleted = true,
            paper_id = paperId,
            replacement_paper_created = replacementCreated
        };
    }

    private object DeleteTodo(JsonElement parameters)
    {
        RequireDeletes();
        var paperId = RequiredString(parameters, "paper_id", 64);
        var todoId = RequiredString(parameters, "todo_id", 64);
        _commands.DeleteTodo(
            new DeleteTodoRequest { PaperId = paperId, TodoId = todoId },
            PaperOperationContext.Mcp());
        return new { deleted = true, paper_id = paperId, todo_id = todoId };
    }

    private object PaperDetails(PaperSnapshot paper)
    {
        if (paper.Type == PaperTypes.Note)
        {
            var model = RequirePaper(paper.Id);
            PaperBodyStoredState? bodyState = null;
            if (!string.Equals(
                    model.BodyProviderId,
                    PaperBodyProviderIds.Markdown,
                    StringComparison.Ordinal) &&
                _controller.PaperBodyPlugins.DataStore.TryReadPaperState(
                    model.BodyProviderId,
                    model.Id,
                    out var storedBodyState))
            {
                bodyState = storedBodyState;
            }
            var note = _commands.GetNote(paper.Id);
            return new
            {
                id = paper.Id,
                type = paper.Type,
                title = paper.Title,
                is_visible = paper.IsVisible,
                body_provider_id = paper.BodyProviderId,
                body_state = bodyState == null
                    ? null
                    : new { version = bodyState.Version, json = bodyState.Json },
                content = note?.ContentAvailable == true ? note.Content : null
            };
        }

        return new
        {
            id = paper.Id,
            type = paper.Type,
            title = paper.Title,
            is_visible = paper.IsVisible,
            todos = _commands.ListTodos(paper.Id, includeBlank: true)
                .OrderBy(item => item.Order)
                .Select(TodoDetails)
                .ToArray()
        };
    }

    private static object TodoDetails(TodoSnapshot item) => new
    {
        id = item.Id,
        text = item.Text,
        done = item.Done,
        order = item.Order,
        linked_paper_id = item.LinkedPaperId,
        linked_path = item.LinkedPath,
        reminder_at = item.ReminderAt?.ToString("O", CultureInfo.InvariantCulture)
    };

    private PaperData RequirePaper(string paperId) =>
        _controller.State.Papers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, paperId, StringComparison.Ordinal))
        ?? throw new McpApiException(
            "paper_not_found",
            "The requested paper does not exist.");

    private PaperSnapshot RequirePaperSnapshot(string paperId) =>
        _commands.GetPaper(paperId)
        ?? throw new McpApiException(
            "paper_not_found",
            "The requested paper does not exist.");

    private TodoSnapshot RequireTodo(string paperId, string todoId) =>
        _commands.ListTodos(paperId, includeBlank: true).FirstOrDefault(item =>
            string.Equals(item.Id, todoId, StringComparison.Ordinal))
        ?? throw new McpApiException(
            "todo_not_found",
            "The requested todo does not exist.");

    private IReadOnlyList<TodoCreateItem> ReadTodoInputs(
        JsonElement parameters,
        bool required)
    {
        if (!parameters.TryGetProperty("todos", out var todos))
        {
            if (!required) return [];
            throw new McpApiException("invalid_params", "todos is required.");
        }
        if (todos.ValueKind == JsonValueKind.Null && !required) return [];
        if (todos.ValueKind != JsonValueKind.Array)
        {
            throw new McpApiException("invalid_params", "todos must be an array.");
        }
        if (todos.GetArrayLength() is 0 or > PaperWindow.MaxPastedTodoLines)
        {
            throw new McpApiException(
                "invalid_params",
                $"todos must contain between 1 and {PaperWindow.MaxPastedTodoLines} items.");
        }

        var result = new List<TodoCreateItem>(todos.GetArrayLength());
        foreach (var value in todos.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(new TodoCreateItem
                {
                    Text = RequiredStringValue(
                        value,
                        "todo",
                        PaperWindow.TodoTextMaxLength)
                });
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new McpApiException(
                    "invalid_params",
                    "Each todo must be a string or an object.");
            }
            var text = RequiredString(
                value,
                "text",
                PaperWindow.TodoTextMaxLength);
            var done = OptionalBoolean(value, "done") ?? false;
            var linkedPaperId = OptionalString(value, "linked_paper_id", 64);
            DateTimeOffset? reminderAt = null;
            if (value.TryGetProperty("reminder_at", out var reminderValue) &&
                reminderValue.ValueKind != JsonValueKind.Null)
            {
                reminderAt = ParseReminderAt(RequiredStringValue(
                    reminderValue,
                    "reminder_at",
                    80));
            }
            result.Add(new TodoCreateItem
            {
                Text = text,
                Done = done,
                LinkedPaperId = linkedPaperId,
                ReminderAt = reminderAt
            });
        }
        return result;
    }

    private void RequireFullWritesForTodoMetadata(
        IReadOnlyList<TodoCreateItem> inputs)
    {
        if (inputs.Any(input =>
                input.Done ||
                input.ReminderAt.HasValue ||
                !string.IsNullOrWhiteSpace(input.LinkedPaperId)))
        {
            RequireFullWrites();
        }
    }

    private void RequireAdditiveWrites()
    {
        if (!_controller.State.McpAllowBlankWrites &&
            !_controller.State.McpAllowFullWrites)
        {
            throw new McpApiException(
                "blank_writes_disabled",
                "Blank/additive writes are disabled in PaperTodo Settings.");
        }
    }

    private void RequireFullWrites()
    {
        if (!_controller.State.McpAllowFullWrites)
        {
            throw new McpApiException(
                "full_writes_disabled",
                "Full writes are disabled in PaperTodo Settings.");
        }
    }

    private void RequireDeletes()
    {
        if (!_controller.State.McpAllowDeletes)
        {
            throw new McpApiException(
                "deletes_disabled",
                "Direct MCP deletion is disabled in PaperTodo Settings.");
        }
    }

    private static DateTimeOffset ParseReminderAt(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new McpApiException(
                "invalid_params",
                "reminder_at must be ISO 8601 with a UTC offset.");
        }
        return parsed;
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new McpApiException("invalid_params", $"{name} must be an object.");
        }
        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value))
        {
            throw new McpApiException("invalid_params", $"{name} is required.");
        }
        return RequiredStringValue(value, name, maxLength, allowEmpty);
    }

    private static string RequiredStringValue(
        JsonElement value,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new McpApiException("invalid_params", $"{name} must be a string.");
        }
        var text = value.GetString() ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new McpApiException("invalid_params", $"{name} cannot be empty.");
        }
        if (text.Length > maxLength)
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} cannot exceed {maxLength} characters.");
        }
        return text;
    }

    private static string? OptionalString(
        JsonElement parent,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequiredStringValue(value, name, maxLength, allowEmpty);
    }

    private static bool? OptionalBoolean(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequiredBooleanValue(value, name);
    }

    private static bool RequiredBooleanValue(JsonElement value, string name)
    {
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new McpApiException("invalid_params", $"{name} must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static int RequiredIntegerValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new McpApiException("invalid_params", $"{name} must be an integer.");
        }
        return result;
    }
}
