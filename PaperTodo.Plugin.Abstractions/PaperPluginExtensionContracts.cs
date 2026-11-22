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

/// <summary>Requires papers.read. Entries belong to the existing provider Runtime and are volatile.</summary>
public interface IPaperPluginPaperActions
{
    void SetActionHandler(Action<PaperActionInvocation>? handler);
    void SetActions(string paperId, IReadOnlyList<PaperAction> actions);
    void Clear(string paperId);
    void Clear();
}

/// <summary>An independent copy of the encoded image, never a store buffer or file path.</summary>
public sealed record PaperNoteImage(string ImageId, string Mime, byte[] Bytes);

/// <summary>The saved image and the Markdown reference already appended to the note.</summary>
public sealed record PaperNoteImageInsertionResult(string PaperId, string ImageId, string MarkdownReference);

/// <summary>Accesses assets of built-in Markdown notes.</summary>
public interface IPaperNoteAssetsApi
{
    const int MaximumImageBytes = 16 * 1024 * 1024;
    const int MaximumImageAppendBytes = 8 * 1024 * 1024;

    /// <summary>Requires notes.read. Reads an image owned by the specified note.</summary>
    PaperNoteImage ReadImage(string paperId, string imageId);

    /// <summary>
    /// API 2.4. Requires notes.append. Saves encoded image bytes and appends a Markdown image
    /// at the end of the note. The optional image name is metadata.
    /// </summary>
    PaperNoteImageInsertionResult AppendImage(string paperId, byte[] imageBytes, string? imageName = null) =>
        throw new PaperTodoPluginException("note_assets_unavailable", "Appending note images is unavailable.");
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
