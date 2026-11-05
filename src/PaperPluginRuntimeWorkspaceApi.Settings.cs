using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperPluginRuntimeWorkspaceApi : IPaperSettingsApi
{
    IReadOnlyList<PaperSettingSnapshot> IPaperSettingsApi.List(string? category) =>
        OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).List(category); });
    PaperSettingSnapshot IPaperSettingsApi.Get(string id) =>
        OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).Get(id); });
    PaperSettingChangeResult IPaperSettingsApi.Set(string id, JsonElement value)
    {
        var copy = value.ValueKind == JsonValueKind.Undefined ? value : value.Clone();
        return OnUi(() => { EnsureUsable(); return ((IPaperSettingsApi)_inner).Set(id, copy); });
    }
}
