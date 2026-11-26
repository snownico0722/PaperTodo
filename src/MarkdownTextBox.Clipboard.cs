using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private const string NoteImageClipboardFormat = "PaperTodo.NoteImages.v1";
    // The encoded images share the existing rich-copy budget. Allow for Base64 and JSON keys.
    private const int MaxNoteImageClipboardBytes = 33 * 1024 * 1024;

    private sealed record NoteImageClipboard(Guid SourceStore, Dictionary<string, byte[]> Images);

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
        // AvalonEdit owns ordinary text cut/copy/paste. PaperTodo handles the clipboard
        // image formats and uses the target's Markdown semantics for image blocks.
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnClipboardPreviewKeyDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MarkdownTextBox),
            DataObject.CopyingEvent,
            new DataObjectCopyingEventHandler(OnNativeClipboardCopying));
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

    private static void OnNativeClipboardCopying(object sender, DataObjectCopyingEventArgs e)
    {
        if (sender is not MarkdownTextBox editor || e.IsDragDrop ||
            editor._imageStore is not { } store || editor.TextArea.Selection is RectangleSelection)
        {
            return;
        }

        // AvalonEdit owns the cut (including removal and undo). Attach the same source data
        // before it publishes the clipboard, without performing another copy.
        try
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text ||
                !ContainsPotentialPaperTodoImageReference(text))
            {
                return;
            }

            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            var references = editor.SelectionLength > 0
                ? ImageReferencesInDocumentRange(editor.Text, editor.SelectionStart, editor.SelectionLength)
                : ImageReferencesInDocumentRange(editor.Text, line.Offset, line.Length);
            if (references.Count > 0)
            {
                var images = editor.ReadClipboardImages(store, references, out _);
                WriteNoteImageClipboard(e.DataObject, store, images);
            }
        }
        catch
        {
            // Plain-text cut/copy remains available if the optional image data cannot be written.
        }
    }

    private static void OnImageAwarePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not MarkdownTextBox editor || editor.IsReadOnly)
        {
            return;
        }

        // AvalonEdit owns native drag/drop placement and undo grouping.
        if (e.IsDragDrop) return;

        string? clipboardText;
        try
        {
            clipboardText = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : e.DataObject.GetData(DataFormats.Text) as string;
        }
        catch
        {
            e.CancelCommand();
            return;
        }

        if (string.IsNullOrEmpty(clipboardText))
        {
            if (editor.TryInsertImageFromClipboard(e.DataObject))
                e.CancelCommand();
            return;
        }

        // Rectangular paste owns segmented replacement. Preflight without subtracting the
        // surrounding selection range, since that range includes unselected text.
        var selectedLength = editor.TextArea.Selection is RectangleSelection ? 0 : editor.SelectionLength;
        if (!editor.TryBuildSafePasteText(clipboardText, selectedLength, out _))
        {
            e.CancelCommand();
            editor.PasteRejected?.Invoke();
            return;
        }

        // Leave rectangular replacement to AvalonEdit. Its segmented paste cannot
        // transfer image blocks, so keep their source as plain text.
        if (editor.TextArea.Selection is RectangleSelection)
        {
            if (ContainsPotentialPaperTodoImageReference(clipboardText))
            {
                var plainText = RewritePastedImageLines(clipboardText,
                    MarkdownImageReferences.Enumerate(clipboardText).ToArray(), AsPlainImageReference);
                if (!editor.TryBuildSafePasteText(plainText, selectedLength: 0, out _))
                {
                    e.CancelCommand();
                    editor.PasteRejected?.Invoke();
                    return;
                }

                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, plainText);
                e.DataObject = data;
                e.FormatToApply = DataFormats.UnicodeText;
                if (plainText != clipboardText)
                    editor.Dispatcher.BeginInvoke(new Action(editor.NotifyImagePasteDegradedToText));
            }
            return;
        }

        if (!ContainsPotentialPaperTodoImageReference(clipboardText))
        {
            return;
        }

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

        try
        {
            var imageMissing = false;
            if (effectiveReferences.Count == 0)
            {
                // Keep literal Markdown unchanged when the target treats it as code.
                pasteText = clipboardText;
            }
            else
            {
                var maximumIdPasteText = RewritePastedImageLines(pasteText, effectiveReferences,
                    reference => reference.WithUrl(MarkdownImageReferences.UriPrefix + "99999999"));
                if (!IsSafeImageReferencePaste(maximumIdPasteText, replacementTarget))
                {
                    e.CancelCommand();
                    PasteRejected?.Invoke();
                    return;
                }

                var source = ReadNoteImageClipboard(e.DataObject);
                var replacements = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var reference in effectiveReferences)
                {
                    if (!replacements.ContainsKey(reference.ImageId))
                        replacements.Add(reference.ImageId, TryPasteClipboardImage(source, reference.ImageId));
                }

                pasteText = RewritePastedImageLines(pasteText, effectiveReferences, reference =>
                    replacements[reference.ImageId] is { } id
                        ? reference.WithUrl(MarkdownImageReferences.UriPrefix + id)
                        : AsPlainImageReference(reference));
                imageMissing = replacements.ContainsValue(null);

                if (!IsSafeImageReferencePaste(pasteText, replacementTarget))
                {
                    e.CancelCommand();
                    PasteRejected?.Invoke();
                    return;
                }
            }

            e.CancelCommand();
            var committedCaret = CommitDocumentReplacement(replacementTarget, pasteText);
            CaretIndex = committedCaret;
            Select(committedCaret, 0);
            Focus();
            QueuePostPasteRefresh();
            if (imageMissing)
                Dispatcher.BeginInvoke(new Action(NotifyImagePasteDegradedToText));
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
        if (result == ImageAwareCopyResult.NotApplicable)
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
            if (!TryGetImageReferenceForLine(line, out var parsed, out _) ||
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
            var images = ReadClipboardImages(imageStore, references, out var tooLarge);
            var data = new DataObject();
            if (images.Count > 0)
            {
                AddImageAwareExternalClipboardFormats(data, markdown,
                    references.Where(reference => images.ContainsKey(reference.ImageId)).ToArray(), images);
            }

            WriteNoteImageClipboard(data, imageStore, images);
            // Keep Markdown as the canonical plain-text representation for other applications.
            data.SetData(DataFormats.UnicodeText, markdown);
            data.SetData(DataFormats.Text, markdown);
            Clipboard.SetDataObject(data, copy: true);
            return tooLarge ? ImageAwareCopyResult.TextOnlyDueToSize : ImageAwareCopyResult.Copied;
        }
        catch
        {
            return ImageAwareCopyResult.NotApplicable;
        }
    }

    private Dictionary<string, ClipboardImagePayload> ReadClipboardImages(NoteImageStore store,
        IReadOnlyList<MarkdownImageReference> references, out bool tooLarge)
    {
        // CF_HTML embeds each occurrence, even when the same stored image is referenced twice.
        var assets = new Dictionary<string, NoteImageAsset>(StringComparer.Ordinal);
        long htmlSourceBytes = 0;
        foreach (var reference in references)
        {
            if (!assets.TryGetValue(reference.ImageId, out var asset))
            {
                if (!store.TryGetAsset(reference.ImageId, out asset) || asset.NoteId != _noteId)
                    continue;
                assets.Add(reference.ImageId, asset);
            }

            htmlSourceBytes += asset.ByteLength;
        }

        var images = new Dictionary<string, ClipboardImagePayload>(StringComparer.Ordinal);
        tooLarge = htmlSourceBytes > MaxExternalClipboardHtmlSourceBytes;
        if (tooLarge) return images;

        foreach (var id in assets.Keys)
        {
            if (store.TryGetEncodedImageBytes(id, out var asset, out var bytes) && asset.NoteId == _noteId)
                images.Add(id, new ClipboardImagePayload(asset, bytes));
        }

        return images;
    }

    private static void WriteNoteImageClipboard(IDataObject data, NoteImageStore store,
        IReadOnlyDictionary<string, ClipboardImagePayload> images)
    {
        var source = new NoteImageClipboard(store.ClipboardIdentity,
            images.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes, StringComparer.Ordinal));
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, source);
        stream.Position = 0;
        data.SetData(NoteImageClipboardFormat, stream, autoConvert: false);
    }

    private static NoteImageClipboard? ReadNoteImageClipboard(IDataObject data)
    {
        try
        {
            if (data.GetData(NoteImageClipboardFormat, autoConvert: false) is Stream stream &&
                stream.CanSeek && stream.Length <= MaxNoteImageClipboardBytes)
            {
                stream.Position = 0;
                return JsonSerializer.Deserialize<NoteImageClipboard>(stream);
            }
        }
        catch
        {
            // The source can lose its custom clipboard format. Paste its text in that case.
        }

        return null;
    }

    private string? TryPasteClipboardImage(NoteImageClipboard? source, string imageId)
    {
        if (source == null || _imageStore is not { } store)
            return null;

        try
        {
            if (source.SourceStore == store.ClipboardIdentity && store.TryGetAsset(imageId, out _))
            {
                var reference = MarkdownImageReferences.CreateReference(imageId);
                var cloned = store.CloneForeignImageReferencesForNote(_noteId, reference);
                return MarkdownImageReferences.TryParseLine(cloned, out var id) ? id : null;
            }

            return source.Images?.TryGetValue(imageId, out var bytes) == true
                ? store.ImportImageBytes(_noteId, bytes, "").Id
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException
                                   or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void NotifyImagePasteDegradedToText()
    {
        if (Window.GetWindow(this) is PaperWindow owner)
        {
            PaperNoticeDialog.Show(owner, Strings.Get("NotePasteImageTextOnlyTitle"),
                Strings.Get("NotePasteImageTextOnlyMessage"));
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

    private static string RewritePastedImageLines(string markdown,
        IReadOnlyList<MarkdownImageReference> references, Func<MarkdownImageReference, string> rewrite)
    {
        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var reference in references)
        {
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            builder.Append(rewrite(reference));
            cursor = reference.LineStart + reference.LineLength;
        }
        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static string AsPlainImageReference(MarkdownImageReference reference)
    {
        var line = reference.OriginalLine!;
        var imageStart = line.Length - line.TrimStart().Length;
        return line.Insert(imageStart, "\\");
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
