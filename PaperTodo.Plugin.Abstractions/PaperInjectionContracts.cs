using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin;

/// <summary>A host-owned injection point. Defaults may describe a live calculation instead of a number.</summary>
public sealed record PaperInjectionParameterSnapshot
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("unit")] public string Unit { get; init; } = "";
    [JsonPropertyName("default_value")] public JsonElement? DefaultValue { get; init; }
    [JsonPropertyName("default_description")] public string? DefaultDescription { get; init; }
    /// <summary>live, next_use, or restart. Unknown file keys use unknown.</summary>
    [JsonPropertyName("application")] public string Application { get; init; } = "live";
    [JsonPropertyName("known")] public bool Known { get; init; } = true;
    [JsonPropertyName("has_file_override")] public bool HasFileOverride { get; init; }
    [JsonPropertyName("file_value")] public JsonElement? FileValue { get; init; }
    [JsonPropertyName("has_applied_override")] public bool HasAppliedOverride { get; init; }
    [JsonPropertyName("applied_value")] public JsonElement? AppliedValue { get; init; }
    /// <summary>default, pending, applied, or restart. A saved value is not automatically an applied value.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "default";
    [JsonPropertyName("recommended_minimum")] public double? RecommendedMinimum { get; init; }
    [JsonPropertyName("recommended_maximum")] public double? RecommendedMaximum { get; init; }
    [JsonPropertyName("outside_recommended_range")] public bool OutsideRecommendedRange { get; init; }
}

public sealed record PaperInjectionSnapshot
{
    [JsonPropertyName("file_path")] public required string FilePath { get; init; }
    /// <summary>Revision of the file observed by Read; supply it when submitting a local edit.</summary>
    [JsonPropertyName("revision")] public required string Revision { get; init; }
    [JsonPropertyName("overrides")] public required IReadOnlyDictionary<string, JsonElement> Overrides { get; init; }
    [JsonPropertyName("parameters")] public required IReadOnlyList<PaperInjectionParameterSnapshot> Parameters { get; init; }
}

/// <summary>Edits named keys; other entries, including unknown keys, remain intact unless ResetAll is set.</summary>
public sealed record PaperInjectionPatch
{
    [JsonPropertyName("expected_revision")] public string? ExpectedRevision { get; init; }
    [JsonPropertyName("set")] public IReadOnlyDictionary<string, JsonElement> Set { get; init; } = new Dictionary<string, JsonElement>();
    [JsonPropertyName("remove")] public IReadOnlyList<string> Remove { get; init; } = [];
    [JsonPropertyName("reset_all")] public bool ResetAll { get; init; }
}

public sealed record PaperInjectionApplyResult(
    [property: JsonPropertyName("snapshot")] PaperInjectionSnapshot Snapshot,
    [property: JsonPropertyName("changed_keys")] IReadOnlyList<string> ChangedKeys);

public sealed record PaperInjectionSuggestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] JsonElement Value,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// Optional, process-global user.json capability. Read/Suggest require settings.read;
/// Apply/Restart require settings.update. It never writes ordinary workspace settings.
/// </summary>
public interface IPaperInjectionApi
{
    PaperInjectionSnapshot Read();
    /// <summary>Null or an empty patch adopts the latest external file without rewriting it.</summary>
    PaperInjectionApplyResult Apply(PaperInjectionPatch? patch = null);
    /// <summary>
    /// Suggests only the named value without applying it. When supplied, draftOverrides is the
    /// complete proposed override document, including local removals; missing keys use defaults.
    /// </summary>
    PaperInjectionSuggestion? Suggest(string id, JsonElement value,
        IReadOnlyDictionary<string, JsonElement>? draftOverrides = null);
    /// <summary>
    /// Requests normal application shutdown/restart. A Body call returns to that paper;
    /// a provider Runtime call has no paper target.
    /// </summary>
    void Restart();
}
