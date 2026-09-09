using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPluginRuntime
{
    private string? _extensionDocumentToken;

    internal static bool AcceptsExtensionRequest(JsonElement root, JsonElement payload, string? expectedToken)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("method", out var methodValue) ||
            methodValue.ValueKind != JsonValueKind.String) return true;
        var method = methodValue.GetString()!;
        if (!method.StartsWith("paperActions.", StringComparison.Ordinal) &&
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
    private void TryResetExtensionUi()
    {
        _extensionDocumentToken = null;
        try { (_workspace as PaperPluginRuntimeWorkspaceApi)?.ResetExtensionUi(); } catch { }
    }
}
