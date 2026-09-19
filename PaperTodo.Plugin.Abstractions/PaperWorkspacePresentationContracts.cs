using System.Text.Json.Serialization;

namespace PaperTodo.Plugin;

/// <summary>Logical state after the host processed a presentation request, not animation completion.</summary>
public sealed record PaperPresentationResult(
    [property: JsonPropertyName("paper_id")] string PaperId,
    [property: JsonPropertyName("is_visible")] bool IsVisible,
    [property: JsonPropertyName("is_collapsed")] bool IsCollapsed);

/// <summary>
/// Optional API 2.2 capability for any existing paper, including other providers' papers.
/// Requires papers.presentation. Unlike the session-scoped IPaperPresentationApi, every call
/// takes an exact paper ID. The host owns windows, animations, focus, geometry and persistence.
/// Show preserves folding; Expand shows and unfolds; Collapse does not reveal a hidden paper.
/// Results describe logical state, not visual completion or a synchronous disk-save guarantee.
/// </summary>
public interface IPaperWorkspacePresentationApi
{
    PaperPresentationResult ShowPaper(string paperId, bool activate = true);
    PaperPresentationResult HidePaper(string paperId);
    PaperPresentationResult TogglePaperVisibility(string paperId, bool activate = true);
    PaperPresentationResult ExpandPaper(string paperId, bool activate = true);
    PaperPresentationResult CollapsePaper(string paperId);
    PaperPresentationResult TogglePaperCollapsed(string paperId, bool activate = true);
    PaperPresentationResult ActivatePaper(string paperId);
}
