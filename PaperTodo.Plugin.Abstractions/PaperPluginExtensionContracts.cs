using System.Windows;

namespace PaperTodo.Plugin;

/// <summary>
/// An opaque, short-lived host placement token. It conveys neither a WPF object nor screen
/// coordinates. Only the session/runtime which received it can use it; retain business ids,
/// not anchors. A moved, hidden or removed source can invalidate an anchor before it expires.
/// </summary>
public sealed record PaperUiAnchor(string Id);

[Flags]
public enum PaperActionPlacement
{
    None = 0,
    TopBar = 1,
    ContextMenu = 2
}

/// <summary>
/// Runtime-owned action on an existing paper. BodyProviderId optionally restricts visibility
/// and invocation to that provider (for example builtin.markdown); null matches any provider.
/// These are volatile contributions, not settings or paper data.
/// </summary>
public sealed record PaperAction
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string ToolTip { get; init; } = string.Empty;
    public PaperTopBarIcon Icon { get; init; } = new();
    public int Priority { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public string? BodyProviderId { get; init; }
    public PaperActionPlacement Placement { get; init; } = PaperActionPlacement.ContextMenu;
}

public sealed record PaperActionInvocation(
    string ActionId,
    PaperSnapshot Paper,
    PaperActionPlacement Placement,
    PaperUiAnchor? Anchor);

/// <summary>Requires papers.read. Registrations belong to the current provider Runtime.</summary>
public interface IPaperPluginPaperActions
{
    void SetActionHandler(Action<PaperActionInvocation>? handler);
    void SetActions(string paperId, IReadOnlyList<PaperAction> actions);
    void Clear(string paperId);
    void Clear();
}

/// <summary>Encoded bytes are an independent copy, never an LMDB buffer or a decoded bitmap.</summary>
public sealed record PaperNoteImage(string ImageId, string Mime, byte[] Bytes);

/// <summary>
/// Requires notes.read. Only images owned by the specified built-in Markdown note are readable.
/// Missing, corrupt and wrong-owner images all report asset_not_found. Each read is limited to
/// MaximumImageBytes before copying the encoded data. This is not an atomic whole-note export.
/// </summary>
public interface IPaperNoteAssetsApi
{
    const int MaximumImageBytes = 16 * 1024 * 1024;
    PaperNoteImage ReadImage(string paperId, string imageId);
}

public sealed record PaperPluginWindowOptions
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public double Width { get; init; } = 640;
    public double Height { get; init; } = 480;
    public PaperUiAnchor? Anchor { get; init; }
}

public sealed record PaperPluginPopupOptions
{
    public string Id { get; init; } = string.Empty;
    public double Width { get; init; } = 320;
    public double Height { get; init; } = 240;
}

/// <summary>
/// Created on the host UI dispatcher. Controls uses the same implementation as app settings,
/// with this surface's current theme. Close is safe during creation and after disposal.
/// </summary>
public sealed record PaperPluginSurfaceContext(
    PaperBodyTheme Theme,
    IPaperBodyControls Controls,
    Action Close);

/// <summary>
/// Return a fresh, unparented WPF root on the factory's dispatcher. The plugin owns content,
/// layout, input and business state. The host owns the shell and calls Dispose exactly once
/// after accepting ownership, including failed openings. Theme callbacks must refresh any
/// dynamically styled controls; applying a style once does not subscribe to theme changes.
/// </summary>
public interface IPaperPluginSurfaceContent : IDisposable
{
    FrameworkElement View { get; }
    void OnThemeChanged(PaperBodyTheme theme) { }
}

public interface IPaperPluginSurface
{
    string Id { get; }
    bool IsOpen { get; }
    void Close();
}

/// <summary>
/// Optional additive native capability. Each body session/runtime has its own ids and lifetime.
/// Factories run on the host dispatcher, including calls made from Runtime worker threads.
/// Reopening an existing window id activates it without calling the factory again. At most
/// eight windows and one popup may be open per lease. Popups close on outside click, unhandled
/// Escape, source movement/disappearance or owner deactivation; there is no nested-popup API.
/// A surface is not a Paper and does not keep a provider Runtime alive.
/// </summary>
public interface IPaperPluginSurfaces
{
    IPaperPluginSurface OpenWindow(
        PaperPluginWindowOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent);
    IPaperPluginSurface OpenPopup(
        PaperUiAnchor anchor,
        PaperPluginPopupOptions options,
        Func<PaperPluginSurfaceContext, IPaperPluginSurfaceContent> createContent);
    void Close(string id);
    void CloseAll();
}
