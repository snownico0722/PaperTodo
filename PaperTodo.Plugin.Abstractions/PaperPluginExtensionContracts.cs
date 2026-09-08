using System.Windows;

namespace PaperTodo.Plugin;

/// <summary>A one-time screen position in physical pixels, not a live control or tracking token.</summary>
public sealed record PaperPopupPosition(double X, double Y);

/// <summary>A plain-text paper context-menu entry. Entries keep their declaration order.</summary>
public sealed record PaperAction
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

public sealed record PaperActionInvocation(
    string ActionId, PaperSnapshot Paper, PaperPopupPosition Position);

/// <summary>An independent copy of the encoded image, never a store buffer or file path.</summary>
public sealed record PaperNoteImage(string ImageId, string Mime, byte[] Bytes);

/// <summary>Requires notes.read. Only the specified built-in Markdown note's images are readable.</summary>
public interface IPaperNoteAssetsApi
{
    const int MaximumImageBytes = 16 * 1024 * 1024;
    PaperNoteImage ReadImage(string paperId, string imageId);
}

public sealed record PaperPluginPopupOptions
{
    /// <summary>Shell size in DIPs, clamped to the opening monitor's work area.</summary>
    public double Width { get; init; } = 320;
    public double Height { get; init; } = 240;
}

public sealed record PaperPluginPopupContext(
    PaperBodyTheme Theme, IPaperBodyControls Controls, Action Close);

public sealed record PaperPluginPopupFailure(string Code, string Message);

/// <summary>Return a fresh unparented view on the UI dispatcher. The shell disposes accepted content.</summary>
public interface IPaperPluginPopupContent : IDisposable
{
    FrameworkElement View { get; }
    void OnThemeChanged(PaperBodyTheme theme) { }
}

public interface IPaperPluginPopup
{
    /// <summary>True while opening or visible; false after Close or a failed open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Set when the deferred shell show/placement/activation step fails. Synchronous validation
    /// and content-factory failures are still thrown directly from Open.
    /// </summary>
    PaperPluginPopupFailure? OpenFailure { get; }

    /// <summary>Raised once when a deferred shell show/placement/activation step fails.</summary>
    event Action<PaperPluginPopupFailure>? OpenFailed;

    void Close();
}

/// <summary>
/// One transient popup per body session or provider Runtime. Opening replaces the previous popup.
/// Placement happens once near the supplied click position; moving the source does not track it.
/// Leaving the popup window closes it; moving focus between its controls does not.
/// The host owns UI-thread creation and cleanup when the session/Runtime ends. Content owns layout,
/// business logic and keyboard behavior. Deferred shell failures are observable from the returned
/// popup handle. No ordinary windows, ids, nested shells or source leases.
/// </summary>
public interface IPaperPluginPopups
{
    IPaperPluginPopup Open(PaperPopupPosition position, PaperPluginPopupOptions options,
        Func<PaperPluginPopupContext, IPaperPluginPopupContent> createContent);
    void Close();
}