namespace PaperTodo;

// Immutable inputs cross the worker boundary. Inactive workspaces never instantiate editors,
// plugins, image stores, recovery flows or write-enabled StateStore sessions for an overview.
internal sealed record WorkbenchPaperSnapshot(
    string Id, string Title, string Summary, string SearchText, string Kind, string Affinity, string BindingTitle,
    double Width, double Height, WorkbenchCardState? Placement, bool DesktopVisible,
    bool MaterialCollapsed = false);
internal sealed record WorkbenchWorkspaceSnapshot(
    int Number, IReadOnlyList<WorkbenchPaperSnapshot> Papers,
    IReadOnlyList<WorkbenchLayoutEdge> Edges, string? Error = null, WorkbenchState? Layout = null)
{
    internal WorkbenchLayoutNode[] Nodes => Papers.Select(p => new WorkbenchLayoutNode(
        p.Id, p.Width, p.Placement?.Collapsed == true ? WorkbenchCardState.CollapsedHeight : p.Height,
        p.Placement?.LayoutBounds, p.Placement?.Pinned ?? false,
        p.Placement?.Preferred ?? false, p.Affinity, p.Kind)).ToArray();
}
