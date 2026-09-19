using System.Text.Json;

namespace PaperTodo;

internal readonly record struct CodexMcpAccess(
    bool Enabled,
    bool BlankWrites,
    bool FullWrites,
    bool Deletes,
    bool SettingsControl);

internal static class CodexMcpPermission
{
    internal const string PluginId = "tools.codex-cli-bridge.native";

    internal static CodexMcpAccess FullAccess => new(
        Enabled: true,
        BlankWrites: true,
        FullWrites: true,
        Deletes: true,
        SettingsControl: true);

    // Settings are resolved by the host DataStore, including manifest defaults. Missing
    // settings (old plugin), invalid JSON and failed/inactive runtimes cannot grant access.
    internal static bool CanEnable(bool appRunning, bool pluginRunning, string settingsJson)
    {
        if (!appRunning || !pluginRunning) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("allowMcp", out var value) &&
                value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
