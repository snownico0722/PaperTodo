using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class PluginLocalizationChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var host = Assembly.Load("PaperTodo");
        CheckLocalizedOverlay(host);
        CheckBaseFallback(host);
        Console.WriteLine("PaperTodo plugin localization checks passed.");
    }

    private static void CheckLocalizedOverlay(Assembly host)
    {
        const string json = """
        {
          "name": "基础名称",
          "settings": [
            {
              "id": "mode",
              "type": "select",
              "name": "模式",
              "category": "general",
              "options": [{ "value": "compact", "name": "紧凑" }]
            }
          ],
          "advancedSettings": true,
          "settingCategories": [{ "name": "general" }],
          "locales": {
            "en": {
              "name": "Localized name",
              "settingCategories": { "general": "General" },
              "settings": {
                "mode": {
                  "name": "Mode",
                  "options": { "compact": "Compact" }
                }
              }
            },
            "not-a-real-culture": {
              "settings": { "missing": { "name": "Ignored" } }
            }
          }
        }
        """;

        var manifest = Prepare(host, json, CultureInfo.GetCultureInfo("en-GB"));
        Assert(StringProperty(manifest, "Name") == "Localized name", "Language fallback failed.");

        var setting = First(manifest, "Settings");
        Assert(StringProperty(setting, "Id") == "mode", "Localization changed a setting id.");
        Assert(StringProperty(setting, "Name") == "Mode", "Setting name was not localized.");
        Assert(StringProperty(setting, "Category") == "General", "Category was not localized.");

        var option = First(setting, "Options");
        Assert(StringProperty(option, "Value") == "compact", "Localization changed an option value.");
        Assert(StringProperty(option, "Name") == "Compact", "Option name was not localized.");
    }

    private static void CheckBaseFallback(Assembly host)
    {
        const string json = """
        {
          "name": "Base",
          "settings": [{ "id": "enabled", "type": "boolean", "name": "Enabled" }],
          "locales": { "ja-JP": { "name": "日本語" } }
        }
        """;

        var manifest = Prepare(host, json, CultureInfo.GetCultureInfo("fr-FR"));
        Assert(StringProperty(manifest, "Name") == "Base", "Missing locale must use base text.");
        Assert(StringProperty(First(manifest, "Settings"), "Name") == "Enabled",
            "Missing locale changed base setting text.");
    }

    private static object Prepare(Assembly host, string json, CultureInfo culture)
    {
        var manifestType = host.GetType("PaperTodo.PaperBodyPluginManifest", true)!;
        var manifest = JsonSerializer.Deserialize(
            json,
            manifestType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var registry = host.GetType("PaperTodo.PaperBodyPluginRegistry", true)!;
        registry.GetMethod("ValidateSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [manifest]);

        host.GetType("PaperTodo.PaperBodyPluginLocalization", true)!
            .GetMethod("ValidateAndApply", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [manifest, culture]);
        return manifest;
    }

    private static object First(object owner, string propertyName) =>
        ((Array)Property(owner, propertyName)!).GetValue(0)!;

    private static string StringProperty(object owner, string propertyName) =>
        Property(owner, propertyName) as string ?? string.Empty;

    private static object? Property(object owner, string propertyName) =>
        owner.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(owner);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
