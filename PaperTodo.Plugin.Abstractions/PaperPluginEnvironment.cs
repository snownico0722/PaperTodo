using System.Globalization;

namespace PaperTodo.Plugin;

/// <summary>
/// Read-only host environment shared by Native plugins. Changing the PaperTodo UI language takes
/// effect after restart, so the process-wide default UI culture is authoritative for this value.
/// </summary>
public static class PaperPluginEnvironment
{
    public static string UiLanguage =>
        CultureInfo.DefaultThreadCurrentUICulture?.Name ??
        CultureInfo.CurrentUICulture.Name;
}
