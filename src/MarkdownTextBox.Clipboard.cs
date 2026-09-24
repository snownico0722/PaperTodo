using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private sealed record ClipboardImagePayload(
        NoteImageAsset Asset,
        byte[] Bytes);

    private enum ImageAwareCopyResult
    {
        NotApplicable,
        Copied,
        TextOnlyDueToSize
    }

    public event Action? ImageCopyDegradedToText;

    static MarkdownTextBox()
    {
        // Keep clipboard shortcuts/paste policy outside the core editor implementation so the
        // Markdig semantic editor remains the authority for Markdown state. Class handlers run
        // before instance handlers and let this optional clipboard layer take ownership only when
        // PaperTodo image-reference semantics are involved.
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnClipboardPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            DataObject.PastingEvent,
            new DataObjectPastingEventHandler(OnImageAwarePasting),
            handledEventsToo: true);
    }

    private static void OnClipboardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MarkdownTextBox editor ||
            (e.Key != Key.C && e.Key != Key.Insert) ||
            Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        editor.Copy();
        e.Handled = true;
    }

    private static void OnImageAwarePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not MarkdownTextBox editor || editor.IsReadOnly)
        {
            return;
        }

        // AvalonEdit has already moved the caret to the real drop target and owns copy/move plus
        // undo grouping for text drag-drop. Mark this routed event handled only to keep the legacy
        // MarkdownTextBox paste handler from reinterpreting the source selection; do not cancel the
        // command, so AvalonEdit continues its native drop at the caret target.
        if (e.IsDragDrop)
        {
            e.Handled = true;
            return;
        }

        string? clipboardText;
        try
        {
            clipboardText = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : null;
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrEmpty(clipboardText))
        {
            // This class handler runs before the legacy instance paste handler. Reject obviously
            // unsafe text here so neither path can pay for a full Markdig parse first. Rectangle
            // selections use a surrounding segment, so assume no replacement there for a safe
            // conservative length preflight.
            var selectedLength = editor.TextArea.Selection is RectangleSelection
                ? 0
                : editor.SelectionLength;
            if (!editor.TryBuildSafePasteText(clipboardText, selectedLength, out _))
            {
                e.Handled = true;
                e.CancelCommand();
                editor.PasteRejected?.Invoke();
                return;
            }
        }

        // Rich image paste only supports one continuous source range. AvalonEdit already owns
        // segmented rectangular replacement correctly; suppress the legacy instance handler but
        // leave the command alive so its native rectangle paste completes without touching the
        // surrounding unselected text.
        if (editor.TextArea.Selection is RectangleSelection)
        {
            e.Handled = true;
            return;
        }

        if (string.IsNullOrEmpty(clipboardText) ||
            !ContainsPotentialPaperTodoImageReference(clipboardText))
        {
            return;
        }

        // Prevent the legacy instance paste handler from independently classifying the same image-
        // looking text. This handler performs the full target-context semantic check below.
        e.Handled = true;
        editor.HandleContextAwareImageReferencePaste(e, clipboardText);
    }

    private static bool ContainsPotentialPaperTodoImageReference(string text)
    {
        foreach (var line in EnumerateClipboardLines(text))
        {
            if (MarkdownImageReferences.TryParseReferenceLine(line.Text, out _))
            {
                return true;
            }
        }

        return false;
    }

    private void HandleContextAwareImageReferencePaste(
        DataObjectPastingEventArgs e,
        string clipboardText)
    {
        var replacementTarget = CaptureDocumentReplacementTarget();
        if (!TryBuildSafePasteText(
                clipboardText,
                replacementTarget.SelectionLength,
                out _))
        {
            e.CancelCommand();
            PasteRejected?.Invoke();
            return;
        }

        var pasteText = EnsureContextAwareImageReferencePasteIsBlock(
            clipboardText,
            replacementTarget);
        var candidateText = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            pasteText);
        var effectiveReferences = ImageReferencesInDocumentRange(
            candidateText,
            replacementTarget.Start,
            pasteText.Length);

        if (effectiveReferences.Count == 0)
        {
            // The source can look like a PaperTodo image reference while the target semantic context
            // makes it literal text (most importantly inside a fenced code block). Commit it as plain
            // text here so the older instance handler cannot reinterpret the standalone clipboard
            // fragment without its target context.
            if (!TryBuildSafePasteText(
                    clipboardText,
                    replacementTarget.SelectionLength,
                    out var safeText))
            {
                e.CancelCommand();
                PasteRejected?.Invoke();
                return;
            }

            try
            {
                e.CancelCommand();
                var caret = CommitDocumentReplacement(replacementTarget, safeText);
                CaretIndex = caret;
                Select(caret, 0);
                Focus();
                QueuePostPasteRefresh();
            }
            catch (Exception ex)
            {
                ImageImportFailed?.Invoke(ex);
                e.CancelCommand();
            }
            return;
        }

        try
        {
            if (!IsSafeImageReferencePaste(pasteText, replacementTarget))
            {
                e.CancelCommand();
                PasteRejected?.Invoke();
                return;
            }

            var maximumIdPasteText = ReplaceImageReferenceIdsAtReferences(
                pasteText,
                effectiveReferences,
                Enumerable.Repeat("99999999", effectiveReferences.Count).ToArray());
            if (!IsSafeImageReferencePaste(maximumIdPasteText, replacementTarget))
            {
                e.CancelCommand();
                PasteRejected?.Invoke();
                return;
            }

            if (_imageStore != null)
            {
                // Clone only references that are real images in the target document. Building a
                // standalone probe keeps the store transaction reusable without allowing code-block
                // literals to participate in id rewriting.
                var cloneProbe = BuildImageReferenceCloneProbe(pasteText, effectiveReferences);
                var clonedProbe = _imageStore.CloneForeignImageReferencesForNote(_noteId, cloneProbe);
                var clonedReferences = MarkdownImageReferences.Enumerate(clonedProbe).ToList();
                if (clonedReferences.Count != effectiveReferences.Count)
                {
                    throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
                }

                pasteText = ReplaceImageReferenceIdsAtReferences(
                    pasteText,
                    effectiveReferences,
                    clonedReferences.Select(reference => reference.ImageId).ToArray());
            }

            if (!IsSafeImageReferencePaste(pasteText, replacementTarget))
            {
                e.CancelCommand();
                PasteRejected?.Invoke();
                return;
            }

            e.CancelCommand();
            var committedCaret = CommitDocumentReplacement(replacementTarget, pasteText);
            CaretIndex = committedCaret;
            Select(committedCaret, 0);
            Focus();
            QueuePostPasteRefresh();
        }
        catch (Exception ex)
        {
            ImageImportFailed?.Invoke(ex);
            e.CancelCommand();
        }
    }

    public new void Copy()
    {
        var result = CopySelectionWithImagesToClipboard();
        if (result == ImageAwareCopyResult.Copied)
        {
            return;
        }

        base.Copy();
        if (result == ImageAwareCopyResult.TextOnlyDueToSize)
        {
            NotifyImageCopyDegradedToText();
        }
    }

    private void NotifyImageCopyDegradedToText()
    {
        if (ImageCopyDegradedToText is { } handler)
        {
            handler();
            return;
        }

        // The clipboard feature can live beside the Markdig refactor without modifying
        // PaperWindow.Note.cs. Keep the existing event contract, but provide the same user-facing
        // notice when no host subscriber has been wired yet.
        if (Window.GetWindow(this) is PaperWindow owner)
        {
            PaperNoticeDialog.Show(
                owner,
                Strings.Get("NoteCopyTextOnlyTitle"),
                Strings.Get("NoteCopyTextOnlyMessage"));
        }
    }

    private ImageAwareCopyResult CopySelectionWithImagesToClipboard()
    {
        // AvalonEdit exposes rectangular selections through the surrounding source segment.
        // Rich image copy only supports one continuous source range; let AvalonEdit preserve its
        // native rectangular-copy semantics instead of including unselected text between rows.
        if (TextArea.Selection is RectangleSelection)
        {
            return ImageAwareCopyResult.NotApplicable;
        }

        var documentText = Text ?? "";
        var selectionStart = Math.Clamp(SelectionStart, 0, documentText.Length);
        var selectionLength = Math.Clamp(SelectionLength, 0, documentText.Length - selectionStart);
        if (selectionLength <= 0)
        {
            return TryCopySelectedImageReferenceToClipboard();
        }

        var selected = documentText.Substring(selectionStart, selectionLength);
        var references = ImageReferencesInDocumentRange(
            documentText,
            selectionStart,
            selectionLength);
        return references.Count == 0
            ? ImageAwareCopyResult.NotApplicable
            : TrySetImageAwareClipboardData(selected, references);
    }

    private ImageAwareCopyResult TryCopySelectedImageReferenceToClipboard()
    {
        if (Document == null ||
            _selectedImageReferenceAnchor is not { IsDeleted: false } anchor ||
            string.IsNullOrWhiteSpace(_selectedImageId))
        {
            return ImageAwareCopyResult.NotApplicable;
        }

        try
        {
            var line = Document.GetLineByOffset(Math.Clamp(anchor.Offset, 0, Document.TextLength));
            if (!TryGetImageReferenceForStableEditingLine(line, out var parsed, out _) ||
                !string.Equals(parsed.ImageId, _selectedImageId, StringComparison.Ordinal))
            {
                return ImageAwareCopyResult.NotApplicable;
            }

            var markdown = Document.GetText(line);
            var reference = parsed with
            {
                LineStart = 0,
                LineLength = markdown.Length
            };
            return TrySetImageAwareClipboardData(markdown, new[] { reference });
        }
        catch
        {
            return ImageAwareCopyResult.NotApplicable;
        }
    }

    private ImageAwareCopyResult TrySetImageAwareClipboardData(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references)
    {
        var imageStore = _imageStore;
        if (imageStore == null ||
            string.IsNullOrWhiteSpace(_noteId) ||
            references.Count == 0)
        {
            return ImageAwareCopyResult.NotApplicable;
        }

        try
        {
            // Preflight from metadata before reading any blobs. Count every occurrence because
            // HTML embeds the encoded bytes once per rendered image occurrence.
            var assets = new Dictionary<string, NoteImageAsset>(StringComparer.Ordinal);
            long htmlSourceBytes = 0;
            foreach (var reference in references)
            {
                if (!assets.TryGetValue(reference.ImageId, out var asset))
                {
                    if (!imageStore.TryGetAsset(reference.ImageId, out asset) ||
                        !string.Equals(asset.NoteId, _noteId, StringComparison.Ordinal))
                    {
                        return ImageAwareCopyResult.NotApplicable;
                    }
                    assets.Add(reference.ImageId, asset);
                }

                if (asset.ByteLength <= 0)
                {
                    return ImageAwareCopyResult.NotApplicable;
                }
                if (htmlSourceBytes > MaxExternalClipboardHtmlSourceBytes - asset.ByteLength)
                {
                    return ImageAwareCopyResult.TextOnlyDueToSize;
                }
                htmlSourceBytes += asset.ByteLength;
            }

            var images = new Dictionary<string, ClipboardImagePayload>(StringComparer.Ordinal);
            foreach (var pair in assets)
            {
                if (!imageStore.TryGetEncodedImageBytes(pair.Key, out var asset, out var bytes) ||
                    !string.Equals(asset.NoteId, _noteId, StringComparison.Ordinal))
                {
                    return ImageAwareCopyResult.NotApplicable;
                }

                images.Add(pair.Key, new ClipboardImagePayload(asset, bytes));
            }

            var data = new DataObject();
            AddImageAwareExternalClipboardFormats(data, markdown, references, images);
            // Keep Markdown as the canonical PaperTodo-to-PaperTodo and plain-text representation.
            data.SetData(DataFormats.UnicodeText, markdown);
            data.SetData(DataFormats.Text, markdown);
            Clipboard.SetDataObject(data, copy: true);
            return ImageAwareCopyResult.Copied;
        }
        catch
        {
            return ImageAwareCopyResult.NotApplicable;
        }
    }

    private static string EnsureContextAwareImageReferencePasteIsBlock(
        string pasteText,
        DocumentReplacementTarget replacementTarget)
    {
        if (string.IsNullOrWhiteSpace(pasteText))
        {
            return pasteText;
        }

        var firstLineEnd = pasteText.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? pasteText : pasteText[..firstLineEnd];
        var lastLineStart = pasteText.LastIndexOfAny(['\r', '\n']) + 1;
        var lastLine = pasteText[lastLineStart..];
        var selectionStart = replacementTarget.Start;
        var selectionEnd = selectionStart + replacementTarget.SelectionLength;
        var couldNeedLeadingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(firstLine, out var firstReference) &&
            selectionStart > 0 &&
            replacementTarget.OriginalText[selectionStart - 1] is not '\r' and not '\n';
        var couldNeedTrailingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(lastLine, out var lastReference) &&
            (selectionEnd >= replacementTarget.OriginalText.Length ||
                replacementTarget.OriginalText[selectionEnd] is not '\r' and not '\n');
        if (!couldNeedLeadingNewLine && !couldNeedTrailingNewLine)
        {
            return pasteText;
        }

        // Probe both possible boundaries together. A single image pasted in the middle of a text
        // line needs both newlines before it can become a valid block image; testing one edge at a
        // time would incorrectly classify it as ordinary text.
        var leadingLength = couldNeedLeadingNewLine ? Environment.NewLine.Length : 0;
        var probe = string.Concat(
            couldNeedLeadingNewLine ? Environment.NewLine : "",
            pasteText,
            couldNeedTrailingNewLine ? Environment.NewLine : "");
        var candidate = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            probe);
        var probeReferences = ImageReferencesInDocumentRange(
            candidate,
            replacementTarget.Start,
            probe.Length);

        var addLeadingNewLine = couldNeedLeadingNewLine && probeReferences.Any(reference =>
            reference.LineStart == leadingLength &&
            reference.LineLength == firstLine.Length &&
            string.Equals(reference.ImageId, firstReference.ImageId, StringComparison.Ordinal));
        var lastReferenceStart = leadingLength + lastLineStart;
        var addTrailingNewLine = couldNeedTrailingNewLine && probeReferences.Any(reference =>
            reference.LineStart == lastReferenceStart &&
            reference.LineLength == lastLine.Length &&
            string.Equals(reference.ImageId, lastReference.ImageId, StringComparison.Ordinal));

        if (!addLeadingNewLine && !addTrailingNewLine)
        {
            return pasteText;
        }

        var builder = new StringBuilder(
            pasteText.Length +
            (addLeadingNewLine ? Environment.NewLine.Length : 0) +
            (addTrailingNewLine ? Environment.NewLine.Length : 0));
        if (addLeadingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        builder.Append(pasteText);
        if (addTrailingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    private static string BuildImageReferenceCloneProbe(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < references.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(Environment.NewLine);
            }

            var reference = references[index];
            builder.Append(markdown, reference.LineStart, reference.LineLength);
        }
        return builder.ToString();
    }

    private static string ReplaceImageReferenceIdsAtReferences(
        string markdown,
        IReadOnlyList<MarkdownImageReference> references,
        IReadOnlyList<string> replacementIds)
    {
        if (references.Count != replacementIds.Count)
        {
            throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
        }

        var additionalLength = 0L;
        for (var index = 0; index < references.Count; index++)
        {
            additionalLength += replacementIds[index].Length - references[index].ImageId.Length;
        }
        var capacity = checked((int)(markdown.Length + additionalLength));
        var builder = new StringBuilder(capacity);
        var cursor = 0;
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            var line = markdown.Substring(reference.LineStart, reference.LineLength);
            var imageToken = MarkdownImageReferences.UriPrefix + reference.ImageId;
            var urlMarker = line.IndexOf("](", StringComparison.Ordinal);
            var tokenStart = urlMarker >= 0
                ? line.IndexOf(imageToken, urlMarker + 2, StringComparison.Ordinal)
                : -1;
            if (tokenStart < 0)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }

            var idStart = tokenStart + MarkdownImageReferences.UriPrefix.Length;
            builder.Append(line, 0, idStart);
            builder.Append(replacementIds[index]);
            builder.Append(
                line,
                idStart + reference.ImageId.Length,
                line.Length - idStart - reference.ImageId.Length);
            cursor = reference.LineStart + reference.LineLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static IReadOnlyList<MarkdownImageReference> ImageReferencesInDocumentRange(
        string documentText,
        int rangeStart,
        int rangeLength)
    {
        var rangeEnd = rangeStart + rangeLength;
        var references = new List<MarkdownImageReference>();

        // Parse the complete document before intersecting the requested range. Markdig semantics
        // therefore carry the target fence/code state into this query instead of reparsing the
        // selected fragment as an isolated Markdown document.
        foreach (var reference in MarkdownImageReferences.Enumerate(documentText))
        {
            var referenceEnd = reference.LineStart + reference.LineLength;
            if (reference.LineStart < rangeStart || referenceEnd > rangeEnd)
            {
                continue;
            }

            references.Add(reference with
            {
                LineStart = reference.LineStart - rangeStart
            });
        }

        return references;
    }


    internal bool TryCopySelectionAsPlainText()
    {
        var markdown = TextArea.Selection.GetText();
        if (string.IsNullOrEmpty(markdown))
        {
            return false;
        }

        return ClipboardHelper.TrySetText(
            MarkdownSemanticSnapshot.ToPlainText(markdown));
    }
}
