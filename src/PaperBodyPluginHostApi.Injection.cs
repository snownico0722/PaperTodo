using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginHostApi
{
    PaperInjectionSnapshot IPaperInjectionApi.Read() => PopupOnUi(() =>
    {
        Require(PaperTodoPermissionNames.SettingsRead);
        return InjectionCall(UserInjection.Read);
    });

    PaperInjectionApplyResult IPaperInjectionApi.Apply(PaperInjectionPatch? patch)
    {
        var copy = patch == null ? null : patch with
        {
            Set = patch.Set.ToDictionary(pair => pair.Key, pair => CloneInjectionValue(pair.Value),
                StringComparer.Ordinal),
            Remove = patch.Remove.ToArray()
        };
        return PopupOnUi(() =>
        {
            Require(PaperTodoPermissionNames.SettingsUpdate);
            return InjectionCall(() => _controller.ApplyUserInjection(copy));
        });
    }

    PaperInjectionSuggestion? IPaperInjectionApi.Suggest(string id, JsonElement value,
        IReadOnlyDictionary<string, JsonElement>? draftOverrides)
    {
        var copy = CloneInjectionValue(value);
        var drafts = draftOverrides?.ToDictionary(pair => pair.Key,
            pair => CloneInjectionValue(pair.Value), StringComparer.Ordinal);
        return PopupOnUi(() =>
        {
            Require(PaperTodoPermissionNames.SettingsRead);
            return InjectionCall(() => UserInjection.Suggest(id, copy, drafts));
        });
    }

    void IPaperInjectionApi.Restart() => PopupOnUi(() =>
    {
        Require(PaperTodoPermissionNames.SettingsUpdate);
        return InjectionCall(() =>
        {
            _controller.RestartForUserInjection(_hostPaperId);
            return true;
        });
    });

    private static JsonElement CloneInjectionValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? value : value.Clone();

    private static T InjectionCall<T>(Func<T> action)
    {
        try { return action(); }
        catch (UserInjectionException ex) { throw new PaperTodoPluginException(ex.Code, ex.Message); }
    }
}
