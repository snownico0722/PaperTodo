using System.Resources;

namespace PaperTodo;

internal static class TelemetryStrings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.TelemetryStrings", typeof(TelemetryStrings).Assembly);

    public static string Get(string key)
    {
        return Manager.GetString(key, UiLanguages.EffectiveUiCulture) ?? key;
    }
}
