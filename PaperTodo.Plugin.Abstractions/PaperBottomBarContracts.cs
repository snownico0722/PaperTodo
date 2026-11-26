namespace PaperTodo.Plugin;

public enum PaperBottomBarSide
{
    Right,
    Left
}

/// <summary>A host-rendered item on an expanded paper. Larger order values are closer to the edge.
/// Larger priorities are retained first. Plugin values are at most 998.</summary>
public sealed record PaperBottomBarItem
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public PaperTopBarIcon? Icon { get; init; }
    public string ToolTip { get; init; } = "";
    public PaperBottomBarSide Side { get; init; }
    public int Order { get; init; }
    public int Priority { get; init; }
    public bool Visible { get; init; } = true;
    public bool Clickable { get; init; }
}

public sealed record PaperBottomBarTarget(string PaperId, string Type);

/// <summary>API 2.5 Runtime capability. Any current paper matching the plugin’s declared bottomBarTargets may receive items.
/// Use Workspace for content access and change subscriptions under their usual permissions.</summary>
public interface IPaperPluginBottomBarApi
{
    event Action? TargetsChanged;
    IReadOnlyList<PaperBottomBarTarget> ListTargets();
    void SetItems(string paperId, IReadOnlyList<PaperBottomBarItem> items);
    void SetActionHandler(Action<PaperActionInvocation>? handler);
}
