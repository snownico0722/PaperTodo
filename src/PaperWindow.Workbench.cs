namespace PaperTodo;

public sealed partial class PaperWindow
{
    /// <summary>
    /// Title of the external window this paper is currently attached to, for the workbench's
    /// "bound to another program" overview. Read-only runtime fact; never persisted.
    /// </summary>
    internal string? WorkbenchExternalAttachmentTitle =>
        _experimentalWindowAttachment is
        {
            TargetKind: ExperimentalAttachmentTargetKind.ExternalWindow
        } session
            ? string.IsNullOrWhiteSpace(session.TargetTitle) ? "" : session.TargetTitle
            : null;
}
