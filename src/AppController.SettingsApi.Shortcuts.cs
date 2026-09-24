using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private IEnumerable<PaperSettingDefinition> CreateShortcutSettingsCatalog()
    {
        foreach (var command in GlobalShortcutCatalog.Definitions.Where(d => !d.IsEdgeCapsule || d.EdgeOrdinal == 1))
        {
            var id = command.IsEdgeCapsule
                ? (command.Group == GlobalShortcutGroup.EdgeLeft ? "edge_left" : "edge_right") : command.Id;
            var description = command.IsEdgeCapsule
                ? "One sequence per edge: a 2-3 modifier chord plus 1. Changing its modifiers updates keys 1-9 together."
                : "A modifier chord and key, for example Ctrl+Alt+K; empty removes the binding. Enabling is separate.";
            yield return new PaperSettingDefinition
            {
                Metadata = new PaperSettingSnapshot
                {
                    Id = "shortcuts." + id + ".gesture", Category = "shortcuts",
                    Title = Strings.Get(command.LabelKey), Description = description, Type = "string",
                    Value = JsonSerializer.SerializeToElement(""), Writable = true, MaxLength = 80
                },
                Read = () => JsonSerializer.SerializeToElement(GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys)[command.Id]),
                Unavailable = PublicShortcutUnavailable,
                Validate = value =>
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 80 ||
                        !ShortcutGesture.TryParse(value.GetString(), out var gesture))
                        throw PaperSettingsService.Error("invalid_setting_value", "Invalid shortcut gesture.");
                    if (command.IsEdgeCapsule && (!ShortcutGesture.HasEdgePrefixModifiers(gesture.Modifiers) ||
                        !ShortcutGesture.IsEdgeOrdinalKey(gesture.Key, 1)))
                        throw PaperSettingsService.Error("invalid_setting_value", "Use a 2-3 modifier chord plus 1 for an edge sequence.");
                    return JsonSerializer.SerializeToElement(gesture.ToStorageString());
                },
                Begin = value =>
                {
                    var bindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
                    var gestureText = value.GetString()!;
                    if (command.IsEdgeCapsule)
                    {
                        ShortcutGesture.TryParse(gestureText, out var gesture);
                        foreach (var member in GlobalShortcutCatalog.DefinitionsInGroup(command.Group))
                            bindings[member.Id] = ShortcutGesture.ForEdgeOrdinal(gesture.Modifiers, member.EdgeOrdinal).ToStorageString();
                    }
                    else bindings[command.Id] = gestureText;
                    return BeginPublicShortcutChange(bindings, null, null);
                }
            };
            yield return new PaperSettingDefinition
            {
                Metadata = new PaperSettingSnapshot
                {
                    Id = "shortcuts." + id + ".enabled", Category = "shortcuts",
                    Title = Strings.Get(command.LabelKey), Description = "Enable or disable this shortcut/edge sequence.",
                    Type = "boolean", Value = JsonSerializer.SerializeToElement(false), Writable = true
                },
                Read = () => JsonSerializer.SerializeToElement(GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled)[command.Id]),
                Unavailable = PublicShortcutUnavailable,
                Validate = value => ValidatePublicSetting(value, "boolean", null, null, null, null, ""),
                Begin = value =>
                {
                    var enabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
                    foreach (var member in command.IsEdgeCapsule
                        ? GlobalShortcutCatalog.DefinitionsInGroup(command.Group) : new[] { command })
                        enabled[member.Id] = value.GetBoolean();
                    return BeginPublicShortcutChange(null, enabled, null);
                }
            };
        }
    }

    private string? PublicShortcutUnavailable()
    {
        if (_shortcutRecordingCommandId != null || _pluginShortcutRecordingCommandId != null)
            return "Finish shortcut recording before changing shortcuts through the API.";
        if (_shortcutDraft != null && !SameMap(_shortcutDraft, GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys)) ||
            _shortcutEnabledDraft != null && !SameMap(_shortcutEnabledDraft, GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled)))
            return "Apply or discard the shortcut editor draft first.";
        return null;
    }

    private static bool SameMap<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && EqualityComparer<T>.Default.Equals(p.Value, v));

    private PaperSettingChange BeginPublicShortcutChange(Dictionary<string, string>? newBindings,
        Dictionary<string, bool>? newEnabled, bool? newMode)
    {
        var oldBindings = GlobalShortcutCatalog.NormalizeBindings(State.GlobalHotkeys);
        var oldEnabled = GlobalShortcutCatalog.NormalizeEnabled(State.GlobalHotkeyEnabled);
        var oldMode = State.DistinguishNumpadShortcutDigits;
        var bindings = newBindings ?? oldBindings;
        var enabled = newEnabled ?? oldEnabled;
        var mode = newMode ?? oldMode;
        if (!mode && NumpadEquivalentConflictIds(bindings, enabled).Count > 0)
            throw PaperSettingsService.Error("shortcut_conflict", "These digit shortcuts require distinguish_numpad.");
        var active = GlobalShortcutCatalog.ExecutableIds.Where(id => enabled.GetValueOrDefault(id)).ToArray();
        var oldActive = GlobalShortcutCatalog.ExecutableIds.Where(id => oldEnabled.GetValueOrDefault(id)).ToArray();
        var applied = false;
        return new PaperSettingChange(
            () =>
            {
                if (newMode.HasValue) SuspendPluginShortcutRegistrations();
                if (!EnsureGlobalHotkeyManager().TryApply(bindings, active, mode, out var failed, out var failure))
                    throw PaperSettingsService.Error("shortcut_conflict", $"Could not register shortcut {failed}: {failure}.");
                applied = true;
                State.GlobalHotkeys = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
                State.GlobalHotkeyEnabled = new Dictionary<string, bool>(enabled, StringComparer.Ordinal);
                State.DistinguishNumpadShortcutDigits = mode;
            },
            () =>
            {
                State.GlobalHotkeys = oldBindings;
                State.GlobalHotkeyEnabled = oldEnabled;
                State.DistinguishNumpadShortcutDigits = oldMode;
                if (applied && !EnsureGlobalHotkeyManager().TryApply(oldBindings, oldActive, oldMode, out var failed, out var failure))
                    Trace.TraceError("Could not restore shortcut registration {0}: {1}", failed, failure);
                if (newMode.HasValue) RefreshPluginShortcuts();
            },
            () =>
            {
                foreach (var command in GlobalShortcutCatalog.Definitions.Where(d => d.Group == GlobalShortcutGroup.Labs))
                    if (oldEnabled.GetValueOrDefault(command.Id) != enabled.GetValueOrDefault(command.Id))
                        HandleExperimentalShortcutFeatureChanged(command, enabled.GetValueOrDefault(command.Id));
                ResetShortcutDraftToSavedState();
                ClearShortcutApplyFailure();
                RefreshPluginShortcuts();
                RefreshSettingsForChange("shortcuts.bindings");
            });
    }
}
