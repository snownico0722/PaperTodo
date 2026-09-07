using System.IO;
using System.Text;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class WebPluginPopupRequests
{
    internal static object Execute(IPaperPluginPopups popups, IPaperTodoHostApi workspace,
        PaperBodyPluginManifest manifest, string method, JsonElement parameters, Action<object> send)
    {
        if (method == "popups.close") { popups.Close(); return new { closed = true }; }
        if (method != "popups.open")
            throw new PaperTodoPluginException("method_not_found", "Unknown popup operation.");
        try
        {
            var entry = WebPluginRuntimeInfrastructure.RequiredString(parameters, "entry");
            var entryPath = ResolveEntry(Path.GetDirectoryName(manifest.EntryPath)!, entry);
            var data = parameters.TryGetProperty("data", out var value) ? value.Clone() :
                JsonSerializer.SerializeToElement<object?>(null);
            ValidateMessage(data);
            var options = parameters.Deserialize<PaperPluginPopupOptions>(WebPluginRuntimeInfrastructure.JsonOptions)!;
            var position = parameters.GetProperty("position").Deserialize<PaperPopupPosition>(WebPluginRuntimeInfrastructure.JsonOptions)
                ?? throw new PaperTodoPluginException("invalid_popup_position", "A click position is required.");
            var popup = popups.Open(position, options, context => new WebPluginPopupContent(
                manifest, entryPath, workspace, context, data,
                message => send(new { type = "popupMessage", message }),
                error => send(new { type = "popupError", code = "web_popup_failed", message = error })));
            return new { isOpen = popup.IsOpen };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or KeyNotFoundException)
        {
            throw new PaperTodoPluginException("invalid_params", ex.GetBaseException().Message);
        }
    }

    internal static string ResolveEntry(string webRoot, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) ||
            entry.IndexOfAny([':', '?', '#', '\0']) >= 0)
            throw new PaperTodoPluginException("invalid_popup_entry", "Use a relative local HTML entry path.");
        var root = Path.GetFullPath(webRoot);
        var path = Path.GetFullPath(Path.Combine(root, entry));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) || !File.Exists(path) ||
            Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm"))
            throw new PaperTodoPluginException("invalid_popup_entry", "The HTML entry must exist inside the plugin Web root.");
        return path;
    }

    internal static void ValidateMessage(JsonElement message)
    {
        if (Encoding.UTF8.GetByteCount(message.GetRawText()) > 64 * 1024)
            throw new PaperTodoPluginException("popup_message_too_large", "Popup data/messages are limited to 64 KiB UTF-8.");
    }
}
