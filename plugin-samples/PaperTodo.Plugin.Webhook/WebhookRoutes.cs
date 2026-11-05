using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.Webhook;

internal static class WebhookRoutes
{
    public static string EventName(PaperTodoEventKind kind) =>
        JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public static bool TryRead(string json, out Dictionary<string, Uri> routes, out string? error)
    {
        routes = new(StringComparer.OrdinalIgnoreCase);
        error = null;
        try
        {
            using var settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (settings.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Settings must be an object.");
            if (!settings.RootElement.TryGetProperty("routes", out var field) ||
                field.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(field.GetString()))
                return true;
            if (field.ValueKind != JsonValueKind.String)
                throw new FormatException("Routes must be JSON text.");
            using var document = JsonDocument.Parse(field.GetString()!);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Routes must be a JSON object.");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(property.Value.GetString()?.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new FormatException($"Invalid HTTP(S) URL for event '{property.Name}'.");
                if (uri.UserInfo.Length > 0)
                    throw new FormatException($"Webhook URL for event '{property.Name}' must not contain a username or password.");
                if (string.IsNullOrWhiteSpace(property.Name) || !routes.TryAdd(property.Name, uri))
                    throw new FormatException("Event names must be nonempty and unique.");
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            routes.Clear();
            error = ex.Message;
            return false;
        }
    }
}
