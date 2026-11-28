using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    private const string NoteImageClipboardFormat = "PaperTodo.NoteImages.v1";
    // The internal format embeds each distinct image once. The wire limit includes Base64 and JSON.
    private const long MaxNoteImageSourceBytes = 24L * 1024 * 1024;
    private const int MaxNoteImageClipboardBytes = 33 * 1024 * 1024;

    private sealed record NoteImageClipboard(Guid SourceStore, string? SourceNote, Dictionary<string, byte[]> Images);

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
                var images = editor.ReadClipboardImages(store, references, out var tooLarge);
                if (tooLarge)
                {
                    editor.CancelIncompleteClipboardOperation(e);
                    return;
                }

                editor.WriteNoteImageClipboard(e.DataObject, store, images);
            }
        }
        catch
        {
            // AvalonEdit may delete the selected source as soon as this event returns.
            editor.CancelIncompleteClipboardOperation(e);
        }
    }

    private void CancelIncompleteClipboardOperation(DataObjectCopyingEventArgs e)
    {
        e.CancelCommand();
        if (Window.GetWindow(this) is PaperWindow owner)
            Dispatcher.BeginInvoke(new Action(() => PaperNoticeDialog.Show(owner,
                Strings.Get("NoteClipboardIncompleteTitle"), Strings.Get("NoteClipboardIncompleteMessage"))));
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

        var containsImageReference = ContainsPotentialPaperTodoImageReference(clipboardText);
        // Rectangular paste cannot credit the surrounding selection. Native whole-line paste
        // inserts at the start of the line; it also leaves the current selection intact.
        var insertsWholeLine = !containsImageReference && editor.Options.CutCopyWholeLine &&
            e.DataObject.GetDataPresent("MSDEVLineSelect");
        var selectedLength = insertsWholeLine || editor.TextArea.Selection is RectangleSelection ? 0 : editor.SelectionLength;
        var preflightText = clipboardText;
        if (!containsImageReference && clipboardText.Length <= MaxSafePasteLength)
        {
            // AvalonEdit normalizes line endings after this event, before inserting the text.
            var newLine = TextUtilities.GetNewLineFromDocument(editor.Document, editor.TextArea.Caret.Line);
            preflightText = TextUtilities.NormalizeNewLines(clipboardText, newLine);
        }
        if (!editor.TryBuildSafePasteText(preflightText, selectedLength, out _))
        {
            e.CancelCommand();
            editor.PasteRejected?.Invoke();
            return;
        }

        // Leave rectangular replacement to AvalonEdit. Its segmented paste cannot
        // transfer image blocks, so keep their source as plain text.
        if (editor.TextArea.Selection is RectangleSelection rectangle)
        {
            if (containsImageReference)
            {
                var plainText = editor.PrepareRectangularImagePaste(rectangle, clipboardText);
                if (plainText == null)
                {
                    e.CancelCommand();
                    editor.PasteRejected?.Invoke();
                    return;
                }
                if (plainText == clipboardText) return;

                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, plainText);
                e.DataObject = data;
                e.FormatToApply = DataFormats.UnicodeText;
                editor.Dispatcher.BeginInvoke(new Action(editor.NotifyImagePasteDegradedToText));
            }
            return;
        }

        if (!containsImageReference)
        {
            return;
        }

        editor.HandleContextAwareImageReferencePaste(e, clipboardText);
    }

    private string? PrepareRectangularImagePaste(RectangleSelection selection, string text)
    {
        // AvalonEdit repeats a single line across the rectangle. For multiline text the final
        // selected row receives the remaining lines. Parse that projection with its surrounding
        // Markdown so fences, indentation and unselected text decide which markers are active.
        var segments = selection.Segments.ToArray();
        if (segments.Length == 0) return text;
        var newline = TextUtilities.GetNewLineFromDocument(Document, TextArea.Caret.Line);
        var normalized = TextUtilities.NormalizeNewLines(text, newline);
        var repeat = !normalized.Contains(newline, StringComparison.Ordinal);
        if (repeat && (long)normalized.Length * segments.Length > MaxSafePasteLength)
            return null;

        var rows = repeat ? Enumerable.Repeat(normalized, segments.Length).ToArray()
            : normalized.Split([newline], segments.Length, StringSplitOptions.None);
        var original = Text;
        var candidate = new StringBuilder(original.Length);
        var starts = new int[rows.Length];
        var cursor = 0;
        var inserted = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var segment = segments[i];
            candidate.Append(original, cursor, segment.StartOffset - cursor);
            var line = Document.GetLineByOffset(segment.StartOffset);
            if (segment.StartOffset == line.EndOffset && rows[i].Length > 0)
            {
                var visual = TextArea.TextView.GetOrConstructVisualLine(line);
                var padding = Math.Max(0, segment.StartVisualColumn - visual.VisualLengthWithEndOfLineMarker);
                if (padding > MaxSafePasteLength - inserted) return null;
                // Spaces at these columns have the same Markdown indentation as Avalon's virtual tabs.
                candidate.Append(' ', padding);
                inserted += padding;
            }
            starts[i] = candidate.Length;
            inserted += rows[i].Length;
            if (inserted > MaxSafePasteLength) return null;
            candidate.Append(rows[i]);
            cursor = segment.EndOffset;
        }
        candidate.Append(original, cursor, original.Length - cursor);

        var projected = candidate.ToString();
        var markers = MarkdownImageReferences.Enumerate(projected)
            .Select(reference => reference.LineStart + reference.OriginalLine!.IndexOf('!')).ToArray();
        var index = markers.Length - 1;
        var escapes = 0;
        var lastEscapedMarker = -1;
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            var end = starts[i] + rows[i].Length;
            while (index >= 0 && markers[index] >= end) index--;
            while (index >= 0 && markers[index] >= starts[i])
            {
                if (lastEscapedMarker < 0) lastEscapedMarker = markers[index];
                rows[i] = rows[i].Insert(markers[index--] - starts[i], "\\");
                escapes++;
            }
        }
        if (inserted + escapes > MaxSafePasteLength ||
            rows.Any(row => ContainsLineLongerThan(row, MaxSafePasteLineLength))) return null;
        if (MaxLength > 0 && projected.Length + escapes > MaxLength)
        {
            var anchor = MarkdownImageReferences.TrailingAnchorDelimiterLength(projected);
            if (projected.Length + escapes - anchor > MaxLength) return null;
            // Escaping the last image also removes its trailing-newline allowance.
            if (lastEscapedMarker > projected.LastIndexOfAny(['\r', '\n'], projected.Length - anchor - 1))
                return null;
        }
        if (escapes == 0) return text;
        // Preserve native single-line column editing when every row needs the same replacement.
        if (repeat && rows.All(row => row == rows[0])) return rows[0];
        return string.Join(newline, rows);
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
            if (!tooLarge && images.Count > 0)
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
        var owned = new List<NoteImageAsset>();
        foreach (var id in references.Select(reference => reference.ImageId).Distinct(StringComparer.Ordinal))
        {
            if (store.TryGetAsset(id, out var asset) && asset.NoteId == _noteId && !store.IsImageCorrupted(id))
                owned.Add(asset);
        }

        tooLarge = owned.Sum(asset => (long)asset.ByteLength) > MaxNoteImageSourceBytes;
        var images = new Dictionary<string, ClipboardImagePayload>(StringComparer.Ordinal);
        foreach (var asset in owned)
        {
            if (tooLarge)
            {
                // A normal copy may still reuse these verified owners within this live store.
                images.Add(asset.Id, new ClipboardImagePayload(asset, []));
            }
            else if (store.TryGetEncodedImageBytes(asset.Id, out var current, out var bytes) && current.NoteId == _noteId)
            {
                images.Add(asset.Id, new ClipboardImagePayload(current, bytes));
            }
        }

        return images;
    }

    private void WriteNoteImageClipboard(IDataObject data, NoteImageStore store,
        IReadOnlyDictionary<string, ClipboardImagePayload> images)
    {
        var source = new NoteImageClipboard(store.ClipboardIdentity, _noteId,
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
        if (_imageStore is not { } store || source?.Images == null ||
            !source.Images.TryGetValue(imageId, out var bytes))
            return null;

        try
        {
            if (source.SourceStore == store.ClipboardIdentity && store.TryGetAsset(imageId, out var asset) &&
                asset.NoteId == source.SourceNote && !store.IsImageCorrupted(imageId))
            {
                var reference = MarkdownImageReferences.CreateReference(imageId);
                var cloned = store.CloneForeignImageReferencesForNote(_noteId, reference);
                return MarkdownImageReferences.TryParseLine(cloned, out var id) ? id : null;
            }

            return bytes is { Length: > 0 }
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
