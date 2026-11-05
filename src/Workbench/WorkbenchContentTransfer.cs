namespace PaperTodo;

/// <summary>In-process drag identity. The source content is revalidated at drop time.</summary>
internal sealed record WorkbenchContentTransferPayload(
    string SourcePaperId,
    long WorkspaceGeneration,
    string WorkspaceBindingToken,
    IReadOnlyList<string> TodoIds,
    int NoteStart,
    int NoteLength,
    string ExpectedText)
{
    internal const string DataFormat = "PaperTodo.WorkbenchContent";
}

internal sealed class WorkbenchContentSnapshot
{
    internal string Content { get; }
    internal List<PaperItem> Items { get; }

    internal WorkbenchContentSnapshot(PaperData paper)
    {
        Content = paper.Content ?? "";
        Items = TodoRules.CloneAll(paper.Items);
    }

    internal WorkbenchContentSnapshot(string content, IEnumerable<PaperItem> items)
    {
        Content = content;
        Items = TodoRules.CloneAll(items);
    }

    internal void Apply(PaperData paper)
    {
        paper.Content = Content;
        paper.Items = TodoRules.CloneAll(Items);
    }

    internal bool Matches(PaperData paper, string currentContent) => paper.Type == PaperTypes.Note
        ? string.Equals(Content, currentContent, StringComparison.Ordinal)
        : Items.Count == paper.Items.Count && Items.Zip(paper.Items).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Text == pair.Second.Text &&
            pair.First.Done == pair.Second.Done && pair.First.Order == pair.Second.Order &&
            pair.First.LinkedPaperId == pair.Second.LinkedPaperId &&
            pair.First.LinkedPath == pair.Second.LinkedPath &&
            pair.First.LinkedPathIsDirectory == pair.Second.LinkedPathIsDirectory &&
            pair.First.ReminderAt == pair.Second.ReminderAt);
}

/// <summary>Pure planning: no source is changed until both sides have passed validation.</summary>
internal static class WorkbenchContentTransfer
{
    internal static bool TryPlan(
        PaperData source, PaperData target, WorkbenchContentTransferPayload payload,
        string sourceContent, string targetContent,
        out WorkbenchContentSnapshot sourceAfter, out WorkbenchContentSnapshot targetAfter)
    {
        sourceAfter = new(source);
        targetAfter = new(target);
        if (source.Id == target.Id || source.Type != target.Type || source.WorkspaceMaterials != null ||
            target.WorkspaceMaterials != null || payload.SourcePaperId != source.Id) return false;
        if (source.Type == PaperTypes.Todo)
        {
            var ids = payload.TodoIds.ToHashSet(StringComparer.Ordinal);
            var moved = source.Items.OrderBy(i => i.Order).Where(i => ids.Contains(i.Id)).ToArray();
            if (ids.Count == 0 || moved.Length != ids.Count || moved.Any(item =>
                    item.LinkedPaperId == target.Id || target.Items.Any(existing => existing.Id == item.Id))) return false;
            var sourceItems = source.Items.OrderBy(i => i.Order).Where(i => !ids.Contains(i.Id)).Select(TodoRules.Clone).ToList();
            var targetItems = target.Items.OrderBy(i => i.Order).Where(i => !TodoRules.IsPlaceholder(i)).Select(TodoRules.Clone).ToList();
            targetItems.AddRange(moved.Select(TodoRules.Clone));
            if (sourceItems.Count == 0) sourceItems.Add(new PaperItem());
            TodoRules.NormalizeOrders(sourceItems);
            TodoRules.NormalizeOrders(targetItems);
            sourceAfter = new(sourceContent, sourceItems);
            targetAfter = new(targetContent, targetItems);
            return true;
        }
        if (source.Type != PaperTypes.Note || source.BodyProviderId != PaperBodyProviderIds.Markdown ||
            target.BodyProviderId != PaperBodyProviderIds.Markdown || payload.NoteStart < 0 ||
            payload.NoteLength <= 0 || payload.NoteStart > sourceContent.Length - payload.NoteLength ||
            !sourceContent.AsSpan(payload.NoteStart, payload.NoteLength).SequenceEqual(payload.ExpectedText)) return false;
        var separator = targetContent.Length > 0 && !targetContent.EndsWith('\n') ? "\n" : "";
        if ((long)targetContent.Length + separator.Length + payload.NoteLength > PaperWindow.NoteTextMaxLength) return false;
        sourceAfter = new(sourceContent.Remove(payload.NoteStart, payload.NoteLength), source.Items);
        targetAfter = new(targetContent + separator + payload.ExpectedText, target.Items);
        return true;
    }
}
