using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Brushes = System.Windows.Media.Brushes;
using TextWrapping = System.Windows.TextWrapping;

namespace PaperTodo;

public sealed partial class MarkdownTextBox : TextEditor
{
    private const int MaxSafePasteLength = 30000;
    private const int MaxSafePasteLineLength = 6000;
    private const int MaxClipboardEncodedImageBytes = 32 * 1024 * 1024;
    private const double ImageBlockVerticalPadding = 7;
    private const double ImageBlockHorizontalPadding = 2;
    private static readonly string[] EncodedClipboardImageFormats =
        ["PNG", "image/png", "JFIF", "image/jpeg", "image/jpg", "GIF", "image/gif"];

    private bool _acceptsReturn = true;
    private bool _acceptsTab = true;
    private bool _isPreviewMode;
    private bool _isPostPasteRefreshQueued;
    private bool _isImageRenderRedrawQueued;
    private bool _isMarkdownSuffixRedrawQueued;
    private bool _isEnsuringImageAnchorLine;
    private string _imageReferenceTextMode = ImageReferenceTextModes.Always;
    private bool _hadInternalImageReferences;
    private int _queuedMarkdownRedrawStartLine = int.MaxValue;
    private readonly HashSet<int> _queuedImageRedrawLines = new();
    private TextAnchor? _selectedImageReferenceAnchor;
    private string? _selectedImageId;
    private double _textZoom = 1.0;
    private string _noteId = "";
    private NoteImageStore? _imageStore;

    public event Action<Exception>? ImageImportFailed;

    public event Action? PasteRejected;

    public event Action? ImageContextMenuClosed;

    public bool IsImageContextMenuOpen { get; private set; }

    internal Func<ContextMenu>? ImageContextMenuFactory { get; set; }

    public MarkdownTextBox()
    {
        FontFamily = NoteTypography.FontFamily;
        FontSize = NoteTypography.FontSize;
        FontStyle = NoteTypography.FontStyle;
        FontWeight = NoteTypography.FontWeight;
        FontStretch = NoteTypography.FontStretch;
        Language = NoteTypography.Language;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        TextArea.Margin = new Thickness(0);
        TextArea.Language = NoteTypography.Language;
        TextArea.TextView.Language = NoteTypography.Language;
        WordWrap = true;
        ShowLineNumbers = false;
        TextArea.LeftMargins.Clear();
        ApplyTypographyRendering();

        Options.ConvertTabsToSpaces = false;
        Options.IndentationSize = 4;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;

        // Rebuild viewport pin set after visual lines change so on-screen decodes skip LRU eviction.
        TextArea.TextView.VisualLinesChanged += (_, _) => QueueRefreshViewportProtectedBitmaps();
        DataObject.AddPastingHandler(this, OnPaste);
        SizeChanged += (_, _) => HandleImageViewportSizeChanged();
        RefreshVisualStyle();
    }

    public int MaxLength { get; set; }

    public bool AcceptsReturn
    {
        get => _acceptsReturn;
        set => _acceptsReturn = value;
    }

    public bool AcceptsTab
    {
        get => _acceptsTab;
        set => _acceptsTab = value;
    }

    public TextWrapping TextWrapping
    {
        get => WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        set => WordWrap = value != TextWrapping.NoWrap;
    }

    public int CaretIndex
    {
        get => CaretOffset;
        set => CaretOffset = Math.Clamp(value, 0, Text.Length);
    }

    public Brush? CaretBrush
    {
        get => TextArea.Caret.CaretBrush;
        set => TextArea.Caret.CaretBrush = value;
    }

    public bool IsPreviewMode => _isPreviewMode;

    public string PersistentText => Text;

    public void ConfigureNoteImages(string noteId, NoteImageStore imageStore)
    {
        if (_imageStore != null &&
            !string.IsNullOrWhiteSpace(_noteId) &&
            !string.Equals(_noteId, noteId, StringComparison.Ordinal))
        {
            _imageStore.SetViewportProtectedBitmapIds(_noteId, Array.Empty<string>());
        }

        _noteId = noteId ?? "";
        _imageStore = imageStore;
        EnsureTrailingImageAnchorLine();
        _hadInternalImageReferences = HasInternalImageReference(Document.Text);
        RefreshTextView();
        QueueRefreshViewportProtectedBitmaps();
    }

    private double ScaledFontSize(double baseFontSize)
    {
        return Math.Round(baseFontSize * _textZoom, 1);
    }

    public void SetTextZoom(double zoom)
    {
        var normalized = Math.Clamp(zoom, 0.5, 1.5);
        if (Math.Abs(_textZoom - normalized) < 0.001 && Math.Abs(FontSize - ScaledFontSize(NoteTypography.FontSize)) < 0.001)
        {
            return;
        }

        _textZoom = normalized;
        FontSize = ScaledFontSize(NoteTypography.FontSize);
        RefreshVisualStyle();
    }

    public string MarkdownRenderMode => _markdownRenderMode;

    /// <summary>当前渲染档位是否为 Full（所见即所得块级编辑态）。</summary>
    public bool RenderModeIsFull =>
        string.Equals(_markdownRenderMode, MarkdownRenderModes.Full, StringComparison.Ordinal);

    private string _markdownRenderMode = MarkdownRenderModes.Enhanced;

    public void SetPreviewMode(bool isPreviewMode)
    {
        _isPreviewMode = isPreviewMode;
        IsReadOnly = isPreviewMode;
        Focusable = !isPreviewMode;
        TextArea.Focusable = !isPreviewMode;
        SetInteractionCursor(isPreviewMode ? Cursors.Arrow : Cursors.IBeam);
        RefreshVisualStyle();
    }

    public void SetMarkdownRenderMode(string mode)
    {
        _markdownRenderMode = MarkdownRenderModes.IsValid(mode)
            ? mode
            : MarkdownRenderModes.Enhanced;
        RefreshVisualStyle();
    }

    public void SetImageReferenceTextMode(string mode)
    {
        var normalized = ImageReferenceTextModes.Normalize(mode);
        if (_imageReferenceTextMode == normalized)
        {
            return;
        }

        _imageReferenceTextMode = normalized;
        RefreshTextView();
    }

    public void SetInteractionCursor(Cursor cursor)
    {
        Cursor = cursor;
        TextArea.Cursor = cursor;
        TextArea.TextView.Cursor = cursor;
    }

    public void RefreshVisualStyle()
    {
        Foreground = Theme.TextBrush;
        CaretBrush = _isPreviewMode || HasSelectedImageReference
            ? Brushes.Transparent
            : Theme.TextBrush;
        TextArea.TextView.LinkTextForegroundBrush = Theme.LinkBrush;
        RefreshTextView();
    }

    public void RefreshTypography()
    {
        FontFamily = NoteTypography.FontFamily;
        FontStyle = NoteTypography.FontStyle;
        FontWeight = NoteTypography.FontWeight;
        FontStretch = NoteTypography.FontStretch;
        Language = NoteTypography.Language;
        TextArea.Language = NoteTypography.Language;
        TextArea.TextView.Language = NoteTypography.Language;
        ApplyTypographyRendering();
        FontSize = ScaledFontSize(NoteTypography.FontSize);
        RefreshVisualStyle();
    }

    private void ApplyTypographyRendering()
    {
        NoteTypography.ApplyTextRendering(this);
        NoteTypography.ApplyTextRendering(TextArea);
        NoteTypography.ApplyTextRendering(TextArea.TextView);
    }

    private void RefreshTextView()
    {
        var textView = TextArea.TextView;
        if (Document != null && Document.TextLength > 0)
        {
            textView.Redraw(0, Document.TextLength, System.Windows.Threading.DispatcherPriority.Render);
        }
        else
        {
            textView.Redraw(System.Windows.Threading.DispatcherPriority.Render);
        }
        textView.InvalidateMeasure();
        textView.InvalidateArrange();

        if (IsLoaded)
        {
            textView.UpdateLayout();
            textView.EnsureVisualLines();
        }

        textView.InvalidateLayer(KnownLayer.Background);
        textView.InvalidateLayer(KnownLayer.Text);
        textView.InvalidateLayer(KnownLayer.Caret);
        textView.InvalidateLayer(KnownLayer.Background, System.Windows.Threading.DispatcherPriority.Render);
        textView.InvalidateLayer(KnownLayer.Text, System.Windows.Threading.DispatcherPriority.Render);
        textView.InvalidateLayer(KnownLayer.Caret, System.Windows.Threading.DispatcherPriority.Render);
        textView.InvalidateVisual();
        TextArea.InvalidateVisual();
        InvalidateVisual();
    }

    public void WrapSelection(string prefix, string suffix)
    {
        var start = SelectionStart;
        var length = SelectionLength;
        var selected = SelectedText ?? "";
        var wrapEachLine =
            length > 0 &&
            HasLineBreak(selected) &&
            IsSingleLineMarker(prefix) &&
            IsSingleLineMarker(suffix);
        var replacement = wrapEachLine
            ? WrapEachSelectedLine(selected, prefix, suffix)
            : prefix + selected + suffix;

        SelectedText = replacement;
        Focus();

        if (length == 0)
        {
            Select(start + prefix.Length, 0);
        }
        else if (wrapEachLine)
        {
            Select(start, replacement.Length);
        }
        else
        {
            Select(start + prefix.Length, length);
        }
    }

    private static bool HasLineBreak(string text)
    {
        return text.IndexOfAny(['\r', '\n']) >= 0;
    }

    private static bool IsSingleLineMarker(string marker)
    {
        return marker.IndexOfAny(['\r', '\n']) < 0;
    }

    private static string WrapEachSelectedLine(string selected, string prefix, string suffix)
    {
        var builder = new StringBuilder(selected.Length + prefix.Length + suffix.Length);
        var index = 0;

        while (index < selected.Length)
        {
            var lineStart = index;
            while (index < selected.Length && selected[index] != '\r' && selected[index] != '\n')
            {
                index++;
            }

            var line = selected[lineStart..index];
            builder.Append(string.IsNullOrWhiteSpace(line) ? line : prefix + line + suffix);

            if (index >= selected.Length)
            {
                break;
            }

            if (selected[index] == '\r' && index + 1 < selected.Length && selected[index + 1] == '\n')
            {
                builder.Append("\r\n");
                index += 2;
            }
            else
            {
                builder.Append(selected[index]);
                index++;
            }
        }

        return builder.ToString();
    }

    public void InsertMarkdownLink()
    {
        var start = SelectionStart;
        var selected = string.IsNullOrWhiteSpace(SelectedText) ? Strings.Get("MarkdownDefaultLinkLabel") : SelectedText;
        var markdown = $"[{selected}](https://)";

        SelectedText = markdown;
        Focus();

        var urlStart = start + markdown.LastIndexOf("https://", StringComparison.Ordinal);
        Select(urlStart, "https://".Length);
    }

    public void InsertLinePrefix(string prefix)
    {
        if (Document == null)
        {
            return;
        }

        var start = Math.Clamp(SelectionStart, 0, Document.TextLength);
        var lineStart = Document.GetLineByOffset(start).Offset;
        Select(lineStart, 0);
        SelectedText = prefix;

        Focus();
        Select(start + prefix.Length, 0);
    }

    public int GetFirstVisibleLineIndex()
    {
        EnsureVisualLines();
        var lines = TextArea.TextView.VisualLines;
        if (lines.Count > 0)
        {
            return Math.Max(0, lines[0].FirstDocumentLine.LineNumber - 1);
        }

        return GetLineIndexFromCharacterIndex(CaretIndex);
    }

    public int GetLastVisibleLineIndex()
    {
        EnsureVisualLines();
        var lines = TextArea.TextView.VisualLines;
        if (lines.Count > 0)
        {
            return Math.Max(0, lines[^1].LastDocumentLine.LineNumber - 1);
        }

        return GetLineIndexFromCharacterIndex(CaretIndex);
    }

    public int GetLineIndexFromCharacterIndex(int charIndex)
    {
        if (Document == null || Document.TextLength == 0)
        {
            return 0;
        }

        var offset = Math.Clamp(charIndex, 0, Document.TextLength);
        return Math.Max(0, Document.GetLineByOffset(offset).LineNumber - 1);
    }

    public Rect GetRectFromCharacterIndex(int charIndex, bool trailingEdge)
    {
        if (Document == null)
        {
            return Rect.Empty;
        }

        EnsureVisualLines();

        var offset = Math.Clamp(charIndex, 0, Document.TextLength);
        var location = Document.GetLocation(offset);
        var position = new TextViewPosition(location);
        var top = TextArea.TextView.GetVisualPosition(position, VisualYPosition.LineTop);
        var bottom = TextArea.TextView.GetVisualPosition(position, VisualYPosition.LineBottom);
        var lineHeight = Math.Max(1, bottom.Y - top.Y);
        var left = Math.Max(0, top.X - HorizontalOffset);
        var y = top.Y - VerticalOffset;

        return new Rect(left, y, 1, lineHeight);
    }

    public bool TryGetCharacterIndexFromPoint(Point point, out int charIndex)
    {
        charIndex = CaretIndex;
        if (Document == null)
        {
            return false;
        }

        try
        {
            EnsureVisualLines();
            var position = GetPositionFromPoint(point);
            if (position == null)
            {
                return false;
            }

            charIndex = Math.Clamp(Document.GetOffset(position.Value.Location), 0, Text.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetImageCaretFromSource(DependencyObject? source, out int caretIndex)
    {
        caretIndex = 0;
        if (Document == null ||
            !TryGetImageBlockTagFromSource(source, out var tag) ||
            tag.CaretAnchor.IsDeleted)
        {
            return false;
        }

        caretIndex = Math.Clamp(tag.CaretAnchor.Offset, 0, Document.TextLength);
        return true;
    }

    public bool TryGetImageCaretFromPoint(Point point, out int caretIndex)
    {
        caretIndex = 0;
        if (Document == null ||
            !TryGetImageBlockTagFromPoint(point, out var tag) ||
            tag.CaretAnchor.IsDeleted)
        {
            return false;
        }

        caretIndex = Math.Clamp(tag.CaretAnchor.Offset, 0, Document.TextLength);
        return true;
    }

    public bool TryGetImageReferenceFromSource(
        DependencyObject? source,
        out int referenceOffset,
        out string imageId)
    {
        referenceOffset = 0;
        imageId = "";
        if (Document == null ||
            !TryGetImageBlockTagFromSource(source, out var tag) ||
            tag.ReferenceAnchor.IsDeleted)
        {
            return false;
        }

        referenceOffset = Math.Clamp(tag.ReferenceAnchor.Offset, 0, Document.TextLength);
        imageId = tag.ImageId;
        return true;
    }

    public bool TryGetImageReferenceFromPoint(
        Point point,
        out int referenceOffset,
        out string imageId)
    {
        referenceOffset = 0;
        imageId = "";
        if (Document == null ||
            !TryGetImageBlockTagFromPoint(point, out var tag) ||
            tag.ReferenceAnchor.IsDeleted)
        {
            return false;
        }

        referenceOffset = Math.Clamp(tag.ReferenceAnchor.Offset, 0, Document.TextLength);
        imageId = tag.ImageId;
        return true;
    }

    private bool TryGetImageBlockTagFromSource(DependencyObject? source, out ImageBlockTag tag)
    {
        tag = null!;
        if (source == null)
        {
            return false;
        }

        var current = source;
        while (current != null && !ReferenceEquals(current, this))
        {
            if (current is FrameworkElement { Tag: ImageBlockTag found })
            {
                tag = found;
                return true;
            }

            current = VisualParentOf(current);
        }

        return false;
    }

    private bool TryGetImageBlockTagFromPoint(Point point, out ImageBlockTag tag)
    {
        tag = null!;
        try
        {
            EnsureVisualLines();
            TextArea.TextView.UpdateLayout();
            return TryFindImageBlockAt(TextArea.TextView, point, out tag);
        }
        catch
        {
            return false;
        }
    }

    private bool TryFindImageBlockAt(DependencyObject node, Point editorPoint, out ImageBlockTag tag)
    {
        tag = null!;
        if (node is FrameworkElement { Tag: ImageBlockTag found } element &&
            IsPointInsideElement(element, editorPoint))
        {
            tag = found;
            return true;
        }

        var count = 0;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (TryFindImageBlockAt(VisualTreeHelper.GetChild(node, i), editorPoint, out tag))
            {
                return true;
            }
        }

        return false;
    }

    public bool SelectImageReference(int referenceOffset, string imageId)
    {
        if (Document == null || string.IsNullOrWhiteSpace(imageId))
        {
            return false;
        }

        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(Math.Clamp(referenceOffset, 0, Document.TextLength));
        }
        catch
        {
            return false;
        }

        if (!MarkdownImageReferences.TryParseLine(Document.GetText(line), out var parsedId) ||
            !string.Equals(parsedId, imageId, StringComparison.Ordinal))
        {
            return false;
        }

        var previousLineNumber = SelectedImageReferenceLineNumber();
        ClearImageSelection(redraw: false);
        var anchor = Document.CreateAnchor(line.Offset);
        anchor.MovementType = AnchorMovementType.BeforeInsertion;
        anchor.SurviveDeletion = false;
        _selectedImageReferenceAnchor = anchor;
        _selectedImageId = imageId;
        Focus();
        Select(line.Offset, 0);
        CaretBrush = Brushes.Transparent;
        TextArea.Caret.DesiredXPos = double.NaN;
        if (previousLineNumber.HasValue)
        {
            QueueImageLineRedraw(previousLineNumber.Value);
        }
        QueueImageLineRedraw(line.LineNumber);
        return true;
    }

    public void ClearImageSelection()
        => ClearImageSelection(redraw: true);

    private void ClearImageSelection(bool redraw)
    {
        if (_selectedImageReferenceAnchor == null && string.IsNullOrEmpty(_selectedImageId))
        {
            return;
        }

        var lineNumber = redraw ? SelectedImageReferenceLineNumber() : null;
        _selectedImageReferenceAnchor = null;
        _selectedImageId = null;
        CaretBrush = _isPreviewMode ? Brushes.Transparent : Theme.TextBrush;
        if (lineNumber.HasValue)
        {
            QueueImageLineRedraw(lineNumber.Value);
        }
    }

    private int? SelectedImageReferenceLineNumber()
    {
        if (_selectedImageReferenceAnchor is not { IsDeleted: false } anchor || Document == null)
        {
            return null;
        }

        try
        {
            return Document.GetLineByOffset(Math.Clamp(anchor.Offset, 0, Document.TextLength)).LineNumber;
        }
        catch
        {
            return null;
        }
    }

    private bool HasSelectedImageReference =>
        _selectedImageReferenceAnchor is { IsDeleted: false } &&
        !string.IsNullOrWhiteSpace(_selectedImageId);

    private bool IsPointInsideElement(FrameworkElement element, Point editorPoint)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = element
                .TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Contains(editorPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static DependencyObject? VisualParentOf(DependencyObject current)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent != null)
            {
                return parent;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return current is FrameworkElement element
            ? element.Parent as DependencyObject
            : null;
    }

    public bool TryPlaceCaretOnImageFromSource(DependencyObject? source)
    {
        if (!TryGetImageCaretFromSource(source, out var caretIndex))
        {
            return false;
        }

        PlaceCaretAfterImage(caretIndex);
        return true;
    }

    public bool TryPlaceCaretOnImageFromPoint(Point point)
    {
        if (!TryGetImageCaretFromPoint(point, out var caretIndex))
        {
            return false;
        }

        PlaceCaretAfterImage(caretIndex);
        return true;
    }

    public void PlaceCaretAfterImage(int caretIndex)
    {
        if (Document == null)
        {
            return;
        }

        var safeOffset = EnsureImageCaretLine(Math.Clamp(caretIndex, 0, Document.TextLength));
        ApplyImageCaretOffset(safeOffset);
    }

    private void ApplyImageCaretOffset(int safeOffset)
    {
        if (Document == null)
        {
            return;
        }

        Focus();
        safeOffset = Math.Clamp(safeOffset, 0, Document.TextLength);
        CaretOffset = safeOffset;
        Select(safeOffset, 0);
        var caretLine = Document.GetLineByOffset(safeOffset);
        if (safeOffset == caretLine.Offset)
        {
            TextArea.Caret.VisualColumn = 0;
        }
        TextArea.Caret.DesiredXPos = double.NaN;
    }

    private int EnsureImageCaretLine(int caretOffset)
    {
        if (Document == null)
        {
            return caretOffset;
        }

        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(caretOffset);
        }
        catch
        {
            return Math.Clamp(caretOffset, 0, Document.TextLength);
        }

        if (caretOffset == Document.TextLength &&
            caretOffset == line.EndOffset &&
            TryGetImageReferenceForLine(line, out _, out _))
        {
            var newLine = NewLineTextFor(line);
            // The trailing delimiter is structural and is excluded by CurrentTextLengthLimit().
            if (MaxLength > 0 && Text.Length > MaxLength)
            {
                return caretOffset;
            }

            Document.Insert(caretOffset, newLine);
            return caretOffset + newLine.Length;
        }

        if (caretOffset == line.Offset &&
            line.PreviousLine != null &&
            TryGetImageReferenceForLine(line.PreviousLine, out _, out _) &&
            TryGetImageReferenceForLine(line, out _, out _))
        {
            var newLine = NewLineTextFor(line.PreviousLine);
            if (MaxLength <= 0 || Text.Length + newLine.Length <= CurrentTextLengthLimit())
            {
                Document.Insert(caretOffset, newLine);
            }
        }

        return caretOffset;
    }

    public new void ScrollToLine(int lineIndex)
    {
        base.ScrollToLine(Math.Max(1, lineIndex + 1));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);

        if (_selectedImageReferenceAnchor?.IsDeleted == true)
        {
            ClearImageSelection(redraw: false);
        }

        if (!_isEnsuringImageAnchorLine)
        {
            EnsureTrailingImageAnchorLine();
        }
    }

    private static void GetAffectedLineRange(
        TextDocument document,
        int offset,
        int length,
        out int startLine,
        out int endLine)
    {
        var startOffset = Math.Clamp(offset, 0, document.TextLength);
        var endOffset = Math.Clamp(offset + Math.Max(0, length), 0, document.TextLength);
        startLine = document.GetLineByOffset(startOffset).LineNumber;
        endLine = document.GetLineByOffset(endOffset).LineNumber;
    }

    public bool CanInsertImagesFromDataObject(IDataObject dataObject)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId))
        {
            return false;
        }

        // External dragging can temporarily move keyboard focus away, which puts the note
        // into preview mode. This method only identifies supported data; the drop handler
        // enters edit mode before the guarded insertion methods are called.
        if (TryGetImageFileDrop(dataObject, out var paths) && paths.Count > 0)
        {
            return true;
        }

        // DragOver runs for every pointer move. It must only inspect advertised formats;
        // retrieving or decoding the payload here can repeatedly allocate a full bitmap.
        return HasBitmapDataFormat(dataObject);
    }

    public bool ValidateTextDrop(IDataObject dataObject)
    {
        if (IsReadOnly)
        {
            return true;
        }

        string? text;
        try
        {
            text = dataObject.GetDataPresent(DataFormats.UnicodeText)
                ? dataObject.GetData(DataFormats.UnicodeText) as string
                : dataObject.GetDataPresent(DataFormats.Text)
                    ? dataObject.GetData(DataFormats.Text) as string
                    : null;
        }
        catch
        {
            PasteRejected?.Invoke();
            return false;
        }

        if (string.IsNullOrEmpty(text) || TryBuildSafePasteText(text, selectedLength: 0, out _))
        {
            return true;
        }

        PasteRejected?.Invoke();
        return false;
    }

    public bool TryInsertImagesFromDataObject(IDataObject dataObject)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId) || IsReadOnly)
        {
            return false;
        }

        try
        {
            if (TryGetImageFileDrop(dataObject, out var paths) && paths.Count > 0)
            {
                InsertImagesFromFiles(paths);
                return true;
            }

            if (TryGetBitmapSource(dataObject, out var bitmap) && bitmap != null)
            {
                ImportAndInsertBitmap(bitmap);
                return true;
            }
        }
        catch (Exception ex)
        {
            ImageImportFailed?.Invoke(ex);
            return true;
        }

        return false;
    }

    public bool TryInsertImageFromClipboard(IDataObject? clipboardData = null)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId) || IsReadOnly)
        {
            return false;
        }

        BitmapSource? bitmap = null;
        try
        {
            clipboardData ??= Clipboard.GetDataObject();
            if (clipboardData != null &&
                TryInsertImagesFromDataObject(clipboardData))
            {
                return true;
            }

            bitmap = Clipboard.GetImage();
        }
        catch
        {
            return false;
        }

        if (bitmap == null)
        {
            return false;
        }

        try
        {
            ImportAndInsertBitmap(bitmap);
        }
        catch (Exception ex)
        {
            ImageImportFailed?.Invoke(ex);
        }

        return true;
    }

    public int InsertImagesFromFiles(IEnumerable<string> paths)
    {
        var imageStore = _imageStore;
        var noteId = _noteId;
        if (imageStore == null || string.IsNullOrWhiteSpace(noteId) || IsReadOnly)
        {
            return 0;
        }

        var candidatePaths = paths.ToList();
        if (candidatePaths.Count == 0)
        {
            return 0;
        }

        // Reserve enough text for the longest possible generated IDs before the store is touched.
        // ImportImageFiles is all-or-nothing, so the source count is also the reference count.
        var insertionPlan = CreateImageInsertionPlan(candidatePaths.Count);
        var imported = imageStore.ImportImageFiles(noteId, candidatePaths);
        InsertImportedImages(imageStore, noteId, imported, insertionPlan);
        return imported.Count;
    }

    private void ImportAndInsertBitmap(BitmapSource bitmap)
    {
        var imageStore = _imageStore
            ?? throw new InvalidOperationException(Strings.Get("ImageStoreUnavailable"));
        var noteId = _noteId;
        if (string.IsNullOrWhiteSpace(noteId) || IsReadOnly)
        {
            throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
        }

        // The length check intentionally runs before ImportBitmapSource writes the blob to LMDB.
        var insertionPlan = CreateImageInsertionPlan(imageCount: 1);
        var asset = imageStore.ImportBitmapSource(noteId, bitmap);
        InsertImportedImages(imageStore, noteId, new[] { asset }, insertionPlan);
    }

    private ImageInsertionPlan CreateImageInsertionPlan(int imageCount)
    {
        if (imageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageCount));
        }

        var target = CaptureDocumentReplacementTarget();
        var placeholderBlock = BuildImageReferenceBlock(
            imageCount,
            static _ => MarkdownImageReferences.CreateReference("99999999"));
        var placeholderInsertion = BuildBlockInsertion(
            target.OriginalText,
            target.Start,
            placeholderBlock);
        EnsureImageInsertionFits(
            ReplaceRange(
                target.OriginalText,
                target.Start,
                target.SelectionLength,
                placeholderInsertion),
            imageCount);

        return new ImageInsertionPlan(target, imageCount);
    }

    private void InsertImportedImages(
        NoteImageStore imageStore,
        string noteId,
        IReadOnlyList<NoteImageAsset> assets,
        ImageInsertionPlan insertionPlan)
    {
        if (!ReferenceEquals(_imageStore, imageStore) ||
            !string.Equals(_noteId, noteId, StringComparison.Ordinal) ||
            assets.Any(asset => !string.Equals(asset.NoteId, noteId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(Strings.Get("ImageImportInvalidNote"));
        }

        var caret = CommitImportedImages(assets, insertionPlan);
        CaretIndex = caret;
        Select(caret, 0);
        Focus();
        QueuePostPasteRefresh();
    }

    private int CommitImportedImages(
        IReadOnlyList<NoteImageAsset> assets,
        ImageInsertionPlan insertionPlan)
    {
        if (assets.Count != insertionPlan.ImageCount)
        {
            throw new InvalidOperationException(Strings.Get("ImageImportUnsupported"));
        }

        var referenceBlock = BuildImageReferenceBlock(
            assets.Count,
            index => MarkdownImageReferences.CreateReference(assets[index].Id));
        var insertion = BuildBlockInsertion(
            insertionPlan.Target.OriginalText,
            insertionPlan.Target.Start,
            referenceBlock);
        var candidateText = ReplaceRange(
            insertionPlan.Target.OriginalText,
            insertionPlan.Target.Start,
            insertionPlan.Target.SelectionLength,
            insertion);
        EnsureImageInsertionFits(candidateText, assets.Count);

        return CommitDocumentReplacement(insertionPlan.Target, insertion);
    }

    private DocumentReplacementTarget CaptureDocumentReplacementTarget()
    {
        var document = Document
            ?? throw new InvalidOperationException(Strings.Get("ImageImportUnsupported"));
        var originalText = Text;
        var start = Math.Clamp(SelectionStart, 0, originalText.Length);
        var selectionLength = Math.Clamp(SelectionLength, 0, originalText.Length - start);
        return new DocumentReplacementTarget(
            document,
            originalText,
            start,
            selectionLength);
    }

    private int CommitDocumentReplacement(
        DocumentReplacementTarget target,
        string replacement)
    {
        if (!ReferenceEquals(Document, target.Document) ||
            !string.Equals(Text, target.OriginalText, StringComparison.Ordinal) ||
            SelectionStart != target.Start ||
            SelectionLength != target.SelectionLength)
        {
            throw new InvalidOperationException(Strings.Get("ImageImportUnsupported"));
        }

        // One replacement inside one update is one document/undo operation for the whole batch.
        // Callers validate the exact candidate first, so OnTextChanged never needs to trim old text.
        target.Document.BeginUpdate();
        try
        {
            target.Document.Replace(target.Start, target.SelectionLength, replacement);
        }
        finally
        {
            target.Document.EndUpdate();
        }

        return target.Start + replacement.Length;
    }

    private void EnsureImageInsertionFits(string candidateText, int imageCount)
    {
        if (MaxLength <= 0 || candidateText.Length <= TextLengthLimitFor(candidateText))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot insert {imageCount} image reference(s): the note's {MaxLength:N0}-character limit would be exceeded.");
    }

    private static string BuildImageReferenceBlock(
        int imageCount,
        Func<int, string> createReference)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < imageCount; index++)
        {
            if (index > 0)
            {
                builder.Append(Environment.NewLine);
            }

            builder.Append(createReference(index));
        }

        return builder.ToString();
    }

    private static string BuildBlockInsertion(
        string documentText,
        int start,
        string blockText)
    {
        var builder = new StringBuilder();

        if (start > 0 && documentText[start - 1] is not '\n' and not '\r')
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(blockText);
        builder.Append(Environment.NewLine);

        return builder.ToString();
    }

    private static string ReplaceRange(string text, int start, int length, string replacement)
        => string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + length));

    private int CurrentTextLengthLimit()
    {
        return TextLengthLimitFor(Text);
    }

    private int TextLengthLimitFor(string text)
    {
        if (MaxLength <= 0 || text.Length <= 0)
        {
            return MaxLength;
        }

        var structuralDelimiterLength = TrailingImageReferenceDelimiterLength(text);
        return MaxLength > int.MaxValue - structuralDelimiterLength
            ? int.MaxValue
            : MaxLength + structuralDelimiterLength;
    }

    private static int TrailingImageReferenceDelimiterLength(string text)
    {
        var delimiterStart = text.Length;
        if (delimiterStart > 0 && text[delimiterStart - 1] == '\n')
        {
            delimiterStart--;
            if (delimiterStart > 0 && text[delimiterStart - 1] == '\r')
            {
                delimiterStart--;
            }
        }
        else if (delimiterStart > 0 && text[delimiterStart - 1] == '\r')
        {
            delimiterStart--;
        }
        else
        {
            return 0;
        }

        var lineStart = delimiterStart;
        while (lineStart > 0 && text[lineStart - 1] is not '\r' and not '\n')
        {
            lineStart--;
        }

        return MarkdownImageReferences.TryParseReferenceLine(text[lineStart..delimiterStart], out _)
            ? text.Length - delimiterStart
            : 0;
    }

    private readonly record struct DocumentReplacementTarget(
        TextDocument Document,
        string OriginalText,
        int Start,
        int SelectionLength);

    private readonly record struct ImageInsertionPlan(
        DocumentReplacementTarget Target,
        int ImageCount);

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // AvalonEdit's built-in paste command no-ops when the clipboard holds only image
        // data or copied image files (no text), so its pasting handler never fires. Intercept
        // Ctrl+V and insert the clipboard images ourselves before the editor swallows the key.
        if (!IsReadOnly &&
            e.Key == System.Windows.Input.Key.V &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            !ClipboardHasText() &&
            TryInsertImageFromClipboard())
        {
            e.Handled = true;
            return;
        }

        if (HasSelectedImageReference &&
            e.Key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryDeleteSelectedImageReference())
            {
                e.Handled = true;
                return;
            }
        }

        if (HasSelectedImageReference &&
            e.Key == System.Windows.Input.Key.Escape &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            ClearImageSelection();
            e.Handled = true;
            return;
        }

        if (!_acceptsReturn && e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            return;
        }

        if (!_acceptsTab && e.Key == System.Windows.Input.Key.Tab)
        {
            e.Handled = true;
            return;
        }

        if (!IsReadOnly &&
            _acceptsReturn &&
            e.Key == System.Windows.Input.Key.Enter &&
            !CanApplyTextReplacement(NewLineTextAtCaret()))
        {
            e.Handled = true;
            return;
        }

        if (!IsReadOnly &&
            _acceptsTab &&
            e.Key == System.Windows.Input.Key.Tab &&
            !CanApplyTextReplacement("\t"))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (HasSelectedImageReference && !string.IsNullOrEmpty(e.Text))
        {
            TryDeleteSelectedImageReference();
        }

        if (!IsReadOnly &&
            !string.IsNullOrEmpty(e.Text) &&
            !CanApplyTextReplacement(e.Text))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewTextInput(e);
    }

    private bool CanApplyTextReplacement(string replacement)
    {
        if (MaxLength <= 0)
        {
            return true;
        }

        var start = Math.Clamp(SelectionStart, 0, Text.Length);
        var selectedLength = Math.Clamp(SelectionLength, 0, Text.Length - start);
        var projectedLength = checked(Text.Length - selectedLength + replacement.Length);
        if (projectedLength <= MaxLength)
        {
            return true;
        }

        // Never make an already-oversized legacy note larger, but always permit an edit that
        // reduces or preserves its length. Most importantly, never "repair" it by deleting a
        // different part of the document after the edit has happened.
        if (Text.Length > CurrentTextLengthLimit() && projectedLength <= Text.Length)
        {
            return true;
        }

        // Only a trailing delimiter after an internal image reference may live just beyond the
        // nominal limit. Build the exact candidate for that narrow boundary case.
        if (projectedLength > MaxLength + Environment.NewLine.Length)
        {
            return false;
        }

        var candidate = ReplaceRange(Text, start, selectedLength, replacement);
        return candidate.Length <= TextLengthLimitFor(candidate);
    }

    private string NewLineTextAtCaret()
    {
        if (Document == null || Document.TextLength == 0)
        {
            return Environment.NewLine;
        }

        try
        {
            var offset = Math.Clamp(CaretOffset, 0, Document.TextLength);
            return NewLineTextFor(Document.GetLineByOffset(offset));
        }
        catch
        {
            return Environment.NewLine;
        }
    }

    private bool TryDeleteSelectedImageReference()
    {
        if (Document == null ||
            _selectedImageReferenceAnchor is not { IsDeleted: false } anchor ||
            string.IsNullOrWhiteSpace(_selectedImageId))
        {
            ClearImageSelection();
            return false;
        }

        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(Math.Clamp(anchor.Offset, 0, Document.TextLength));
        }
        catch
        {
            ClearImageSelection();
            return false;
        }

        var imageId = _selectedImageId;
        if (!MarkdownImageReferences.TryParseLine(Document.GetText(line), out var parsedId) ||
            !string.Equals(parsedId, imageId, StringComparison.Ordinal))
        {
            ClearImageSelection();
            return false;
        }

        ClearImageSelection(redraw: false);
        DeleteImageReferenceLine(line, imageId);
        return true;
    }

    private void RemoveEmptyListMarker(DocumentLine line, int markerStart, int removeEnd)
    {
        var start = line.Offset + Math.Clamp(markerStart, 0, line.Length);
        var end = line.Offset + Math.Clamp(removeEnd, markerStart, line.Length);
        var length = Math.Max(0, end - start);

        Document.BeginUpdate();
        try
        {
            if (length > 0)
            {
                Document.Remove(start, length);
            }
            CaretOffset = start;
            Select(CaretOffset, 0);
        }
        finally
        {
            Document.EndUpdate();
        }
    }

    private string NewLineTextFor(DocumentLine line)
    {
        if (Document != null && line.DelimiterLength > 0)
        {
            return Document.GetText(line.EndOffset, line.DelimiterLength);
        }

        return Environment.NewLine;
    }

    private static bool IsLineContentEmpty(string text, int contentStart)
    {
        for (var i = Math.Clamp(contentStart, 0, text.Length); i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        string? clipboardText;
        try
        {
            clipboardText = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : null;
        }
        catch
        {
            e.CancelCommand();
            return;
        }

        if (!IsReadOnly &&
            string.IsNullOrEmpty(clipboardText) &&
            TryInsertImageFromClipboard(e.DataObject))
        {
            e.CancelCommand();
            return;
        }

        if (string.IsNullOrEmpty(clipboardText))
        {
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        var text = clipboardText;
        var replacementTarget = CaptureDocumentReplacementTarget();
        var selectedLength = replacementTarget.SelectionLength;
        var containsImageReference = MarkdownImageReferences.Enumerate(text).Any();
        var blockPasteText = EnsureImageReferencePasteIsBlock(text, replacementTarget);
        string pasteText;
        if (containsImageReference)
        {
            try
            {
                if (!IsSafeImageReferencePaste(blockPasteText, replacementTarget))
                {
                    e.CancelCommand();
                    PasteRejected?.Invoke();
                    return;
                }

                var maximumIdPasteText = ExpandImageReferenceIdsForLengthPreflight(blockPasteText);
                if (!IsSafeImageReferencePaste(maximumIdPasteText, replacementTarget))
                {
                    e.CancelCommand();
                    PasteRejected?.Invoke();
                    return;
                }
            }
            catch (Exception ex)
            {
                ImageImportFailed?.Invoke(ex);
                e.CancelCommand();
                return;
            }

            // Image references are atomic: unlike ordinary text, never clip a partial batch.
            pasteText = blockPasteText;
        }
        else if (!TryBuildSafePasteText(blockPasteText, selectedLength, out pasteText))
        {
            e.CancelCommand();
            PasteRejected?.Invoke();
            return;
        }

        if (containsImageReference && _imageStore != null)
        {
            try
            {
                pasteText = _imageStore.CloneForeignImageReferencesForNote(_noteId, pasteText);
            }
            catch (Exception ex)
            {
                ImageImportFailed?.Invoke(ex);
                e.CancelCommand();
                return;
            }
        }

        if (containsImageReference)
        {
            try
            {
                if (!IsSafeImageReferencePaste(pasteText, replacementTarget))
                {
                    e.CancelCommand();
                    PasteRejected?.Invoke();
                    return;
                }

                e.CancelCommand();
                var caret = CommitDocumentReplacement(replacementTarget, pasteText);
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

        if (string.Equals(pasteText, clipboardText, StringComparison.Ordinal))
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, pasteText);
        data.SetData(DataFormats.Text, pasteText);
        e.DataObject = data;
        e.FormatToApply = DataFormats.UnicodeText;
        QueuePostPasteRefresh();
    }

    private static string EnsureImageReferencePasteIsBlock(
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
        var needsLeadingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(firstLine, out _) &&
            selectionStart > 0 &&
            replacementTarget.OriginalText[selectionStart - 1] is not '\r' and not '\n';
        var needsTrailingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(lastLine, out _) &&
            (selectionEnd >= replacementTarget.OriginalText.Length ||
                replacementTarget.OriginalText[selectionEnd] is not '\r' and not '\n');
        if (!needsLeadingNewLine && !needsTrailingNewLine)
        {
            return pasteText;
        }

        var builder = new StringBuilder(pasteText.Length + Environment.NewLine.Length * 2);
        if (needsLeadingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        builder.Append(pasteText);
        if (needsTrailingNewLine)
        {
            builder.Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    private bool IsSafeImageReferencePaste(
        string pasteText,
        DocumentReplacementTarget replacementTarget)
    {
        if (pasteText.Length > MaxSafePasteLength ||
            ContainsLineLongerThan(pasteText, MaxSafePasteLineLength))
        {
            return false;
        }

        var candidateText = ReplaceRange(
            replacementTarget.OriginalText,
            replacementTarget.Start,
            replacementTarget.SelectionLength,
            pasteText);
        return MaxLength <= 0 || candidateText.Length <= TextLengthLimitFor(candidateText);
    }

    private static string ExpandImageReferenceIdsForLengthPreflight(string markdown)
    {
        const string maximumImageId = "99999999";
        var references = MarkdownImageReferences.Enumerate(markdown).ToList();
        if (references.Count == 0)
        {
            return markdown;
        }

        var additionalLength = references.Sum(reference => maximumImageId.Length - reference.ImageId.Length);
        var builder = new StringBuilder(checked(markdown.Length + additionalLength));
        var cursor = 0;
        foreach (var reference in references)
        {
            builder.Append(markdown, cursor, reference.LineStart - cursor);
            var line = markdown.Substring(reference.LineStart, reference.LineLength);
            var urlMarker = line.IndexOf("](", StringComparison.Ordinal);
            var imageToken = MarkdownImageReferences.UriPrefix + reference.ImageId;
            var tokenStart = urlMarker >= 0
                ? line.IndexOf(imageToken, urlMarker + 2, StringComparison.Ordinal)
                : -1;
            if (tokenStart < 0)
            {
                throw new InvalidDataException(Strings.Get("ImageImportUnsupported"));
            }

            var idStart = tokenStart + MarkdownImageReferences.UriPrefix.Length;
            builder.Append(line, 0, idStart);
            builder.Append(maximumImageId);
            builder.Append(line, idStart + reference.ImageId.Length, line.Length - idStart - reference.ImageId.Length);
            cursor = reference.LineStart + reference.LineLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private bool EnsureTrailingImageAnchorLine()
    {
        if (Document == null || Document.TextLength <= 0 || _isEnsuringImageAnchorLine)
        {
            return false;
        }

        var lastLine = Document.GetLineByOffset(Document.TextLength);
        if (!MarkdownImageReferences.TryParseReferenceLine(Document.GetText(lastLine), out _))
        {
            return false;
        }

        try
        {
            _isEnsuringImageAnchorLine = true;
            Document.Insert(Document.TextLength, NewLineTextFor(lastLine));
            return true;
        }
        finally
        {
            _isEnsuringImageAnchorLine = false;
        }
    }

    private static bool HasInternalImageReference(string? text)
    {
        foreach (var _ in MarkdownImageReferences.Enumerate(text))
        {
            return true;
        }

        return false;
    }

    private void EnsureVisualLines()
    {
        TextArea.TextView.EnsureVisualLines();
    }

    private bool TryBuildSafePasteText(string text, int selectedLength, out string pasteText)
    {
        pasteText = text;
        if (text.Length > MaxSafePasteLength ||
            ContainsLineLongerThan(text, MaxSafePasteLineLength))
        {
            return false;
        }

        var candidateLength = Text.Length - Math.Max(0, selectedLength) + text.Length;
        return MaxLength <= 0 || candidateLength <= CurrentTextLengthLimit();
    }

    private static bool ContainsLineLongerThan(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return false;
        }

        var lineLength = 0;
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                lineLength = 0;
                continue;
            }

            lineLength++;
            if (lineLength > maxLength)
            {
                return true;
            }
        }

        return false;
    }

    private void QueuePostPasteRefresh()
    {
        if (_isPostPasteRefreshQueued)
        {
            return;
        }

        _isPostPasteRefreshQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isPostPasteRefreshQueued = false;
                RefreshTextView();
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void QueueImageLineRedraw(int lineNumber)
    {
        _queuedImageRedrawLines.Add(Math.Max(1, lineNumber));
        if (_isImageRenderRedrawQueued)
        {
            return;
        }

        _isImageRenderRedrawQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isImageRenderRedrawQueued = false;
                var document = Document;
                var textView = TextArea.TextView;
                foreach (var queuedLineNumber in _queuedImageRedrawLines)
                {
                    var currentLineNumber = Math.Clamp(queuedLineNumber, 1, Math.Max(1, document.LineCount));
                    var line = document.GetLineByNumber(currentLineNumber);
                    var length = Math.Min(line.TotalLength, document.TextLength - line.Offset);
                    if (length > 0)
                    {
                        textView.Redraw(line.Offset, length, System.Windows.Threading.DispatcherPriority.Render);
                    }
                }
                _queuedImageRedrawLines.Clear();
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void QueueMarkdownSuffixRedraw(int startLine)
    {
        _queuedMarkdownRedrawStartLine = Math.Min(_queuedMarkdownRedrawStartLine, Math.Max(1, startLine));
        if (_isMarkdownSuffixRedrawQueued)
        {
            return;
        }

        _isMarkdownSuffixRedrawQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isMarkdownSuffixRedrawQueued = false;
                var document = Document;
                var lineNumber = Math.Clamp(
                    _queuedMarkdownRedrawStartLine,
                    1,
                    Math.Max(1, document.LineCount));
                _queuedMarkdownRedrawStartLine = int.MaxValue;
                var line = document.GetLineByNumber(lineNumber);
                var length = document.TextLength - line.Offset;
                if (length > 0)
                {
                    TextArea.TextView.Redraw(
                        line.Offset,
                        length,
                        System.Windows.Threading.DispatcherPriority.Render);
                }
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private bool ShouldRenderImages =>
        _imageStore != null &&
        !_imageRenderingSuspended &&
        !string.Equals(_markdownRenderMode, MarkdownRenderModes.Off, StringComparison.Ordinal);
    private bool ShouldHideImageReferenceText =>
        ShouldRenderImages &&
        (RenderModeIsFull || _imageReferenceTextMode switch
        {
            ImageReferenceTextModes.Hidden => true,
            ImageReferenceTextModes.Editing => _isPreviewMode,
            _ => false
        });

    private FrameworkElement CreateImageBlock(
        MarkdownImageReference reference,
        NoteImageAsset? asset,
        DocumentLine referenceLine)
    {
        var targetWidth = ImageTargetWidth();
        var displayWidth = ResolveImageDisplayWidth(reference.DisplayOptions, asset, targetWidth);
        var decodePixelWidth = ImageDecodePixelWidth(Math.Min(targetWidth, displayWidth));
        var bitmap = asset == null
            ? null
            : _imageStore?.GetBitmapSource(
                asset.Id,
                decodePixelWidth,
                allowDecodeUpgrade: !_isImageResizePreview,
                protectInViewport: true);
        var isCorrupted = _imageStore?.IsImageCorrupted(reference.ImageId) == true;
        var document = Document!;
        var referenceAnchor = document.CreateAnchor(referenceLine.Offset);
        referenceAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        referenceAnchor.SurviveDeletion = false;
        var caretOffset = referenceLine.NextLine?.Offset ?? referenceLine.EndOffset;
        var caretAnchor = document.CreateAnchor(caretOffset);
        caretAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        caretAnchor.SurviveDeletion = true;
        var isSelected = IsImageReferenceSelected(referenceLine);

        var host = new Border
        {
            Padding = new Thickness(0, ImageBlockVerticalPadding - 1, 0, ImageBlockVerticalPadding - 1),
            Background = Brushes.Transparent,
            BorderBrush = isSelected ? Theme.CapsuleFocusBorderBrush : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 24,
            Width = targetWidth,
            ToolTip = asset?.OriginalName,
            Tag = new ImageBlockTag(
                referenceAnchor,
                caretAnchor,
                reference.ImageId,
                reference.DisplayOptions,
                Math.Max(1, asset?.Width ?? 180))
        };
        host.ContextMenu = CreateImageContextMenu(reference.ImageId, referenceLine, canCopy: bitmap != null);
        // Raise the guard before the menu (and its focus grab) opens; LostKeyboardFocus
        // on the editor can fire before the menu's own Opened event.
        host.ContextMenuOpening += (_, _) =>
        {
            IsImageContextMenuOpen = true;
            Dispatcher.BeginInvoke(
                (Action)(() =>
                {
                    if (host.ContextMenu is not { IsOpen: true })
                    {
                        IsImageContextMenuOpen = false;
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        };

        if (bitmap == null)
        {
            host.Child = new Border
            {
                Width = Math.Max(120, Math.Min(targetWidth, displayWidth)),
                Height = 42,
                CornerRadius = new CornerRadius(5),
                Background = isCorrupted
                    ? Theme.Danger((byte)(Theme.IsDark ? 30 : 18))
                    : Theme.Tint((byte)(Theme.IsDark ? 30 : 18)),
                BorderBrush = isCorrupted ? Theme.Danger(70) : Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = Strings.Get(isCorrupted ? "ImageCorrupted" : "ImageMissing"),
                    Foreground = isCorrupted ? Theme.DangerBrush : Theme.WeakTextBrush,
                    FontSize = ScaledFontSize(NoteTypography.FontSize),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            return host;
        }

        host.Child = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = displayWidth,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        return host;
    }

    private bool IsImageReferenceSelected(DocumentLine referenceLine)
    {
        if (_selectedImageReferenceAnchor is not { IsDeleted: false } anchor ||
            string.IsNullOrWhiteSpace(_selectedImageId))
        {
            return false;
        }

        return anchor.Offset == referenceLine.Offset &&
            MarkdownImageReferences.TryParseLine(Document!.GetText(referenceLine), out var imageId) &&
            string.Equals(imageId, _selectedImageId, StringComparison.Ordinal);
    }

    internal static double ResolveImageDisplayWidth(
        MarkdownImageDisplayOptions options,
        NoteImageAsset? asset,
        double targetWidth)
        => ResolveImageDisplayWidth(options, Math.Max(1, asset?.Width ?? 180), targetWidth);

    internal static double ResolveImageDisplayWidth(
        MarkdownImageDisplayOptions options,
        double naturalWidth,
        double targetWidth)
    {
        targetWidth = Math.Max(80, targetWidth);
        naturalWidth = Math.Max(1, naturalWidth);
        var width = Math.Min(targetWidth, naturalWidth);

        if (options.WidthAttribute is { } widthAttribute)
        {
            width = widthAttribute.IsPercent
                ? targetWidth * Math.Clamp(widthAttribute.Value, 1, 1000) / 100.0
                : widthAttribute.Value;
        }
        else if (options.LabelWidth.HasValue)
        {
            width = options.LabelWidth.Value;
            if (options.LabelScalePercent.HasValue)
            {
                width *= Math.Clamp(options.LabelScalePercent.Value, 1, 1000) / 100.0;
            }
        }
        else if (options.LabelScalePercent.HasValue)
        {
            width = naturalWidth * Math.Clamp(options.LabelScalePercent.Value, 1, 1000) / 100.0;
        }

        return Math.Round(Math.Clamp(width, 24, targetWidth), 1);
    }

    private int ImageDecodePixelWidth(double displayWidth)
    {
        var dpiScale = VisualTreeHelper.GetDpi(TextArea.TextView).DpiScaleX;
        if (double.IsNaN(dpiScale) || double.IsInfinity(dpiScale) || dpiScale <= 0)
        {
            dpiScale = 1;
        }

        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, displayWidth) * dpiScale));
    }

    private ContextMenu CreateImageContextMenu(string imageId, DocumentLine referenceLine, bool canCopy)
    {
        var menu = ImageContextMenuFactory?.Invoke() ?? new ContextMenu
        {
            HasDropShadow = true
        };
        AppTypography.ApplyTextRendering(menu);
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => IsImageContextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            IsImageContextMenuOpen = false;
            ImageContextMenuClosed?.Invoke();
        };

        var copy = new MenuItem
        {
            Header = Strings.Get("MenuCopyImage"),
            IsEnabled = canCopy
        };
        copy.Click += (_, _) => CopyImageToClipboard(imageId);
        menu.Items.Add(copy);

        var delete = new MenuItem
        {
            Header = Strings.Get("MenuDeleteImage")
        };
        delete.Click += (_, _) => DeleteImageReferenceLine(referenceLine, imageId);
        menu.Items.Add(delete);

        return menu;
    }

    private void CopyImageToClipboard(string imageId)
    {
        try
        {
            var bitmap = _imageStore?.GetBitmapSourceForClipboard(imageId);
            if (bitmap != null)
            {
                Clipboard.SetImage(bitmap);
            }
        }
        catch
        {
            // Clipboard access is best-effort.
        }
    }

    private void DeleteImageReferenceLine(DocumentLine line, string imageId)
    {
        if (Document == null || line.IsDeleted)
        {
            return;
        }

        var text = Document.GetText(line);
        if (!MarkdownImageReferences.TryParseLine(text, out var parsedId) ||
            !string.Equals(parsedId, imageId, StringComparison.Ordinal))
        {
            return;
        }

        var removeStart = line.Offset;
        var removeLength = line.Length + line.DelimiterLength;
        Document.BeginUpdate();
        try
        {
            Document.Remove(removeStart, Math.Min(removeLength, Document.TextLength - removeStart));
            CaretOffset = Math.Min(removeStart, Document.TextLength);
            Select(CaretOffset, 0);
        }
        finally
        {
            Document.EndUpdate();
        }

        QueuePostPasteRefresh();
    }

    private double ImageTargetWidth()
    {
        var viewport = TextArea.TextView.ActualWidth;
        if (viewport <= 0)
        {
            viewport = ActualWidth;
        }

        var width = viewport - ImageBlockHorizontalPadding * 2 - 10;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return 240;
        }

        return Math.Max(80, width);
    }

    private static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetBitmapSource(IDataObject dataObject, out BitmapSource? bitmap)
    {
        // Prefer encoded clipboard formats (PNG/JPEG/GIF). Windows screenshot tools often
        // put a correct PNG next to a CF_DIB/Bitmap whose alpha channel is all zeros; taking
        // Bitmap first would re-encode a fully transparent image.
        foreach (var format in EncodedClipboardImageFormats)
        {
            if (TryGetBitmapData(dataObject, format, autoConvert: false, out bitmap))
            {
                return true;
            }
        }

        if (TryGetBitmapData(dataObject, DataFormats.Bitmap, autoConvert: true, out bitmap))
        {
            return true;
        }

        bitmap = null;
        return false;
    }

    private static bool HasBitmapDataFormat(IDataObject dataObject)
    {
        try
        {
            if (dataObject.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
            {
                return true;
            }

            foreach (var format in EncodedClipboardImageFormats)
            {
                if (dataObject.GetDataPresent(format, autoConvert: false))
                {
                    return true;
                }
            }
        }
        catch
        {
            // A third-party IDataObject can throw while enumerating delayed formats.
        }

        return false;
    }

    private static bool TryGetBitmapData(
        IDataObject dataObject,
        string format,
        bool autoConvert,
        out BitmapSource? bitmap)
    {
        bitmap = null;
        try
        {
            return dataObject.GetDataPresent(format, autoConvert) &&
                TryDecodeBitmapData(dataObject.GetData(format, autoConvert), out bitmap);
        }
        catch
        {
            bitmap = null;
            return false;
        }
    }

    private static bool TryDecodeBitmapData(object? data, out BitmapSource? bitmap)
    {
        bitmap = data as BitmapSource;
        if (bitmap != null)
        {
            return true;
        }

        try
        {
            switch (data)
            {
                case byte[] bytes when bytes.Length is > 0 and <= MaxClipboardEncodedImageBytes:
                    using (var stream = new MemoryStream(bytes, writable: false))
                    {
                        bitmap = DecodeBitmapStream(stream);
                    }
                    break;

                case Stream source when source.CanRead:
                    using (var stream = new MemoryStream())
                    {
                        var originalPosition = source.CanSeek ? source.Position : 0;
                        try
                        {
                            if (source.CanSeek)
                            {
                                source.Position = 0;
                                if (source.Length > MaxClipboardEncodedImageBytes)
                                {
                                    break;
                                }
                            }

                            CopyStreamWithLimit(source, stream, MaxClipboardEncodedImageBytes);
                        }
                        finally
                        {
                            if (source.CanSeek)
                            {
                                source.Position = originalPosition;
                            }
                        }
                        stream.Position = 0;
                        bitmap = DecodeBitmapStream(stream);
                    }
                    break;
            }
        }
        catch
        {
            bitmap = null;
        }

        return bitmap != null;
    }

    private static void CopyStreamWithLimit(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[81920];
        var totalBytes = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes - totalBytes + 1));
            if (read <= 0)
            {
                return;
            }

            totalBytes = checked(totalBytes + read);
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException(Strings.Format(
                    "ImageImportSourceTooLarge",
                    maximumBytes / 1024 / 1024));
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static BitmapSource DecodeBitmapStream(Stream stream)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        return frame;
    }
    private static bool TryGetImageFileDrop(IDataObject dataObject, out List<string> paths)
    {
        paths = new List<string>();
        try
        {
            if (!dataObject.GetDataPresent(DataFormats.FileDrop) ||
                dataObject.GetData(DataFormats.FileDrop) is not string[] dropped)
            {
                return false;
            }

            // Capability probe and insert share this filter so drag/paste of non-image files
            // does not claim the image path or surface an import-failure dialog.
            paths = dropped
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                    NoteImageStore.IsSupportedImageFile(path))
                .ToList();
            return paths.Count > 0;
        }
        catch
        {
            paths.Clear();
            return false;
        }
    }

    private sealed class BlockImageElement : InlineObjectElement
    {
        public BlockImageElement(UIElement element)
            : base(0, element)
        {
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return new BlockImageRun(1, TextRunProperties, Element);
        }
    }

    private sealed class BlockImageRun : InlineObjectRun
    {
        public BlockImageRun(int length, TextRunProperties properties, UIElement element)
            : base(length, properties, element)
        {
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakAlways;
    }

    private sealed record ImageBlockTag(
        TextAnchor ReferenceAnchor,
        TextAnchor CaretAnchor,
        string ImageId,
        MarkdownImageDisplayOptions DisplayOptions,
        double NaturalWidth);

    private static bool TryNormalizeMarkdownUrl(string rawUrl, out string normalizedUrl)
    {
        normalizedUrl = "";
        var trimmed = rawUrl.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (TryNormalizeLocalMarkdownPath(trimmed, out normalizedUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.IsFile)
        {
            return TryNormalizeLocalMarkdownPath(uri.LocalPath, out normalizedUrl);
        }

        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool TryNormalizeLocalMarkdownPath(string rawPath, out string normalizedPath)
    {
        normalizedPath = "";
        var trimmed = rawPath.Trim();
        if (!LooksLikeLocalMarkdownPath(trimmed) || IsDevicePath(trimmed))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            if (IsDevicePath(fullPath))
            {
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool LooksLikeLocalMarkdownPath(string text)
    {
        return IsWindowsDrivePath(text) || IsUncPath(text);
    }

    private static bool IsWindowsDrivePath(string text)
    {
        return text.Length >= 3 &&
            IsAsciiLetter(text[0]) &&
            text[1] == ':' &&
            IsDirectorySeparator(text[2]);
    }

    private static bool IsUncPath(string text)
    {
        return text.Length >= 3 &&
            IsDirectorySeparator(text[0]) &&
            IsDirectorySeparator(text[1]) &&
            !IsDirectorySeparator(text[2]);
    }

    private static bool IsDevicePath(string text)
    {
        return text.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            text.StartsWith(@"\\?\", StringComparison.Ordinal);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value is '\\' or '/';
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }
}
