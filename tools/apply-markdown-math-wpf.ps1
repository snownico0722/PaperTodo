$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Read-RepoText([string]$Path) {
    return [System.IO.File]::ReadAllText((Join-Path $root $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    $full = Join-Path $root $Path
    [System.IO.File]::WriteAllText($full, $Text, $utf8)
}

function Replace-RepoText(
    [string]$Path,
    [string]$Old,
    [string]$New,
    [int]$ExpectedCount = 1) {
    $text = Read-RepoText $Path
    $count = 0
    $cursor = 0
    while (($index = $text.IndexOf($Old, $cursor, [System.StringComparison]::Ordinal)) -ge 0) {
        $count++
        $cursor = $index + $Old.Length
    }
    if ($count -ne $ExpectedCount) {
        throw "${Path}: expected $ExpectedCount occurrence(s), found $count."
    }
    Write-RepoText $Path ($text.Replace($Old, $New, [System.StringComparison]::Ordinal))
}

Replace-RepoText "PaperTodo.csproj" @'
    <!-- Markdig is the semantic parser only; AvalonEdit remains the source document and renderer. -->
    <PackageReference Include="Markdig" Version="1.3.2" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.10" />
'@ @'
    <!-- Markdig owns ordinary Markdown grammar; AvalonEdit remains the only source document. -->
    <PackageReference Include="Markdig" Version="1.3.2" />
    <!-- Native WPF vector layout for PaperTodo's bounded TeX math surface. -->
    <PackageReference Include="WpfMath" Version="2.1.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.10" />
'@

Replace-RepoText "src/MarkdownTextBox.cs" @'
    public event Action? ImageContextMenuClosed;

    public bool IsImageContextMenuOpen { get; private set; }
'@ @'
    public event Action? ImageContextMenuClosed;

    /// <summary>
    /// Raised after Markdown presentation settings/theme/typography have changed but before the
    /// TextView is synchronously refreshed. Presentation layers use this boundary to update native
    /// line-collapse state before AvalonEdit constructs visual lines.
    /// </summary>
    internal event Action? MarkdownPresentationRefreshing;

    public bool IsImageContextMenuOpen { get; private set; }
'@

Replace-RepoText "src/MarkdownTextBox.cs" @'
    public void RefreshVisualStyle()
    {
        Foreground = Theme.TextBrush;
'@ @'
    public void RefreshVisualStyle()
    {
        MarkdownPresentationRefreshing?.Invoke();
        Foreground = Theme.TextBrush;
'@

Replace-RepoText "src/MarkdownSemanticSnapshot.cs" @'
    Strikethrough,
    InlineCode,
    Image,
'@ @'
    Strikethrough,
    InlineCode,
    InlineMath,
    BlockMath,
    Image,
'@

Replace-RepoText "src/MarkdownSemanticSnapshot.cs" @'
        CollectBlocks(document, source, lineStarts, spans, links);
        ApplyLegacyCompatibilityBoundaries(source, spans, links);
        CollectEscapeMarkers(source, spans);
        CollectBareHttpLinks(source, spans, links);
        spans.Sort(CompareSemanticSpans);
'@ @'
        CollectBlocks(document, source, lineStarts, spans, links);
        ApplyLegacyCompatibilityBoundaries(source, spans, links);
        CollectEscapeMarkers(source, spans);
        CollectBareHttpLinks(source, spans, links);
        CollectMathSemantics(source, spans, links);
        spans.Sort(CompareSemanticSpans);
'@

Replace-RepoText "src/MarkdownSemanticSnapshot.Incremental.cs" @'
        if (ReferenceEquals(oldSource, newSource))
        {
            snapshot = oldSnapshot;
            return true;
        }

        FindContiguousDifference(
'@ @'
        if (ReferenceEquals(oldSource, newSource))
        {
            snapshot = oldSnapshot;
            return true;
        }

        if (MarkdownMathIncremental.ChangeMayAffectDelimiterState(
                oldSource,
                oldSnapshot,
                newSource))
        {
            return false;
        }

        FindContiguousDifference(
'@

Replace-RepoText "src/MarkdownSemanticReveal.cs" @'
    /// <summary>两端带分隔符、需整段显隐的行内 span 种类。</summary>
'@ @'
    /// <summary>两端带分隔符、需整段显隐的行内或块级 span 种类。</summary>
'@

Replace-RepoText "src/MarkdownSemanticReveal.cs" @'
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode or
            MarkdownSemanticSpanKind.HtmlContainer;
'@ @'
            MarkdownSemanticSpanKind.Strikethrough or
            MarkdownSemanticSpanKind.InlineCode or
            MarkdownSemanticSpanKind.InlineMath or
            MarkdownSemanticSpanKind.BlockMath or
            MarkdownSemanticSpanKind.HtmlContainer;
'@

Replace-RepoText "src/MarkdownSemanticPresentation.cs" @'
        SyncCaretReveal();
        SyncRevealFade();
        AttachCollapseGenerator();
'@ @'
        SyncCaretReveal();
        SyncRevealFade();
        AttachMathPresentation();
        AttachCollapseGenerator();
'@

Replace-RepoText "src/MarkdownSemanticPresentation.cs" @'
        SyncCaretReveal();
        SyncRevealFade();
        AlignCollapseTableToReveal(scheduleRedraw: true);
'@ @'
        SyncCaretReveal();
        SyncRevealFade();
        AlignCollapseTableToReveal(scheduleRedraw: true);
        SyncMathRevealRedraw();
'@ 2

Replace-RepoText "src/MarkdownSemanticPresentation.cs" @'
            SyncCaretReveal();
            SyncRevealFade();
            AlignCollapseTableToReveal(scheduleRedraw: true);
'@ @'
            SyncCaretReveal();
            SyncRevealFade();
            AlignCollapseTableToReveal(scheduleRedraw: true);
            SyncMathRevealRedraw();
'@

Replace-RepoText "src/MarkdownSemanticPresentation.cs" @'
        // 文本编辑会使标记位移：中止进行中的淡入，避免把旧 alpha 施加到新布局的标记上。
        AbortRevealFade();

        // 静态候选随 snapshot 重建：优先按语义层增量窗口局部 rebase（逐键、不整篇扫）；
'@ @'
        // 文本编辑会使标记位移：中止进行中的淡入，避免把旧 alpha 施加到新布局的标记上。
        AbortRevealFade();
        ResetMathPresentationState();

        // 静态候选随 snapshot 重建：优先按语义层增量窗口局部 rebase（逐键、不整篇扫）；
'@

Replace-RepoText "src/MarkdownSemanticPresentation.cs" @'
        _editor.CaretRevealGestureStarted -= OnCaretRevealGestureStarted;
        _editor.CaretRevealGestureEnded -= OnCaretRevealGestureEnded;
        DetachCollapseGenerator();
        AbortRevealFade();
'@ @'
        _editor.CaretRevealGestureStarted -= OnCaretRevealGestureStarted;
        _editor.CaretRevealGestureEnded -= OnCaretRevealGestureEnded;
        DetachCollapseGenerator();
        DetachMathPresentation();
        AbortRevealFade();
'@

Replace-RepoText "tests/PaperTodo.MarkdownSemanticChecks/PaperTodo.MarkdownSemanticChecks.csproj" @'
    <PackageReference Include="AvalonEdit" Version="6.3.1.120" />
    <PackageReference Include="Markdig" Version="1.3.2" />
    <Compile Include="..\..\src\MarkdownSemanticDocument.cs" Link="MarkdownSemanticDocument.cs" />
'@ @'
    <PackageReference Include="AvalonEdit" Version="6.3.1.120" />
    <PackageReference Include="Markdig" Version="1.3.2" />
    <Compile Include="..\..\src\MarkdownMathIncremental.cs" Link="MarkdownMathIncremental.cs" />
    <Compile Include="..\..\src\MarkdownMathScanner.cs" Link="MarkdownMathScanner.cs" />
    <Compile Include="..\..\src\MarkdownSemanticDocument.cs" Link="MarkdownSemanticDocument.cs" />
'@

Replace-RepoText "tests/PaperTodo.MarkdownEditingChecks/Program.cs" @'
        RunLocalRedrawChecks(Check);

        Console.WriteLine($"Markdown editing checks: {failures} failure(s).");
'@ @'
        RunMathFormulaChecks(Check);
        RunLocalRedrawChecks(Check);

        Console.WriteLine($"Markdown editing checks: {failures} failure(s).");
'@

Write-Output "Markdown WpfMath integration applied."
