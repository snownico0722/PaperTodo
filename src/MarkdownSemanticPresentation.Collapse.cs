using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PaperTodo;

internal sealed partial class MarkdownSemanticPresentation
{
    private SyntaxCollapseElementGenerator? _collapseGenerator;

    /// <summary>增量折叠表：静态候选随 snapshot 重建，光标只做局部摘除/插入。</summary>
    private MarkdownCollapseTable? _collapseTable;

    /// <summary>
    /// 当前应塌缩的源码区间（Full 档）。由增量表按需维护：文本/模式变化时整表重建，光标移动时
    /// 只翻转旧/新光标行的显灵位。返回的是与「当前显灵值」一致的表内 Runs。
    /// </summary>
    internal IReadOnlyList<MarkdownCollapseRun> CollapseRuns
    {
        get
        {
            if (!IsFullMode)
            {
                return Array.Empty<MarkdownCollapseRun>();
            }

            AlignCollapseTableToReveal(scheduleRedraw: false);
            return _collapseTable?.Runs ?? Array.Empty<MarkdownCollapseRun>();
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

    /// <summary>首次进入 Full / snapshot 变化后重建一次增量表（O(n)）。</summary>
    private MarkdownCollapseTable EnsureCollapseTable()
    {
        if (_collapseTable == null)
        {
            _collapseTable = MarkdownCollapseTable.Build(
                CurrentSnapshot(),
                _editor.Text ?? string.Empty,
                CaretReveal);
        }

        return _collapseTable;
    }

    /// <summary>
    /// 把增量表对齐到当前显灵值（CaretReveal：预览= None、手势冻结=快照、其余=实际光标）。
    /// 仅当显灵驱动的视觉真的变化时才可选地重排受影响行；跨行范围保留全量兜底。
    /// </summary>
    private void AlignCollapseTableToReveal(bool scheduleRedraw)
    {
        if (!IsFullMode)
        {
            _collapseTable = null;
            return;
        }

        var table = EnsureCollapseTable();
        var target = CaretReveal;
        if (table.Caret == target)
        {
            return;
        }

        var previous = table.Caret;
        var change = table.SyncTo(target);
        if (scheduleRedraw && change.VisualChanged)
        {
            ScheduleRevealRedraw(previous, target);
        }
    }

    private void ScheduleRevealRedraw(MarkdownCaretReveal previous, MarkdownCaretReveal target)
    {
        if (!TryGetLocalRevealLine(previous, out var oldLine) ||
            !TryGetLocalRevealLine(target, out var newLine))
        {
            ScheduleRedraw();
            return;
        }

        if (oldLine != null)
        {
            ScheduleLocalRedraw(oldLine.Offset, oldLine.TotalLength);
        }

        if (newLine != null &&
            (oldLine == null || newLine.Offset != oldLine.Offset))
        {
            ScheduleLocalRedraw(newLine.Offset, newLine.TotalLength);
        }
    }

    /// <summary>单行显灵可局部刷新；任一活动范围越过该行时交给全量兜底。</summary>
    private bool TryGetLocalRevealLine(MarkdownCaretReveal reveal, out DocumentLine? line)
    {
        line = null;
        if (!reveal.Active)
        {
            return true;
        }

        var document = _editor.Document;
        if (document == null)
        {
            return false;
        }

        line = document.GetLineByOffset(Math.Clamp(reveal.CaretOffset, 0, document.TextLength));
        return !TryGetRevealedRangeExtent(reveal, out var start, out var end) ||
            (start >= line.Offset && end <= line.EndOffset);
    }

    /// <summary>在有序 Runs 上二分首个 run.Start &gt;= value 的下标。</summary>
    private static int LowerBoundStart(IReadOnlyList<MarkdownCollapseRun> runs, int value)
    {
        var low = 0;
        var high = runs.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (runs[middle].Start < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private sealed class SyntaxCollapseElementGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownSemanticPresentation _owner;
        private int _quotePrefixOffset = -1;
        private int _quoteLevels;

        public SyntaxCollapseElementGenerator(MarkdownSemanticPresentation owner)
        {
            _owner = owner;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _quotePrefixOffset = -1;
            _quoteLevels = 0;
            if (!_owner.IsFullMode ||
                !_owner.TryCurrentSnapshot(out var snapshot))
            {
                return;
            }

            var line = context.VisualLine.FirstDocumentLine;
            var text = context.Document.GetText(line);
            var container = MarkdownContainerPrefix.Parse(
                text,
                snapshot,
                line.Offset,
                line.EndOffset);

            // 真实列表/引用/任务前缀统一由 MarkerSlotElementGenerator 持有；这里仅保留没有
            // 实际 `>` 源码字符的惰性引用续行占位，以及一般 Markdown 控制符的塌缩。
            if (container.MissingQuoteLevels > 0)
            {
                _quotePrefixOffset = line.Offset + container.ContentStart;
                _quoteLevels = container.MissingQuoteLevels;
            }
        }

        public override void FinishGeneration()
        {
            _quotePrefixOffset = -1;
            _quoteLevels = 0;
            base.FinishGeneration();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.IsFullMode)
            {
                return -1;
            }

            var interested = int.MaxValue;
            if (_quotePrefixOffset >= startOffset)
            {
                interested = Math.Min(interested, _quotePrefixOffset);
            }

            var runs = _owner.CollapseRuns;
            var index = LowerBoundStart(runs, startOffset);
            if (index < runs.Count && runs[index].End > runs[index].Start)
            {
                interested = Math.Min(interested, runs[index].Start);
            }

            return interested == int.MaxValue ? -1 : interested;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var hasQuotePrefix =
                offset == _quotePrefixOffset &&
                _quoteLevels > 0;
            var runs = _owner.CollapseRuns;
            var index = LowerBoundStart(runs, offset);
            if (index < runs.Count && runs[index].Start == offset)
            {
                var run = runs[index];
                return hasQuotePrefix
                    ? new QuoteIndentElement(
                        _owner,
                        _quoteLevels,
                        run.Length,
                        run.IsClosingEdge)
                    : new CollapsedSyntaxElement(
                        run.Length,
                        run.IsClosingEdge);
            }

            return hasQuotePrefix
                ? new QuoteIndentElement(
                    _owner,
                    _quoteLevels,
                    documentLength: 0,
                    isClosingEdge: false)
                : null!;
        }
    }

    /// <summary>
    /// 为缺少显式 `>` 的惰性引用续行保留与 marker 等宽的透明前缀。DocumentLength=0 时只参与
    /// TextView 排版；若同一偏移恰有待塌缩控制符，则同一元素同时消费该源码区间，避免两个
    /// ElementGenerator 在同一 offset 竞争。任何情况下都不修改 TextDocument 或 undo。
    /// </summary>
    private sealed class QuoteIndentElement : VisualLineElement
    {
        private readonly MarkdownSemanticPresentation _owner;
        private readonly bool _isClosingEdge;

        public QuoteIndentElement(
            MarkdownSemanticPresentation owner,
            int levels,
            int documentLength,
            bool isClosingEdge)
            : base(visualLength: 1, documentLength: documentLength)
        {
            _owner = owner;
            Levels = levels;
            _isClosingEdge = isClosingEdge;
        }

        public int Levels { get; }
        public double UnitWidth { get; private set; }

        public override TextRun CreateTextRun(
            int startVisualColumn,
            ITextRunConstructionContext context)
        {
            // A lazy continuation reserves the same fixed quote cell + native space as a real
            // prefix. Ignore styling from a collapsed opener consumed at the same source offset.
            var global = context.GlobalTextRunProperties;
            TextRunProperties.SetTypeface(global.Typeface);
            TextRunProperties.SetFontRenderingEmSize(global.FontRenderingEmSize);
            TextRunProperties.SetFontHintingEmSize(global.FontHintingEmSize);
            TextRunProperties.SetCultureInfo(global.CultureInfo);
            TextRunProperties.SetBaselineAlignment(global.BaselineAlignment);
            TextRunProperties.SetTypographyProperties(global.TypographyProperties);
            TextRunProperties.SetNumberSubstitution(global.NumberSubstitution);
            TextRunProperties.SetForegroundBrush(Brushes.Transparent);
            UnitWidth = _owner.GetQuoteUnitWidth(context.TextView, global);
            var metrics = new FormattedText(
                " ",
                global.CultureInfo ?? UiLanguages.EffectiveUiCulture,
                FlowDirection.LeftToRight,
                global.Typeface,
                global.FontRenderingEmSize,
                Brushes.Transparent,
                null,
                AppTypography.TextFormattingMode,
                VisualTreeHelper.GetDpi(context.TextView).PixelsPerDip);
            return new FixedMarkerCellRun(TextRunProperties, UnitWidth * Levels, metrics);
        }

        public override int GetVisualColumn(int relativeTextOffset)
        {
            // A combined element consumes the collapsed opener in source coordinates but draws the
            // presentation-only quote prefix in visual coordinates. Map the source boundary after
            // that opener to the right edge of the prefix; offsets inside the hidden opener remain
            // at its left edge, matching CollapsedSyntaxElement semantics.
            return DocumentLength == 0 ||
                   relativeTextOffset >= RelativeTextOffset + DocumentLength
                ? VisualColumn + VisualLength
                : VisualColumn;
        }

        public override int GetRelativeOffset(int visualColumn)
        {
            if (DocumentLength == 0)
            {
                return RelativeTextOffset;
            }

            return _isClosingEdge
                ? RelativeTextOffset
                : RelativeTextOffset + DocumentLength;
        }

        public override int GetNextCaretPosition(
            int visualColumn,
            LogicalDirection direction,
            CaretPositioningMode mode)
        {
            if (mode != CaretPositioningMode.Normal)
            {
                return -1;
            }

            if (DocumentLength == 0 || !_isClosingEdge)
            {
                var stop = VisualColumn + VisualLength;
                if (direction == LogicalDirection.Forward)
                {
                    return visualColumn < stop ? stop : -1;
                }

                return visualColumn > stop ? stop : -1;
            }

            // Preserve the original closing-cell boundary: backward selection stops before the
            // hidden closing marker instead of silently including it.
            if (direction == LogicalDirection.Forward)
            {
                return visualColumn < VisualColumn ? VisualColumn : -1;
            }

            return visualColumn > VisualColumn ? VisualColumn : -1;
        }

        public override bool IsWhitespace(int visualColumn) => true;

        public override bool HandlesLineBorders => DocumentLength == 0;
    }

    /// <summary>
    /// 单视觉列 + WPF TextHidden（零宽、无字形、不引入断行点），消耗 N 个文档字符。闭 cell 的
    /// GetRelativeOffset 返回 cell 起点，阻止拖选越过右侧的 ** / ] / </tag> 等闭标记。
    /// </summary>
    private sealed class CollapsedSyntaxElement : VisualLineElement
    {
        private readonly bool _isClosingEdge;

        public CollapsedSyntaxElement(int documentLength, bool isClosingEdge)
            : base(1, documentLength)
        {
            _isClosingEdge = isClosingEdge;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            // 用 WPF TextHidden 而非 U+200B 之类的零宽假字符：零 advance、不绘制，且不带「允许
            // 在此断行」语义，不会在隐藏标记两端引入幻影断行点。长度取 VisualLength（恒为 1）而非
            // 源码字符数：AvalonEdit 强制 run.Length ≤ element.VisualLength，否则抛异常。
            return new TextHidden(VisualLength);
        }

        public override int GetVisualColumn(int relativeTextOffset)
        {
            // 区间内任意文档偏移一律映射到本列（编辑不关心控制符内部）。
            return VisualColumn;
        }

        public override int GetRelativeOffset(int visualColumn)
        {
            // 开 cell → 区间末尾（光标/选区落在内容起点）；闭 cell → 区间起点（光标/选区停在内容终点，
            // 不越过闭标记），从而拖选 abc 不会把右侧 ** / ] / </tag> 带进选区。
            return _isClosingEdge
                ? RelativeTextOffset
                : RelativeTextOffset + DocumentLength;
        }

        public override int GetNextCaretPosition(
            int visualColumn,
            LogicalDirection direction,
            CaretPositioningMode mode)
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
