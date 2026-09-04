using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private SyntaxCollapseElementGenerator? _collapseGenerator;
    private IReadOnlyList<MarkdownCollapseRun> _collapseRuns = Array.Empty<MarkdownCollapseRun>();
    private bool _collapseDirty = true;
    private bool _collapseWasFull;
    private bool _collapseWasPreview;
    private MarkdownCaretReveal _collapseCaret = MarkdownCaretReveal.None;

    /// <summary>当前应塌缩的源码区间（Full 档；随光标/预览/结构变化按需重算）。</summary>
    internal IReadOnlyList<MarkdownCollapseRun> CollapseRuns
    {
        get
        {
            var stillValid = !_collapseDirty &&
                _collapseWasFull == IsFullMode &&
                _collapseWasPreview == _editor.IsPreviewMode &&
                _collapseCaret == CaretReveal;
            if (stillValid)
            {
                return _collapseRuns;
            }

            _collapseDirty = false;
            _collapseWasFull = IsFullMode;
            _collapseWasPreview = _editor.IsPreviewMode;
            _collapseCaret = CaretReveal;
            _collapseRuns = IsFullMode
                ? MarkdownSemanticCollapseLayout.ComputeCollapsedRuns(
                    CurrentSnapshot(),
                    _editor.Text ?? string.Empty,
                    CaretReveal)
                : Array.Empty<MarkdownCollapseRun>();
            return _collapseRuns;
        }
    }

    private void AttachCollapseGenerator()
    {
        _collapseGenerator = new SyntaxCollapseElementGenerator(this);
        _editor.TextArea.TextView.ElementGenerators.Add(_collapseGenerator);
    }

    private void DetachCollapseGenerator()
    {
        if (_collapseGenerator != null)
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_collapseGenerator);
            _collapseGenerator = null;
        }
    }

    private sealed class SyntaxCollapseElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownSemanticPresentation _owner;

        public SyntaxCollapseElementGenerator(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.IsFullMode)
            {
                return -1;
            }

            var runs = _owner.CollapseRuns;
            foreach (var run in runs)
            {
                if (run.End > run.Start && run.Start >= startOffset)
                {
                    return run.Start;
                }
            }

            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            foreach (var run in _owner.CollapseRuns)
            {
                if (run.Start == offset)
                {
                    return new CollapsedSyntaxElement(run.Length);
                }
            }

            return null!;
        }
    }

    /// <summary>单视觉列 + U+200B（~0 宽），消耗 N 个文档字符；该列↔偏移映射一律取“内容侧”。</summary>
    private sealed class CollapsedSyntaxElement : VisualLineElement
    {
        private static readonly char[] ZeroWidth = { (char)0x200B };

        public CollapsedSyntaxElement(int documentLength)
            : base(1, documentLength)
        {
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            return new TextCharacters(ZeroWidth, 0, 1, TextRunProperties);
        }

        public override int GetVisualColumn(int relativeTextOffset)
        {
            // 区间内任意文档偏移一律映射到本列（编辑不关心控制符内部）。
            return VisualColumn;
        }

        public override int GetRelativeOffset(int visualColumn)
        {
            // 本列 → 区间末尾（内容侧），让光标/点击落在隐藏标记之后的内容上。
            return RelativeTextOffset + DocumentLength;
        }

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            if (mode != CaretPositioningMode.Normal)
            {
                return -1;
            }

            if (direction == LogicalDirection.Forward)
            {
                if (visualColumn < VisualColumn)
                {
                    return VisualColumn;
                }
            }
            else
            {
                if (visualColumn > VisualColumn)
                {
                    return VisualColumn;
                }
            }
            return -1;
        }
    }
}
