namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private MarkdownSemanticDocument? _semanticDocument;

    internal void SetSemanticDocument(MarkdownSemanticDocument? semanticDocument)
    {
        if (ReferenceEquals(_semanticDocument, semanticDocument))
        {
            return;
        }

        if (_semanticDocument != null)
        {
            DisableSemanticTracking();
            DisableSemanticImagePresentation();
        }

        _semanticDocument = semanticDocument;
        if (_semanticDocument != null)
        {
            EnableSemanticTracking();
            EnableSemanticImagePresentation();
        }
    }

    private bool TryGetCurrentSemanticSnapshot(out MarkdownSemanticSnapshot snapshot)
    {
        snapshot = null!;
        return _semanticDocument != null &&
            _semanticDocument.TryGetCurrent(out snapshot);
    }
}
