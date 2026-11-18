using System.Text;

namespace PaperTodo;

public static class WorkspaceMaterialKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Link = "link";
    public const string File = "file";
    public const string Node = "node";

    internal static string Normalize(string? kind) => kind is Image or Link or File or Node ? kind : Text;
}

/// <summary>The authoritative content and canvas metadata attached to one materials Note.</summary>
public sealed class WorkspaceMaterialsDocument
{
    public Dictionary<string, WorkspaceMaterialItem> Items { get; set; } = new(StringComparer.Ordinal);
    public WorkbenchState Layout { get; set; } = new();

    internal WorkspaceMaterialsDocument Copy() => new()
    {
        Items = Items.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Layout = Layout.Copy()
    };

    internal void Normalize(IEnumerable<string> paperIds)
    {
        Items ??= new(StringComparer.Ordinal);
        Layout ??= new();
        var normalized = new Dictionary<string, WorkspaceMaterialItem>(StringComparer.Ordinal);
        // Preserve already canonical identities before repairing unprefixed imports.
        // Otherwise renaming "a" to "material:a" could steal the latter's placement.
        foreach (var pair in Items.OrderBy(p => p.Key.StartsWith(WorkspaceMaterialItem.IdPrefix,
            StringComparison.Ordinal) ? 0 : 1))
        {
            if (pair.Value == null) continue;
            var id = WorkspaceMaterialItem.CardId(pair.Key);
            if (normalized.ContainsKey(id))
            {
                // A malformed external ID must not overwrite a different material's content.
                var suffix = 2;
                while (normalized.ContainsKey(id + ":" + suffix)) suffix++;
                id += ":" + suffix;
            }
            if (id != pair.Key) Layout.RenameCard(pair.Key, id);
            var item = pair.Value;
            item.Id = id;
            item.Kind = WorkspaceMaterialKinds.Normalize(item.Kind);
            item.Title ??= "";
            item.Content ??= "";
            item.Source ??= "";
            normalized.Add(id, item);
        }
        Items = normalized;
        Layout.Normalize(paperIds.Concat(Items.Keys));
    }

    // A read-only projection for search, ordinary Markdown/HTML export and copying.
    // Canvas and collection editors write Items directly, never this generated text.
    internal string RenderMarkdown()
    {
        var result = new StringBuilder();
        foreach (var item in Items.Values)
        {
            if (result.Length > 0) result.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(item.Title))
                result.Append("## ").Append(item.Title.Replace('\r', ' ').Replace('\n', ' ')).Append("\n\n");
            result.Append(item.Content);
            if (!string.IsNullOrWhiteSpace(item.Source))
            {
                if (!string.IsNullOrWhiteSpace(item.Content)) result.Append("\n\n");
                result.Append(item.Source);
            }
        }
        return result.ToString();
    }
}

public sealed class WorkspaceMaterialItem
{
    public const string IdPrefix = "material:";
    public string Id { get; set; } = IdPrefix + Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = WorkspaceMaterialKinds.Text;
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Collapsed { get; set; }

    internal WorkspaceMaterialItem Copy() => (WorkspaceMaterialItem)MemberwiseClone();
    internal static string CardId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal) ? id : IdPrefix + id;
}
