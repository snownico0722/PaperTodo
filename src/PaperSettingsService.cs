using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PaperSettingsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// A setting may use the core StateStore or an existing independent owner (startup registry /
// paper-background preferences). It never introduces a second writer for either data domain.
internal sealed record PaperSettingChange(Action Apply, Action Rollback, Action Publish,
    Func<bool>? Commit = null);

internal sealed class PaperSettingDefinition
{
    internal required PaperSettingSnapshot Metadata { get; init; }
    internal required Func<JsonElement> Read { get; init; }
    internal required Func<JsonElement, JsonElement> Validate { get; init; }
    internal required Func<JsonElement, PaperSettingChange> Begin { get; set; }
    internal Func<string?>? Unavailable { get; init; }

    internal PaperSettingSnapshot Snapshot()
    {
        var reason = Unavailable?.Invoke();
        return Metadata with { Value = Read().Clone(), Writable = Metadata.Writable && reason == null,
            UnavailableReason = reason };
    }
}

/// <summary>
/// Shared Native/Web/MCP application-settings boundary. Adapters authorize; this service validates,
/// applies one named setting, persists synchronously, rolls back failed writes, and only then
/// publishes UI/runtime changes. AppState property names and reflection are not part of the API.
/// </summary>
internal sealed class PaperSettingsService
{
    private readonly Dictionary<string, PaperSettingDefinition> _definitions;
    private readonly Func<bool> _running;
    private readonly Func<bool> _commit;
    private readonly Action<Action> _publish;
    // Saving calls live provider sessions; do not allow a provider to nest a second transaction.
    private bool _changing;

    internal PaperSettingsService(IEnumerable<PaperSettingDefinition> definitions, Func<bool> running,
        Func<bool> commit, Action<Action> publish)
    {
        _definitions = definitions.ToDictionary(d => d.Metadata.Id, StringComparer.Ordinal);
        _running = running;
        _commit = commit;
        _publish = publish;
    }

    internal IReadOnlyList<PaperSettingSnapshot> List(string? category = null)
    {
        EnsureRunning();
        return _definitions.Values.Where(d => string.IsNullOrEmpty(category) ||
            string.Equals(d.Metadata.Category, category, StringComparison.Ordinal))
            .Select(d => d.Snapshot()).ToArray();
    }

    internal PaperSettingSnapshot Get(string id)
    {
        EnsureRunning();
        return Definition(id).Snapshot();
    }

    internal PaperSettingChangeResult Set(string id, JsonElement value)
    {
        EnsureRunning();
        if (_changing) throw Error("settings_busy", "Another settings change is being committed.");
        var definition = Definition(id);
        var before = definition.Snapshot();
        if (!before.Writable)
            throw Error("setting_read_only", before.UnavailableReason ?? "This setting is read-only.");
        // Validate once before changing state; the persistence owner synchronizes content when saving.
        var normalized = definition.Validate(value);
        if (JsonElement.DeepEquals(before.Value, normalized))
            return new PaperSettingChangeResult(before, before.Value, false);
        _changing = true;
        try
        {
            var change = definition.Begin(normalized);
            try
            {
                change.Apply();
                if (!(change.Commit ?? _commit)())
                    throw Error("save_failed", "PaperTodo could not save the setting; the previous value was restored.");
            }
            catch
            {
                change.Rollback();
                throw;
            }
            // Post-commit failures must not pretend the persisted mutation failed and invite replay.
            _publish(change.Publish);
            return new PaperSettingChangeResult(definition.Snapshot(), before.Value, true);
        }
        finally { _changing = false; }
    }

    private PaperSettingDefinition Definition(string id)
        => id != null && _definitions.TryGetValue(id, out var definition)
            ? definition : throw Error("setting_not_found", "Unknown public setting ID. Call list_settings first.");

    private void EnsureRunning()
    {
        if (!_running()) throw Error("app_exiting", "PaperTodo is exiting.");
    }

    internal static PaperSettingsException Error(string code, string message) => new(code, message);

    // This reports this caller's actual ability, not merely the catalog's global writability.
    internal static PaperSettingSnapshot WithAccess(PaperSettingSnapshot value, bool update, bool control)
        => value with
        {
            Writable = value.Writable && update && (!value.Sensitive || control),
            UnavailableReason = value.UnavailableReason ?? (!update ? "settings_update_required" :
                value.Sensitive && !control ? "settings_control_required" : null)
        };
}
