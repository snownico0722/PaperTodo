namespace PaperTodo;

/// <summary>一段需要在 Full 档从布局中“塌缩”的源码区间（控制符不参与排版、其余内容重排）。</summary>
internal readonly record struct MarkdownCollapseRun(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// 一个可塌缩语法单元（由一个 MarkdownSemanticSpan 或 MarkdownSemanticLink 派生）的静态快照。
/// 是否真正塌缩只取决于光标是否使其“显灵”（活动块不塌缩）；cell 区间与显灵判定入参都只依赖
/// 源码，随文本语义版本（snapshot）重建一次，与光标移动无关。
/// </summary>
internal readonly record struct MarkdownCollapseCandidate(
    bool IsRange,          // true=成对闭区间型（强调/加粗/删除线/行内代码/链接）：显灵=光标落于 [Start,End]
    int LineZero,          // 行边界型所在行（IsRange 时忽略）
    int Start,             // 区间型起点 / 行边界型 marker 起点
    int End,               // 区间型终点
    int Cell1Start,
    int Cell1End,          // 第一个塌缩格；== Cell1Start 表示无此格
    int Cell2Start,
    int Cell2End)          // 第二个塌缩格（标题闭合 #、成对后段）；== Cell2Start 表示无此格
{
    public bool HasCell1 => Cell1End > Cell1Start;
    public bool HasCell2 => Cell2End > Cell2Start;
}

/// <summary>一次光标显灵同步后，折叠区间表 / 显灵取色是否需要变化的报告。</summary>
internal readonly record struct MarkdownCollapseChange(
    bool CollapseChanged,  // 折叠区间表发生变化（控制符折叠↔显灵翻转）
    bool VisualChanged);   // 任一显灵驱动的视觉变化（含折叠 + 非塌缩取色标记），决定是否触发重排

/// <summary>
/// 计算 Full（WYSIWYG 块级编辑态）下应当真塌缩的源码区间——纯逻辑、无 WPF 依赖，可被
/// MarkdownSemanticChecks 直接链接测试。
///
/// 仅收窄覆盖“隐藏后不需要留白/缩进”的控制符：ATX 标题标记（开/闭 #）、行内成对分隔符
/// （** / * / ~~ / 反引号）、行内链接语法（[label](url) 的非 label 部分）、HTML 标签、转义
/// 反斜杠。列表/任务标记、引用 &gt;、围栏行、setext、分隔线等需要保留视觉格子/行高的不在此列。
///
/// 折叠区间按两阶段得到：
/// - 静态候选（BuildCandidates）：每个可塌缩单元随 snapshot 重建一次，含其塌缩 cell 区间。
/// - Resolve：按当前光标过滤“显灵单元”后把 cell 排序、相邻合并，即最终折叠区间表。
///
/// MarkdownCollapseTable 复用同一份候选做增量维护：光标变化只翻转旧/新光标行上登记单元的显灵
/// 位，对 cell 做局部摘除/插入，避免每次光标移动都全篇重算。
/// </summary>
internal static class MarkdownSemanticCollapseLayout
{
    /// <summary>整篇重算折叠区间（参考实现 / 首次 Build 用）。语义与历史版本完全一致。</summary>
    public static IReadOnlyList<MarkdownCollapseRun> ComputeCollapsedRuns(
        MarkdownSemanticSnapshot snapshot,
        string? source,
        MarkdownCaretReveal caret)
    {
        var candidates = BuildCandidates(snapshot, source);
        return Resolve(candidates, caret);
    }

    /// <summary>重建静态候选集（仅随 snapshot/source 变化调用）。</summary>
    internal static MarkdownCollapseCandidate[] BuildCandidates(
        MarkdownSemanticSnapshot snapshot,
        string? source)
    {
        var text = source ?? string.Empty;
        if (snapshot.Spans.Count == 0 && snapshot.Links.Count == 0)
        {
            return Array.Empty<MarkdownCollapseCandidate>();
        }

        var lineStarts = BuildLineStarts(text);
        if (lineStarts.Length == 0)
        {
            return Array.Empty<MarkdownCollapseCandidate>();
        }

        var candidates = new List<MarkdownCollapseCandidate>();
        var unusedSpans = new List<MarkdownSemanticSpan>();
        var unusedLinks = new List<MarkdownSemanticLink>();
        CollectCandidates(snapshot, text, lineStarts, candidates, unusedSpans, unusedLinks);
        return candidates.Count == 0
            ? Array.Empty<MarkdownCollapseCandidate>()
            : candidates.ToArray();
    }

    /// <summary>
    /// 由静态候选 + 光标求得最终折叠区间：收集“未显灵单元”的 cell，排序后相邻合并。
    /// 合并规则与历史实现一致（touch 或重叠即并成一条）。
    /// </summary>
    internal static IReadOnlyList<MarkdownCollapseRun> Resolve(
        MarkdownCollapseCandidate[] candidates,
        MarkdownCaretReveal caret)
    {
        if (candidates.Length == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        var runs = new List<MarkdownCollapseRun>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (RevealedAt(candidate, caret))
            {
                continue;
            }

            if (candidate.HasCell1)
            {
                runs.Add(new MarkdownCollapseRun(candidate.Cell1Start, candidate.Cell1End));
            }

            if (candidate.HasCell2)
            {
                runs.Add(new MarkdownCollapseRun(candidate.Cell2Start, candidate.Cell2End));
            }
        }

        if (runs.Count == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        runs.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<MarkdownCollapseRun>(runs.Count);
        foreach (var run in runs)
        {
            if (merged.Count == 0)
            {
                merged.Add(run);
                continue;
            }

            var previous = merged[^1];
            if (run.Start <= previous.End)
            {
                merged[^1] = new MarkdownCollapseRun(previous.Start, Math.Max(previous.End, run.End));
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }

    /// <summary>单元是否因当前光标而“显灵”（显灵则不塌缩）。判定与历史 Collect* 逐点一致。</summary>
    internal static bool RevealedAt(MarkdownCollapseCandidate candidate, MarkdownCaretReveal caret)
    {
        if (!caret.Active)
        {
            return false;
        }

        return candidate.IsRange
            ? MarkdownSemanticReveal.RevealRange(caret, candidate.Start, candidate.End)
            : caret.CaretLineZeroBased == candidate.LineZero &&
                caret.CaretOffset >= candidate.Start;
    }

    /// <summary>
    /// 扫描 snapshot 的可塌缩单元并把静态 cell 填入候选列表。cell 的计算逻辑与原 Collect* 一致，
    /// 唯一差别是不再按光标“显灵”跳过——显灵过滤后移到 Resolve/增量表，使候选只随文本版本重建。
    /// </summary>
    internal static void CollectCandidates(
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts,
        List<MarkdownCollapseCandidate> candidates,
        List<MarkdownSemanticSpan> spanOrigins,
        List<MarkdownSemanticLink> linkOrigins)
    {
        foreach (var span in snapshot.Spans)
        {
            MarkdownCollapseCandidate? built = span.Kind switch
            {
                MarkdownSemanticSpanKind.Heading => BuildAtxHeading(span, source, lineStarts),
                MarkdownSemanticSpanKind.Emphasis or
                MarkdownSemanticSpanKind.Strong or
                MarkdownSemanticSpanKind.Strikethrough or
                MarkdownSemanticSpanKind.InlineCode => BuildInlineDelimiters(span, source, lineStarts),
                MarkdownSemanticSpanKind.HtmlMarker or
                MarkdownSemanticSpanKind.EscapeMarker => BuildCell(span, source, lineStarts),
                _ => null
            };
            if (built is { } candidate)
            {
                candidates.Add(candidate);
                spanOrigins.Add(span);
                linkOrigins.Add(default);
            }
        }

        foreach (var link in snapshot.Links)
        {
            if (link.IsAuto)
            {
                continue;
            }

            if (BuildLink(link, source, lineStarts) is { } candidate)
            {
                candidates.Add(candidate);
                spanOrigins.Add(default);
                linkOrigins.Add(link);
            }
        }
    }

    /// <summary>ATX 标题：开标记 `#…` + 后随空格、以及行尾闭合 `#…`（若有）。</summary>
    private static MarkdownCollapseCandidate? BuildAtxHeading(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts)
    {
        if (span.Length <= 0)
        {
            return null;
        }

        var line = FindLine(lineStarts, span.Start);
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : source.Length;

        // 开标记：从 '#' 起到其后空白结束，作为内容起点。
        var contentStart = span.Start;
        while (contentStart < source.Length && source[contentStart] == '#')
        {
            contentStart++;
        }

        while (contentStart < source.Length && (source[contentStart] == ' ' || source[contentStart] == '\t'))
        {
            contentStart++;
        }

        if (contentStart <= span.Start)
        {
            return null;
        }

        // 闭合标记：标题内容之后的尾部 `#…`（被前导空白分隔，且位于行尾）。
        // ATX 标题是单行叶块，按整行扫描（Markdig 的 span.End 可能不含闭合标记）。
        var contentEnd = lineEnd;
        while (contentEnd > contentStart && char.IsWhiteSpace(source[contentEnd - 1]))
        {
            contentEnd--;
        }

        var closingStart = contentEnd;
        while (closingStart > contentStart && source[closingStart - 1] == '#')
        {
            closingStart--;
        }

        var hasClosing = closingStart < contentEnd &&
            closingStart > contentStart &&
            char.IsWhiteSpace(source[closingStart - 1]);

        return new MarkdownCollapseCandidate(
            IsRange: false,
            LineZero: line,
            Start: span.Start,
            End: contentStart,
            Cell1Start: span.Start,
            Cell1End: contentStart,
            Cell2Start: hasClosing ? closingStart : 0,
            Cell2End: hasClosing ? contentEnd : 0);
    }

    /// <summary>行内成对分隔符：前/后两段（**、*、~~、反引号）。</summary>
    private static MarkdownCollapseCandidate? BuildInlineDelimiters(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts)
    {
        var markerLength = Math.Clamp(span.MarkerLength, 1, Math.Max(1, span.Length / 2));
        var openerStart = span.Start;
        var openerEnd = span.Start + markerLength;
        var closerStart = span.End - markerLength;
        var closerEnd = span.End;

        // 历史 AddIfSingleLine：仅保留完全落在一行内的格。
        if (!SingleLine(lineStarts, source.Length, openerStart, openerEnd))
        {
            openerStart = openerEnd = 0;
        }

        if (!SingleLine(lineStarts, source.Length, closerStart, closerEnd))
        {
            closerStart = closerEnd = 0;
        }

        if (openerEnd <= openerStart && closerEnd <= closerStart)
        {
            return null;
        }

        return new MarkdownCollapseCandidate(
            IsRange: true,
            LineZero: FindLine(lineStarts, span.Start),
            Start: span.Start,
            End: span.End,
            Cell1Start: openerStart,
            Cell1End: openerEnd,
            Cell2Start: closerStart,
            Cell2End: closerEnd);
    }

    /// <summary>整段塌缩的单格标记（HTML 标签、转义反斜杠）。</summary>
    private static MarkdownCollapseCandidate? BuildCell(
        MarkdownSemanticSpan span,
        string source,
        int[] lineStarts)
    {
        if (span.Length <= 0)
        {
            return null;
        }

        return new MarkdownCollapseCandidate(
            IsRange: false,
            LineZero: FindLine(lineStarts, span.Start),
            Start: span.Start,
            End: span.End,
            Cell1Start: span.Start,
            Cell1End: span.End,
            Cell2Start: 0,
            Cell2End: 0);
    }

    /// <summary>显式链接语法：`[label](url)` 中塌缩开 `[`、`](`、url、闭 `)` 的非 label 部分。</summary>
    private static MarkdownCollapseCandidate? BuildLink(
        MarkdownSemanticLink link,
        string source,
        int[] lineStarts)
    {
        var openingStart = link.Start;
        var openingEnd = link.LabelStart;
        var closingStart = link.LabelEnd;
        var closingEnd = link.End;

        if (!SingleLine(lineStarts, source.Length, openingStart, openingEnd))
        {
            openingStart = openingEnd = 0;
        }

        if (!SingleLine(lineStarts, source.Length, closingStart, closingEnd))
        {
            closingStart = closingEnd = 0;
        }

        if (openingEnd <= openingStart && closingEnd <= closingStart)
        {
            return null;
        }

        return new MarkdownCollapseCandidate(
            IsRange: true,
            LineZero: FindLine(lineStarts, link.Start),
            Start: link.Start,
            End: link.End,
            Cell1Start: openingStart,
            Cell1End: openingEnd,
            Cell2Start: closingStart,
            Cell2End: closingEnd);
    }

    private static bool SingleLine(int[] lineStarts, int sourceLength, int start, int end)
    {
        if (end <= start)
        {
            return true;
        }

        return FindLine(lineStarts, start) == FindLine(lineStarts, Math.Min(end - 1, sourceLength - 1));
    }

    internal static int FindLine(int[] lineStarts, int offset)
    {
        var normalized = Math.Clamp(offset, 0, lineStarts.Length == 0 ? 0 : lineStarts[^1]);
        var index = Array.BinarySearch(lineStarts, normalized);
        if (index >= 0)
        {
            return index;
        }

        return Math.Max(0, ~index - 1);
    }

    internal static int[] BuildLineStarts(string source)
    {
        var starts = new List<int>(Math.Max(1, source.Length / 32)) { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\r')
            {
                if (index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (source[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts.ToArray();
    }
}

/// <summary>
/// 增量折叠区间表：静态候选（随文本版本重建一次）+ 一份始终有序、已合并的折叠区间 Runs。
/// 光标变化时由 SyncTo 只对「旧/新光标行上显灵状态翻转」的候选做局部摘除/插入，保持
/// Runs 等于 Resolve(候选, 当前光标) 的不变式；同区间移动不触发任何改动。
/// </summary>
internal sealed class MarkdownCollapseTable
{
    private readonly MarkdownSemanticSnapshot _snapshot;
    private readonly string _source;
    private readonly int[] _lineStarts;
    private readonly MarkdownCollapseCandidate[] _candidates;
    private readonly Dictionary<MarkdownSemanticSpan, int> _spanIndex;
    private readonly Dictionary<MarkdownSemanticLink, int> _linkIndex;
    private readonly List<MarkdownCollapseRun> _runs;

    private MarkdownCollapseTable(
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts,
        MarkdownCollapseCandidate[] candidates,
        Dictionary<MarkdownSemanticSpan, int> spanIndex,
        Dictionary<MarkdownSemanticLink, int> linkIndex,
        IReadOnlyList<MarkdownCollapseRun> initialRuns,
        MarkdownCaretReveal caret)
    {
        _snapshot = snapshot;
        _source = source;
        _lineStarts = lineStarts;
        _candidates = candidates;
        _spanIndex = spanIndex;
        _linkIndex = linkIndex;
        _runs = new List<MarkdownCollapseRun>(initialRuns);
        Caret = caret;
    }

    /// <summary>当前 Runs 所对应的显灵值（预览= None；手势冻结=按下瞬间快照）。</summary>
    public MarkdownCaretReveal Caret { get; private set; }

    /// <summary>有序、已合并、互不重叠的当前折叠区间。</summary>
    public IReadOnlyList<MarkdownCollapseRun> Runs => _runs;

    /// <summary>整篇重建一张表（首次进入 Full / snapshot 变化后调用，O(n) 一次）。</summary>
    public static MarkdownCollapseTable Build(
        MarkdownSemanticSnapshot snapshot,
        string? source,
        MarkdownCaretReveal caret)
    {
        var text = source ?? string.Empty;
        var lineStarts = MarkdownSemanticCollapseLayout.BuildLineStarts(text);
        var candidates = new List<MarkdownCollapseCandidate>();
        var spanOrigins = new List<MarkdownSemanticSpan>();
        var linkOrigins = new List<MarkdownSemanticLink>();
        MarkdownSemanticCollapseLayout.CollectCandidates(
            snapshot, text, lineStarts, candidates, spanOrigins, linkOrigins);

        var spanIndex = new Dictionary<MarkdownSemanticSpan, int>(candidates.Count);
        var linkIndex = new Dictionary<MarkdownSemanticLink, int>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            // 候选必有非默认 origin（默认 span/link 的 Length==0，不可能是任何候选）。
            if (spanOrigins[index].Length > 0)
            {
                spanIndex[spanOrigins[index]] = index;
            }
            else
            {
                linkIndex[linkOrigins[index]] = index;
            }
        }

        var array = candidates.Count == 0
            ? Array.Empty<MarkdownCollapseCandidate>()
            : candidates.ToArray();
        var initial = MarkdownSemanticCollapseLayout.Resolve(array, caret);
        return new MarkdownCollapseTable(
            snapshot, text, lineStarts, array, spanIndex, linkIndex, initial, caret);
    }

    /// <summary>
    /// 把显灵值增量地同步到 target（以当前 Caret 为基线）。翻转集 = 旧/新光标行上登记的全部
    /// span/link + 引用单元；同区间移动翻转集为空 → 零改动、VisualChanged=false。
    /// </summary>
    public MarkdownCollapseChange SyncTo(MarkdownCaretReveal target)
    {
        var previous = Caret;
        if (previous == target)
        {
            Caret = target;
            return default;
        }

        var collapseChanged = false;
        var visualChanged = false;
        var handled = new HashSet<int>();

        void CollectLine(int line)
        {
            if (line < 0)
            {
                return;
            }

            // 引用 `>` 单元不进 spans：按行首连续 `>` 前缀计数，翻越任一格视为视觉变化。
            if (QuoteCellCount(previous, line) != QuoteCellCount(target, line))
            {
                visualChanged = true;
            }

            foreach (var span in _snapshot.SpansForLine(line))
            {
                if (span.Length <= 0)
                {
                    continue;
                }

                var oldVisible = RevealSpan(previous, line, span);
                var newVisible = RevealSpan(target, line, span);
                if (oldVisible != newVisible)
                {
                    visualChanged = true;
                }

                if (_spanIndex.TryGetValue(span, out var candidateIndex) && handled.Add(candidateIndex))
                {
                    ApplyCandidateFlip(candidateIndex, previous, target, ref collapseChanged);
                }
            }

            foreach (var link in _snapshot.LinksForLine(line))
            {
                var oldVisible = MarkdownSemanticReveal.RevealRange(previous, link.Start, link.End);
                var newVisible = MarkdownSemanticReveal.RevealRange(target, link.Start, link.End);
                if (oldVisible != newVisible)
                {
                    visualChanged = true;
                }

                if (_linkIndex.TryGetValue(link, out var candidateIndex) && handled.Add(candidateIndex))
                {
                    ApplyCandidateFlip(candidateIndex, previous, target, ref collapseChanged);
                }
            }
        }

        if (previous.Active)
        {
            CollectLine(previous.CaretLineZeroBased);
        }

        if (target.Active && target.CaretLineZeroBased != previous.CaretLineZeroBased)
        {
            CollectLine(target.CaretLineZeroBased);
        }

        Caret = target;
        return new MarkdownCollapseChange(collapseChanged, visualChanged || collapseChanged);
    }

    /// <summary>
    /// 仅当候选在两个光标态间「显灵位真的翻转」时才改动 Runs（同区间移动零改动）。
    /// 翻转方向：转为显灵 → 摘出 cell；转回塌缩 → 插入 cell。
    /// </summary>
    private void ApplyCandidateFlip(
        int candidateIndex,
        MarkdownCaretReveal previous,
        MarkdownCaretReveal target,
        ref bool collapseChanged)
    {
        var candidate = _candidates[candidateIndex];
        var wasRevealed = MarkdownSemanticCollapseLayout.RevealedAt(candidate, previous);
        var nowRevealed = MarkdownSemanticCollapseLayout.RevealedAt(candidate, target);
        if (wasRevealed == nowRevealed)
        {
            return;
        }

        if (nowRevealed)
        {
            RemoveCell(candidate.Cell1Start, candidate.Cell1End);
            RemoveCell(candidate.Cell2Start, candidate.Cell2End);
        }
        else
        {
            AddCell(candidate.Cell1Start, candidate.Cell1End);
            AddCell(candidate.Cell2Start, candidate.Cell2End);
        }

        collapseChanged = true;
    }

    /// <summary>摘除 [start,end)：该格此时必落在某一条已合并 run 内，原地劈成 ≤2 段。</summary>
    private void RemoveCell(int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var index = LowerBoundStart(start);
        if (index == _runs.Count || _runs[index].Start > start)
        {
            index--;
        }

        if (index < 0 || index >= _runs.Count ||
            _runs[index].Start > start || _runs[index].End < end)
        {
            // 不变量被破坏的兜底：不做破坏性操作（oracle 测试会暴露漏删）。
            return;
        }

        var run = _runs[index];
        var hasLeft = start > run.Start;
        var hasRight = end < run.End;
        _runs.RemoveAt(index);
        if (hasLeft)
        {
            _runs.Insert(index, new MarkdownCollapseRun(run.Start, start));
        }

        if (hasRight)
        {
            _runs.Insert(hasLeft ? index + 1 : index, new MarkdownCollapseRun(end, run.End));
        }
    }

    /// <summary>插入 [start,end)：与左/右邻接（touch）的已塌缩 run 合并成一条。</summary>
    private void AddCell(int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var index = LowerBoundStart(start);
        if (index > 0 && _runs[index - 1].End >= start)
        {
            index--;
        }

        var newStart = start;
        var newEnd = end;
        var walk = index;
        while (walk < _runs.Count && _runs[walk].Start <= newEnd)
        {
            if (_runs[walk].Start < newStart)
            {
                newStart = _runs[walk].Start;
            }

            if (_runs[walk].End > newEnd)
            {
                newEnd = _runs[walk].End;
            }

            walk++;
        }

        if (walk > index)
        {
            _runs.RemoveRange(index, walk - index);
        }

        _runs.Insert(index, new MarkdownCollapseRun(newStart, newEnd));
    }

    /// <summary>首条 run.Start &gt;= value 的下标（runs 按 Start 有序）。</summary>
    private int LowerBoundStart(int value)
    {
        var low = 0;
        var high = _runs.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_runs[middle].Start < value)
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

    /// <summary>该光标下某行已显灵的引用 `&gt;` 单元数（前导连续 `&gt;`，行首≤3空格）。</summary>
    private int QuoteCellCount(MarkdownCaretReveal caret, int line)
    {
        if (!caret.Active || line < 0 || line >= _lineStarts.Length)
        {
            return 0;
        }

        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _source.Length;
        var index = lineStart;
        var revealed = 0;
        while (index < lineEnd)
        {
            var spaces = 0;
            while (index < lineEnd && spaces < 3 && _source[index] == ' ')
            {
                index++;
                spaces++;
            }

            if (index >= lineEnd || _source[index] != '>')
            {
                break;
            }

            if (caret.CaretOffset >= index)
            {
                revealed++;
            }

            index++;
            if (index < lineEnd && (_source[index] is ' ' or '\t'))
            {
                index++;
            }
        }

        return revealed;
    }

    /// <summary>span 在当前光标下是否显灵：区间型看闭区间、行边界型看光标所在行与起点。</summary>
    private static bool RevealSpan(MarkdownCaretReveal caret, int gatheredLine, MarkdownSemanticSpan span)
    {
        if (!caret.Active)
        {
            return false;
        }

        if (MarkdownSemanticReveal.IsRangeKind(span.Kind))
        {
            return MarkdownSemanticReveal.RevealRange(caret, span.Start, span.End);
        }

        // 行边界型：单行标记按其所在行判定（gatheredLine 即标记所在的可视行）。
        return caret.CaretLineZeroBased == gatheredLine && caret.CaretOffset >= span.Start;
    }
}
