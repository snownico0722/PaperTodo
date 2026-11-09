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
            "popups.open" or "popups.close" => WebPluginPopupRequests.Execute(
                _context.Popups, _context.Host, _manifest, method, parameters, message => Send(message)),
            "paper.show" or
            "paper.hide" or
            "paper.toggle" or
            "paper.expand" or
            "paper.collapse" or
            "paper.toggleCollapsed" or
            "paper.activate" => ExecutePaperPresentationHostRequest(method, parameters),
            "topbar.paper.set" => SetPaperTopBarActionsFromWeb(parameters),
            "topbar.global.set" => SetGlobalTopBarActionsFromWeb(parameters),
            _ => WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters, BridgeJsonOptions)
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
