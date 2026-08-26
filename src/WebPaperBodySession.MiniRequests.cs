using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecuteMiniHostRequest(string method, JsonElement parameters)
    {
        if (string.Equals(method, "runtime.post", StringComparison.Ordinal))
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

        return WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters);
    }
}
