using System.Text.Json;

namespace PaperTodo;

/// <summary>Ownership and migration rules shared by the live workspace and read-only snapshots.</summary>
internal static class WorkspaceMaterials
{
    internal const string DefaultTitle = "灵感空间素材";

    internal static PaperData? FindOwner(AppState state) =>
        state.Papers.FirstOrDefault(p => p.WorkspaceMaterials != null);

    internal static bool IsMaterialId(string? id) =>
        id?.StartsWith(WorkspaceMaterialItem.IdPrefix, StringComparison.Ordinal) == true;

    internal static string MaterialCardId(string id) => WorkspaceMaterialItem.CardId(id);

    internal static PaperData CreateOwner(AppState state, string? title = null)
    {
        if (FindOwner(state) is { } existing) return existing;
        // Deterministic migration identity makes the same legacy baseline normalize to
        // the same paper during a three-way merge. It is independent of the display title.
        var id = "workspace-materials";
        for (var suffix = 2; state.Papers.Any(p => p.Id == id); suffix++)
            id = "workspace-materials-" + suffix;
        var paper = new PaperData
        {
            Id = id,
            Type = PaperTypes.Note,
            Title = title ?? DefaultTitle,
            Width = PaperLayoutDefaults.NoteDefaultWidth,
            Height = PaperLayoutDefaults.NoteDefaultHeight,
            IsVisible = false,
            WorkspaceMaterials = new WorkspaceMaterialsDocument
            {
                Layout = state.Workbench ?? new WorkbenchState()
            }
        };
        state.Workbench = null;
        state.Papers.Add(paper);
        return paper;
    }

    internal static void Normalize(AppState state)
    {
        var owners = state.Papers.Where(p => p.WorkspaceMaterials != null).ToArray();
        if (owners.Length > 1)
            throw new JsonException("A workspace can contain only one workspace materials document.");
        var owner = owners.FirstOrDefault();
        if (owner == null && state.Workbench != null) owner = CreateOwner(state);
        if (owner == null) return;

        owner.Type = PaperTypes.Note;
        owner.BodyProviderId = PaperBodyProviderIds.Markdown;
        var document = owner.WorkspaceMaterials!;
        if (document.Items != null && state.Papers.Any(p => document.Items.ContainsKey(p.Id)))
            throw new JsonException("Material ids must not collide with paper ids.");
        document.Layout ??= new();
        document.Layout.Cards ??= new(StringComparer.Ordinal);
        if (state.Workbench != null)
        {
            // Files written during migration may carry both fields. New document positions
            // are authoritative; import only legacy positions that have not been migrated.
            foreach (var pair in state.Workbench.Cards ?? [])
                document.Layout.Cards.TryAdd(pair.Key, pair.Value);
            state.Workbench = null;
        }
        document.Normalize(state.Papers.Where(p => p.Id != owner.Id).Select(p => p.Id));
    }

    internal static string RenderMarkdown(WorkspaceMaterialsDocument document) => document.RenderMarkdown();

    internal static string GetReadableContent(PaperData paper)
    {
        if (paper.WorkspaceMaterials == null) return paper.Content ?? "";
        var materials = RenderMarkdown(paper.WorkspaceMaterials);
        return string.IsNullOrEmpty(paper.Content) ? materials :
            string.IsNullOrEmpty(materials) ? paper.Content : paper.Content + "\n\n" + materials;
    }
}
