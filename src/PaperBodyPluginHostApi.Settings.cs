using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi : IPaperSettingsApi
{
    IReadOnlyList<PaperSettingSnapshot> IPaperSettingsApi.List(string? category) => PopupOnUi(() =>
    {
        Require(PaperTodoPermissionNames.SettingsRead);
        return SettingsCall(() => _controller.PublicSettings.List(category).Select(SettingsAccess).ToArray());
    });

    PaperSettingSnapshot IPaperSettingsApi.Get(string id) => PopupOnUi(() =>
    {
        Require(PaperTodoPermissionNames.SettingsRead);
        return SettingsAccess(SettingsCall(() => _controller.PublicSettings.Get(id)));
    });

    PaperSettingChangeResult IPaperSettingsApi.Set(string id, JsonElement value)
    {
        var copy = value.ValueKind == JsonValueKind.Undefined ? value : value.Clone();
        return PopupOnUi(() =>
        {
            Require(PaperTodoPermissionNames.SettingsUpdate);
            return SettingsCall(() =>
            {
                var result = _controller.PublicSettings.Set(id, copy);
                return result with { Setting = SettingsAccess(result.Setting) };
            });
        });
    }

    private PaperSettingSnapshot SettingsAccess(PaperSettingSnapshot setting) =>
        PaperSettingsService.WithAccess(setting,
            _permissions.Contains(PaperTodoPermissionNames.SettingsUpdate));

    private static T SettingsCall<T>(Func<T> action)
    {
        try { return action(); }
        catch (PaperSettingsException ex) { throw new PaperTodoPluginException(ex.Code, ex.Message); }
    }
}
