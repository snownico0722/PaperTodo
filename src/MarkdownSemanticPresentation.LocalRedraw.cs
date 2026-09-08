using System.Windows.Threading;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private const int MaxQueuedLocalRedrawRanges = 8;

    private readonly List<(int Start, int End)> _queuedLocalRedrawRanges = new();
    private bool _localRedrawQueued;

    /// <summary>
    /// Coalesces only overlapping or adjacent line ranges. Distant caret jumps remain two redraws
    /// instead of invalidating every visual line between the old and new caret positions.
    /// </summary>
    private void ScheduleLocalRedraw(int start, int length)
    {
        if (_disposed || length <= 0)
        {
            return;
        }

        var end = start + length;
        var index = 0;
        while (index < _queuedLocalRedrawRanges.Count &&
               _queuedLocalRedrawRanges[index].End < start)
        {
            index++;
        }

        while (index < _queuedLocalRedrawRanges.Count &&
               _queuedLocalRedrawRanges[index].Start <= end)
        {
            start = Math.Min(start, _queuedLocalRedrawRanges[index].Start);
            end = Math.Max(end, _queuedLocalRedrawRanges[index].End);
            _queuedLocalRedrawRanges.RemoveAt(index);
        }

        _queuedLocalRedrawRanges.Insert(index, (start, end));
        if (_queuedLocalRedrawRanges.Count > MaxQueuedLocalRedrawRanges)
        {
            _queuedLocalRedrawRanges.Clear();
            ScheduleRedraw();
            return;
        }

        if (_localRedrawQueued)
        {
            return;
        }

        _localRedrawQueued = true;
        _editor.Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _localRedrawQueued = false;
                var ranges = _queuedLocalRedrawRanges.ToArray();
                _queuedLocalRedrawRanges.Clear();
                if (_disposed || ranges.Length == 0)
                {
                    return;
                }

                var document = _editor.Document;
                if (document == null)
                {
                    return;
                }

                var textLength = document.TextLength;
                var textView = _editor.TextArea.TextView;
                foreach (var range in ranges)
                {
                    // A synchronous text edit may supersede a queued caret redraw. The snapshot
                    // path also queues a full redraw; clamping here merely keeps the stale callback safe.
                    var redrawStart = Math.Clamp(range.Start, 0, textLength);
                    var redrawEnd = Math.Clamp(range.End, redrawStart, textLength);
                    if (redrawEnd > redrawStart)
                    {
                        textView.Redraw(
                            redrawStart,
                            redrawEnd - redrawStart,
                            DispatcherPriority.Render);
                    }
                }
            }),
            DispatcherPriority.Render);
    }
}
