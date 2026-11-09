using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPluginRuntime
{
    private readonly Dictionary<string, IDisposable> _hostSubscriptions = new(StringComparer.Ordinal);
    private string? _extensionDocumentToken;

    internal static bool AcceptsExtensionRequest(JsonElement root, JsonElement payload, string? expectedToken)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("method", out var methodValue) ||
            methodValue.ValueKind != JsonValueKind.String) return true;
        var method = methodValue.GetString()!;
        if (!method.StartsWith("paperActions.", StringComparison.Ordinal) &&
            !method.StartsWith("events.", StringComparison.Ordinal) &&
            !method.StartsWith("popups.", StringComparison.Ordinal)) return true;
        return expectedToken != null && root.TryGetProperty("uiToken", out var token) &&
            token.ValueKind == JsonValueKind.String && token.GetString() == expectedToken;
    }

    private object ExecutePaperActionRequest(string method, JsonElement parameters)
    {
        var actions = (IPaperPluginPaperActions)_workspace;
        if (method == "paperActions.clearAll") { actions.Clear(); return new { updated = 0 }; }
        var paperId = WebPluginRuntimeInfrastructure.RequiredString(parameters, "paperId");
        if (method == "paperActions.clear") { actions.Clear(paperId); return new { updated = 0 }; }
        PaperAction[] descriptors;
        try
        {
            descriptors = RequiredProperty(parameters, "actions").Deserialize<PaperAction[]>(WebPluginRuntimeInfrastructure.JsonOptions)
                ?? throw new JsonException("actions must be an array.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new PaperTodoPluginException("invalid_params", ex.GetBaseException().Message); }
        actions.SetActionHandler(invocation => Send(new { type = "paperActionInvoked", action = invocation }));
        actions.SetActions(paperId, descriptors);
        return new { updated = descriptors.Length };
    }

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
        var documentToken = _extensionDocumentToken;
        var replacement = _workspace.Subscribe(filter, value =>
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
        _hostSubscriptions.TryGetValue(id, out var existing);
        _hostSubscriptions[id] = replacement;
        existing?.Dispose();
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

    private void ResetRuntimeDocument()
    {
        _extensionDocumentToken = null;
        ClearHostSubscriptions();
        try { (_workspace as PaperPluginRuntimeWorkspaceApi)?.ResetExtensionUi(); } catch { }
    }
}
