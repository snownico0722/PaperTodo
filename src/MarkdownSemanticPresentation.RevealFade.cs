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
        if (!RevealFadeEnabled)
        {
            AbortRevealFade();
            _lastLineReveal = false;
            _lastRevealCaretLine = -1;
            return;
        }

        if (!TryGetCaretLineZero(out var lineZero))
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
        RedrawCaretLine();

        if (t >= 1)
        {
            _fadeTimer?.Stop();
        }
    }

    private void RedrawCaretLine()
    {
        var document = _editor.Document;
        var textView = _editor.TextArea.TextView;
        if (document == null || document.TextLength <= 0)
        {
            return;
        }

        var offset = Math.Clamp(_editor.TextArea.Caret.Offset, 0, document.TextLength);
        var line = document.GetLineByOffset(offset);
        textView.Redraw(line.Offset, line.Length + 1, DispatcherPriority.Render);
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
