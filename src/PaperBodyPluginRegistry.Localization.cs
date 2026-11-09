using System.Globalization;
using System.Runtime.CompilerServices;

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
    // Preserve the discovered source labels, not a reread of plugin.json. Re-localizing must
    // neither rediscover code nor carry a previous locale's missing-key fallback into the next.
    private static readonly ConditionalWeakTable<PaperBodyPluginManifest, SourceLabels> OriginalLabels = new();

    private sealed class SourceLabels
    {
        private readonly List<Action> _restore = new();

        internal SourceLabels(PaperBodyPluginManifest manifest)
        {
            var name = manifest.Name;
            var description = manifest.Description;
            _restore.Add(() => { manifest.Name = name; manifest.Description = description; });
            foreach (var category in manifest.SettingCategories ?? [])
            {
                var original = category.Name;
                _restore.Add(() => category.Name = original);
            }
            foreach (var setting in manifest.Settings ?? [])
            {
                var title = setting.Name;
                var detail = setting.Description;
                var suffix = setting.Suffix;
                var placeholder = setting.Placeholder;
                var group = setting.Category;
                _restore.Add(() =>
                {
                    setting.Name = title;
                    setting.Description = detail;
                    setting.Suffix = suffix;
                    setting.Placeholder = placeholder;
                    setting.Category = group;
                });
                foreach (var option in setting.Options ?? [])
                {
                    var original = option.Name;
                    _restore.Add(() => option.Name = original);
                }
            }
            if (manifest.StartupPaper is { } startup)
            {
                var title = startup.Title;
                _restore.Add(() => startup.Title = title);
            }
        }

        internal void Restore() { foreach (var action in _restore) action(); }
    }

    private static void RestoreDiscoveredLabels(PaperBodyPluginManifest manifest) =>
        OriginalLabels.GetValue(manifest, static source => new SourceLabels(source)).Restore();

    internal static void ValidateAndApply(PaperBodyPluginManifest manifest, CultureInfo culture)
    {
        RestoreDiscoveredLabels(manifest);
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

internal sealed partial class PaperBodyPluginRegistry
{
    internal void RefreshWorkspaceLocalization()
    {
        foreach (var (id, descriptor) in _descriptors.ToArray())
        {
            if (descriptor.Manifest is not { } manifest) continue;
            PaperBodyPluginLocalization.ValidateAndApply(manifest, UiLanguages.EffectiveUiCulture);
            var next = descriptor with
            {
                DisplayName = string.IsNullOrWhiteSpace(manifest.Name) ? id : manifest.Name.Trim(),
                Description = manifest.Description?.Trim() ?? ""
            };
            _descriptors[id] = next;
            if (_loadedNativeByDirectory.TryGetValue(descriptor.PluginDirectory, out var loaded))
                _loadedNativeByDirectory[descriptor.PluginDirectory] = loaded with { Descriptor = next };
        }
    }
}
