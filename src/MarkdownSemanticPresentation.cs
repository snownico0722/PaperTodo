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
    private bool _redrawAllQueued;
    private int _redrawStart = int.MaxValue;
    private int _redrawEnd;
    private bool _disposed;
    private MarkdownCaretReveal _caretReveal = MarkdownCaretReveal.None;
    private bool _revealGestureFrozen;
    private MarkdownCaretReveal _frozenGestureReveal = MarkdownCaretReveal.None;

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
        _editor.CaretRevealGestureStarted += OnCaretRevealGestureStarted;
        _editor.CaretRevealGestureEnded += OnCaretRevealGestureEnded;
        SyncCaretReveal();
        SyncRevealFade();
        AttachCollapseGenerator();
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

    internal MarkdownCaretReveal CaretReveal =>
        !FullRevealEnabled
            ? MarkdownCaretReveal.None // 预览态绝不显灵：优先级高于冻结快照，覆盖手势中途退回预览
            : _revealGestureFrozen ? _frozenGestureReveal : _caretReveal;

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
            return RevealColor(Theme.ActiveBrush, revealed);
        }

        return FadeSyntax ? Theme.SyntaxFadeBrush : Theme.ActiveBrush;
    }

    /// <summary>引用 &gt; 标记取色：Enhanced 预览沿用「完全透明保留宽度」，与一般语法淡化不同。</summary>
    internal Brush QuoteControlBrush(bool revealed)
    {
        if (IsFullMode)
        {
            return RevealColor(Theme.ActiveBrush, revealed);
        }

        return FadeSyntax ? Brushes.Transparent : Theme.ActiveBrush;
    }

    private double ComputeScale()
    {
        var baseSize = Math.Max(1, NoteTypography.FontSize);
        return Math.Clamp(_editor.FontSize / baseSize, 0.5, 1.5);
    }

    private double ScaledFontSize(double baseFontSize)
    {
        var scale = ComputeScale();
        return Math.Round(baseFontSize * scale, 1);
    }

    /// <summary>当前字号缩放系数（0.5..1.5）。图形元素的像素度量乘它后与文本同步缩放。</summary>
    internal double ZoomFactor() => ComputeScale();

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

        // 鼠标手势内冻结：手势结束时再由 OnCaretRevealGestureEnded 用最终 caret 同步一次，
        // 避免按下落点触发 reveal 重排，使手势内两次命中测试落在不同布局。
        if (_revealGestureFrozen)
        {
            return;
        }

        SyncCaretReveal();
        SyncRevealFade();
        AlignCollapseTableToReveal(scheduleRedraw: true);
    }

    private void OnEditorGotFocus(object? sender, KeyboardFocusChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // 从只读预览进入编辑时 caret 可能未移动（不触发 PositionChanged），这里补刷一次，
        // 避免活动块控制符在上一次预览态里仍保持隐藏。鼠标手势内（预览点入）同样冻结到松开。
        if (_revealGestureFrozen)
        {
            return;
        }

        SyncCaretReveal();
        SyncRevealFade();
        AlignCollapseTableToReveal(scheduleRedraw: true);
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

    private void OnCaretRevealGestureStarted()
    {
        if (_disposed || !IsFullMode)
        {
            return;
        }

        // 快照取「按下瞬间」实际生效的显灵值（预览起点即 None），整段手势内保持该布局不变。
        _frozenGestureReveal = CaretReveal;
        _revealGestureFrozen = true;
    }

    private void OnCaretRevealGestureEnded()
    {
        if (_disposed || !_revealGestureFrozen)
        {
            return;
        }

        _revealGestureFrozen = false;
        _frozenGestureReveal = MarkdownCaretReveal.None;
        if (IsFullMode && FullRevealEnabled)
        {
            // AvalonEdit 已结束本次手势的拖选判定，这里才允许按最终 caret 显灵一次并重排。
            SyncCaretReveal();
            SyncRevealFade();
            AlignCollapseTableToReveal(scheduleRedraw: true);
        }
    }

    private void OnSnapshotChanged(MarkdownSourceChange? change)
    {
        // 文本编辑会使标记位移：中止进行中的淡入，避免把旧 alpha 施加到新布局的标记上。
        AbortRevealFade();

        // 静态候选随 snapshot 重建：优先按语义层增量窗口局部 rebase（逐键、不整篇扫）；
        // 不满足（整篇解析/预览态等）时回退置 null，由下次 Ensure 整篇构建。
        var table = _collapseTable;
        if (table != null &&
            IsFullMode &&
            change is { } sourceChange &&
            !ReferenceEquals(sourceChange.OldSnapshot, sourceChange.NewSnapshot) &&
            ReferenceEquals(table.Snapshot, sourceChange.OldSnapshot))
        {
            var text = _editor.Text ?? string.Empty;
            _collapseTable = table.Rebase(sourceChange.NewSnapshot, text, sourceChange.Window, CaretReveal) ??
                MarkdownCollapseTable.Build(sourceChange.NewSnapshot, text, CaretReveal);
        }
        else
        {
            _collapseTable = null;
        }

        ScheduleRedraw();
    }

    private void ScheduleRedraw(int start = 0, int length = -1)
    {
        if (_disposed)
        {
            return;
        }

        // 快照变化请求全量刷新，覆盖此前排队的旧文本偏移；同一帧的光标移动合并到一个行区间。
        _redrawAllQueued |= length < 0;
        if (!_redrawAllQueued)
        {
            _redrawStart = Math.Min(_redrawStart, start);
            _redrawEnd = Math.Max(_redrawEnd, start + length);
        }

        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _redrawQueued = false;
                var redrawAll = _redrawAllQueued;
                var redrawStart = _redrawStart;
                var redrawEnd = _redrawEnd;
                _redrawAllQueued = false;
                _redrawStart = int.MaxValue;
                _redrawEnd = 0;
                if (!_disposed)
                {
                    if (redrawAll)
                    {
                        RedrawAll();
                    }
                    else
                    {
                        _editor.TextArea.TextView.Redraw(
                            redrawStart, redrawEnd - redrawStart,
                            System.Windows.Threading.DispatcherPriority.Render);
                    }
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
        _editor.CaretRevealGestureStarted -= OnCaretRevealGestureStarted;
        _editor.CaretRevealGestureEnded -= OnCaretRevealGestureEnded;
        DetachCollapseGenerator();
        AbortRevealFade();
        var textView = _editor.TextArea.TextView;
        textView.LineTransformers.Remove(_colorizer);
        textView.BackgroundRenderers.Remove(_backgroundRenderer);
        textView.BackgroundRenderers.Remove(_listRenderer);
        textView.BackgroundRenderers.Remove(_horizontalRuleRenderer);
    }
}
