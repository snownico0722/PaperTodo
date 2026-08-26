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
}

/// <summary>
/// Optional protocol-2.0 Native capability. A plugin declaring manifest capability "runtime"
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
