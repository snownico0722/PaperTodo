using System.Resources;

namespace PaperTodo;

public static class Strings
{
    private static readonly ResourceManager Manager = new("PaperTodo.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
    {
        return Manager.GetString(key, UiLanguages.EffectiveUiCulture) ?? key;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(UiLanguages.EffectiveCulture, Get(key), args);
    }
}
