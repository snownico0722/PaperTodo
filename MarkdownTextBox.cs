using System.Globalization;
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

public sealed class MarkdownTextBox : TextEditor
{
    private const int MaxSafePasteLength = 30000;
    private const int MaxSafePasteLineLength = 6000;
    private const double ImageBlockVerticalPadding = 7;
    private const double ImageBlockHorizontalPadding = 2;
    private static readonly string[] EncodedClipboardImageFormats =
        ["PNG", "image/png", "JFIF", "image/jpeg", "image/jpg", "GIF", "image/gif"];

    private bool _isTrimmingText;
    private bool _isTrimQueued;
    private bool _acceptsReturn = true;
    private bool _acceptsTab = true;
    private bool _isPreviewMode;
    private bool _isPostPasteRefreshQueued;
    private bool _isImageRenderRedrawQueued;
    private bool _isEnsuringImageAnchorLine;
    private bool _hadInternalImageReferences;
    private TextAnchor? _selectedImageReferenceAnchor;
    private string? _selectedImageId;
    private double _textZoom = 1.0;
    private string _noteId = "";
    private NoteImageStore? _imageStore;
    private readonly MarkdownMarkerColorizer _markerColorizer;
    private readonly MarkdownListBulletRenderer _listBulletRenderer;
    private readonly MarkdownHorizontalRuleRenderer _horizontalRuleRenderer;
    private readonly MarkdownImageElementGenerator _imageElementGenerator;
    private readonly FencedCodeStateCache _fencedCodeStateCache = new();

    public event Action<Exception>? ImageImportFailed;

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

        _markerColorizer = new MarkdownMarkerColorizer(this);
        _listBulletRenderer = new MarkdownListBulletRenderer(this);
        _horizontalRuleRenderer = new MarkdownHorizontalRuleRenderer(this);
        _imageElementGenerator = new MarkdownImageElementGenerator(this);
        TextArea.TextView.ElementGenerators.Add(_imageElementGenerator);
        TextArea.TextView.BackgroundRenderers.Add(new MarkdownBlockBackgroundRenderer(this));
        TextArea.TextView.BackgroundRenderers.Add(_listBulletRenderer);
        TextArea.TextView.BackgroundRenderers.Add(_horizontalRuleRenderer);
        TextArea.TextView.LineTransformers.Add(_markerColorizer);
        DataObject.AddPastingHandler(this, OnPaste);
        DataObject.AddCopyingHandler(this, OnCopying);
        SizeChanged += (_, _) => QueuePostPasteRefresh();
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

    public string PersistentText => MarkdownImageReferences.StripRenderMarkers(Text);

    public void ConfigureNoteImages(string noteId, NoteImageStore imageStore)
    {
        _noteId = noteId ?? "";
        _imageStore = imageStore;
        var cleaned = MarkdownImageReferences.StripRenderMarkers(Text);
        if (!string.Equals(cleaned, Text, StringComparison.Ordinal))
        {
            Text = cleaned;
        }
        EnsureTrailingImageAnchorLine();
        _hadInternalImageReferences = HasInternalImageReference(Text);
        RefreshTextView();
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
        TextArea.TextView.BackgroundRenderers.Remove(_listBulletRenderer);
        TextArea.TextView.BackgroundRenderers.Remove(_horizontalRuleRenderer);
        TextArea.TextView.BackgroundRenderers.Add(_listBulletRenderer);
        TextArea.TextView.BackgroundRenderers.Add(_horizontalRuleRenderer);
        TextArea.TextView.LineTransformers.Remove(_markerColorizer);
        TextArea.TextView.LineTransformers.Add(_markerColorizer);
        RefreshTextView();
        Dispatcher.BeginInvoke(
            (Action)RefreshTextView,
            System.Windows.Threading.DispatcherPriority.Render);
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
        var start = SelectionStart;
        var lineStart = Text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

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
        QueueImageRenderRedraw();
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

        _selectedImageReferenceAnchor = null;
        _selectedImageId = null;
        CaretBrush = _isPreviewMode ? Brushes.Transparent : Theme.TextBrush;
        if (redraw)
        {
            QueueImageRenderRedraw();
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

    public bool TryGetMarkdownLinkFromPoint(Point point, out string url)
    {
        url = "";
        if (Document == null ||
            !TryGetCharacterIndexFromPoint(point, out var charIndex))
        {
            return false;
        }

        return TryGetMarkdownLinkAtOffset(charIndex, out url);
    }

    public bool TryGetMarkdownLinkFromTextViewPoint(Point point, out string url)
    {
        url = "";
        if (!RenderOptions.HighlightLinks || Document == null)
        {
            return false;
        }

        try
        {
            EnsureVisualLines();
            var textView = TextArea.TextView;
            if (!textView.VisualLinesValid)
            {
                return false;
            }

            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    var text = Document.GetText(line);
                    foreach (var link in EnumerateInlineLinks(text))
                    {
                        var segment = new TextSegment
                        {
                            StartOffset = line.Offset + link.Start,
                            Length = link.End - link.Start
                        };

                        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, true))
                        {
                            var hitRect = new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4);
                            if (hitRect.Contains(point))
                            {
                                url = link.Url;
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public new void ScrollToLine(int lineIndex)
    {
        base.ScrollToLine(Math.Max(1, lineIndex + 1));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _fencedCodeStateCache.Clear();

        if (_selectedImageReferenceAnchor?.IsDeleted == true)
        {
            ClearImageSelection();
        }

        if (!_isEnsuringImageAnchorLine)
        {
            EnsureTrailingImageAnchorLine();
        }

        var hasInternalImageReferences = HasInternalImageReference(Text);
        if (_hadInternalImageReferences || hasInternalImageReferences)
        {
            QueueImageRenderRedraw();
        }
        _hadInternalImageReferences = hasInternalImageReferences;

        if (_isTrimmingText || MaxLength <= 0 || Text.Length <= CurrentTextLengthLimit())
        {
            return;
        }

        QueueTrimToMaxLength();
    }

    public bool CanInsertImagesFromDataObject(IDataObject dataObject)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId))
        {
            return false;
        }

        if (TryGetImageFileDrop(dataObject, out var paths) && paths.Count > 0)
        {
            return true;
        }

        return TryGetBitmapSource(dataObject, out _);
    }

    public bool TryInsertImagesFromDataObject(IDataObject dataObject)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId))
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
                var asset = _imageStore.ImportBitmapSource(_noteId, bitmap);
                InsertImageReference(asset);
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

    public bool TryInsertImageFromClipboard()
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId) || IsReadOnly)
        {
            return false;
        }

        BitmapSource? bitmap = null;
        try
        {
            var clipboardData = Clipboard.GetDataObject();
            if (clipboardData != null)
            {
                TryGetBitmapSource(clipboardData, out bitmap);
            }

            bitmap ??= Clipboard.GetImage();
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
            var asset = _imageStore.ImportBitmapSource(_noteId, bitmap);
            InsertImageReference(asset);
        }
        catch (Exception ex)
        {
            ImageImportFailed?.Invoke(ex);
        }

        return true;
    }

    public int InsertImagesFromFiles(IEnumerable<string> paths)
    {
        if (_imageStore == null || string.IsNullOrWhiteSpace(_noteId) || IsReadOnly)
        {
            return 0;
        }

        var imported = _imageStore.ImportImageFiles(_noteId, paths);
        foreach (var asset in imported)
        {
            InsertImageReference(asset);
        }

        return imported.Count;
    }

    private void InsertImageReference(NoteImageAsset asset)
    {
        var insertion = BuildBlockInsertion(
            MarkdownImageReferences.CreateReference(asset.Id, asset.Width, asset.Height));
        var start = SelectionStart;
        SelectedText = insertion;
        var caret = start + insertion.Length;
        CaretIndex = caret;
        Select(caret, 0);
        Focus();
        QueuePostPasteRefresh();
    }

    private string BuildBlockInsertion(string blockText)
    {
        var start = SelectionStart;
        var end = SelectionStart + SelectionLength;
        var builder = new StringBuilder();

        if (start > 0 && Text[start - 1] is not '\n' and not '\r')
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(blockText);

        if (end < Text.Length && Text[end] is not '\n' and not '\r')
        {
            builder.Append(Environment.NewLine);
        }
        else
        {
            builder.Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    private void QueueTrimToMaxLength()
    {
        if (_isTrimQueued)
        {
            return;
        }

        _isTrimQueued = true;
        Dispatcher.BeginInvoke((Action)TrimTextToMaxLength, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void TrimTextToMaxLength()
    {
        _isTrimQueued = false;
        var limit = CurrentTextLengthLimit();
        if (_isTrimmingText || MaxLength <= 0 || Text.Length <= limit)
        {
            return;
        }

        try
        {
            _isTrimmingText = true;
            var caret = CaretIndex;
            Text = Text[..SafeTrimLength(limit)];
            var finalLimit = CurrentTextLengthLimit();
            if (Text.Length > finalLimit)
            {
                Text = Text[..SafeTrimLength(finalLimit)];
            }

            caret = Math.Min(caret, Text.Length);
            CaretIndex = caret;
            Select(caret, 0);
        }
        finally
        {
            _isTrimmingText = false;
        }
    }

    private int SafeTrimLength(int limit)
    {
        if (Document == null || Text.Length <= limit)
        {
            return Text.Length;
        }

        var cut = Math.Clamp(limit, 0, Text.Length);
        if (cut <= 0 || cut >= Document.TextLength)
        {
            return cut;
        }

        var line = Document.GetLineByOffset(cut);
        if (cut > line.Offset &&
            cut < line.EndOffset &&
            MarkdownImageReferences.TryParseReferenceLine(Document.GetText(line), out _))
        {
            return line.Offset;
        }

        return cut;
    }

    private int CurrentTextLengthLimit()
    {
        if (MaxLength <= 0 || Document == null || Document.TextLength <= 0)
        {
            return MaxLength;
        }

        var lastLine = Document.GetLineByOffset(Document.TextLength);
        var structuralDelimiterLength = lastLine.PreviousLine is { } previousLine &&
            previousLine.DelimiterLength > 0 &&
            MarkdownImageReferences.TryParseReferenceLine(Document.GetText(previousLine), out _)
                ? previousLine.DelimiterLength
                : 0;
        return MaxLength > int.MaxValue - structuralDelimiterLength
            ? int.MaxValue
            : MaxLength + structuralDelimiterLength;
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // AvalonEdit's built-in paste command no-ops when the clipboard holds only a
        // bitmap (no text), so its pasting handler never fires. Intercept Ctrl+V and
        // insert the clipboard image ourselves before the editor swallows the key.
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

        if (e.Key == System.Windows.Input.Key.Back && TryDeleteImageBeforeCaret())
        {
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

        if (_acceptsReturn &&
            e.Key == System.Windows.Input.Key.Enter &&
            TryHandleMarkdownListEnter())
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

        base.OnPreviewTextInput(e);
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

    private bool TryDeleteImageBeforeCaret()
    {
        if (_isPreviewMode ||
            IsReadOnly ||
            Document == null ||
            SelectionLength != 0 ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        var caret = Math.Clamp(CaretOffset, 0, Document.TextLength);
        DocumentLine line;
        try
        {
            line = Document.GetLineByOffset(caret);
        }
        catch
        {
            return false;
        }

        if (caret == line.Offset &&
            line.PreviousLine != null &&
            TryGetImageReferenceForLine(line.PreviousLine, out var previousReference, out _))
        {
            DeleteImageReferenceLine(line.PreviousLine, previousReference.ImageId);
            return true;
        }

        if (line.NextLine == null &&
            caret == line.EndOffset &&
            TryGetImageReferenceForLine(line, out var currentReference, out _))
        {
            DeleteImageReferenceLine(line, currentReference.ImageId);
            return true;
        }

        return false;
    }

    private bool TryHandleMarkdownListEnter()
    {
        if (_isPreviewMode ||
            IsReadOnly ||
            Document == null ||
            SelectionLength != 0 ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        DocumentLine line;
        var caret = Math.Clamp(CaretOffset, 0, Document.TextLength);
        try
        {
            line = Document.GetLineByOffset(caret);
        }
        catch
        {
            return false;
        }

        var text = Document.GetText(line);
        var style = AnalyzeLine(Document, line, text);
        if (style.Kind is not (MarkdownLineKind.UnorderedList or MarkdownLineKind.OrderedList))
        {
            return false;
        }

        if (!TryBuildListContinuation(text, style, out var continuation, out var markerStart, out var emptyContentStart))
        {
            return false;
        }

        var indexInLine = Math.Clamp(caret - line.Offset, 0, text.Length);
        if (indexInLine < Math.Min(style.ContentStart, text.Length))
        {
            return false;
        }

        if (IsLineContentEmpty(text, emptyContentStart))
        {
            if (indexInLine < Math.Min(emptyContentStart, text.Length))
            {
                return false;
            }

            RemoveEmptyListMarker(line, markerStart, emptyContentStart);
            return true;
        }

        var insertion = NewLineTextFor(line) + continuation;
        if (MaxLength > 0 && Text.Length + insertion.Length > MaxLength)
        {
            return false;
        }

        Document.BeginUpdate();
        try
        {
            Document.Insert(caret, insertion);
            CaretOffset = caret + insertion.Length;
            Select(CaretOffset, 0);
        }
        finally
        {
            Document.EndUpdate();
        }

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

    private static bool TryBuildListContinuation(
        string text,
        MarkdownLineStyle style,
        out string continuation,
        out int markerStart,
        out int emptyContentStart)
    {
        continuation = "";
        markerStart = FindListMarkerStart(text, style);
        emptyContentStart = style.ContentStart;
        if (markerStart < 0)
        {
            return false;
        }

        var isTask = IsTaskList(style, text);
        if (style.Kind == MarkdownLineKind.UnorderedList)
        {
            continuation = text[..style.ContentStart];
            if (isTask)
            {
                continuation += "[ ] ";
                emptyContentStart = TaskListContentStart(style, text);
            }

            return true;
        }

        if (!TryBuildOrderedListContinuation(text, style, markerStart, out continuation))
        {
            return false;
        }

        if (isTask)
        {
            continuation += "[ ] ";
            emptyContentStart = TaskListContentStart(style, text);
        }

        return true;
    }

    private static int FindListMarkerStart(string text, MarkdownLineStyle style)
    {
        var indent = CountIndent(text);
        if (IsMatchingListAt(text, indent, style))
        {
            return indent;
        }

        var leadingSpaces = CountLeadingSpaces(text);
        if (leadingSpaces != indent && IsMatchingListAt(text, leadingSpaces, style))
        {
            return leadingSpaces;
        }

        return -1;
    }

    private static bool IsMatchingListAt(string text, int start, MarkdownLineStyle style)
    {
        return TryAnalyzeList(text, start, out var candidate) &&
            candidate.Kind == style.Kind &&
            candidate.ContentStart == style.ContentStart;
    }

    private static bool TryBuildOrderedListContinuation(
        string text,
        MarkdownLineStyle style,
        int markerStart,
        out string continuation)
    {
        continuation = "";
        var delimiter = markerStart;
        while (delimiter < text.Length && char.IsDigit(text[delimiter]))
        {
            delimiter++;
        }

        if (delimiter == markerStart ||
            delimiter >= text.Length ||
            text[delimiter] is not ('.' or ')') ||
            delimiter + 1 > style.ContentStart)
        {
            return false;
        }

        var numberText = text[markerStart..delimiter];
        if (!long.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number == long.MaxValue)
        {
            return false;
        }

        var markerEnd = delimiter + 1;
        continuation = text[..markerStart] +
            (number + 1).ToString(CultureInfo.InvariantCulture) +
            text[delimiter] +
            text[markerEnd..style.ContentStart];
        return true;
    }

    private static int TaskListContentStart(MarkdownLineStyle style, string text)
    {
        var start = Math.Min(text.Length, style.ContentStart + 3);
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
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
        var clipboardText = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            ? e.DataObject.GetData(DataFormats.UnicodeText) as string
            : null;

        if (!IsReadOnly &&
            string.IsNullOrEmpty(clipboardText) &&
            (TryInsertImagesFromDataObject(e.DataObject) || TryInsertImageFromClipboard()))
        {
            e.CancelCommand();
            return;
        }

        if (string.IsNullOrEmpty(clipboardText))
        {
            return;
        }

        var text = MarkdownImageReferences.StripRenderMarkers(clipboardText);

        var selectedLength = Math.Max(0, SelectionLength);
        var containsImageReference = MarkdownImageReferences.Enumerate(text).Any();
        var blockPasteText = EnsureImageReferencePasteIsBlock(text, selectedLength);
        if (!TryBuildSafePasteText(blockPasteText, selectedLength, out var pasteText) ||
            (containsImageReference && pasteText.Length != blockPasteText.Length))
        {
            e.CancelCommand();
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

    private string EnsureImageReferencePasteIsBlock(string pasteText, int selectedLength)
    {
        if (string.IsNullOrWhiteSpace(pasteText))
        {
            return pasteText;
        }

        var firstLineEnd = pasteText.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? pasteText : pasteText[..firstLineEnd];
        var lastLineStart = pasteText.LastIndexOfAny(['\r', '\n']) + 1;
        var lastLine = pasteText[lastLineStart..];
        var selectionStart = Math.Clamp(SelectionStart, 0, Text.Length);
        var selectionEnd = Math.Clamp(selectionStart + selectedLength, selectionStart, Text.Length);
        var needsLeadingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(firstLine, out _) &&
            selectionStart > 0 &&
            Text[selectionStart - 1] is not '\r' and not '\n';
        var needsTrailingNewLine =
            MarkdownImageReferences.TryParseReferenceLine(lastLine, out _) &&
            (selectionEnd >= Text.Length || Text[selectionEnd] is not '\r' and not '\n');
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
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var search = 0;
        while ((search = text.IndexOf("](", search, StringComparison.Ordinal)) >= 0)
        {
            var uriStart = search + 2;
            while (uriStart < text.Length &&
                   text[uriStart] is not '\r' and not '\n' &&
                   char.IsWhiteSpace(text[uriStart]))
            {
                uriStart++;
            }

            if (text.IndexOf(MarkdownImageReferences.UriPrefix, uriStart, StringComparison.Ordinal) == uriStart)
            {
                return true;
            }

            search += 2;
        }

        return false;
    }

    private void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        try
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                return;
            }

            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var cleaned = MarkdownImageReferences.StripRenderMarkers(text);
            if (string.Equals(cleaned, text, StringComparison.Ordinal))
            {
                return;
            }

            e.DataObject.SetData(DataFormats.UnicodeText, cleaned);
            e.DataObject.SetData(DataFormats.Text, cleaned);
        }
        catch
        {
            // Clipboard cleanup should not block copy/cut.
        }
    }

    private void EnsureVisualLines()
    {
        TextArea.TextView.EnsureVisualLines();
    }

    private static Brush PreviewSyntaxBrush => Theme.SyntaxFadeBrush;
    private MarkdownRenderOptions RenderOptions => MarkdownRenderOptions.From(_markdownRenderMode, _isPreviewMode);

    private bool TryBuildSafePasteText(string text, int selectedLength, out string pasteText)
    {
        pasteText = text;
        var allowed = MaxLength > 0
            ? CurrentTextLengthLimit() - Math.Max(0, Text.Length - selectedLength)
            : int.MaxValue;
        if (allowed <= 0)
        {
            pasteText = "";
            return false;
        }

        var maxPasteLength = Math.Min(allowed, MaxSafePasteLength);
        if (text.Length <= maxPasteLength && !ContainsLineLongerThan(text, MaxSafePasteLineLength))
        {
            return true;
        }

        pasteText = ClipPasteText(text, maxPasteLength, MaxSafePasteLineLength);
        return pasteText.Length > 0;
    }

    private static string ClipPasteText(string text, int maxLength, int maxLineLength)
    {
        if (maxLength <= 0 || maxLineLength <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var lineLength = 0;
        foreach (var c in text)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (c is '\r' or '\n')
            {
                builder.Append(c);
                lineLength = 0;
                continue;
            }

            if (lineLength >= maxLineLength)
            {
                break;
            }

            builder.Append(c);
            lineLength++;
        }

        return builder.ToString();
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

    private void QueueImageRenderRedraw()
    {
        if (_isImageRenderRedrawQueued)
        {
            return;
        }

        _isImageRenderRedrawQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _isImageRenderRedrawQueued = false;
                TextArea.TextView.Redraw(System.Windows.Threading.DispatcherPriority.Render);
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static bool TryGetLinePoint(TextView textView, DocumentLine line, int indexInLine, VisualYPosition yPosition, out Point point)
    {
        point = default;
        try
        {
            var column = Math.Clamp(indexInLine, 0, line.Length) + 1;
            point = textView.GetVisualPosition(new TextViewPosition(line.LineNumber, column), yPosition);
            point.X -= textView.HorizontalOffset;
            point.Y -= textView.VerticalOffset;
            return IsFinite(point.X) && IsFinite(point.Y);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double SnapStrokeY(double y)
    {
        return Math.Round(y) + 0.5;
    }

    private bool TryGetMarkdownLinkAtOffset(int offset, out string url)
    {
        url = "";
        if (!RenderOptions.HighlightLinks || Document == null || Document.TextLength == 0)
        {
            return false;
        }

        var clampedOffset = Math.Clamp(offset, 0, Document.TextLength);
        var line = Document.GetLineByOffset(clampedOffset);
        var text = Document.GetText(line);
        var indexInLine = Math.Clamp(clampedOffset - line.Offset, 0, text.Length);

        foreach (var link in EnumerateInlineLinks(text))
        {
            if (indexInLine >= link.Start && indexInLine < link.End)
            {
                url = link.Url;
                return true;
            }
        }

        return false;
    }

    private enum MarkdownLineKind
    {
        Plain,
        Heading1,
        Heading2,
        Heading3,
        Heading,
        Quote,
        UnorderedList,
        OrderedList,
        CodeFence,
        CodeBlock,
        HorizontalRule
    }

    private readonly struct MarkdownRenderOptions
    {
        private MarkdownRenderOptions(
            bool applyMarkdownStyle,
            bool fadeSyntax,
            bool renderListBullets,
            bool highlightLinks,
            bool renderBlocks)
        {
            ApplyMarkdownStyle = applyMarkdownStyle;
            FadeSyntax = fadeSyntax;
            RenderListBullets = renderListBullets;
            HighlightLinks = highlightLinks;
            RenderBlocks = renderBlocks;
        }

        public bool ApplyMarkdownStyle { get; }
        public bool FadeSyntax { get; }
        public bool RenderListBullets { get; }
        public bool HighlightLinks { get; }
        public bool RenderBlocks { get; }

        public static MarkdownRenderOptions From(string mode, bool isPreviewMode)
        {
            return mode switch
            {
                MarkdownRenderModes.Off => new MarkdownRenderOptions(false, false, false, false, false),
                MarkdownRenderModes.Basic => new MarkdownRenderOptions(true, false, false, true, true),
                _ => new MarkdownRenderOptions(true, isPreviewMode, isPreviewMode, true, true)
            };
        }
    }

    private readonly struct MarkdownLineStyle
    {
        public MarkdownLineStyle(MarkdownLineKind kind, int markerLength, int contentStart)
        {
            Kind = kind;
            MarkerLength = markerLength;
            ContentStart = contentStart;
        }

        public MarkdownLineKind Kind { get; }
        public int MarkerLength { get; }
        public int ContentStart { get; }
    }

    private readonly struct InlineSpan
    {
        public InlineSpan(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; }
        public int End { get; }

        public bool Intersects(int start, int end)
        {
            return start < End && end > Start;
        }
    }

    private readonly struct MarkdownLinkSpan
    {
        public MarkdownLinkSpan(int start, int end, string url)
        {
            Start = start;
            End = end;
            Url = url;
        }

        public int Start { get; }
        public int End { get; }
        public string Url { get; }
    }

    private bool ShouldRenderImages => _imageStore != null && RenderOptions.RenderBlocks;

    private bool TryGetImageReferenceForLine(DocumentLine line, out MarkdownImageReference reference, out NoteImageAsset? asset)
    {
        reference = default;
        asset = null;
        if (_imageStore == null || Document == null || !ShouldRenderImages)
        {
            return false;
        }

        var text = Document.GetText(line);
        var style = AnalyzeLine(Document, line, text);
        if (style.Kind is MarkdownLineKind.CodeFence or MarkdownLineKind.CodeBlock)
        {
            return false;
        }

        if (!MarkdownImageReferences.TryParseReferenceLine(text, out reference))
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

    private FrameworkElement CreateImageBlock(
        MarkdownImageReference reference,
        NoteImageAsset? asset,
        DocumentLine referenceLine)
    {
        var targetWidth = ImageTargetWidth();
        var displayWidth = ResolveImageDisplayWidth(reference.DisplayOptions, asset, targetWidth);
        var bitmap = asset == null ? null : _imageStore?.GetBitmapSource(asset.Id, Math.Min(targetWidth, displayWidth));
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
            Tag = new ImageBlockTag(referenceAnchor, caretAnchor, reference.ImageId)
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

    private static double ResolveImageDisplayWidth(
        MarkdownImageDisplayOptions options,
        NoteImageAsset? asset,
        double targetWidth)
    {
        targetWidth = Math.Max(80, targetWidth);
        var naturalWidth = Math.Max(1, asset?.Width ?? 180);
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

    private ContextMenu CreateImageContextMenu(string imageId, DocumentLine referenceLine, bool canCopy)
    {
        var menu = ImageContextMenuFactory?.Invoke() ?? new ContextMenu
        {
            HasDropShadow = true
        };
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
            var bitmap = _imageStore?.GetBitmapSource(imageId);
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
        if (TryGetBitmapData(dataObject, DataFormats.Bitmap, autoConvert: true, out bitmap))
        {
            return true;
        }

        foreach (var format in EncodedClipboardImageFormats)
        {
            if (TryGetBitmapData(dataObject, format, autoConvert: false, out bitmap))
            {
                return true;
            }
        }

        bitmap = null;
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
                case byte[] bytes when bytes.Length > 0:
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
                            }
                            source.CopyTo(stream);
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

            paths = dropped
                .Where(NoteImageStore.IsSupportedImageFile)
                .ToList();
            return paths.Count > 0;
        }
        catch
        {
            paths.Clear();
            return false;
        }
    }

    private static bool IsIgnored(IReadOnlyList<InlineSpan> ignoredSpans, int start, int length)
    {
        var end = start + length;
        foreach (var span in ignoredSpans)
        {
            if (span.Intersects(start, end))
            {
                return true;
            }
        }

        return false;
    }

    private MarkdownLineStyle AnalyzeLine(IDocument document, DocumentLine line, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new MarkdownLineStyle(MarkdownLineKind.Plain, 0, 0);
        }

        var indent = CountIndent(text);
        if (indent >= text.Length)
        {
            return new MarkdownLineStyle(MarkdownLineKind.Plain, 0, 0);
        }

        if (IsFenceLine(text, out _))
        {
            return new MarkdownLineStyle(MarkdownLineKind.CodeFence, text.Length, text.Length);
        }

        if (_fencedCodeStateCache.IsInFencedCodeBlockBeforeLine(document, line))
        {
            return new MarkdownLineStyle(MarkdownLineKind.CodeBlock, 0, 0);
        }

        if (IsHorizontalRuleLine(text, indent))
        {
            return new MarkdownLineStyle(MarkdownLineKind.HorizontalRule, text.Length, text.Length);
        }

        if (text[indent] == '#')
        {
            var count = CountRepeated(text, indent, '#');
            var end = indent + count;
            if (count <= 6 && end < text.Length && char.IsWhiteSpace(text[end]))
            {
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                var kind = count switch
                {
                    1 => MarkdownLineKind.Heading1,
                    2 => MarkdownLineKind.Heading2,
                    3 => MarkdownLineKind.Heading3,
                    _ => MarkdownLineKind.Heading
                };
                return new MarkdownLineStyle(kind, end, end);
            }
        }

        if (text[indent] == '>')
        {
            var end = indent + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }
            return new MarkdownLineStyle(MarkdownLineKind.Quote, end, end);
        }

        if (TryAnalyzeList(text, indent, out var listStyle))
        {
            return listStyle;
        }

        var leadingSpaces = CountLeadingSpaces(text);
        if (leadingSpaces != indent &&
            TryAnalyzeList(text, leadingSpaces, out listStyle))
        {
            return listStyle;
        }

        return new MarkdownLineStyle(MarkdownLineKind.Plain, 0, 0);
    }

    private static bool TryAnalyzeList(string text, int start, out MarkdownLineStyle style)
    {
        style = new MarkdownLineStyle(MarkdownLineKind.Plain, 0, 0);
        if (start >= text.Length)
        {
            return false;
        }

        if (text[start] is '-' or '*' or '+')
        {
            if (start + 1 < text.Length && !char.IsWhiteSpace(text[start + 1]))
            {
                return false;
            }

            var end = start + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            style = new MarkdownLineStyle(MarkdownLineKind.UnorderedList, end, end);
            return true;
        }

        if (!char.IsDigit(text[start]))
        {
            return false;
        }

        var delimiter = start + 1;
        while (delimiter < text.Length && char.IsDigit(text[delimiter]))
        {
            delimiter++;
        }

        if (delimiter >= text.Length || text[delimiter] is not ('.' or ')'))
        {
            return false;
        }

        if (delimiter + 1 < text.Length && !char.IsWhiteSpace(text[delimiter + 1]))
        {
            return false;
        }

        var endMarker = delimiter + 1;
        while (endMarker < text.Length && char.IsWhiteSpace(text[endMarker]))
        {
            endMarker++;
        }

        style = new MarkdownLineStyle(MarkdownLineKind.OrderedList, endMarker, endMarker);
        return true;
    }

    private static int CountIndent(string text)
    {
        var indent = 0;
        while (indent < text.Length && indent < 3 && text[indent] == ' ')
        {
            indent++;
        }
        return indent;
    }

    private static int CountLeadingSpaces(string text)
    {
        var indent = 0;
        while (indent < text.Length && text[indent] == ' ')
        {
            indent++;
        }
        return indent;
    }

    private static bool IsTaskList(MarkdownLineStyle style, string text)
    {
        if (style.Kind is not (MarkdownLineKind.UnorderedList or MarkdownLineKind.OrderedList) ||
            style.ContentStart + 2 >= text.Length)
        {
            return false;
        }

        var start = style.ContentStart;
        if (text[start] != '[' || text[start + 2] != ']')
        {
            return false;
        }

        var value = text[start + 1];
        return value is ' ' or 'x' or 'X';
    }

    private static bool IsFenceLine(string text, out int start)
    {
        start = CountIndent(text);
        return MarkdownImageReferences.IsFenceLine(text);
    }

    private static bool IsHorizontalRuleLine(string text, int start)
    {
        if (start >= text.Length || text[start] is not ('-' or '_' or '*'))
        {
            return false;
        }

        var marker = text[start];
        var count = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == marker)
            {
                count++;
                continue;
            }

            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return count >= 3;
    }

    private sealed class FencedCodeStateCache
    {
        private readonly Dictionary<int, bool> _insideBeforeLine = new();
        private IDocument? _document;
        private int _maxCachedLineNumber = 1;

        public void Clear()
        {
            _document = null;
            _insideBeforeLine.Clear();
            _maxCachedLineNumber = 1;
        }

        public bool IsInFencedCodeBlockBeforeLine(IDocument document, DocumentLine line)
        {
            if (!ReferenceEquals(_document, document))
            {
                _document = document;
                _insideBeforeLine.Clear();
                _insideBeforeLine[1] = false;
                _maxCachedLineNumber = 1;
            }

            var lineNumber = Math.Max(1, line.LineNumber);
            if (_insideBeforeLine.TryGetValue(lineNumber, out var cached))
            {
                return cached;
            }

            var nearestLine = Math.Min(_maxCachedLineNumber, lineNumber);
            var inside = _insideBeforeLine.TryGetValue(nearestLine, out var nearestState)
                ? nearestState
                : false;

            for (var number = nearestLine; number < lineNumber; number++)
            {
                var current = document.GetLineByNumber(number);
                if (IsFenceLine(document.GetText(current), out _))
                {
                    inside = !inside;
                }

                _insideBeforeLine[number + 1] = inside;
            }

            _maxCachedLineNumber = Math.Max(_maxCachedLineNumber, lineNumber);
            return inside;
        }
    }

    private sealed class MarkdownImageElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownTextBox _owner;

        public MarkdownImageElementGenerator(MarkdownTextBox owner)
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
                !_owner.TryGetImageReferenceForLine(referenceLine, out var reference, out var asset))
            {
                return null!;
            }

            var element = _owner.CreateImageBlock(reference, asset, referenceLine);
            return new BlockImageElement(element);
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
        string ImageId);

    private static int CountRepeated(string text, int start, char c)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == c)
        {
            count++;
        }
        return count;
    }

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

    private readonly struct HtmlInlineSpan
    {
        public HtmlInlineSpan(
            int start,
            int openEnd,
            int closeStart,
            int end,
            string tagName,
            string? url)
        {
            Start = start;
            OpenEnd = openEnd;
            CloseStart = closeStart;
            End = end;
            TagName = tagName;
            Url = url;
        }

        public int Start { get; }
        public int OpenEnd { get; }
        public int ContentStart => OpenEnd;
        public int ContentEnd => CloseStart;
        public int CloseStart { get; }
        public int End { get; }
        public string TagName { get; }
        public string? Url { get; }
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

    private static IEnumerable<MarkdownLinkSpan> EnumerateMarkdownLinks(string text)
    {
        var search = 0;
        while (search < text.Length)
        {
            var labelStart = text.IndexOf('[', search);
            if (labelStart < 0)
            {
                yield break;
            }

            var labelEnd = text.IndexOf("](", labelStart + 1, StringComparison.Ordinal);
            if (labelEnd < 0)
            {
                yield break;
            }

            var urlStart = labelEnd + 2;
            var urlEnd = text.IndexOf(')', urlStart);
            if (urlEnd < 0)
            {
                yield break;
            }

            var spanEnd = urlEnd + 1;
            if (TryNormalizeMarkdownUrl(text[urlStart..urlEnd], out var url))
            {
                yield return new MarkdownLinkSpan(labelStart, spanEnd, url);
            }

            search = spanEnd;
        }
    }

    private static IEnumerable<MarkdownLinkSpan> EnumerateInlineLinks(string text)
    {
        var ignoredSpans = EnumerateClosedInlineCodeSpans(text).ToList();
        foreach (var link in EnumerateMarkdownLinks(text))
        {
            if (!IsIgnored(ignoredSpans, link.Start, link.End - link.Start))
            {
                yield return link;
            }
        }

        foreach (var html in EnumerateHtmlInlineSpans(text, ignoredSpans))
        {
            if (html.Url != null)
            {
                yield return new MarkdownLinkSpan(html.Start, html.End, html.Url);
            }
        }
    }

    private static IEnumerable<HtmlInlineSpan> EnumerateHtmlInlineSpans(string text, IReadOnlyList<InlineSpan> ignoredSpans)
    {
        var search = 0;
        while (search < text.Length)
        {
            var openStart = text.IndexOf('<', search);
            if (openStart < 0)
            {
                yield break;
            }

            if (!TryParseHtmlOpeningTag(text, openStart, out var tagName, out var openEnd, out var url))
            {
                search = openStart + 1;
                continue;
            }

            if (!TryFindHtmlClosingTag(text, tagName, openEnd, out var closeStart, out var closeEnd))
            {
                search = openEnd;
                continue;
            }

            if (closeStart > openEnd &&
                !IsIgnored(ignoredSpans, openStart, closeEnd - openStart))
            {
                yield return new HtmlInlineSpan(openStart, openEnd, closeStart, closeEnd, tagName, url);
            }

            search = closeEnd;
        }
    }

    private static bool TryParseHtmlOpeningTag(
        string text,
        int openStart,
        out string tagName,
        out int openEnd,
        out string? url)
    {
        tagName = "";
        openEnd = openStart;
        url = null;

        if (openStart + 2 >= text.Length ||
            text[openStart] != '<' ||
            text[openStart + 1] == '/')
        {
            return false;
        }

        var nameStart = openStart + 1;
        var nameEnd = nameStart;
        while (nameEnd < text.Length && IsHtmlTagNameChar(text[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == nameStart)
        {
            return false;
        }

        tagName = text[nameStart..nameEnd].ToLowerInvariant();
        if (!IsSupportedHtmlInlineTag(tagName))
        {
            return false;
        }

        var tagEnd = FindHtmlTagEnd(text, nameEnd);
        if (tagEnd < 0)
        {
            return false;
        }

        var attributes = text[nameEnd..tagEnd];
        if (tagName == "a")
        {
            if (!TryGetHtmlHrefAttribute(attributes, out url) ||
                !TryNormalizeMarkdownUrl(url, out url))
            {
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(attributes))
        {
            return false;
        }

        openEnd = tagEnd + 1;
        return true;
    }

    private static bool TryFindHtmlClosingTag(
        string text,
        string tagName,
        int searchStart,
        out int closeStart,
        out int closeEnd)
    {
        closeStart = -1;
        closeEnd = -1;
        var search = searchStart;

        while (search < text.Length)
        {
            var start = text.IndexOf("</", search, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            var nameStart = start + 2;
            var nameEnd = nameStart;
            while (nameEnd < text.Length && IsHtmlTagNameChar(text[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd > nameStart &&
                string.Equals(text[nameStart..nameEnd], tagName, StringComparison.OrdinalIgnoreCase))
            {
                var end = nameEnd;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                if (end < text.Length && text[end] == '>')
                {
                    closeStart = start;
                    closeEnd = end + 1;
                    return true;
                }
            }

            search = start + 2;
        }

        return false;
    }

    private static int FindHtmlTagEnd(string text, int start)
    {
        char quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetHtmlHrefAttribute(string attributes, out string href)
    {
        href = "";
        var index = 0;
        while (index < attributes.Length)
        {
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            var nameStart = index;
            while (index < attributes.Length && IsHtmlAttributeNameChar(attributes[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                return false;
            }

            var name = attributes[nameStart..index];
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            if (index >= attributes.Length || attributes[index] != '=')
            {
                return false;
            }

            index++;
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            if (index >= attributes.Length)
            {
                return false;
            }

            string value;
            if (attributes[index] is '"' or '\'' )
            {
                var quote = attributes[index];
                var valueStart = ++index;
                var valueEnd = attributes.IndexOf(quote, valueStart);
                if (valueEnd < 0)
                {
                    return false;
                }

                value = attributes[valueStart..valueEnd];
                index = valueEnd + 1;
            }
            else
            {
                var valueStart = index;
                while (index < attributes.Length && !char.IsWhiteSpace(attributes[index]))
                {
                    index++;
                }

                value = attributes[valueStart..index];
            }

            if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
            {
                href = value;
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedHtmlInlineTag(string tagName)
    {
        return tagName is "b" or "strong" or "i" or "em" or "s" or "del" or "u" or "code" or "a";
    }

    private static bool IsHtmlTagNameChar(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool IsHtmlAttributeNameChar(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
    }

    private static IEnumerable<InlineSpan> EnumerateClosedInlineCodeSpans(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf('`', index);
            if (start < 0)
            {
                yield break;
            }

            var end = text.IndexOf('`', start + 1);
            if (end < 0)
            {
                yield break;
            }

            yield return new InlineSpan(start, end + 1);
            index = end + 1;
        }
    }

    private sealed class MarkdownBlockBackgroundRenderer : IBackgroundRenderer
    {
        private readonly MarkdownTextBox _owner;

        public MarkdownBlockBackgroundRenderer(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            var options = _owner.RenderOptions;
            if (!options.RenderBlocks || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var width = textView.ActualWidth;
            var codeBrush = Theme.CodeBrush;
            var quotePen = new Pen(Theme.QuoteBorderBrush, 3);
            var inlineCodeBuilder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                CornerRadius = 3,
                BorderThickness = 0
            };

            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    var text = document.GetText(line);
                    var style = _owner.AnalyzeLine(document, line, text);
                    if (style.Kind is not (MarkdownLineKind.CodeBlock or MarkdownLineKind.CodeFence))
                    {
                        foreach (var span in EnumerateClosedInlineCodeSpans(text))
                        {
                            inlineCodeBuilder.AddSegment(
                                textView,
                                new TextSegment
                                {
                                    StartOffset = line.Offset + span.Start,
                                    Length = span.End - span.Start
                                });
                        }
                    }

                    if (style.Kind is not (MarkdownLineKind.CodeBlock or MarkdownLineKind.Quote))
                    {
                        continue;
                    }

                    var top = textView.GetVisualTopByDocumentLine(line.LineNumber) - textView.VerticalOffset;
                    var height = visualLine.Height;
                    if (line.NextLine != null)
                    {
                        var nextTop = textView.GetVisualTopByDocumentLine(line.NextLine.LineNumber) - textView.VerticalOffset;
                        height = Math.Max(textView.DefaultLineHeight, nextTop - top);
                    }

                    if (style.Kind == MarkdownLineKind.CodeBlock)
                    {
                        drawingContext.DrawRoundedRectangle(
                            codeBrush,
                            null,
                            new Rect(0, top + 1, Math.Max(0, width - 4), Math.Max(1, height - 2)),
                            4,
                            4);
                    }
                    else
                    {
                        var x = 2.5;
                        drawingContext.DrawLine(quotePen, new Point(x, top + 2), new Point(x, top + Math.Max(2, height - 2)));
                    }
                }
            }

            var inlineCodeGeometry = inlineCodeBuilder.CreateGeometry();
            if (inlineCodeGeometry != null)
            {
                drawingContext.DrawGeometry(codeBrush, null, inlineCodeGeometry);
            }
        }
    }

    private sealed class MarkdownListBulletRenderer : IBackgroundRenderer
    {
        private static Typeface ListMarkerTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private readonly MarkdownTextBox _owner;

        public MarkdownListBulletRenderer(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (!_owner.RenderOptions.RenderListBullets || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    var text = document.GetText(line);
                    var style = _owner.AnalyzeLine(document, line, text);
                    if (style.Kind is not (MarkdownLineKind.UnorderedList or MarkdownLineKind.OrderedList) ||
                        IsTaskList(style, text))
                    {
                        continue;
                    }

                    var markerStart = CountLeadingSpaces(text);
                    if (markerStart >= text.Length)
                    {
                        continue;
                    }

                    var markerLength = VisibleListMarkerLength(text, markerStart, style);
                    if (markerLength <= 0)
                    {
                        continue;
                    }

                    if (!TryGetLinePoint(textView, line, markerStart, VisualYPosition.TextTop, out var markerTop) ||
                        !TryGetLinePoint(textView, line, markerStart, VisualYPosition.TextMiddle, out var markerMiddle) ||
                        !TryGetLinePoint(textView, line, markerStart + markerLength, VisualYPosition.TextBottom, out var markerBottom))
                    {
                        continue;
                    }

                    var markerLeft = markerTop.X;
                    var markerRight = markerBottom.X;
                    if (markerRight < markerLeft)
                    {
                        (markerLeft, markerRight) = (markerRight, markerLeft);
                    }

                    var markerWidth = Math.Max(1, markerRight - markerLeft);
                    var markerHeight = Math.Max(1, markerBottom.Y - markerTop.Y);
                    drawingContext.DrawRectangle(
                        Theme.PaperBrush,
                        null,
                        new Rect(markerLeft - 1, markerTop.Y - 1, markerWidth + 2, markerHeight + 2));

                    if (style.Kind == MarkdownLineKind.UnorderedList)
                    {
                        var radius = Math.Max(2.0, Math.Min(3.2, _owner.ScaledFontSize(NoteTypography.FontSize) * 0.16));
                        var center = new Point(markerLeft + markerWidth / 2, markerMiddle.Y);
                        drawingContext.DrawEllipse(Theme.TextBrush, null, center, radius, radius);
                    }
                    else
                    {
                        var formatted = new FormattedText(
                            text.Substring(markerStart, markerLength),
                            CultureInfo.CurrentUICulture,
                            FlowDirection.LeftToRight,
                            ListMarkerTypeface,
                            _owner.ScaledFontSize(NoteTypography.FontSize),
                            Theme.TextBrush,
                            VisualTreeHelper.GetDpi(textView).PixelsPerDip);

                        drawingContext.DrawText(
                            formatted,
                            new Point(markerLeft, markerMiddle.Y - formatted.Height / 2));
                    }
                }
            }
        }

        private static int VisibleListMarkerLength(string text, int markerStart, MarkdownLineStyle style)
        {
            var markerEnd = Math.Min(text.Length, style.MarkerLength);
            while (markerEnd > markerStart && char.IsWhiteSpace(text[markerEnd - 1]))
            {
                markerEnd--;
            }
            return markerEnd - markerStart;
        }
    }

    private sealed class MarkdownHorizontalRuleRenderer : IBackgroundRenderer
    {
        private readonly MarkdownTextBox _owner;

        public MarkdownHorizontalRuleRenderer(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            var options = _owner.RenderOptions;
            if (!options.RenderBlocks || !_owner.IsPreviewMode || document == null || !textView.VisualLinesValid)
            {
                return;
            }

            var width = textView.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            var pen = new Pen(Theme.PaperBorderBrush, 1);
            foreach (var visualLine in textView.VisualLines)
            {
                for (var line = visualLine.FirstDocumentLine;
                     line != null && line.LineNumber <= visualLine.LastDocumentLine.LineNumber;
                     line = line.NextLine)
                {
                    var text = document.GetText(line);
                    var style = _owner.AnalyzeLine(document, line, text);
                    if (style.Kind != MarkdownLineKind.HorizontalRule)
                    {
                        continue;
                    }

                    var ruleStart = CountLeadingSpaces(text);
                    var ruleEnd = text.Length;
                    while (ruleEnd > ruleStart && char.IsWhiteSpace(text[ruleEnd - 1]))
                    {
                        ruleEnd--;
                    }
                    if (!TryGetLinePoint(textView, line, ruleStart, VisualYPosition.TextMiddle, out var ruleStartPoint) ||
                        !TryGetLinePoint(textView, line, ruleEnd, VisualYPosition.TextMiddle, out var ruleEndPoint))
                    {
                        continue;
                    }

                    var top = textView.GetVisualTopByDocumentLine(line.LineNumber) - textView.VerticalOffset;
                    var height = visualLine.Height;
                    if (line.NextLine != null)
                    {
                        var nextTop = textView.GetVisualTopByDocumentLine(line.NextLine.LineNumber) - textView.VerticalOffset;
                        height = Math.Max(textView.DefaultLineHeight, nextTop - top);
                    }

                    var y = SnapStrokeY(ruleStartPoint.Y);
                    if (options.FadeSyntax)
                    {
                        drawingContext.DrawRectangle(Theme.PaperBrush, null, new Rect(0, top, width, Math.Max(1, height)));
                    }
                    var left = options.FadeSyntax
                        ? ruleStartPoint.X
                        : ruleEndPoint.X + 8;
                    left = Math.Max(0, left);
                    drawingContext.DrawLine(pen, new Point(left, y), new Point(Math.Max(left, width - 4), y));
                }
            }
        }
    }

    private sealed class MarkdownMarkerColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownTextBox _owner;

        private static Typeface NormalTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static Typeface HeadingTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.HeadingFontWeight,
            NoteTypography.FontStretch);

        private static Typeface StrongTypeface => new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.HeadingFontWeight,
            NoteTypography.FontStretch);

        private static Typeface EmphasisTypeface => new(
            NoteTypography.FontFamily,
            FontStyles.Italic,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static Typeface CodeTypeface => new(
            NoteTypography.CodeFontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        public MarkdownMarkerColorizer(MarkdownTextBox owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var document = CurrentContext.Document;
            var text = document.GetText(line);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var options = _owner.RenderOptions;
            if (!options.ApplyMarkdownStyle)
            {
                return;
            }

            var weak = Theme.WeakTextBrush;
            var link = Theme.LinkBrush;
            var code = Theme.ActiveBrush;
            var style = _owner.AnalyzeLine(document, line, text);
            var isPreviewMode = _owner.IsPreviewMode;
            var symbol = options.FadeSyntax ? PreviewSyntaxBrush : Theme.ActiveBrush;
            var rawLink = options.FadeSyntax ? PreviewSyntaxBrush : weak;

            ApplyBlockStyle(line, text, style, symbol, weak);
            HideEnhancedBlockMarker(line, text, style, options);
            HideRenderedListMarker(line, text, style, options);

            if (style.Kind is MarkdownLineKind.CodeFence or MarkdownLineKind.HorizontalRule)
            {
                return;
            }

            if (style.Kind == MarkdownLineKind.CodeBlock)
            {
                MarkCode(line, 0, text.Length, Theme.TextBrush);
                return;
            }

            var ignoredSpans = MarkInlineCode(line, text, symbol, code, isPreviewMode);
            AddListMarkerIgnoredSpan(text, style, ignoredSpans);
            ignoredSpans.AddRange(MarkLinks(line, text, symbol, rawLink, link, ignoredSpans, isPreviewMode));
            ignoredSpans.AddRange(MarkHtmlInlineTags(line, text, symbol, code, link, ignoredSpans, isPreviewMode));
            MarkInlineEmphasis(line, text, symbol, ignoredSpans);
            MarkCheckbox(line, text, style, Theme.ActiveBrush, code);
            MarkInlineMarkers(line, text, symbol, ignoredSpans, isPreviewMode);
            HideEnhancedBlockMarker(line, text, style, options);
            HideRenderedListMarker(line, text, style, options);
        }

        private void ApplyBlockStyle(DocumentLine line, string text, MarkdownLineStyle style, Brush symbol, Brush weak)
        {
            if (style.MarkerLength > 0)
            {
                var markerLength = Math.Min(style.MarkerLength, text.Length);
                switch (style.Kind)
                {
                    case MarkdownLineKind.Heading1:
                        MarkHeading(line, 0, markerLength, _owner.ScaledFontSize(17), symbol);
                        break;
                    case MarkdownLineKind.Heading2:
                        MarkHeading(line, 0, markerLength, _owner.ScaledFontSize(15), symbol);
                        break;
                    case MarkdownLineKind.Heading3:
                        MarkHeading(line, 0, markerLength, _owner.ScaledFontSize(14), symbol);
                        break;
                    case MarkdownLineKind.Heading:
                        MarkHeading(line, 0, markerLength, _owner.ScaledFontSize(NoteTypography.FontSize), symbol);
                        break;
                    case MarkdownLineKind.CodeFence:
                        MarkCode(line, 0, markerLength, symbol);
                        break;
                    default:
                        MarkSymbol(line, 0, markerLength, symbol);
                        break;
                }
            }

            var contentLength = Math.Max(0, text.Length - style.ContentStart);
            if (contentLength == 0)
            {
                return;
            }

            switch (style.Kind)
            {
                case MarkdownLineKind.Heading1:
                    MarkHeading(line, style.ContentStart, contentLength, _owner.ScaledFontSize(17), null);
                    break;
                case MarkdownLineKind.Heading2:
                    MarkHeading(line, style.ContentStart, contentLength, _owner.ScaledFontSize(15), null);
                    break;
                case MarkdownLineKind.Heading3:
                    MarkHeading(line, style.ContentStart, contentLength, _owner.ScaledFontSize(14), null);
                    break;
                case MarkdownLineKind.Heading:
                    MarkHeading(line, style.ContentStart, contentLength, _owner.ScaledFontSize(NoteTypography.FontSize), null);
                    break;
                case MarkdownLineKind.Quote:
                    MarkSymbol(line, style.ContentStart, contentLength, weak);
                    break;
            }
        }

        private void HideRenderedListMarker(DocumentLine line, string text, MarkdownLineStyle style, MarkdownRenderOptions options)
        {
            if (!options.RenderListBullets ||
                style.Kind is not (MarkdownLineKind.UnorderedList or MarkdownLineKind.OrderedList) ||
                IsTaskList(style, text))
            {
                return;
            }

            var markerStart = CountLeadingSpaces(text);
            var markerEnd = Math.Min(text.Length, style.MarkerLength);
            if (markerEnd <= markerStart)
            {
                return;
            }

            MarkSymbol(line, markerStart, markerEnd - markerStart, Brushes.Transparent);
        }

        private void HideEnhancedBlockMarker(DocumentLine line, string text, MarkdownLineStyle style, MarkdownRenderOptions options)
        {
            if (!options.FadeSyntax || style.Kind != MarkdownLineKind.Quote)
            {
                return;
            }

            var markerLength = Math.Min(style.MarkerLength, text.Length);
            if (markerLength > 0)
            {
                MarkSymbol(line, 0, markerLength, Brushes.Transparent);
            }
        }

        private static void AddListMarkerIgnoredSpan(string text, MarkdownLineStyle style, List<InlineSpan> ignoredSpans)
        {
            if (style.Kind is not (MarkdownLineKind.UnorderedList or MarkdownLineKind.OrderedList))
            {
                return;
            }

            var markerStart = CountLeadingSpaces(text);
            var markerEnd = Math.Min(text.Length, style.MarkerLength);
            if (markerEnd > markerStart)
            {
                ignoredSpans.Add(new InlineSpan(markerStart, markerEnd));
            }
        }

        private List<InlineSpan> MarkInlineCode(
            DocumentLine line,
            string text,
            Brush markerBrush,
            Brush codeBrush,
            bool isPreviewMode)
        {
            var spans = new List<InlineSpan>();
            var index = 0;
            while (index < text.Length)
            {
                var start = text.IndexOf('`', index);
                if (start < 0)
                {
                    return spans;
                }

                var end = text.IndexOf('`', start + 1);
                if (end < 0)
                {
                    if (isPreviewMode)
                    {
                        MarkCode(line, start, 1, markerBrush);
                        spans.Add(new InlineSpan(start, start + 1));
                        return spans;
                    }

                    Mark(line, start, text.Length - start, markerBrush);
                    spans.Add(new InlineSpan(start, text.Length));
                    return spans;
                }

                MarkCode(line, start, 1, markerBrush);
                if (end > start + 1)
                {
                    MarkCode(line, start + 1, end - start - 1, codeBrush);
                }
                MarkCode(line, end, 1, markerBrush);
                spans.Add(new InlineSpan(start, end + 1));
                index = end + 1;
            }

            return spans;
        }

        private List<InlineSpan> MarkLinks(
            DocumentLine line,
            string text,
            Brush symbol,
            Brush urlBrush,
            Brush link,
            List<InlineSpan> ignoredSpans,
            bool isPreviewMode)
        {
            var spans = new List<InlineSpan>();
            var index = 0;
            while (index < text.Length)
            {
                var labelStart = text.IndexOf('[', index);
                if (labelStart < 0)
                {
                    return spans;
                }

                var labelEnd = text.IndexOf("](", labelStart + 1, StringComparison.Ordinal);
                if (labelEnd < 0)
                {
                    return spans;
                }

                var urlStart = labelEnd + 2;
                var urlEnd = text.IndexOf(')', urlStart);
                if (urlEnd < 0)
                {
                    return spans;
                }

                var spanEnd = urlEnd + 1;
                if (IsIgnored(ignoredSpans, labelStart, spanEnd - labelStart))
                {
                    index = spanEnd;
                    continue;
                }

                MarkSymbol(line, labelStart, 1, symbol);
                MarkSymbol(line, labelEnd, 2, symbol);
                MarkSymbol(line, urlEnd, 1, symbol);

                if (labelEnd > labelStart + 1)
                {
                    if (isPreviewMode)
                    {
                        MarkLinkLabel(line, labelStart + 1, labelEnd - labelStart - 1, link);
                    }
                }

                if (urlEnd > urlStart)
                {
                    MarkSymbol(line, urlStart, urlEnd - urlStart, urlBrush);
                }

                spans.Add(new InlineSpan(labelStart, spanEnd));
                index = spanEnd;
            }

            return spans;
        }

        private List<InlineSpan> MarkHtmlInlineTags(
            DocumentLine line,
            string text,
            Brush symbol,
            Brush code,
            Brush link,
            List<InlineSpan> ignoredSpans,
            bool isPreviewMode)
        {
            var spans = new List<InlineSpan>();
            foreach (var html in EnumerateHtmlInlineSpans(text, ignoredSpans))
            {
                var openLength = html.OpenEnd - html.Start;
                var contentLength = html.ContentEnd - html.ContentStart;
                var closeLength = html.End - html.CloseStart;
                if (contentLength <= 0)
                {
                    continue;
                }

                MarkSymbol(line, html.Start, openLength, symbol);
                MarkSymbol(line, html.CloseStart, closeLength, symbol);

                switch (html.TagName)
                {
                    case "b":
                    case "strong":
                        MarkStyled(line, html.ContentStart, contentLength, StrongTypeface, null);
                        break;
                    case "i":
                    case "em":
                        MarkStyled(line, html.ContentStart, contentLength, EmphasisTypeface, null);
                        break;
                    case "s":
                    case "del":
                        MarkStyled(line, html.ContentStart, contentLength, NormalTypeface, TextDecorations.Strikethrough);
                        break;
                    case "u":
                        MarkStyled(line, html.ContentStart, contentLength, NormalTypeface, TextDecorations.Underline);
                        break;
                    case "code":
                        MarkCode(line, html.ContentStart, contentLength, code);
                        break;
                    case "a":
                        if (isPreviewMode)
                        {
                            MarkLinkLabel(line, html.ContentStart, contentLength, link);
                        }
                        break;
                }

                spans.Add(new InlineSpan(html.Start, html.End));
            }

            return spans;
        }

        private void MarkInlineEmphasis(DocumentLine line, string text, Brush weak, List<InlineSpan> ignoredSpans)
        {
            MarkDelimited(line, text, "~~", weak, NormalTypeface, TextDecorations.Strikethrough, ignoredSpans);
            MarkDelimited(line, text, "**", weak, StrongTypeface, null, ignoredSpans);
            MarkDelimited(line, text, "__", weak, StrongTypeface, null, ignoredSpans);
            MarkSingleDelimited(line, text, '*', weak, EmphasisTypeface, ignoredSpans);
            MarkSingleDelimited(line, text, '_', weak, EmphasisTypeface, ignoredSpans);
        }

        private void MarkDelimited(
            DocumentLine line,
            string text,
            string delimiter,
            Brush markerBrush,
            Typeface contentTypeface,
            TextDecorationCollection? decorations,
            List<InlineSpan> ignoredSpans)
        {
            var index = 0;
            while (index < text.Length)
            {
                var start = text.IndexOf(delimiter, index, StringComparison.Ordinal);
                if (start < 0)
                {
                    return;
                }

                var contentStart = start + delimiter.Length;
                var end = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
                if (end < 0)
                {
                    return;
                }

                var spanEnd = end + delimiter.Length;
                if (end == contentStart || IsIgnored(ignoredSpans, start, spanEnd - start))
                {
                    index = spanEnd;
                    continue;
                }

                MarkSymbol(line, start, delimiter.Length, markerBrush);
                MarkStyled(line, contentStart, end - contentStart, contentTypeface, decorations);
                MarkSymbol(line, end, delimiter.Length, markerBrush);
                index = spanEnd;
            }
        }

        private void MarkSingleDelimited(
            DocumentLine line,
            string text,
            char delimiter,
            Brush markerBrush,
            Typeface contentTypeface,
            List<InlineSpan> ignoredSpans)
        {
            var index = 0;
            while (index < text.Length)
            {
                var start = FindSingleDelimiter(text, delimiter, index);
                if (start < 0)
                {
                    return;
                }

                var end = FindSingleDelimiter(text, delimiter, start + 1);
                if (end < 0)
                {
                    return;
                }

                if (end == start + 1 || IsIgnored(ignoredSpans, start, end - start + 1))
                {
                    index = end + 1;
                    continue;
                }

                MarkSymbol(line, start, 1, markerBrush);
                MarkStyled(line, start + 1, end - start - 1, contentTypeface, null);
                MarkSymbol(line, end, 1, markerBrush);
                index = end + 1;
            }
        }

        private void MarkCheckbox(DocumentLine line, string text, MarkdownLineStyle style, Brush weak, Brush active)
        {
            if (!IsTaskList(style, text))
            {
                return;
            }

            var start = style.ContentStart;
            var value = text[start + 1];
            MarkSymbol(line, start, 1, weak);
            MarkSymbol(line, start + 2, 1, weak);
            MarkStyled(line, start + 1, 1, value == ' ' ? NormalTypeface : StrongTypeface, null, value == ' ' ? weak : active);
        }

        private void MarkInlineMarkers(
            DocumentLine line,
            string text,
            Brush weak,
            List<InlineSpan> ignoredSpans,
            bool isPreviewMode)
        {
            if (isPreviewMode)
            {
                return;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (IsIgnored(ignoredSpans, i, 1))
                {
                    continue;
                }

                var c = text[i];
                if (c == '[' || c == ']' || c == '(' || c == ')')
                {
                    MarkSymbol(line, i, 1, weak);
                    continue;
                }

                if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
                {
                    MarkSymbol(line, i, 2, weak);
                    i++;
                    continue;
                }

                if (c == '*' || c == '_')
                {
                    var length = i + 1 < text.Length && text[i + 1] == c ? 2 : 1;
                    MarkSymbol(line, i, length, weak);
                    i += length - 1;
                }
            }
        }

        private static int FindSingleDelimiter(string text, char delimiter, int startIndex)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                if (text[i] != delimiter)
                {
                    continue;
                }

                var previousSame = i > 0 && text[i - 1] == delimiter;
                var nextSame = i + 1 < text.Length && text[i + 1] == delimiter;
                if (!previousSame && !nextSame)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsIgnored(List<InlineSpan> ignoredSpans, int start, int length)
        {
            var end = start + length;
            foreach (var span in ignoredSpans)
            {
                if (span.Intersects(start, end))
                {
                    return true;
                }
            }

            return false;
        }

        private void Mark(DocumentLine line, int startInLine, int length, Brush brush)
        {
            Change(line, startInLine, length, element => element.TextRunProperties.SetForegroundBrush(brush));
        }

        private void MarkSymbol(DocumentLine line, int startInLine, int length, Brush brush)
        {
            Change(line, startInLine, length, element =>
            {
                element.TextRunProperties.SetTypeface(NormalTypeface);
                var fontSize = _owner.ScaledFontSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontRenderingEmSize(fontSize);
                element.TextRunProperties.SetFontHintingEmSize(fontSize);
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }

        private void MarkHeading(DocumentLine line, int startInLine, int length, double fontSize, Brush? foreground)
        {
            Change(line, startInLine, length, element =>
            {
                element.TextRunProperties.SetTypeface(HeadingTypeface);
                element.TextRunProperties.SetFontRenderingEmSize(fontSize);
                element.TextRunProperties.SetFontHintingEmSize(fontSize);
                if (foreground != null)
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                }
            });
        }

        private void MarkCode(DocumentLine line, int startInLine, int length, Brush foreground)
        {
            Change(line, startInLine, length, element =>
            {
                element.TextRunProperties.SetTypeface(CodeTypeface);
                var fontSize = _owner.ScaledFontSize(NoteTypography.CodeFontSize);
                element.TextRunProperties.SetFontRenderingEmSize(fontSize);
                element.TextRunProperties.SetFontHintingEmSize(fontSize);
                element.TextRunProperties.SetForegroundBrush(foreground);
            });
        }

        private void MarkLinkLabel(DocumentLine line, int startInLine, int length, Brush foreground)
        {
            Change(line, startInLine, length, element =>
            {
                element.TextRunProperties.SetForegroundBrush(foreground);
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }

        private void MarkStyled(
            DocumentLine line,
            int startInLine,
            int length,
            Typeface typeface,
            TextDecorationCollection? decorations,
            Brush? foreground = null)
        {
            Change(line, startInLine, length, element =>
            {
                element.TextRunProperties.SetTypeface(typeface);
                var fontSize = _owner.ScaledFontSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontRenderingEmSize(fontSize);
                element.TextRunProperties.SetFontHintingEmSize(fontSize);
                if (decorations != null)
                {
                    element.TextRunProperties.SetTextDecorations(decorations);
                }
                if (foreground != null)
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                }
            });
        }

        private void Change(DocumentLine line, int startInLine, int length, Action<VisualLineElement> action)
        {
            if (length <= 0 || startInLine >= line.Length)
            {
                return;
            }

            var start = line.Offset + Math.Max(0, startInLine);
            var end = Math.Min(line.Offset + line.Length, start + length);
            if (end <= start)
            {
                return;
            }

            ChangeLinePart(start, end, action);
        }
    }
}
