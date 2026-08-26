using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo.Plugin;

[Flags]
public enum PaperBodyCapabilities
{
    None = 0,
    TextZoom = 1 << 0,
    NoteLinks = 1 << 1
}

[Flags]
public enum PaperBodyInputClaims
{
    None = 0,
    EscapeKey = 1 << 0,
    ContextMenu = 1 << 1
}


public sealed record PaperBodyTheme(
    bool IsDark,
    string PaperColor,
    string TextColor,
    string WeakTextColor,
    string AccentColor,
    string BorderColor,
    string FontFamily,
    double FontScale);

public enum PaperCapsuleComponentKind
{
    Text,
    Glyph,
    StatusDot,
    ProgressRing,
    ProgressBar
}

public enum PaperCapsuleTone
{
    Default,
    Muted,
    Accent,
    Warning,
    Danger
}

public enum PaperTopBarIconKind
{
    Character,
    SvgPath
}

public enum PaperTopBarSvgRenderMode
{
    Fill,
    Stroke
}

public enum PaperTopBarActionScope
{
    Paper,
    Global
}

[Flags]
public enum PaperHostTopBarActions
{
    None = 0,
    NewTodoPaper = 1 << 0,
    NewNotePaper = 1 << 1
}

/// <summary>
/// A small host-rendered top-bar icon. Character renders short text/glyph content; SvgPath accepts
/// SVG/WPF path-data syntax only, not a complete SVG document. PaperTodo owns the button size,
/// theme, hover/focus behavior and clipping. SvgPath can be rendered as either a filled silhouette
/// or a stroked outline; StrokeWidth controls the host-rendered outline thickness.
/// </summary>
public sealed record PaperTopBarIcon
{
    public PaperTopBarIconKind Kind { get; init; }
    public string Value { get; init; } = string.Empty;
    public PaperTopBarSvgRenderMode RenderMode { get; init; } = PaperTopBarSvgRenderMode.Fill;
    public double StrokeWidth { get; init; } = 1.5;

    public static PaperTopBarIcon Character(string value) =>
        new() { Kind = PaperTopBarIconKind.Character, Value = value ?? string.Empty };

    public static PaperTopBarIcon SvgPath(
        string pathData,
        PaperTopBarSvgRenderMode renderMode = PaperTopBarSvgRenderMode.Fill,
        double strokeWidth = 1.5) =>
        new()
        {
            Kind = PaperTopBarIconKind.SvgPath,
            Value = pathData ?? string.Empty,
            RenderMode = renderMode,
            StrokeWidth = strokeWidth
        };
}

/// <summary>
/// One host-rendered plugin action in a PaperTodo top bar. Plugins contribute intent only; they do
/// not provide Button/FrameworkElement instances or control placement, sizing, theme or hover UI.
/// Priority is an ordering hint for Global plugin actions: higher values are placed first. Host
/// actions do not participate in this numeric priority space and always retain host precedence.
/// </summary>
public sealed record PaperTopBarAction
{
    public string Id { get; init; } = string.Empty;
    public PaperTopBarIcon Icon { get; init; } = new();
    public string ToolTip { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
}

/// <summary>
/// Describes a top-bar click. Global actions can be rendered on any paper while their owning plugin
/// plugin runtime is alive, so TargetPaperId/Type/BodyProviderId identify the paper whose button was
/// clicked rather than any plugin-owned paper. TargetBodyProviderId is empty for non-Note papers.
/// </summary>
public sealed record PaperTopBarActionInvocation(
    string ActionId,
    PaperTopBarActionScope Scope,
    string TargetPaperId,
    string TargetPaperType,
    string TargetBodyProviderId);

/// <summary>
/// Paper-session-scoped Top Bar capability. It can contribute actions only to the paper carrying
/// this body session. Process-level Global actions belong to PaperPluginRuntimeContext.GlobalTopBar.
/// PaperTodo owns rendering and automatically removes these Paper actions when the session ends.
/// </summary>
public interface IPaperTopBarApi
{
    void SetActionHandler(Action<PaperTopBarActionInvocation>? handler);

    void SetPaperActions(
        IReadOnlyList<PaperTopBarAction> actions,
        PaperHostTopBarActions hiddenHostActions = PaperHostTopBarActions.None);

    void Clear();
}

/// <summary>
/// One host-rendered item inside the fixed-height capsule content area. Up to three items are
/// accepted and their order is preserved. Fill consumes remaining horizontal space.
/// </summary>
public sealed record PaperCapsuleComponent
{
    public PaperCapsuleComponentKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Value { get; init; }
    public double Width { get; init; }
    public bool Fill { get; init; }
    public PaperCapsuleTone Tone { get; init; }
    public string Color { get; init; } = string.Empty;
}

/// <summary>
/// Host-rendered capsule description. A positive PreferredWidth is the complete capsule content
/// segment width in DIPs. AutomaticWidth asks PaperTodo to measure the natural width of the standard
/// components, including their internal padding and gaps. PaperTodo owns the fixed height, outer
/// chrome, close segment and all input.
/// </summary>
public sealed record PaperCapsulePresentation
{
    public const double AutomaticWidth = 0;

    public PaperCapsuleComponent[] Components { get; init; } = [];
    public double PreferredWidth { get; init; } = 110;
    public string ToolTip { get; init; } = string.Empty;
    public string PlainText { get; init; } = string.Empty;
}

public enum PaperCapsuleSurfaceKind
{
    Regular,
    Docked
}

/// <summary>
/// Geometry and theme of one fixed-height capsule content surface. Width and Height are the exact
/// custom-view layout slot in DIPs. The host keeps ownership of outer chrome and input.
/// </summary>
public sealed record PaperCapsuleViewContext(
    PaperCapsuleSurfaceKind Surface,
    double Width,
    double Height,
    PaperBodyTheme Theme);

/// <summary>
/// Optional native-session capability. A session may create one fresh WPF view for each live
/// capsule surface. The host attempts each surface at most once per live body session and caches
/// either the returned view or a null fallback. AutomaticWidth resolves from the standard component
/// template before this method is called. Any resolved-width geometry change recreates the surface
/// with a new context; presentation, theme and DPI changes that keep the same resolved width reuse
/// the view, so plugins that render live state should retain and update it through the session
/// lifecycle. Returning null falls back to the host template.
/// </summary>
public interface IPaperCapsuleViewProvider
{
    FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context);
}

/// <summary>
/// Preferred complete edge mini-card size in device-independent pixels. The size includes the
/// host-owned chrome and close segment. Width and Height must be positive finite numbers; PaperTodo
/// clamps the requested size only to the usable area of the current monitor. Runtime size changes
/// are supported, but repeatedly changing the preferred size while a mini is visible is discouraged
/// because it can force host/native relayout; keep one browsing session geometrically stable when
/// practical.
/// </summary>
public readonly record struct PaperMiniViewSize(double Width, double Height)
{
    public static PaperMiniViewSize Default => new(320, 220);
}

/// <summary>
/// Exact geometry and theme supplied when PaperTodo creates a native mini view. CardWidth/CardHeight
/// describe the complete visible card. Width/Height describe the inner slot owned by the plugin
/// after the host chrome and close segment have been reserved.
/// </summary>
public sealed record PaperMiniViewContext(
    double CardWidth,
    double CardHeight,
    double Width,
    double Height,
    PaperBodyTheme Theme);

/// <summary>
/// Optional native-session capability for a dedicated edge-browsing surface. The session and mini
/// view may share one business-state model, but CreateMiniView must return a fresh pure-WPF tree.
/// Window, HwndHost, WindowsFormsHost, WebView2 and already-parented controls are rejected.
/// PaperTodo caches one successful view per live session and normalized geometry. Returning null or
/// throwing falls back to the enlarged capsule presentation.
/// </summary>
public interface IPaperMiniViewProvider
{
    PaperMiniViewSize PreferredMiniViewSize => PaperMiniViewSize.Default;

    FrameworkElement? CreateMiniView(PaperMiniViewContext context);

    /// <summary>
    /// Notifies a cached mini tree when it becomes the active preview or starts leaving it. Plugins
    /// can pause timers and input work when hidden, but must keep the last painted tree intact for
    /// the host-owned outgoing animation. Business-state updates should continue according to the
    /// normal body-session visibility contract.
    /// </summary>
    void OnMiniViewVisibilityChanged(bool visible) { }
}


/// <summary>
/// Marks a custom mini-view element as owning pointer input. Standard WPF buttons, selectors,
/// scroll bars, thumbs and hyperlinks are detected automatically. The edge host deliberately does
/// not take keyboard focus; text editing belongs in the full paper body.
/// </summary>
public static class PaperMiniViewInteraction
{
    public static readonly DependencyProperty ConsumesPointerProperty =
        DependencyProperty.RegisterAttached(
            "ConsumesPointer",
            typeof(bool),
            typeof(PaperMiniViewInteraction),
            new FrameworkPropertyMetadata(false));

    public static void SetConsumesPointer(DependencyObject element, bool value) =>
        element.SetValue(ConsumesPointerProperty, value);

    public static bool GetConsumesPointer(DependencyObject element) =>
        (bool)element.GetValue(ConsumesPointerProperty);
}

/// <summary>
/// Host-owned native controls. Plugins provide data and behavior while PaperTodo owns the shared
/// visual language, popup lifecycle, theme and DPI behavior.
/// </summary>
public interface IPaperBodyControls
{
    void ApplySelectStyle(ComboBox comboBox, double fontSize);
}

/// <summary>
/// Operations that belong to the paper carrying this plugin instance. The paper remains the
/// product-level anchor even when the plugin reads or mutates workspace data.
/// </summary>
public sealed class PaperBodyPaperContext
{
    public required string PaperId { get; init; }
    public required Action<string> SetTitle { get; init; }
    public required Action<string> SetHeaderText { get; init; }
    public required Action<PaperCapsulePresentation?> SetCapsulePresentation { get; init; }
}

/// <summary>
/// Operations that belong to the expanded body surface itself.
/// </summary>
public sealed class PaperBodySurfaceContext
{
    public required IPaperBodyControls Controls { get; init; }
    public required PaperBodyTheme Theme { get; init; }
    public required Action<PaperBodyInputClaims> SetInputClaims { get; init; }
    public required Action MarkDirty { get; init; }
    public required Action<string> OpenExternal { get; init; }
    public required Action RequestReload { get; init; }
}

/// <summary>
/// One plugin instance is anchored to one paper. Paper contains paper-owned presentation state,
/// Body contains the expanded body surface, Workspace exposes PaperTodo-wide business-data
/// operations, TopBar exposes the paper-session chrome contribution capability, and Presentation
/// can only request state changes for this session's own host paper.
/// </summary>
public sealed class PaperBodyContext
{
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required string StateJson { get; init; }
    public required int StateVersion { get; init; }
    public required int TargetStateVersion { get; init; }
    public string SettingsJson { get; init; } = "{}";
    public IReadOnlySet<string> GrantedPermissions { get; init; } =
        PaperTodoPermissionNames.None;

    public required PaperBodyPaperContext Paper { get; init; }
    public required PaperBodySurfaceContext Body { get; init; }
    public required IPaperTodoHostApi Workspace { get; init; }
    public required IPaperPluginRuntimeClient Runtime { get; init; }
    public IPaperTopBarApi TopBar => Workspace as IPaperTopBarApi
        ?? throw new InvalidOperationException(
            "This PaperTodo host does not expose the protocol 2.0 paper top-bar capability.");
    public IPaperPresentationApi Presentation => Workspace as IPaperPresentationApi
        ?? throw new InvalidOperationException(
            "This PaperTodo host does not expose own-paper presentation controls.");
    // Per-Paper frontend/body state is independently limited to 10 MiB UTF-8.
    public required Action<string> SaveStateJson { get; init; }

    // Convenience views for non-ambiguous values. Presentation writes stay in Paper / Body /
    // TopBar / Presentation.
    public string PaperId => Paper.PaperId;
    public IPaperTodoHostApi Host => Workspace;
    public IPaperBodyControls Controls => Body.Controls;
    public PaperBodyTheme Theme => Body.Theme;
    public Action<string> SetTitle => Paper.SetTitle;
    public Action<PaperBodyInputClaims> SetInputClaims => Body.SetInputClaims;
    public Action MarkDirty => Body.MarkDirty;
    public Action<string> OpenExternal => Body.OpenExternal;
    public Action RequestReload => Body.RequestReload;
}

/// <summary>
/// A fully trusted, unsandboxed native plugin loaded from one self-contained
/// plugins/&lt;plugin-id&gt;/ folder with the current user's permissions. plugin.json is the single
/// authority for id/name/version/protocol/state/capability/runtime metadata. Implementations provide
/// only behavior, must have a public parameterless constructor and act as stateless factories.
/// PaperTodo creates a fresh plugin object for every body session or app-runtime activation.
/// </summary>
public interface IPaperBodyPlugin
{
    /// <summary>
    /// Migrate persisted JSON before Create is called. The target version comes from plugin.json.
    /// The default implementation keeps the old JSON unchanged.
    /// </summary>
    string MigrateState(string stateJson, int fromVersion) => stateJson;

    IPaperBodySession Create(PaperBodyContext context);
}

/// <summary>
/// One live body instance attached to one PaperTodo paper. Web plugins must call
/// papertodo.saveState after every state mutation; Commit is best-effort only.
/// </summary>
public interface IPaperBodySession : IDisposable
{
    FrameworkElement View { get; }

    void Commit() { }
    void RefreshFromModel() { }
    void CancelInteractions() { }
    void OnActivated() { }
    void OnDeactivated() { }

    // Whether the paper/plugin remains available at all. A visible capsule keeps this true even
    // while its full body is folded away.
    void OnVisibilityChanged(bool visible) { }

    // Whether the full paper body is currently presented and interactive.
    void OnPresentationChanged(bool visible) { }
    void OnThemeChanged(PaperBodyTheme theme) { }
    void OnTypographyChanged(PaperBodyTheme theme) { }
    void OnDpiChanged() { }

    // Message sent by the one provider Runtime to this Paper frontend.
    bool OnRuntimeMessage(JsonElement message) => false;

    // Host-rendered global settings changed for this plugin.
    void OnSettingsChanged(string settingsJson) { }
}
