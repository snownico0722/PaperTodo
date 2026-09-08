namespace PaperTodo;

/// <summary>
/// 一段需要在 Full 档从布局中"塌缩"的源码区间。IsClosingEdge=true 表示该 run 是某语法单元
/// 的闭标记 cell（**…** 的 `**`、…](url) 的 `]…`、HTML pair 的 `</tag>` 等）——光标/选区边界
/// 需停在 cell 起点而不是越过 cell 末尾，从而避免拖选可见正文时把右侧隐藏标记带进选区。
/// </summary>
internal readonly record struct MarkdownCollapseRun(int Start, int End, bool IsClosingEdge)
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
/// - Resolve：按当前光标过滤”显灵单元”后把 cell 排序（不合并，保留开/闭归属）。
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

        // 行起点表已由 snapshot 在解析/增量时算好并携带，整篇构建不再重复逐字符扫行。
        var lineStarts = snapshot.LineStarts;
        if (lineStarts.Length == 0)
        {
            return Array.Empty<MarkdownCollapseCandidate>();
        }

        var candidates = new List<MarkdownCollapseCandidate>();
        var unusedSpans = new List<MarkdownSemanticSpan>();
        var unusedLinks = new List<MarkdownSemanticLink>();
        CollectCandidates(
            snapshot, text, lineStarts, candidates, unusedSpans, unusedLinks,
            0, snapshot.Spans.Count, 0, snapshot.Links.Count);
        return candidates.Count == 0
            ? Array.Empty<MarkdownCollapseCandidate>()
            : candidates.ToArray();
    }

    /// <summary>
    /// 由静态候选 + 光标求得最终折叠区间：收集"未显灵单元"的 cell，按闭 cell 标志设置归属，
    /// 排序后保持 cell 独立——不合并相邻 cell，因为合并会丢掉开/闭归属，使
    /// CollapsedSyntaxElement 无法把闭 cell 选区右边界正确停到 cell 起点。
    /// </summary>
    internal static IReadOnlyList<MarkdownCollapseRun> Resolve(
        MarkdownCollapseCandidate[] candidates,
        MarkdownCaretReveal caret)
    {
        if (candidates.Length == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        var runs = new List<MarkdownCollapseRun>(candidates.Length * 2);
        foreach (var candidate in candidates)
        {
            if (RevealedAt(candidate, caret))
            {
                continue;
            }

            if (candidate.HasCell1)
            {
                runs.Add(new MarkdownCollapseRun(candidate.Cell1Start, candidate.Cell1End, IsClosingEdge: false));
            }

            if (candidate.HasCell2)
            {
                runs.Add(new MarkdownCollapseRun(candidate.Cell2Start, candidate.Cell2End, IsClosingEdge: true));
            }
        }

        if (runs.Count == 0)
        {
            return Array.Empty<MarkdownCollapseRun>();
        }

        runs.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return runs;
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
    /// 扫描 snapshot 的可塌缩单元并把静态 cell 填入候选列表（不按光标显灵跳过——过滤后移到
    /// Resolve/增量表）。范围参数限定 [spanFrom,spanTo)×[linkFrom,linkTo)：整篇传全范围，rebase
    /// 传语义窗口切片（密封保证窗口内 HTML 配对自成体系）。
    /// </summary>
    internal static void CollectCandidates(
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts,
        List<MarkdownCollapseCandidate> candidates,
        List<MarkdownSemanticSpan> spanOrigins,
        List<MarkdownSemanticLink> linkOrigins,
        int spanFrom,
        int spanTo,
        int linkFrom,
        int linkTo)
    {
        // HTML 对是“成对区间型”（开标签 + 闭标签一起显隐），与行内 markdown 分隔符一致：
        // 不再为单个 HtmlMarker 建单格候选。先预扫一次建配对索引，供 HtmlContainer 取两端。
        var openerByStart = new Dictionary<int, int>();
        var closerByEnd = new Dictionary<int, int>();
        var containerRanges = new HashSet<(int Start, int End)>();
        var spans = snapshot.Spans;
        for (var index = spanFrom; index < spanTo; index++)
        {
            var span = spans[index];
            switch (span.Kind)
            {
                case MarkdownSemanticSpanKind.HtmlContainer:
                    containerRanges.Add((span.Start, span.End));
                    break;

                case MarkdownSemanticSpanKind.HtmlMarker when span.Length >= 2 &&
                    span.Start >= 0 && span.Start + 1 < source.Length:
                    // 闭标签形如 `</…>`：起点处第二个字符是 '/'；否则为开标签。
                    if (source[span.Start] == '<' && source[span.Start + 1] == '/')
                    {
                        closerByEnd[span.End] = span.Start;
                    }
                    else
                    {
                        openerByStart[span.Start] = span.End;
                    }
                    break;
            }
        }

        for (var index = spanFrom; index < spanTo; index++)
        {
            var span = spans[index];
            MarkdownCollapseCandidate? built = span.Kind switch
            {
                MarkdownSemanticSpanKind.Heading => BuildAtxHeading(span, source, lineStarts),
                MarkdownSemanticSpanKind.Emphasis or
                MarkdownSemanticSpanKind.Strong or
                MarkdownSemanticSpanKind.Strikethrough or
                MarkdownSemanticSpanKind.InlineCode => BuildInlineDelimiters(span, source, lineStarts),
                MarkdownSemanticSpanKind.EscapeMarker => BuildEscapeCell(span, snapshot, source, lineStarts),
                MarkdownSemanticSpanKind.HtmlContainer =>
                    BuildHtmlPair(span, openerByStart, closerByEnd, source, lineStarts),
                _ => null
            };
            if (built is { } candidate)
            {
                candidates.Add(candidate);
                spanOrigins.Add(span);
                linkOrigins.Add(default);
            }
        }

        var links = snapshot.Links;
        for (var index = linkFrom; index < linkTo; index++)
        {
            var link = links[index];
            // HTML <a>…</a> 的 anchor 链接：其 [Start,End] 恰与某 HtmlContainer 重合，
            // 开闭标签的塌缩已由该 container 候选接管，跳过以免重复 cell / 双候选翻转。
            if (containerRanges.Contains((link.Start, link.End)))
            {
                continue;
            }

            // 裸链（IsAuto 且无可见语法）无任何可塌缩控制符，保持原样。
            if (link.IsAuto && !link.HasVisibleSyntax)
            {
                continue;
            }

            // 其余链接（显式 [label](url)、带 < > 的 autolink）：塌缩 label 两侧语法段。
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

    /// <summary>转义反斜杠仅在未被链接语法格覆盖时独立塌缩；label 内的转义仍独立显隐。</summary>
    private static MarkdownCollapseCandidate? BuildEscapeCell(
        MarkdownSemanticSpan span,
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts)
    {
        if (span.Length <= 0)
        {
            return null;
        }

        if (snapshot.TryGetLinkAtOffset(span.Start, out var link) &&
            link.HasVisibleSyntax &&
            BuildLink(link, source, lineStarts) is { } linkCandidate &&
            ((linkCandidate.HasCell1 && span.Start >= linkCandidate.Cell1Start && span.End <= linkCandidate.Cell1End) ||
             (linkCandidate.HasCell2 && span.Start >= linkCandidate.Cell2Start && span.End <= linkCandidate.Cell2End)))
        {
            // URL、title、reference id 等由链接拥有整个隐藏区间。重叠的小格会让增量表
            // 在光标离开链接时拒绝恢复大格；只在该链接格实际可塌缩时去重（跨行格可能不塌缩）。
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

    /// <summary>
    /// HTML 开闭标签对：一个 HtmlContainer 一个成对候选。Cell1=开标签、Cell2=闭标签，显灵 =
    /// caret ∈ [container.Start, container.End]——两端标签一起显隐。
    /// </summary>
    private static MarkdownCollapseCandidate? BuildHtmlPair(
        MarkdownSemanticSpan container,
        Dictionary<int, int> openerByStart,
        Dictionary<int, int> closerByEnd,
        string source,
        int[] lineStarts)
    {
        if (container.Length <= 0 ||
            !openerByStart.TryGetValue(container.Start, out var openingEnd) ||
            !closerByEnd.TryGetValue(container.End, out var closingStart))
        {
            // 找不到配对 marker（理论上不发生）：防御性降级，不塌缩该对。
            return null;
        }

        var openingStart = container.Start;
        var closingEnd = container.End;

        // 历史 AddIfSingleLine：仅保留完全落在一行内的格。
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
            LineZero: FindLine(lineStarts, container.Start),
            Start: container.Start,
            End: container.End,
            Cell1Start: openingStart,
            Cell1End: openingEnd,
            Cell2Start: closingStart,
            Cell2End: closingEnd);
    }

    /// <summary>
    /// 显式链接语法：`[label](url)` 中塌缩开 `[`、`](`、url、闭 `)` 的非 label 部分；
    /// 带尖括号的 autolink `&lt;url&gt;` 同样适用——Cell1 即 `<`、Cell2 即 `>`。
    /// </summary>
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

    /// <summary>有序 spans 中首个 Start&gt;=offset 的下标（供按窗口 [start,end) 取 span 切片）。</summary>
    internal static int LowerBoundSpanStart(IReadOnlyList<MarkdownSemanticSpan> spans, int offset)
    {
        var low = 0;
        var high = spans.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (spans[middle].Start < offset)
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

    /// <summary>有序 links 中首个 Start&gt;=offset 的下标（供按窗口 [start,end) 取 link 切片）。</summary>
    internal static int LowerBoundLinkStart(IReadOnlyList<MarkdownSemanticLink> links, int offset)
    {
        var low = 0;
        var high = links.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (links[middle].Start < offset)
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
}

/// <summary>
/// 增量折叠区间表：静态候选（随文本版本重建一次）+ 一份始终有序、互不重叠的折叠区间 Runs。
/// 光标变化时由 SyncTo 只对「旧/新光标行上显灵状态翻转」的候选做局部摘除/插入，保持
/// Runs 等于 Resolve(候选, 当前光标) 的不变式；同区间移动不触发任何改动。
/// </summary>
internal sealed class MarkdownCollapseTable
{
    private readonly MarkdownSemanticSnapshot _snapshot;
    private readonly string _source;
    private readonly int[] _lineStarts;
    private readonly MarkdownCollapseCandidate[] _candidates;

    // 与 _candidates 对齐的 origin（候选恰由其中一个派生，另一个为 default）。局部 rebase 需要
    // 按 origin.Start 对既有候选分流（前缀原样 / 窗口丢弃 / 后缀平移）并重建 origin 索引。
    private readonly MarkdownSemanticSpan[] _spanOrigins;
    private readonly MarkdownSemanticLink[] _linkOrigins;
    private readonly Dictionary<MarkdownSemanticSpan, int> _spanIndex;
    private readonly Dictionary<MarkdownSemanticLink, int> _linkIndex;
    private readonly List<MarkdownCollapseRun> _runs;

    private MarkdownCollapseTable(
        MarkdownSemanticSnapshot snapshot,
        string source,
        int[] lineStarts,
        MarkdownCollapseCandidate[] candidates,
        MarkdownSemanticSpan[] spanOrigins,
        MarkdownSemanticLink[] linkOrigins,
        Dictionary<MarkdownSemanticSpan, int> spanIndex,
        Dictionary<MarkdownSemanticLink, int> linkIndex,
        IReadOnlyList<MarkdownCollapseRun> initialRuns,
        MarkdownCaretReveal caret)
    {
        _snapshot = snapshot;
        _source = source;
        _lineStarts = lineStarts;
        _candidates = candidates;
        _spanOrigins = spanOrigins;
        _linkOrigins = linkOrigins;
        _spanIndex = spanIndex;
        _linkIndex = linkIndex;
        _runs = new List<MarkdownCollapseRun>(initialRuns);
        Caret = caret;
    }

    /// <summary>当前 Runs 所对应的显灵值（预览= None；手势冻结=按下瞬间快照）。</summary>
    public MarkdownCaretReveal Caret { get; private set; }

    /// <summary>有序、互不重叠的当前折叠区间。</summary>
    public IReadOnlyList<MarkdownCollapseRun> Runs => _runs;

    /// <summary>本表构建时所基于的语义快照（rebase 前须与编辑描述里的旧快照同一引用）。</summary>
    internal MarkdownSemanticSnapshot Snapshot => _snapshot;

    /// <summary>当前静态候选（仅供 oracle 测试与诊断：与 BuildCandidates 归一化后应逐元素相等）。</summary>
    internal IReadOnlyList<MarkdownCollapseCandidate> Candidates => _candidates;

    /// <summary>
    /// 按 origin 的 Length>0 分流，把每个候选对应的 span/link 映射到候选索引。两个 Dictionary
    /// 必恰好覆盖全部候选——默认 span/link 的 Length==0，不可能是任何候选。
    /// </summary>
    private static (
        Dictionary<MarkdownSemanticSpan, int> spanIndex,
        Dictionary<MarkdownSemanticLink, int> linkIndex)
        BuildOriginIndices(
            List<MarkdownCollapseCandidate> candidates,
            List<MarkdownSemanticSpan> spanOrigins,
            List<MarkdownSemanticLink> linkOrigins)
    {
        var spanIndex = new Dictionary<MarkdownSemanticSpan, int>(candidates.Count);
        var linkIndex = new Dictionary<MarkdownSemanticLink, int>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            if (spanOrigins[index].Length > 0)
            {
                spanIndex[spanOrigins[index]] = index;
            }
            else
            {
                linkIndex[linkOrigins[index]] = index;
            }
        }
        return (spanIndex, linkIndex);
    }

    /// <summary>整篇重建一张表（首次进入 Full / rebase 不可行时回退调用）。行起点表直接取 snapshot 携带的。</summary>
    public static MarkdownCollapseTable Build(
        MarkdownSemanticSnapshot snapshot,
        string? source,
        MarkdownCaretReveal caret)
    {
        var text = source ?? string.Empty;
        var lineStarts = snapshot.LineStarts;
        var candidates = new List<MarkdownCollapseCandidate>();
        var spanOrigins = new List<MarkdownSemanticSpan>();
        var linkOrigins = new List<MarkdownSemanticLink>();
        MarkdownSemanticCollapseLayout.CollectCandidates(
            snapshot, text, lineStarts, candidates, spanOrigins, linkOrigins,
            0, snapshot.Spans.Count, 0, snapshot.Links.Count);

        var (spanIndex, linkIndex) = BuildOriginIndices(candidates, spanOrigins, linkOrigins);

        var array = candidates.Count == 0
            ? Array.Empty<MarkdownCollapseCandidate>()
            : candidates.ToArray();
        var initial = MarkdownSemanticCollapseLayout.Resolve(array, caret);
        return new MarkdownCollapseTable(
            snapshot,
            text,
            lineStarts,
            array,
            spanOrigins.ToArray(),
            linkOrigins.ToArray(),
            spanIndex,
            linkIndex,
            initial,
            caret);
    }

    /// <summary>
    /// 按语义窗口把本表 rebase 到 final snapshot/source：前缀原样复用、窗口段重算、后缀平移 delta，
    /// 再以 reveal 解析重建 runs。语义密封保证无旧 origin 跨窗口边界。窗口坐标非法时返回 null。
    /// </summary>
    internal MarkdownCollapseTable? Rebase(
        MarkdownSemanticSnapshot newSnapshot,
        string newSource,
        MarkdownSemanticIncrementalWindow window,
        MarkdownCaretReveal reveal)
    {
        var oldStart = window.OldStart;
        var oldEnd = window.OldEnd;
        var newStart = window.NewStart;
        var newEnd = window.NewEnd;
        if (oldStart < 0 || oldEnd < oldStart || newStart < 0 || newEnd < newStart)
        {
            return null;
        }

        var newLineStarts = newSnapshot.LineStarts;
        if ((newSource.Length == 0) != (newLineStarts.Length == 0))
        {
            // 防御：行起点表与源长度矛盾（异常快照），不盲目 rebase。
            return null;
        }

        var delta = newEnd - oldEnd;
        var candidates = new List<MarkdownCollapseCandidate>(_candidates.Length + 8);
        var spanOrigins = new List<MarkdownSemanticSpan>(_candidates.Length + 8);
        var linkOrigins = new List<MarkdownSemanticLink>(_candidates.Length + 8);

        // 前缀（Start<OldStart）原样；窗口（[OldStart,OldEnd)）被局部解析替换，丢弃；
        // 后缀（Start>=OldEnd）整体平移 delta。分流只读 origin.Start（恒等于 candidate.Start）。
        for (var index = 0; index < _candidates.Length; index++)
        {
            var originIsSpan = _spanOrigins[index].Length > 0;
            var originStart = originIsSpan ? _spanOrigins[index].Start : _linkOrigins[index].Start;
            if (originStart < oldStart)
            {
                candidates.Add(_candidates[index]);
                spanOrigins.Add(_spanOrigins[index]);
                linkOrigins.Add(_linkOrigins[index]);
            }
            else if (originStart >= oldEnd)
            {
                candidates.Add(ShiftCandidate(_candidates[index], delta, newLineStarts));
                if (originIsSpan)
                {
                    spanOrigins.Add(MarkdownSemanticSnapshot.ShiftSpan(_spanOrigins[index], delta));
                    linkOrigins.Add(default);
                }
                else
                {
                    spanOrigins.Add(default);
                    linkOrigins.Add(MarkdownSemanticSnapshot.ShiftLink(_linkOrigins[index], delta));
                }
            }
        }

        // 窗口段从 final snapshot 重算（spans/links 各自有序 → 对 [NewStart,NewEnd) 二分取界）。
        var spans = newSnapshot.Spans;
        var links = newSnapshot.Links;
        var spanFrom = MarkdownSemanticCollapseLayout.LowerBoundSpanStart(spans, newStart);
        var spanTo = MarkdownSemanticCollapseLayout.LowerBoundSpanStart(spans, newEnd);
        var linkFrom = MarkdownSemanticCollapseLayout.LowerBoundLinkStart(links, newStart);
        var linkTo = MarkdownSemanticCollapseLayout.LowerBoundLinkStart(links, newEnd);
        MarkdownSemanticCollapseLayout.CollectCandidates(
            newSnapshot, newSource, newLineStarts, candidates, spanOrigins, linkOrigins,
            spanFrom, spanTo, linkFrom, linkTo);

        // origin → 候选索引（与 Build 同法）。
        var (spanIndex, linkIndex) = BuildOriginIndices(candidates, spanOrigins, linkOrigins);

        var array = candidates.Count == 0
            ? Array.Empty<MarkdownCollapseCandidate>()
            : candidates.ToArray();
        var initial = MarkdownSemanticCollapseLayout.Resolve(array, reveal);
        return new MarkdownCollapseTable(
            newSnapshot,
            newSource,
            newLineStarts,
            array,
            spanOrigins.ToArray(),
            linkOrigins.ToArray(),
            spanIndex,
            linkIndex,
            initial,
            reveal);
    }

    /// <summary>后缀候选整体平移 delta：各偏移 +delta，空 cell（CellEnd==CellStart）保持 0；LineZero 按新行表重算。</summary>
    private static MarkdownCollapseCandidate ShiftCandidate(
        MarkdownCollapseCandidate candidate,
        int delta,
        int[] newLineStarts) =>
        candidate with
        {
            LineZero = MarkdownSemanticCollapseLayout.FindLine(newLineStarts, candidate.Start + delta),
            Start = candidate.Start + delta,
            End = candidate.End + delta,
            Cell1Start = candidate.HasCell1 ? candidate.Cell1Start + delta : candidate.Cell1Start,
            Cell1End = candidate.HasCell1 ? candidate.Cell1End + delta : candidate.Cell1End,
            Cell2Start = candidate.HasCell2 ? candidate.Cell2Start + delta : candidate.Cell2Start,
            Cell2End = candidate.HasCell2 ? candidate.Cell2End + delta : candidate.Cell2End
        };

    /// <summary>
    /// 把显灵值增量同步到 target：翻转集 = 旧/新光标行上登记的 span/link + 引用单元；同区间移动
    /// 翻转集为空 → 零改动。
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

                // HTML 子级 span（标签/内容样式）的视觉随所属 HtmlContainer 成对区间变化：其真正的
                // 显灵翻转由 container span（现为 range 型）驱动。这里若再按行边界规则单独预检会
                // 在容器内产生假 visualChanged，故跳过预检、仍保留候选翻转查找。
                if (!IsHtmlChildSemanticKind(span.Kind))
                {
                    var oldVisible = RevealSpan(previous, line, span);
                    var newVisible = RevealSpan(target, line, span);
                    if (oldVisible != newVisible)
                    {
                        visualChanged = true;
                    }
                }

                if (_spanIndex.TryGetValue(span, out var candidateIndex) && handled.Add(candidateIndex))
                {
                    ApplyCandidateFlip(candidateIndex, previous, target, ref collapseChanged);
                }
            }

            foreach (var link in _snapshot.LinksForLine(line))
            {
                // 裸链无可见语法且不塌缩：跳过预检，避免光标进出裸链边界触发无谓整篇重算。
                if (link.HasVisibleSyntax)
                {
                    var oldVisible = MarkdownSemanticReveal.RevealRange(previous, link.Start, link.End);
                    var newVisible = MarkdownSemanticReveal.RevealRange(target, link.Start, link.End);
                    if (oldVisible != newVisible)
                    {
                        visualChanged = true;
                    }
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
    /// 仅当候选在两光标态间「显灵位真的翻转」时才改动 Runs。转为显灵摘出 cell，转回塌缩插入 cell，
    /// 并透传开/闭归属。
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
            RemoveCell(candidate.Cell1Start, candidate.Cell1End, isClosingEdge: false);
            RemoveCell(candidate.Cell2Start, candidate.Cell2End, isClosingEdge: true);
        }
        else
        {
            AddCell(candidate.Cell1Start, candidate.Cell1End, isClosingEdge: false);
            AddCell(candidate.Cell2Start, candidate.Cell2End, isClosingEdge: true);
        }

        collapseChanged = true;
    }

    /// <summary>摘除 [start,end)：左段继承原 run 的归属，右段沿用本次被摘 cell 的归属。</summary>
    private void RemoveCell(int start, int end, bool isClosingEdge)
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
            _runs.Insert(index, new MarkdownCollapseRun(run.Start, start, run.IsClosingEdge));
        }

        if (hasRight)
        {
            _runs.Insert(hasLeft ? index + 1 : index, new MarkdownCollapseRun(end, run.End, isClosingEdge));
        }
    }

    /// <summary>插入 [start,end)：有序、互不重叠、不与现有 run 合并——cell 归属作为选区右边界判定依据。</summary>
    private void AddCell(int start, int end, bool isClosingEdge)
    {
        if (end <= start)
        {
            return;
        }

        var index = LowerBoundStart(start);
        if (index < _runs.Count && _runs[index].Start < end)
        {
            // 兜底：cell 之间夹内容，理论上不重叠；撞上说明候选重叠，跳过避免破坏不变量。
            return;
        }

        _runs.Insert(index, new MarkdownCollapseRun(start, end, isClosingEdge));
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

    /// <summary>该光标下某行已显灵的引用 `&gt;` 单元数。</summary>
    private int QuoteCellCount(MarkdownCaretReveal caret, int line)
    {
        if (!caret.Active || line < 0 || line >= _lineStarts.Length)
        {
            return 0;
        }

        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _source.Length;
        var revealed = 0;
        foreach (var marker in MarkdownQuoteMarkers.EnumerateMarkers(_source, lineStart, lineEnd))
        {
            if (caret.CaretOffset >= marker.Start)
            {
                revealed++;
            }
        }

        return revealed;
    }

    /// <summary>
    /// HTML 对内部的子级 span（开/闭标签、内容样式）。它们的显灵统一由所属 HtmlContainer 的成对
    /// 区间驱动，不做行边界型的独立预检（见 <see cref="CollectLine"/>）。
    /// </summary>
    private static bool IsHtmlChildSemanticKind(MarkdownSemanticSpanKind kind) =>
        kind is MarkdownSemanticSpanKind.HtmlMarker or
            MarkdownSemanticSpanKind.HtmlStrong or
            MarkdownSemanticSpanKind.HtmlEmphasis or
            MarkdownSemanticSpanKind.HtmlStrikethrough or
            MarkdownSemanticSpanKind.HtmlUnderline or
            MarkdownSemanticSpanKind.HtmlCode;

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
