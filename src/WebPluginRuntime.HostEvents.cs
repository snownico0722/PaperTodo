using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPluginRuntime
{
    private readonly Dictionary<string, IDisposable> _hostSubscriptions = new(StringComparer.Ordinal);

    private object SubscribeHostEvents(JsonElement parameters)
    {
        var id = WebPluginRuntimeInfrastructure.RequiredString(parameters, "subscriptionId");
        if (parameters.TryGetProperty("excludeOwnOperations", out var exclude) &&
            exclude.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new PaperTodoPluginException("invalid_params", "excludeOwnOperations must be a boolean.");
        var filter = new PaperTodoEventFilter
        {
            Kinds = WebPaperBodySession.ReadEventKinds(parameters),
            PaperIds = WebPaperBodySession.ReadStringSet(parameters, "paperIds"),
            ExcludeOwnOperations = exclude.ValueKind != JsonValueKind.False
        };
        if (_hostSubscriptions.Remove(id, out var existing)) existing.Dispose();
        var documentToken = _extensionDocumentToken;
        _hostSubscriptions[id] = _workspace.Subscribe(filter, value =>
        {
            if (documentToken == null || documentToken != _extensionDocumentToken) return;
            Send(new
            {
                type = "hostEvent",
                subscriptionId = id,
                @event = JsonSerializer.SerializeToElement(value, value.GetType(),
                    WebPluginRuntimeInfrastructure.JsonOptions)
            });
        });
        return new { subscribed = true };
    }

    private object UnsubscribeHostEvents(JsonElement parameters)
    {
        var id = WebPluginRuntimeInfrastructure.RequiredString(parameters, "subscriptionId");
        if (_hostSubscriptions.Remove(id, out var subscription)) subscription.Dispose();
        return new { subscribed = false };
    }

    private void ClearHostSubscriptions()
    {
        foreach (var subscription in _hostSubscriptions.Values)
        {
            try { subscription.Dispose(); } catch { }
        }
        _hostSubscriptions.Clear();
    }
}
