namespace PaperTodo;

/// <summary>
/// Workbench-only paper fields: the canvas origin and undirected paper-to-paper links.
/// Both live on <see cref="PaperData"/> inside the paper's own workspace document; this type
/// only keeps their invariants in one place for persistence, deletion and the workbench UI.
/// </summary>
internal static class WorkbenchPaperState
{
    public const int MaxLinksPerPaper = 64;

    public static bool HasPosition(PaperData paper) =>
        paper.WorkbenchX is double x && paper.WorkbenchY is double y &&
        double.IsFinite(x) && double.IsFinite(y);

    /// <summary>Save only repairs values that would make serialization fail.</summary>
    public static void RepairForSave(PaperData paper)
    {
        if (paper.WorkbenchX is double x && !double.IsFinite(x)) paper.WorkbenchX = null;
        if (paper.WorkbenchY is double y && !double.IsFinite(y)) paper.WorkbenchY = null;
        if (paper.WorkbenchLinks != null)
        {
            paper.WorkbenchLinks.RemoveAll(string.IsNullOrWhiteSpace);
            if (paper.WorkbenchLinks.Count == 0) paper.WorkbenchLinks = null;
        }
    }

    /// <summary>
    /// Load normalization: a half position is not a placement, and a link must name another
    /// existing paper of the same document. A pair stored on both sides keeps one copy.
    /// </summary>
    public static void NormalizeAfterLoad(IReadOnlyList<PaperData> papers, IReadOnlySet<string> paperIds)
    {
        var seenPairs = new HashSet<(string, string)>();
        foreach (var paper in papers)
        {
            if (!HasPosition(paper) ||
                Math.Abs(paper.WorkbenchX!.Value) > WorkbenchLayout.MaxCoordinate ||
                Math.Abs(paper.WorkbenchY!.Value) > WorkbenchLayout.MaxCoordinate)
            {
                paper.WorkbenchX = null;
                paper.WorkbenchY = null;
            }
            else
            {
                paper.WorkbenchX = Math.Max(0, paper.WorkbenchX.Value);
                paper.WorkbenchY = Math.Max(0, paper.WorkbenchY.Value);
            }

            if (paper.WorkbenchLinks == null) continue;
            var normalized = new List<string>();
            foreach (var raw in paper.WorkbenchLinks)
            {
                var id = raw?.Trim();
                if (string.IsNullOrEmpty(id) ||
                    string.Equals(id, paper.Id, StringComparison.Ordinal) ||
                    !paperIds.Contains(id) ||
                    !seenPairs.Add(PairKey(paper.Id, id)) ||
                    normalized.Count >= MaxLinksPerPaper)
                {
                    continue;
                }
                normalized.Add(id);
            }
            paper.WorkbenchLinks = normalized.Count == 0 ? null : normalized;
        }
    }

    public static (string, string) PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    public static bool AreLinked(PaperData a, PaperData b) =>
        Contains(a.WorkbenchLinks, b.Id) || Contains(b.WorkbenchLinks, a.Id);

    public static bool TryLink(PaperData source, PaperData target)
    {
        if (ReferenceEquals(source, target) ||
            string.Equals(source.Id, target.Id, StringComparison.Ordinal) ||
            AreLinked(source, target) ||
            (source.WorkbenchLinks?.Count ?? 0) >= MaxLinksPerPaper)
        {
            return false;
        }
        (source.WorkbenchLinks ??= new List<string>()).Add(target.Id);
        return true;
    }

    public static bool Unlink(PaperData a, PaperData b)
    {
        var removed = Remove(a, b.Id);
        removed |= Remove(b, a.Id);
        return removed;
    }

    /// <summary>
    /// Removes every stored reference to <paramref name="paperId"/>. Returns the papers that
    /// changed so a failed transaction can restore them.
    /// </summary>
    public static IReadOnlyList<PaperData> RemoveLinksTo(IEnumerable<PaperData> papers, string paperId)
    {
        var changed = new List<PaperData>();
        foreach (var paper in papers)
        {
            if (Remove(paper, paperId)) changed.Add(paper);
        }
        return changed;
    }

    /// <summary>Distinct undirected pairs whose two ends both exist in <paramref name="papers"/>.</summary>
    public static IReadOnlyList<(string A, string B)> Pairs(IEnumerable<PaperData> papers)
    {
        var list = papers.ToList();
        var ids = list.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        var pairs = new List<(string, string)>();
        foreach (var paper in list)
        {
            if (paper.WorkbenchLinks == null) continue;
            foreach (var id in paper.WorkbenchLinks)
            {
                if (string.IsNullOrEmpty(id) || !ids.Contains(id) ||
                    string.Equals(id, paper.Id, StringComparison.Ordinal))
                {
                    continue;
                }
                var key = PairKey(paper.Id, id);
                if (seen.Add(key)) pairs.Add(key);
            }
        }
        return pairs;
    }

    /// <summary>Existing todo-row relations (todo paper → linked paper) inside one document.</summary>
    public static IReadOnlyList<(string Todo, string Linked)> TodoLinkPairs(IEnumerable<PaperData> papers)
    {
        var list = papers.ToList();
        var ids = list.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        var pairs = new List<(string, string)>();
        foreach (var paper in list)
        {
            if (paper.Type != PaperTypes.Todo || paper.Items == null) continue;
            foreach (var item in paper.Items)
            {
                var linked = item?.LinkedPaperId;
                if (string.IsNullOrEmpty(linked) || !ids.Contains(linked) ||
                    string.Equals(linked, paper.Id, StringComparison.Ordinal))
                {
                    continue;
                }
                if (seen.Add((paper.Id, linked))) pairs.Add((paper.Id, linked));
            }
        }
        return pairs;
    }

    private static bool Contains(List<string>? links, string id) =>
        links != null && links.Contains(id, StringComparer.Ordinal);

    private static bool Remove(PaperData paper, string id)
    {
        if (paper.WorkbenchLinks == null) return false;
        var removed = paper.WorkbenchLinks.RemoveAll(
            value => string.Equals(value, id, StringComparison.Ordinal)) > 0;
        if (paper.WorkbenchLinks.Count == 0) paper.WorkbenchLinks = null;
        return removed;
    }
}
