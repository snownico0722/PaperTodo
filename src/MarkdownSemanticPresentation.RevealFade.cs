using System;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    /// <summary>进入编辑态（控制符显灵）的淡入时长（毫秒）。</summary>
    private const double RevealFadeMs = 140;

    private DispatcherTimer? _fadeTimer;
    private DateTime _fadeStartedAt;
    private double _fadeAlpha = 1;
    private bool _lastLineReveal;
    private int _lastRevealCaretLine = -1;

    /// <summary>仅在用户开启「编辑态动画」且处于 Full 编辑态时才启用淡入。</summary>
    private bool RevealFadeEnabled =>
        _editor.MarkdownEditAnimationEnabled && FullRevealEnabled;

    /// <summary>
    /// 控制符显灵取色：未显灵保持透明；显灵时叠加当前行淡入 alpha。动画结束后
    /// (alpha=1) 直接返回原实色，与关闭动画的瞬显行为完全一致。
    /// </summary>
    internal Brush RevealColor(Brush fullColor, bool revealed)
    {
        if (!revealed)
        {
            return Brushes.Transparent;
        }

        return Theme.BrushWithAlpha(fullColor, RevealFadeEnabled ? _fadeAlpha : 1);
    }

    /// <summary>
    /// caret 每次移动/聚焦后同步淡入状态：当 caret 行的控制符显灵出现「上升沿」
    /// （首次显灵或切到另一显灵行）时启动一次短淡入；离开显灵区即中止（隐回渲染态无需淡出）。
    /// </summary>
    private void SyncRevealFade()
    {
        if (!RevealFadeEnabled || !TryGetCaretLineZero(out var lineZero))
        {
            AbortRevealFade();
            _lastLineReveal = false;
            _lastRevealCaretLine = -1;
            return;
        }

        var hasReveal = HasRevealOnCaretLine(lineZero);
        if (hasReveal && (lineZero != _lastRevealCaretLine || !_lastLineReveal))
        {
            StartRevealFade();
        }
        else if (!hasReveal)
        {
            // 光标离开显灵区：标记立隐，淡入进度复位（该行不再被取色）。
            AbortRevealFade();
        }

        _lastLineReveal = hasReveal;
        _lastRevealCaretLine = hasReveal ? lineZero : -1;
    }

    private void StartRevealFade()
    {
        _fadeStartedAt = DateTime.UtcNow;
        _fadeAlpha = 0;
        EnsureFadeTimer();
        _fadeTimer!.Start();
    }

    /// <summary>终止淡入并把 alpha 置回满值，使后续取色立即回到瞬显行为。</summary>
    private void AbortRevealFade()
    {
        _fadeTimer?.Stop();
        _fadeAlpha = 1;
    }

    private void EnsureFadeTimer()
    {
        if (_fadeTimer != null)
        {
            return;
        }

        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _fadeTimer.Tick += OnRevealFadeTick;
    }

    private void OnRevealFadeTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!RevealFadeEnabled)
        {
            // 动画开关被关闭或离开 Full 编辑态：停止并回到瞬显。
            AbortRevealFade();
            return;
        }

        var elapsedMs = (DateTime.UtcNow - _fadeStartedAt).TotalMilliseconds;
        var t = Math.Clamp(elapsedMs / RevealFadeMs, 0.0, 1.0);
        _fadeAlpha = 1 - (1 - t) * (1 - t); // EaseOutQuad：先快后慢、无过冲。
        RedrawRevealRange();

        if (t >= 1)
        {
            // 本帧 alpha 已 =1，且上方刚用它对「整段显灵范围」覆盖的行整体重绘过，
            // 即完成动画结束时的最终状态校正，无需再补一次重绘。
            _fadeTimer?.Stop();
        }
    }

    /// <summary>
    /// 每帧重绘「当前 caret 显灵涉及的源码行」。范围型单元（跨行 strong/html 容器等）两端可与
    /// caret 分处不同行，故按显灵 range 覆盖的行区间整段重绘，避免非光标端停留旧帧；无 range
    /// 命中时退化单行重绘（行边界 marker）。
    /// </summary>
    private void RedrawRevealRange()
    {
        var document = _editor.Document;
        var textView = _editor.TextArea.TextView;
        if (document == null || document.TextLength <= 0)
        {
            return;
        }

        // 与 IsRangeRevealed/IsRevealed 共用同一显灵源（手势冻结期取冻结 caret），保证
        // 「本帧会被 alpha 取色的 range」与「本帧重绘的行区间」逐帧同源、范围定义一致。
        var reveal = CaretReveal;
        if (TryGetRevealedRangeExtent(reveal, out var minStart, out var maxEnd))
        {
            // 任一显灵 range 必含 caret offset → 行区间必覆盖 caret 行，故整行重绘即同时
            // 覆盖 caret 行上的全部单格 marker，无需在 union 分支外再单独画 caret 行。
            var firstLine = document.GetLineByOffset(minStart);
            // End-1 与 MarkdownSemanticSnapshot.BuildSpanLineIndex 的行归属语义一致，
            // 回避区间右端恰落在下一行行首/文末时的越界或多余一行。
            var lastLine = document.GetLineByOffset(Math.Max(minStart, maxEnd - 1));
            var length = lastLine.Offset + lastLine.TotalLength - firstLine.Offset;
            textView.Redraw(firstLine.Offset, length, DispatcherPriority.Render);
            // 跨行 range 保留全量兜底，避免非 caret 端 alpha 停留旧帧；单行无需重建其他可视行。
            if (firstLine != lastLine)
            {
                textView.Redraw(DispatcherPriority.Render);
            }
            return;
        }

        if (!reveal.Active)
        {
            return;
        }

        // 无范围型显灵：退化到旧单行重绘（caret 行的标题/引用/列表/围栏/分隔线/转义 marker）。
        var offset = Math.Clamp(reveal.CaretOffset, 0, document.TextLength);
        var caretLine = document.GetLineByOffset(offset);
        textView.Redraw(caretLine.Offset, caretLine.TotalLength, DispatcherPriority.Render);
    }

    /// <summary>
    /// 求 caret 行上所有「整段显灵 range」的并集起止绝对偏移（闭区间）。任一显灵 range 必含
    /// caret offset，故必挂到 caret 行（反例 caret==End 落在下一行首时不启动淡入），从 caret
    /// 行即可穷尽受淡入影响的跨行端。
    /// </summary>
    private bool TryGetRevealedRangeExtent(
        MarkdownCaretReveal reveal,
        out int minStart,
        out int maxEnd)
    {
        minStart = int.MaxValue;
        maxEnd = int.MinValue;
        if (!reveal.Active ||
            reveal.CaretLineZeroBased < 0 ||
            !_semanticDocument.TryGetCurrent(out var snapshot))
        {
            return false;
        }

        foreach (var span in snapshot.SpansForLine(reveal.CaretLineZeroBased))
        {
            // 非 range 型（标题/引用/列表等）只在 caret 行单格显灵，绝不累计，防止把
            // 单格 marker 误扩成整段区间。
            if (span.Length <= 0 ||
                !MarkdownSemanticReveal.IsRangeKind(span.Kind) ||
                !MarkdownSemanticReveal.RevealRange(reveal, span.Start, span.End))
            {
                continue;
            }

            minStart = Math.Min(minStart, span.Start);
            maxEnd = Math.Max(maxEnd, span.End);
        }

        foreach (var link in snapshot.LinksForLine(reveal.CaretLineZeroBased))
        {
            if (link.HasVisibleSyntax &&
                MarkdownSemanticReveal.RevealRange(reveal, link.Start, link.End))
            {
                minStart = Math.Min(minStart, link.Start);
                maxEnd = Math.Max(maxEnd, link.End);
            }
        }

        return maxEnd >= minStart;
    }

    private bool TryGetCaretLineZero(out int lineZero)
    {
        lineZero = -1;
        var document = _editor.Document;
        if (document == null || document.TextLength <= 0)
        {
            return false;
        }

        var offset = Math.Clamp(_editor.TextArea.Caret.Offset, 0, document.TextLength);
        lineZero = document.GetLineByOffset(offset).LineNumber - 1;
        return true;
    }

    private bool HasRevealOnCaretLine(int lineZero)
    {
        if (!_semanticDocument.TryGetCurrent(out var snapshot))
        {
            return false;
        }

        var document = _editor.Document!;
        var line = document.GetLineByNumber(lineZero + 1);
        return MarkdownSemanticReveal.HasRevealOnLine(
            snapshot,
            document.GetText(line),
            line.Offset,
            lineZero,
            _caretReveal);
    }
}
