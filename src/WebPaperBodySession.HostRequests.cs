using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecuteHostRequest(string method, JsonElement parameters)
    {
        return method switch
        {
            "runtime.post" => PostRuntimeMessage(parameters),
            "appSettings.list" or
            "appSettings.get" or
            "appSettings.set" =>
                WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters),
            "papers.show" or
            "papers.hide" or
            "papers.toggle" or
            "papers.expand" or
            "papers.collapse" or
            "papers.toggleCollapsed" or
            "papers.activate" =>
                WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters),
            "papers.list" => _context.Host.ListPapers(OptionalPayloadString(parameters, "type")),
            "papers.get" => _context.Host.GetPaper(PayloadString(parameters, "paperId")),
            "todos.list" => _context.Host.ListTodos(
                OptionalPayloadString(parameters, "paperId"),
                OptionalPayloadBoolean(parameters, "includeBlank") ?? false),
            "notes.get" => _context.Host.GetNote(PayloadString(parameters, "paperId")),
            "noteAssets.readImage" => WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters),
            "popups.open" or "popups.close" => WebPluginPopupRequests.Execute(
                _context.Popups, _context.Host, _manifest, method, parameters, message => Send(message)),
            "papers.create" => _context.Host.CreatePaper(
                DeserializePayload<CreatePaperRequest>(parameters)),
            "todos.append" => _context.Host.AppendTodos(
                DeserializePayload<AppendTodosRequest>(parameters)),
            "todos.update" => _context.Host.UpdateTodo(
                DeserializePayload<UpdateTodoRequest>(parameters)),
            "todos.setReminder" => _context.Host.SetTodoReminder(
                DeserializePayload<SetTodoReminderRequest>(parameters)),
            "notes.write" => _context.Host.WriteNote(
                DeserializePayload<WriteNoteRequest>(parameters)),
            "todos.delete" => _context.Host.DeleteTodo(
                DeserializePayload<DeleteTodoRequest>(parameters)),
            "papers.delete" => _context.Host.DeletePaper(
                PayloadString(parameters, "paperId")),
            "paper.show" or
            "paper.hide" or
            "paper.toggle" or
            "paper.expand" or
            "paper.collapse" or
            "paper.toggleCollapsed" or
            "paper.activate" => ExecutePaperPresentationHostRequest(method, parameters),
            "topbar.paper.set" => SetPaperTopBarActionsFromWeb(parameters),
            "topbar.global.set" => SetGlobalTopBarActionsFromWeb(parameters),
            _ => throw new PaperTodoPluginException(
                "method_not_found",
                $"Unknown PaperTodo plugin host method: {method}")
        };
    }

    private object? ExecuteMiniHostRequest(string method, JsonElement parameters) =>
        method == "runtime.post"
            ? PostRuntimeMessage(parameters)
            : WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters);

    private object? PostRuntimeMessage(JsonElement parameters)
    {
        var message = parameters.ValueKind == JsonValueKind.Object &&
                      parameters.TryGetProperty("message", out var messageValue)
            ? messageValue
            : default;
        if (!_context.Runtime.Post(
                message.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement<object?>(null)
                    : message.Clone()))
        {
            throw new PaperTodoPluginException(
                "runtime_unavailable",
                "The plugin Runtime is not ready to accept this message.");
        }
        return null;
    }

    private object? ExecutePaperPresentationHostRequest(
        string method,
        JsonElement parameters)
    {
        var activate = OptionalPayloadBoolean(parameters, "activate") ?? true;
        switch (method)
        {
            case "paper.show":
                _context.Presentation.Show(activate);
                break;
            case "paper.hide":
                _context.Presentation.Hide();
                break;
            case "paper.toggle":
                _context.Presentation.ToggleVisibility(activate);
                break;
            case "paper.expand":
                _context.Presentation.Expand(activate);
                break;
            case "paper.collapse":
                _context.Presentation.Collapse();
                break;
            case "paper.toggleCollapsed":
                _context.Presentation.ToggleCollapsed(activate);
                break;
            case "paper.activate":
                _context.Presentation.Activate();
                break;
            default:
                throw new PaperTodo.Plugin.PaperTodoPluginException(
                    "method_not_found",
                    $"Unknown PaperTodo paper presentation method: {method}");
        }

        return null;
    }
}
