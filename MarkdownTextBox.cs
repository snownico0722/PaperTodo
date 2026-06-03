using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Brushes = System.Windows.Media.Brushes;
using TextWrapping = System.Windows.TextWrapping;

namespace PaperTodo;

public sealed class MarkdownTextBox : TextEditor
{
    private bool _isTrimmingText;
    private bool _acceptsReturn = true;
    private bool _acceptsTab = true;
    private bool _isPreviewMode;

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
        NoteTypography.ApplyTextRendering(this);
        NoteTypography.ApplyTextRendering(TextArea);
        NoteTypography.ApplyTextRendering(TextArea.TextView);

        Options.ConvertTabsToSpaces = false;
        Options.IndentationSize = 4;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;

        TextArea.TextView.BackgroundRenderers.Add(new MarkdownBlockBackgroundRenderer());
        TextArea.TextView.LineTransformers.Add(new MarkdownMarkerColorizer(this));
        DataObject.AddPastingHandler(this, OnPaste);
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

    public void SetPreviewMode(bool isPreviewMode)
    {
        _isPreviewMode = isPreviewMode;
        IsReadOnly = isPreviewMode;
        Focusable = !isPreviewMode;
        TextArea.Focusable = !isPreviewMode;
        SetInteractionCursor(isPreviewMode ? Cursors.Arrow : Cursors.IBeam);
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
        CaretBrush = _isPreviewMode ? Brushes.Transparent : Theme.TextBrush;
        TextArea.TextView.LinkTextForegroundBrush = Theme.LinkBrush;
        TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TextArea.TextView.Redraw();
    }

    public void WrapSelection(string prefix, string suffix)
    {
        var start = SelectionStart;
        var length = SelectionLength;
        var selected = SelectedText ?? "";

        SelectedText = prefix + selected + suffix;
        Focus();

        if (length == 0)
        {
            Select(start + prefix.Length, 0);
        }
        else
        {
            Select(start + prefix.Length, length);
        }
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
        if (Document == null)
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
                    foreach (var link in EnumerateMarkdownLinks(text))
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

        if (_isTrimmingText || MaxLength <= 0 || Text.Length <= MaxLength)
        {
            return;
        }

        try
        {
            _isTrimmingText = true;
            var caret = Math.Min(CaretIndex, MaxLength);
            Text = Text[..MaxLength];
            CaretIndex = caret;
            Select(caret, 0);
        }
        finally
        {
            _isTrimmingText = false;
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
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

        base.OnPreviewKeyDown(e);
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (MaxLength <= 0 ||
            !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            return;
        }

        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var selectedLength = Math.Max(0, SelectionLength);
        var allowed = MaxLength - Math.Max(0, Text.Length - selectedLength);
        if (allowed <= 0)
        {
            e.CancelCommand();
            return;
        }

        if (text.Length <= allowed)
        {
            return;
        }

        var clipped = text[..allowed];
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, clipped);
        data.SetData(DataFormats.Text, clipped);
        e.DataObject = data;
    }

    private void EnsureVisualLines()
    {
        TextArea.TextView.EnsureVisualLines();
    }

    private bool TryGetMarkdownLinkAtOffset(int offset, out string url)
    {
        url = "";
        if (Document == null || Document.TextLength == 0)
        {
            return false;
        }

        var clampedOffset = Math.Clamp(offset, 0, Document.TextLength);
        var line = Document.GetLineByOffset(clampedOffset);
        var text = Document.GetText(line);
        var indexInLine = Math.Clamp(clampedOffset - line.Offset, 0, text.Length);

        foreach (var link in EnumerateMarkdownLinks(text))
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
        List,
        CodeFence,
        CodeBlock
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

    private static MarkdownLineStyle AnalyzeLine(IDocument document, DocumentLine line, string text)
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

        if (IsInFencedCodeBlockBeforeLine(document, line))
        {
            return new MarkdownLineStyle(MarkdownLineKind.CodeBlock, 0, 0);
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

        if ((text[indent] == '-' || text[indent] == '*' || text[indent] == '+') &&
            indent + 1 < text.Length &&
            char.IsWhiteSpace(text[indent + 1]))
        {
            var end = indent + 2;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }
            return new MarkdownLineStyle(MarkdownLineKind.List, end, end);
        }

        if (char.IsDigit(text[indent]))
        {
            var end = indent + 1;
            while (end < text.Length && char.IsDigit(text[end]))
            {
                end++;
            }

            if (end < text.Length &&
                (text[end] == '.' || text[end] == ')') &&
                end + 1 < text.Length &&
                char.IsWhiteSpace(text[end + 1]))
            {
                end += 2;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }
                return new MarkdownLineStyle(MarkdownLineKind.List, end, end);
            }
        }

        return new MarkdownLineStyle(MarkdownLineKind.Plain, 0, 0);
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

    private static bool IsFenceLine(string text, out int start)
    {
        start = CountIndent(text);
        if (start >= text.Length)
        {
            return false;
        }

        return (text[start] == '`' && CountRepeated(text, start, '`') >= 3) ||
               (text[start] == '~' && CountRepeated(text, start, '~') >= 3);
    }

    private static bool IsInFencedCodeBlockBeforeLine(IDocument document, DocumentLine line)
    {
        var inside = false;
        for (var number = 1; number < line.LineNumber; number++)
        {
            var current = document.GetLineByNumber(number);
            if (IsFenceLine(document.GetText(current), out _))
            {
                inside = !inside;
            }
        }
        return inside;
    }

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

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
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
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (document == null || !textView.VisualLinesValid)
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
                    var style = AnalyzeLine(document, line, text);
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

    private sealed class MarkdownMarkerColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownTextBox _owner;

        private static readonly Typeface NormalTypeface = new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static readonly Typeface HeadingTypeface = new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.HeadingFontWeight,
            NoteTypography.FontStretch);

        private static readonly Typeface StrongTypeface = new(
            NoteTypography.FontFamily,
            NoteTypography.FontStyle,
            NoteTypography.HeadingFontWeight,
            NoteTypography.FontStretch);

        private static readonly Typeface EmphasisTypeface = new(
            NoteTypography.FontFamily,
            FontStyles.Italic,
            NoteTypography.FontWeight,
            NoteTypography.FontStretch);

        private static readonly Typeface CodeTypeface = new(
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

            var weak = Theme.WeakTextBrush;
            var link = Theme.LinkBrush;
            var code = Theme.ActiveBrush;
            var style = AnalyzeLine(document, line, text);
            var isPreviewMode = _owner.IsPreviewMode;
            var symbol = isPreviewMode ? Theme.WeakTextBrush : Theme.ActiveBrush;

            ApplyBlockStyle(line, text, style, symbol, weak);

            if (style.Kind == MarkdownLineKind.CodeFence)
            {
                return;
            }

            if (style.Kind == MarkdownLineKind.CodeBlock)
            {
                MarkCode(line, 0, text.Length, Theme.TextBrush);
                return;
            }

            var ignoredSpans = MarkInlineCode(line, text, symbol, code);
            ignoredSpans.AddRange(MarkLinks(line, text, symbol, weak, link, ignoredSpans, isPreviewMode));
            MarkInlineEmphasis(line, text, symbol, ignoredSpans);
            MarkCheckbox(line, text, style, symbol, code);
            MarkInlineMarkers(line, text, symbol, ignoredSpans);
        }

        private void ApplyBlockStyle(DocumentLine line, string text, MarkdownLineStyle style, Brush symbol, Brush weak)
        {
            if (style.MarkerLength > 0)
            {
                var markerLength = Math.Min(style.MarkerLength, text.Length);
                switch (style.Kind)
                {
                    case MarkdownLineKind.Heading1:
                        MarkHeading(line, 0, markerLength, 18, symbol);
                        break;
                    case MarkdownLineKind.Heading2:
                        MarkHeading(line, 0, markerLength, 16, symbol);
                        break;
                    case MarkdownLineKind.Heading3:
                        MarkHeading(line, 0, markerLength, 15, symbol);
                        break;
                    case MarkdownLineKind.Heading:
                        MarkHeading(line, 0, markerLength, NoteTypography.FontSize, symbol);
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
                    MarkHeading(line, style.ContentStart, contentLength, 18, null);
                    break;
                case MarkdownLineKind.Heading2:
                    MarkHeading(line, style.ContentStart, contentLength, 16, null);
                    break;
                case MarkdownLineKind.Heading3:
                    MarkHeading(line, style.ContentStart, contentLength, 15, null);
                    break;
                case MarkdownLineKind.Heading:
                    MarkHeading(line, style.ContentStart, contentLength, NoteTypography.FontSize, null);
                    break;
                case MarkdownLineKind.Quote:
                    MarkSymbol(line, style.ContentStart, contentLength, weak);
                    break;
            }
        }

        private List<InlineSpan> MarkInlineCode(DocumentLine line, string text, Brush markerBrush, Brush codeBrush)
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
            if (style.Kind != MarkdownLineKind.List || style.ContentStart + 2 >= text.Length)
            {
                return;
            }

            var start = style.ContentStart;
            if (text[start] != '[' || text[start + 2] != ']')
            {
                return;
            }

            var value = text[start + 1];
            if (value != ' ' && value != 'x' && value != 'X')
            {
                return;
            }

            MarkSymbol(line, start, 1, weak);
            MarkSymbol(line, start + 2, 1, weak);
            MarkStyled(line, start + 1, 1, value == ' ' ? NormalTypeface : StrongTypeface, null, value == ' ' ? weak : active);
        }

        private void MarkInlineMarkers(DocumentLine line, string text, Brush weak, List<InlineSpan> ignoredSpans)
        {
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
                element.TextRunProperties.SetFontRenderingEmSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontHintingEmSize(NoteTypography.FontSize);
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
                element.TextRunProperties.SetFontRenderingEmSize(NoteTypography.CodeFontSize);
                element.TextRunProperties.SetFontHintingEmSize(NoteTypography.CodeFontSize);
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
                element.TextRunProperties.SetFontRenderingEmSize(NoteTypography.FontSize);
                element.TextRunProperties.SetFontHintingEmSize(NoteTypography.FontSize);
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
