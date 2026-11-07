using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private SemanticMarkdownImageElementGenerator? _semanticImageElementGenerator;
    private bool _semanticTrackingEnabled;

    internal bool ShouldHideImageReferenceTextForSemanticPresentation =>
        ShouldHideImageReferenceText;

    internal bool IsImageReferenceLineForSemanticPresentation(DocumentLine line)
    {
        return Document != null &&
            !line.IsDeleted &&
            TryParseImageReference(Document.GetText(line), out _);
    }

    private void EnableSemanticImagePresentation()
    {
        var generators = TextArea.TextView.ElementGenerators;
        _semanticImageElementGenerator ??= new SemanticMarkdownImageElementGenerator(this);
        var reuseIsActive = _reusingImageElementGenerator != null &&
            generators.Contains(_reusingImageElementGenerator);
        if (!reuseIsActive && !generators.Contains(_semanticImageElementGenerator))
        {
            generators.Add(_semanticImageElementGenerator);
        }

        RefreshTextView();
    }

    private void DisableSemanticImagePresentation()
    {
        if (_semanticImageElementGenerator != null)
        {
            TextArea.TextView.ElementGenerators.Remove(_semanticImageElementGenerator);
        }
        RefreshTextView();
    }

    private void EnableSemanticTracking()
    {
        if (_semanticTrackingEnabled)
        {
            return;
        }

        Document.Changed += OnSemanticDocumentChanged;
        _semanticTrackingEnabled = true;
    }

    private void DisableSemanticTracking()
    {
        if (!_semanticTrackingEnabled)
        {
            return;
        }

        Document.Changed -= OnSemanticDocumentChanged;
        _semanticTrackingEnabled = false;
    }

    private void OnSemanticDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_hadInternalImageReferences || e.InsertedText.TextLength <= 0)
        {
            return;
        }

        _hadInternalImageReferences = e.InsertedText.IndexOf(
            MarkdownImageReferences.UriPrefix,
            0,
            e.InsertedText.TextLength,
            StringComparison.Ordinal) >= 0;
    }

    private bool TryGetImageReferenceForLine(
        DocumentLine line,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        if (_imageStore == null ||
            Document == null ||
            !ShouldRenderImages ||
            line.IsDeleted ||
            !TryGetCurrentSemanticSnapshot(out var snapshot))
        {
            return false;
        }

        var semantic = snapshot.GetLine(Math.Max(0, line.LineNumber - 1));
        if (semantic.IsCode ||
            !TryParseImageReference(Document.GetText(line), out reference))
        {
            return false;
        }

        if (_imageStore.TryGetAsset(reference.ImageId, out var found) &&
            string.Equals(found.NoteId, _noteId, StringComparison.Ordinal))
        {
            asset = found;
        }

        return true;
    }

    private bool TryGetImageReferenceForStableEditingLine(
        DocumentLine line,
        out MarkdownImageReference reference,
        out NoteImageAsset? asset)
    {
        return TryGetImageReferenceForLine(line, out reference, out asset);
    }

    private sealed class SemanticMarkdownImageElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownTextBox _owner;

        public SemanticMarkdownImageElementGenerator(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return -1;
            }

            var document = CurrentContext.Document;
            if (document == null || document.TextLength <= 0)
            {
                return -1;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            return referenceLine.EndOffset >= startOffset &&
                _owner.TryGetImageReferenceForLine(referenceLine, out _, out _)
                ? referenceLine.EndOffset
                : -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            if (!_owner.ShouldRenderImages)
            {
                return null!;
            }

            var document = CurrentContext.Document;
            if (document == null || offset < 0 || offset > document.TextLength)
            {
                return null!;
            }

            var referenceLine = CurrentContext.VisualLine.FirstDocumentLine;
            if (referenceLine.EndOffset != offset ||
                !_owner.TryGetImageReferenceForLine(
                    referenceLine,
                    out var reference,
                    out var asset))
            {
                return null!;
            }

            var element = _owner.CreateImageBlock(reference, asset, referenceLine);
            return new BlockImageElement(element);
        }
    }
}
