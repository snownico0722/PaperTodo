using System.Text.Json;

namespace PaperTodo.Plugin.CodexCliBridge;

internal sealed record CodexBridgeSettings(
    string CodexPath,
    string WorkingDirectory,
    string Model,
    string ReasoningEffort,
    bool BackgroundExecution,
    bool EnablePluginSkill = true,
    bool EnableOperationSkill = true,
    bool AllowMcp = true)
{
    internal static CodexBridgeSettings Read(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        return new CodexBridgeSettings(
            Text(root, "codexPath", "codex", allowEmpty: false),
            Text(root, "workingDirectory", string.Empty, allowEmpty: true),
            Text(root, "model", "gpt-6-astra", allowEmpty: true),
            Text(root, "reasoningEffort", "xhigh", allowEmpty: true),
            Bool(root, "backgroundExecution", fallback: false),
            Bool(root, "enablePluginSkill", fallback: true),
            Bool(root, "enableOperationSkill", fallback: true),
            Bool(root, "allowMcp", fallback: true));
    }

    private static string Text(JsonElement root, string name, string fallback, bool allowEmpty)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;
        var text = (value.GetString() ?? string.Empty).Trim();
        return allowEmpty || !string.IsNullOrWhiteSpace(text) ? text : fallback;
    }

    private static bool Bool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        // Malformed permission values must never turn a saved opt-out into an opt-in.
        return value.ValueKind == JsonValueKind.True;
    }
}
