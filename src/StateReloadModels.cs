using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>Copies the validated snapshot into the existing model identities held by windows.</summary>
internal static class StateReloadModels
{
    private static readonly PropertyInfo[] AppProperties = Properties<AppState>(nameof(AppState.Papers));
    private static readonly PropertyInfo[] PaperProperties = Properties<PaperData>(nameof(PaperData.Items));
    private static readonly PropertyInfo[] ItemProperties = Properties<PaperItem>();

    private static PropertyInfo[] Properties<T>(params string[] excluded) => typeof(T)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.GetMethod != null && p.SetMethod?.IsPublic == true && !excluded.Contains(p.Name))
        .ToArray();

    internal static void Apply(AppState current, AppState next)
    {
        // Keep the committed comparison snapshot immutable, including dictionaries/new papers.
        next = StateStore.CopyForReload(next);
        Copy(AppProperties, next, current);
        var existing = current.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var papers = new List<PaperData>(next.Papers.Count);
        foreach (var source in next.Papers)
        {
            if (!existing.TryGetValue(source.Id, out var target)) target = source;
            else
            {
                Copy(PaperProperties, source, target);
                var items = target.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
                var ordered = new List<PaperItem>(source.Items.Count);
                foreach (var item in source.Items)
                {
                    if (!items.TryGetValue(item.Id, out var live)) live = item;
                    else
                    {
                        Copy(ItemProperties, item, live);
                        live.RestoreQuickLaunch(item.LinkedPaperId, item.LinkedPath, item.LinkedPathIsDirectory);
                    }
                    ordered.Add(live);
                }
                target.Items.Clear();
                target.Items.AddRange(ordered);
            }
            papers.Add(target);
        }
        current.Papers.Clear();
        current.Papers.AddRange(papers);
    }

    private static void Copy(IEnumerable<PropertyInfo> properties, object source, object target)
    {
        foreach (var property in properties) property.SetValue(target, property.GetValue(source));
    }

    internal static bool SettingsChanged(AppState before, AppState after) =>
        AppProperties.Any(p => !Equivalent(p.GetValue(before), p.GetValue(after)));

    internal static bool Equivalent<T>(T before, T after) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(before), JsonSerializer.SerializeToNode(after));
}
