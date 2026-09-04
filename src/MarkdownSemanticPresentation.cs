using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation : IDisposable
{
    private readonly MarkdownTextBox _editor;
    private readonly MarkdownSemanticDocument _semanticDocument;
    private readonly SemanticColorizer _colorizer;
    private readonly SemanticBackgroundRenderer _backgroundRenderer;
    private readonly SemanticListRenderer _listRenderer;
    private readonly SemanticHorizontalRuleRenderer _horizontalRuleRenderer;
    private bool _redrawQueued;
    private bool _disposed;
    private MarkdownCaretReveal _caretReveal = MarkdownCaretReveal.None;

    public MarkdownSemanticPresentation(
        MarkdownTextBox editor,
        MarkdownSemanticDocument semanticDocument)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _semanticDocument = semanticDocument ?? throw new ArgumentNullException(nameof(semanticDocument));
        _colorizer = new SemanticColorizer(this);
        _backgroundRenderer = new SemanticBackgroundRenderer(this);
        _listRenderer = new SemanticListRenderer(this);
        _horizontalRuleRenderer = new SemanticHorizontalRuleRenderer(this);

        var textView = editor.TextArea.TextView;
        textView.LineTransformers.Insert(0, _colorizer);
        textView.BackgroundRenderers.Insert(0, _backgroundRenderer);
        textView.BackgroundRenderers.Add(_listRenderer);
        textView.BackgroundRenderers.Add(_horizontalRuleRenderer);
        _semanticDocument.SnapshotChanged += OnSnapshotChanged;
        AttachCaretTracking();
        SyncCaretReveal();
        RedrawAll();
    }

    private bool ApplyMarkdownStyle =>
        !string.Equals(
            _editor.MarkdownRenderMode,
            MarkdownRenderModes.Off,
            StringComparison.Ordinal);

    private bool FadeSyntax =>
        string.Equals(
            _editor.MarkdownRenderMode,
            MarkdownRenderModes.Enhanced,
            StringComparison.Ordinal) &&
        _editor.IsPreviewMode;

    /// <summary>Full 档：始终按块级语义呈现最终排版（含编辑态）。</summary>
    private bool IsFullMode =>
        string.Equals(
            _editor.MarkdownRenderMode,
            MarkdownRenderModes.Full,
            StringComparison.Ordinal);

    /// <summary>Full 且可编辑（非只读预览）时，控制符随活动块显灵；否则不参与 reveal。</summary>
    private bool FullRevealEnabled => IsFullMode && !_editor.IsPreviewMode;

    private bool RenderBlocks => ApplyMarkdownStyle;

    private bool RenderListBullets => FadeSyntax || IsFullMode;

    private bool RenderHorizontalRules =>
        RenderBlocks && (_editor.IsPreviewMode || IsFullMode);

    private bool TryCurrentSnapshot(out MarkdownSemanticSnapshot snapshot) =>
        _semanticDocument.TryGetCurrent(out snapshot);

    private MarkdownSemanticSnapshot CurrentSnapshot() =>
        _semanticDocument.TryGetCurrent(out var snapshot)
            ? snapshot
            : MarkdownSemanticSnapshot.Empty;

    private MarkdownSemanticLine SemanticFor(DocumentLine line) =>
        CurrentSnapshot().GetLine(Math.Max(0, line.LineNumber - 1));

    internal MarkdownCaretReveal CaretReveal =>
        FullRevealEnabled ? _caretReveal : MarkdownCaretReveal.None;

    internal bool IsCaretOnLine(int oneBasedLine) =>
        CaretReveal.Active && CaretReveal.CaretLineZeroBased == oneBasedLine - 1;

    /// <summary>该控制符单元（行边界或行内成对范围）在 Full 编辑态是否显灵。</summary>
    internal bool IsRevealed(
        int markerLineOneBased,
        int markerStart,
        int markerLength,
        MarkdownSemanticSpanKind kind,
        int rangeStart = -1,
        int rangeEnd = -1) =>
        FullRevealEnabled &&
        MarkdownSemanticReveal.RevealMarker(
            CaretReveal,
            markerLineOneBased - 1,
            markerStart,
            markerLength,
            kind,
            rangeStart,
            rangeEnd);

    internal bool IsRangeRevealed(int rangeStart, int rangeEnd) =>
        FullRevealEnabled &&
        MarkdownSemanticReveal.RevealRange(CaretReveal, rangeStart, rangeEnd);

    /// <summary>控制符取色：Full 档按显灵取 Active/透明，其余档保留原有淡化/激活语义。</summary>
    internal Brush ControlBrush(bool revealed)
    {
        if (IsFullMode)
        {
            return revealed ? Theme.ActiveBrush : Brushes.Transparent;
        }

        return FadeSyntax ? Theme.SyntaxFadeBrush : Theme.ActiveBrush;
    }

    /// <summary>引用 &gt; 标记取色：Enhanced 预览沿用「完全透明保留宽度」，与一般语法淡化不同。</summary>
    internal Brush QuoteControlBrush(bool revealed)
    {
        if (IsFullMode)
        {
            return revealed ? Theme.ActiveBrush : Brushes.Transparent;
        }

        return FadeSyntax ? Brushes.Transparent : Theme.ActiveBrush;
    }

    private double ScaledFontSize(double baseFontSize)
    {
        var baseSize = Math.Max(1, NoteTypography.FontSize);
        var scale = Math.Clamp(_editor.FontSize / baseSize, 0.5, 1.5);
        return Math.Round(baseFontSize * scale, 1);
    }

    private static bool TryGetTextPoint(
        TextView textView,
        DocumentLine line,
        int absoluteOffset,
        VisualYPosition yPosition,
        out Point point)
    {
        point = default;
        try
        {
            var indexInLine = Math.Clamp(absoluteOffset - line.Offset, 0, line.Length);
            point = textView.GetVisualPosition(
                new TextViewPosition(line.LineNumber, indexInLine + 1),
                yPosition);
            point.X -= textView.HorizontalOffset;
            point.Y -= textView.VerticalOffset;
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }
        catch
        {
            return false;
        }
    }

    private void AttachCaretTracking()
    {
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        _editor.GotKeyboardFocus += OnEditorGotFocus;
    }

    private void DetachCaretTracking()
    {
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _editor.GotKeyboardFocus -= OnEditorGotFocus;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        SyncCaretReveal();
        if (FullRevealEnabled)
        {
            ScheduleRedraw();
        }
    }

    private void OnEditorGotFocus(object? sender, KeyboardFocusChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // 从只读预览进入编辑时 caret 可能未移动（不触发 PositionChanged），这里补刷一次，
        // 避免活动块控制符在上一次预览态里仍保持隐藏。
        SyncCaretReveal();
        if (FullRevealEnabled)
        {
            ScheduleRedraw();
        }
    }

    private void SyncCaretReveal()
    {
        var caret = _editor.TextArea.Caret;
        var document = _editor.Document;
        if (document == null)
        {
            _caretReveal = MarkdownCaretReveal.None;
            return;
        }

        var offset = Math.Clamp(caret.Offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        _caretReveal = new MarkdownCaretReveal(offset, line.LineNumber - 1);
    }

    private void OnSnapshotChanged()
    {
        ScheduleRedraw();
    }

    private void ScheduleRedraw()
    {
        if (_redrawQueued || _disposed)
        {
            return;
        }

        _redrawQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _redrawQueued = false;
                if (!_disposed)
                {
                    RedrawAll();
                }
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RedrawAll()
    {
        _editor.TextArea.TextView.Redraw(
            System.Windows.Threading.DispatcherPriority.Render);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semanticDocument.SnapshotChanged -= OnSnapshotChanged;
        DetachCaretTracking();
        var textView = _editor.TextArea.TextView;
        textView.LineTransformers.Remove(_colorizer);
        textView.BackgroundRenderers.Remove(_backgroundRenderer);
        textView.BackgroundRenderers.Remove(_listRenderer);
        textView.BackgroundRenderers.Remove(_horizontalRuleRenderer);
    }
}
