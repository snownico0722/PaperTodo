using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class WebPluginSettingsRequests
{
    internal static object Execute(IPaperTodoHostApi host, string method, JsonElement parameters)
    {
        var api = host as IPaperSettingsApi
            ?? throw new PaperTodoPluginException("settings_unavailable", "Application settings are unavailable.");
        string? Text(string name, bool required = true)
        {
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && (!required || !string.IsNullOrEmpty(v.GetString())))
                    return v.GetString();
                if (!required && v.ValueKind == JsonValueKind.Null) return null;
            }
            else if (!required) return null;
            throw new PaperTodoPluginException("invalid_params", $"{name} must be a string.");
        }
        switch (method)
        {
            case "appSettings.list": return api.List(Text("category", required: false));
            case "appSettings.get": return api.Get(Text("id")!);
            case "appSettings.set":
                var id = Text("id")!;
                if (!parameters.TryGetProperty("value", out var value))
                    throw new PaperTodoPluginException("invalid_params", "value is required.");
                return api.Set(id, value);
            default: throw new PaperTodoPluginException("method_not_found", "Unknown application settings method.");
        }
    }
}
