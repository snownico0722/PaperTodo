namespace PaperTodo.Plugin;

/// <summary>
/// App-scoped global top-bar capability. Unlike PaperBodyContext.TopBar, this lifetime belongs to
/// the provider runtime rather than any one paper/body session. PaperTodo removes all actions when
/// that provider runtime ends.
/// </summary>
public interface IPaperGlobalTopBarApi
{
    void SetActionHandler(Action<PaperTopBarActionInvocation>? handler);
    void SetActions(IReadOnlyList<PaperTopBarAction> actions);
    void Clear();
}

/// <summary>
/// Host-managed settings for one provider Runtime. Json is read on demand and Subscribe reports
/// later normalized setting changes without borrowing lifecycle from any Body/Mini frontend.
/// </summary>
public interface IPaperPluginRuntimeSettings
{
    string Json { get; }
    IDisposable Subscribe(Action<string> handler);
}

/// <summary>
/// One host-managed shortcut invocation routed to the plugin runtime. SettingId names the manifest
/// setting and ActionId is that setting's shortcutAction value.
/// </summary>
public sealed record PaperShortcutActionInvocation(
    string SettingId,
    string ActionId);

/// <summary>
/// App-scoped callback endpoint for plugin-defined global shortcut actions. PaperTodo owns the
/// actual Windows hotkey registration, conflict handling, persistence and settings UI. Host-owned
/// paper.* shortcut actions are executed directly by PaperTodo and are not sent to this handler.
/// </summary>
public interface IPaperGlobalShortcutApi
{
    void SetActionHandler(Action<PaperShortcutActionInvocation>? handler);
    void Clear();
}

[Flags]
public enum PaperTodoActionPlacement
{
    None = 0,
    Inline = 1 << 0,
    ContextMenu = 1 << 1
}

/// <summary>
/// Protocol 2.1 host-rendered action contributed to one existing Todo. Plugins provide only the
/// descriptor and click behavior; PaperTodo owns the Todo row/menu visual tree, theme, DPI, hover,
/// layout and input. Icon uses the same restricted Character/SVG Path contract as Top Bar actions.
/// </summary>
public sealed record PaperTodoAction
{
    public string Id { get; init; } = string.Empty;
    public PaperTopBarIcon Icon { get; init; } = new();
    public string Text { get; init; } = string.Empty;
    public string ToolTip { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public PaperTodoActionPlacement Placement { get; init; } =
        PaperTodoActionPlacement.Inline | PaperTodoActionPlacement.ContextMenu;
}

/// <summary>
/// Click delivered from a host-rendered Todo action. Todo is captured at invocation time rather
/// than registration time, so plugins receive the current text/completion/binding state.
/// </summary>
public sealed record PaperTodoActionInvocation(
    string ActionId,
    string PaperId,
    string TodoId,
    TodoSnapshot Todo);

/// <summary>
/// Protocol 2.1 Runtime capability for binding host-rendered actions to arbitrary existing Todos.
/// Registrations are volatile presentation contributions: a Runtime rebuilds them from its own
/// state and PaperTodo removes them automatically when that Runtime ends.
/// </summary>
public interface IPaperPluginRuntimeTodoActions
{
    void SetActionHandler(Action<PaperTodoActionInvocation>? handler);
    void SetActions(
        string paperId,
        string todoId,
        IReadOnlyList<PaperTodoAction> actions);
    void Clear(string paperId, string todoId);
    void Clear();
}

/// <summary>
/// Protocol 2.1 host-rendered, non-interactive top-bar metadata. Unlike Runtime.Papers, this surface
/// may target any existing Paper visible through the Workspace, so an enhancement plugin can label
/// built-in Markdown Notes as well as plugin-owned Papers. PaperTodo owns layout/theme/DPI.
/// </summary>
public interface IPaperPluginRuntimeTopBarLabels
{
    void SetLabels(string paperId, IReadOnlyList<PaperTopBarLabel> labels);
    void Clear(string paperId);
    void Clear();
}

/// <summary>
/// Context for the one provider-level backend Runtime. The Runtime exists while PaperTodo has at
/// least one real Note paper whose BodyProviderId is this plugin. It does not depend on any Paper
/// being visible, expanded, or having a live Body/Mini frontend.
///
/// State is one provider-scoped backend document; a multi-instance plugin keeps its own logical
/// instances keyed by PaperId there. Papers exposes the current logical Paper instances plus
/// presentation/message routing. PaperTodo never creates one backend Runtime per Paper.
///
/// Native runtimes may call these APIs from worker threads; PaperTodo marshals host operations to
/// its UI dispatcher when required. Dispose must not synchronously join a worker that can itself be
/// blocked in a host call.
/// </summary>
public sealed class PaperPluginRuntimeContext
{
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required IReadOnlySet<string> GrantedPermissions { get; init; }
    public required IPaperTodoHostApi Workspace { get; init; }
    public required IPaperPluginRuntimeSettings Settings { get; init; }
    public required IPaperPluginRuntimeState State { get; init; }
    public required IPaperPluginRuntimePapers Papers { get; init; }
    public required IPaperGlobalTopBarApi GlobalTopBar { get; init; }
    public required IPaperGlobalShortcutApi GlobalShortcuts { get; init; }

    public IPaperPluginRuntimeTodoActions TodoActions =>
        Workspace as IPaperPluginRuntimeTodoActions
        ?? throw new InvalidOperationException(
            "This PaperTodo host does not expose Protocol 2.1 Todo actions.");

    public IPaperPluginRuntimeTopBarLabels TopBarLabels =>
        Workspace as IPaperPluginRuntimeTopBarLabels
        ?? throw new InvalidOperationException(
            "This PaperTodo host does not expose Protocol 2.1 top-bar labels.");
}

/// <summary>
/// Optional protocol-2.x Native capability. A plugin declaring manifest capability "runtime"
/// implements this interface. PaperTodo starts exactly one provider Runtime when the first real
/// Paper uses the provider and disposes it when the last such Paper disappears. If a plugin needs
/// multiple workers, processes or isolation domains, it owns those internally behind this Runtime.
/// </summary>
public interface IPaperPluginRuntimeProvider
{
    IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context);
}

/// <summary>
/// The single provider-level backend Runtime. It is not a hidden Paper session and has no visible
/// View. Dispose ends the provider backend and revokes its provider-level contributions.
/// </summary>
public interface IPaperPluginRuntime : IDisposable
{
}
