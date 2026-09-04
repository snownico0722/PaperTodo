using System.Resources;

namespace PaperTodo;

internal static class InteractionStrings
{
    private static readonly ResourceManager Manager = new(
        "PaperTodo.Resources.InteractionStrings", typeof(InteractionStrings).Assembly);

    internal static string Get(string key) =>
        Manager.GetString(key, UiLanguages.EffectiveUiCulture) ?? key;

    internal static string Format(string key, params object[] args) =>
        string.Format(UiLanguages.EffectiveCulture, Get(key), args);
}
