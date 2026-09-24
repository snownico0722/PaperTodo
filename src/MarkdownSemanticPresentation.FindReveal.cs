namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    internal void SetTransientFindReveal(int? absoluteOffset, int length)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _transientFindReveal;
        var next = MarkdownCaretReveal.None;
        var document = _editor.Document;

        if (absoluteOffset is int requested &&
            length > 0 &&
            IsFullMode &&
            document != null)
        {
            var start = Math.Clamp(requested, 0, document.TextLength);
            var end = Math.Clamp(start + length, start, document.TextLength);
            if (end > start)
            {
                // Ask the existing collapse table what normal Full preview actually hides. We
                // temporarily sync the same table to None for the probe, then always restore it to
                // the effective reveal below. This stays O(log n + nearby runs) and does not build a
                // second parser, cache or element generator.
                var table = EnsureCollapseTable();
                table.SyncTo(MarkdownCaretReveal.None);
                var runs = table.Runs;
                var index = LowerBoundStart(runs, start);
                if (index > 0 && runs[index - 1].End > start)
                {
                    index--;
                }

                for (; index < runs.Count; index++)
                {
                    var run = runs[index];
                    if (run.Start >= end)
                    {
                        break;
                    }
                    if (run.End <= start)
                    {
                        continue;
                    }

                    // A synthetic caret anywhere inside the hidden syntax unit makes the existing
                    // reveal rules expose the complete semantic unit (for example an inline link).
                    var revealOffset = Math.Max(start, run.Start);
                    var line = document.GetLineByOffset(revealOffset);
                    next = new MarkdownCaretReveal(
                        revealOffset,
                        line.LineNumber - 1);
                    break;
                }

                _transientFindReveal = next;
                table.SyncTo(CaretReveal);
            }
        }

        if (_transientFindReveal != next)
        {
            _transientFindReveal = next;
            if (IsFullMode)
            {
                AlignCollapseTableToReveal(scheduleRedraw: false);
            }
        }

        if (previous != _transientFindReveal)
        {
            ScheduleRedraw();
        }
    }
}
