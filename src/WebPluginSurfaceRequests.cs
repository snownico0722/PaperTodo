using System.IO;
using System.Text;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>Thin transport adapter. Entry paths are relative to the plugin's existing Web root.</summary>
internal static class WebPluginSurfaceRequests
{
    internal static object Execute(IPaperPluginSurfaces surfaces, IPaperTodoHostApi workspace,
        PaperBodyPluginManifest manifest, string method, JsonElement parameters, Action<object> send)
    {
        if (method == "surfaces.close")
        {
            surfaces.Close(WebPluginRuntimeInfrastructure.RequiredString(parameters, "id"));
            return new { closed = true };
        }
        if (method == "surfaces.closeAll")
        {
            surfaces.CloseAll();
            return new { closed = true };
        }
        if (method is not ("surfaces.openWindow" or "surfaces.openPopup"))
            throw new PaperTodoPluginException("method_not_found", "Unknown surface operation.");
        try
        {
            var entry = WebPluginRuntimeInfrastructure.RequiredString(parameters, "entry");
            var webRoot = Path.GetDirectoryName(manifest.EntryPath)!;
            var entryPath = ResolveEntry(webRoot, entry);
            var initialData = parameters.TryGetProperty("data", out var data) ? data.Clone() :
                JsonSerializer.SerializeToElement<object?>(null);
            ValidateMessage(initialData);
            var id = WebPluginRuntimeInfrastructure.RequiredString(parameters, "id");
            IPaperPluginSurfaceContent Create(PaperPluginSurfaceContext context) =>
                new WebPluginSurfaceContent(manifest, entryPath, workspace, context, initialData,
                    message => send(new { type = "surfaceMessage", surfaceId = id, message }),
                    error => send(new { type = "surfaceError", surfaceId = id, code = "web_surface_failed", message = error }));
            var options = parameters.Deserialize<PaperPluginWindowOptions>(WebPluginRuntimeInfrastructure.JsonOptions)!;
            var handle = method == "surfaces.openWindow"
                ? surfaces.OpenWindow(options, Create)
                : surfaces.OpenPopup(options.Anchor ?? throw new PaperTodoPluginException("anchor_unavailable", "An anchor is required."),
                    parameters.Deserialize<PaperPluginPopupOptions>(WebPluginRuntimeInfrastructure.JsonOptions)!, Create);
            return new { id = handle.Id, isOpen = handle.IsOpen };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new PaperTodoPluginException("invalid_params", ex.GetBaseException().Message);
        }
    }

    internal static string ResolveEntry(string webRoot, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) ||
            entry.IndexOfAny([':', '?', '#', '\0']) >= 0)
            throw new PaperTodoPluginException("invalid_surface_entry", "Use a relative local HTML entry path.");
        var root = Path.GetFullPath(webRoot);
        var path = Path.GetFullPath(Path.Combine(root, entry));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) || !File.Exists(path) ||
            Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm"))
            throw new PaperTodoPluginException("invalid_surface_entry", "The HTML entry must exist inside the plugin Web root.");
        return path;
    }

    internal static void ValidateMessage(JsonElement message)
    {
        if (Encoding.UTF8.GetByteCount(message.GetRawText()) > 64 * 1024)
            throw new PaperTodoPluginException("surface_message_too_large", "Surface data/messages are limited to 64 KiB UTF-8.");
    }
}
