using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin;

/// <summary>A public application setting, not a provider's own Settings/State or a raw AppState field.</summary>
public sealed record PaperSettingSnapshot
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("value")] public required JsonElement Value { get; init; }
    [JsonPropertyName("writable")] public bool Writable { get; init; }
    [JsonPropertyName("requires_restart")] public bool RequiresRestart { get; init; }
    [JsonPropertyName("unavailable_reason")] public string? UnavailableReason { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("step")] public double? Step { get; init; }
    [JsonPropertyName("max_length")] public int? MaxLength { get; init; }
    [JsonPropertyName("options")] public IReadOnlyList<string> Options { get; init; } = [];
}

public sealed record PaperSettingChangeResult(
    [property: JsonPropertyName("setting")] PaperSettingSnapshot Setting,
    [property: JsonPropertyName("previous_value")] JsonElement PreviousValue,
    [property: JsonPropertyName("changed")] bool Changed);

/// <summary>
/// Protocol 2.2 optional application Settings capability. List/Get need settings.read; Set needs settings.update.
/// IDs and schemas come from List, not CLR field names.
/// This is distinct from context.Settings/SettingsJson, which belongs to the plugin itself.
/// </summary>
public interface IPaperSettingsApi
{
    IReadOnlyList<PaperSettingSnapshot> List(string? category = null);
    PaperSettingSnapshot Get(string id);
    PaperSettingChangeResult Set(string id, JsonElement value);
}

public static class PaperSettingsApiExtensions
{
    public static PaperSettingChangeResult Set<T>(this IPaperSettingsApi api, string id, T value)
        => api.Set(id, JsonSerializer.SerializeToElement(value));
}
