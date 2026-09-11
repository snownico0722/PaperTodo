using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

[assembly: InternalsVisibleTo("PaperTodo.EdgePreviewExperimentChecks")]

namespace PaperTodo;

// Optional point-based input for a preview that uses its renderer's existing link hit testing.
// The host still owns the presented bounds, pointer routing and preview lifetime.
internal interface IEdgeCapsulePreviewPointerTarget
{
    bool ConsumesPointerAt(Point point);
}

// Experiment: change only the display backend. Keep the existing descriptor sizing so the
// comparison does not attribute a different excerpt/card policy to AvalonEdit. This deliberately
// retains the old lightweight sizing helper; it is not a second new Markdown parser.
internal sealed class AvalonEditEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    internal static AvalonEditEdgeCapsulePreviewProvider Instance { get; } = new();

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var text = context.ReadMarkdownText();
        var mode = context.ReadMarkdownRenderMode();
        var frozen = context with { ReadMarkdownText = () => text, ReadMarkdownRenderMode = () => mode };
        var size = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(frozen).Size;
        AvalonEditEdgeCapsulePreviewView? view = null;
        return new EdgeCapsulePreviewDescriptor(size,
            effective => view = new AvalonEditEdgeCapsulePreviewView(context, effective, text, mode),
            active => view?.SetPreviewActive(active));
    }
}

internal sealed class AvalonEditEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView,
    IEdgeCapsulePreviewPointerTarget
{
    private readonly TextBlock _title;
    internal AvalonEditEdgePreviewViewport Viewport { get; }
    private (string Text, string Mode)? _initial;
    private bool _active = true;

    internal AvalonEditEdgeCapsulePreviewView(EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size, string text, string mode) : base(context, size)
    {
        _initial = (text, mode);
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily, FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 1, 8), VerticalAlignment = VerticalAlignment.Center
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        Children.Add(_title);
        Viewport = new AvalonEditEdgePreviewViewport(context.OpenExternal)
        {
            Margin = new Thickness(1, 0, 2, 0)
        };
        Grid.SetRow(Viewport, 1);
        Children.Add(Viewport);
        Unloaded += (_, _) => Viewport.ClearContent();
        InitializeLiveContent();
    }

    internal void SetPreviewActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Viewport.SetActive(active);
        if (active && IsLoaded) RebuildContent();
    }

    public bool ConsumesPointerAt(Point point) =>
        Viewport.TryGetLink(TranslatePoint(point, Viewport), out _);

    protected override void RebuildContent()
    {
        if (!_active) return;
        _title.Text = Context.Title;
        _title.ToolTip = _title.Text;
        var source = _initial ?? (Context.ReadMarkdownText(), Context.ReadMarkdownRenderMode());
        _initial = null;
        var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(source.Item1, source.Item2);
        // An independent bounded document, never the live editable TextDocument: no undo,
        // selection, accidental task toggles or image bookkeeping can write back to the note.
        var text = string.Join("\n", content.Lines.Select(line =>
            content.RenderMode == MarkdownRenderModes.Full && !line.WasInsideFence &&
            line.FenceKind == MarkdownFenceLineKind.None &&
            MarkdownImageReferences.TryParseReferenceLine(line.Text.Trim(), out var image)
                ? string.IsNullOrWhiteSpace(image.Label) ? "▧" : "▧ " + image.Label
                : line.Text));
        Viewport.SetContent(text, content.RenderMode, Context.Paper.TextZoom,
            content.Truncated, content.IsEmpty);
    }
}

internal sealed class AvalonEditEdgePreviewViewport : Panel
{
    private readonly Action<string> _openExternal;
    private readonly TextBlock _ellipsis;
    private readonly TextBlock _empty;
    private readonly RectangleGeometry _clip = new();
    private MarkdownSemanticDocument? _semantics;
    private MarkdownSemanticPresentation? _presentation;
    private bool _sourceTruncated;
    private bool _sourceEmpty;
    private bool _active = true;
    private string? _pressedLink;
    internal MarkdownTextBox? Editor { get; private set; }
    internal double VisibleBodyHeight { get; private set; }
    internal bool HasPreparedContent => _sourceEmpty
        ? _empty.IsArrangeValid
        : Editor is { IsArrangeValid: true } editor && editor.TextArea.TextView.VisualLinesValid;
    internal bool HasOverflow => _ellipsis.Opacity > 0;

    internal AvalonEditEdgePreviewViewport(Action<string> openExternal)
    {
        _openExternal = openExternal;
        ClipToBounds = true;
        _ellipsis = new TextBlock
        {
            Text = "…", FontFamily = NoteTypography.FontFamily, FontSize = AppTypography.Scale(14),
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false, Opacity = 0
        };
        _empty = new TextBlock
        {
            Text = "—", FontFamily = NoteTypography.FontFamily, FontSize = AppTypography.Scale(16),
            TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 18, 4, 4),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        _ellipsis.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Children.Add(_empty);
        Children.Add(_ellipsis);
        PreviewMouseWheel += (_, e) => e.Handled = true;
        RequestBringIntoView += (_, e) => e.Handled = true;
        PreviewMouseLeftButtonDown += OnLinkDown;
        PreviewMouseLeftButtonUp += OnLinkUp;
        LostMouseCapture += (_, _) => _pressedLink = null;
        MouseMove += (sender, e) => Editor?.SetInteractionCursor(TryGetLink(e.GetPosition(this), out _) ? Cursors.Hand : Cursors.Arrow);
        MouseLeave += (_, _) => Editor?.SetInteractionCursor(Cursors.Arrow);
    }

    internal void SetActive(bool active)
    {
        _active = active;
        if (!active) CancelClick();
    }

    internal void SetContent(string text, string mode, double zoom, bool truncated, bool empty)
    {
        // Configure while detached: MarkdownTextBox refreshes an attached editor synchronously.
        // The normal WPF layout below then measures only a finite viewport, not an infinite panel.
        var editor = new MarkdownTextBox
        {
            Document = new TextDocument(text), Focusable = false, IsTabStop = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ClipToBounds = true, ContextMenu = null
        };
        MarkdownSemanticDocument? semantics = null;
        MarkdownSemanticPresentation? presentation = null;
        try
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("TextArea")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            // No ScrollViewer at all. A finite TextArea still uses AvalonEdit's visible-line cache.
            editor.Template = new ControlTemplate(typeof(MarkdownTextBox)) { VisualTree = presenter };
            editor.SetPreviewMode(true);
            editor.SetMarkdownEditAnimationEnabled(false);
            editor.SetMarkdownRenderMode(mode);
            editor.SetTextZoom(double.IsFinite(zoom) ? Math.Clamp(zoom, 0.5, 1.5) : 1);
            editor.TextArea.ActiveInputHandler = null;
            editor.TextArea.Focusable = false;
            var scrolling = (IScrollInfo)editor.TextArea;
            scrolling.CanHorizontallyScroll = false;
            scrolling.CanVerticallyScroll = false;
            editor.Document.UndoStack.SizeLimit = 0;
            semantics = new MarkdownSemanticDocument(editor.Document);
            editor.SetSemanticDocument(semantics);
            presentation = new MarkdownSemanticPresentation(editor, semantics);
            // No NoteImageStore is attached: the preview never decodes or mutates note images.
            ClearContent();
            Editor = editor;
            _semantics = semantics;
            _presentation = presentation;
            _sourceTruncated = truncated;
            _sourceEmpty = empty;
            editor.Clip = _clip;
            editor.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Children.Insert(0, editor);
            InvalidateMeasure();
        }
        catch
        {
            presentation?.Dispose();
            editor.SetSemanticDocument(null);
            semantics?.Dispose();
            throw;
        }
    }

    internal void ClearContent()
    {
        CancelClick();
        if (Editor is { } editor)
        {
            Children.Remove(editor);
            _presentation?.Dispose();
            editor.SetSemanticDocument(null);
            _semantics?.Dispose();
        }
        Editor = null;
        _presentation = null;
        _semantics = null;
        _sourceEmpty = false;
        _sourceTruncated = false;
        _empty.Visibility = Visibility.Collapsed;
        _ellipsis.Opacity = 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 460,
            double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 410);
        _empty.Measure(size);
        _ellipsis.Measure(size);
        Editor?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflow = _sourceTruncated;
        if (!_sourceEmpty && Editor is { } editor)
        {
            editor.Arrange(new Rect(finalSize));
            var textView = editor.TextArea.TextView;
            if (textView.VisualLinesValid)
            {
                var last = textView.VisualLines.LastOrDefault();
                overflow |= last != null && (last.LastDocumentLine.LineNumber < editor.Document.LineCount ||
                    last.VisualTop + last.Height > finalSize.Height + 0.5);
            }
        }
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _ellipsis.DesiredSize.Height) : 0;
        VisibleBodyHeight = Math.Max(0, finalSize.Height - indicatorHeight);
        _clip.Rect = new Rect(0, 0, finalSize.Width, VisibleBodyHeight);
        _empty.Arrange(new Rect(0, 0, finalSize.Width, VisibleBodyHeight));
        _ellipsis.Opacity = overflow ? 1 : 0;
        _ellipsis.Arrange(new Rect(0, VisibleBodyHeight, finalSize.Width, indicatorHeight));
        return finalSize;
    }

    internal bool TryGetLink(Point point, out string url)
    {
        url = "";
        if (!_active || _sourceEmpty || Editor is not { } editor ||
            !new Rect(0, 0, ActualWidth, VisibleBodyHeight).Contains(point) ||
            !editor.TextArea.TextView.VisualLinesValid) return false;
        var textPoint = TranslatePoint(point, editor.TextArea.TextView);
        return editor.TryGetOpenableLinkFromTextViewPoint(textPoint, out url) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "mailto";
    }

    private void OnLinkDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetLink(e.GetPosition(this), out var url)) return;
        _pressedLink = url;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnLinkUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedLink;
        if (pressed == null) return;
        var activate = TryGetLink(e.GetPosition(this), out var url) && url == pressed;
        CancelClick();
        e.Handled = true;
        if (activate) _openExternal(url);
    }

    private void CancelClick()
    {
        _pressedLink = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
