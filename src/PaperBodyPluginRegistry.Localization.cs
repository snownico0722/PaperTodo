using System.Globalization;

namespace PaperTodo;

internal sealed class PaperBodyPluginLocaleManifest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? SettingCategories { get; set; }
    public Dictionary<string, PaperBodyPluginSettingLocaleManifest>? Settings { get; set; }
    public PaperBodyPluginStartupLocaleManifest? StartupPaper { get; set; }
}

internal sealed class PaperBodyPluginSettingLocaleManifest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Suffix { get; set; }
    public string? Placeholder { get; set; }
    public Dictionary<string, string>? Options { get; set; }
}

internal sealed class PaperBodyPluginStartupLocaleManifest
{
    public string? Title { get; set; }
}

internal static class PaperBodyPluginLocalization
{
    internal static void ValidateAndApply(PaperBodyPluginManifest manifest, CultureInfo culture)
    {
        if (!TryResolve(manifest.Locales, culture, out var locale))
        {
            return;
        }

        if (locale.Name != null) manifest.Name = locale.Name.Trim();
        if (locale.Description != null) manifest.Description = locale.Description.Trim();

        ApplyCategories(manifest, locale.SettingCategories);

        foreach (var setting in manifest.Settings ?? [])
        {
            if (locale.Settings?.TryGetValue(setting.Id, out var localized) != true ||
                localized == null)
            {
                continue;
            }

            if (localized.Name != null) setting.Name = localized.Name.Trim();
            if (localized.Description != null) setting.Description = localized.Description.Trim();
            if (localized.Suffix != null) setting.Suffix = localized.Suffix.Trim();
            if (localized.Placeholder != null) setting.Placeholder = localized.Placeholder.Trim();

            foreach (var option in setting.Options ?? [])
            {
                if (localized.Options?.TryGetValue(option.Value, out var optionName) == true)
                {
                    option.Name = optionName.Trim();
                }
            }
        }

        if (locale.StartupPaper?.Title is { } title && manifest.StartupPaper != null)
        {
            manifest.StartupPaper.Title = title.Trim();
        }
    }

    private static void ApplyCategories(
        PaperBodyPluginManifest manifest,
        IReadOnlyDictionary<string, string>? labels)
    {
        if (labels == null)
        {
            return;
        }

        foreach (var category in manifest.SettingCategories ?? [])
        {
            if (labels.TryGetValue(category.Key, out var label))
            {
                category.Name = label.Trim();
            }
        }
        foreach (var setting in manifest.Settings ?? [])
        {
            if (labels.TryGetValue(setting.CategoryKey, out var label))
            {
                setting.Category = label.Trim();
            }
        }
    }

    private static bool TryResolve(
        IReadOnlyDictionary<string, PaperBodyPluginLocaleManifest>? locales,
        CultureInfo culture,
        out PaperBodyPluginLocaleManifest locale)
    {
        if (locales != null)
        {
            foreach (var name in LocaleCandidates(culture))
            {
                var match = locales.FirstOrDefault(item =>
                    string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Key) && match.Value != null)
                {
                    locale = match.Value;
                    return true;
                }
            }
        }

        locale = null!;
        return false;
    }

    private static IEnumerable<string> LocaleCandidates(CultureInfo culture)
    {
        if (!string.IsNullOrWhiteSpace(culture.Name))
        {
            yield return culture.Name;
        }
        if (culture.Parent != CultureInfo.InvariantCulture &&
            !string.IsNullOrWhiteSpace(culture.Parent.Name))
        {
            yield return culture.Parent.Name;
        }
        if (!string.IsNullOrWhiteSpace(culture.TwoLetterISOLanguageName) &&
            !string.Equals(culture.TwoLetterISOLanguageName, "iv", StringComparison.OrdinalIgnoreCase))
        {
            yield return culture.TwoLetterISOLanguageName;
        }
    }
}
