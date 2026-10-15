using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static partial class PaperBodyPluginLocalization
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
}
