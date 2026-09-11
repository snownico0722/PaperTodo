from pathlib import Path
import shutil, re

root = Path('candidate')
def edit(path, old, new):
    p = root / path
    text = p.read_text(encoding='utf-8-sig')
    if text.count(old) != 1:
        raise RuntimeError(f'Unexpected patch context in {path}: {old[:90]!r}')
    p.write_text(text.replace(old, new), encoding='utf-8', newline='\n')
def save(path, text):
    p = root / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8', newline='\n')

# Retain the real-host performance harness; remove its obsolete AvalonEdit-control route.
p = root / 'tests/PaperTodo.EdgePreviewExperimentChecks/Program.cs'
s = p.read_text(encoding='utf-8-sig')
a = s.index('    private static void Checks()'); b = s.index('    private static EdgeCapsuleHost NewHost()', a)
s = s[:a] + s[b:]
s = s.replace('internal static class Program', 'internal static partial class Program')
s = s.replace('if (args.Contains("--profile")) Profile(args.Contains("--avalon"));',
'''if (args.Contains("--profile")) Profile();
            else if (args.Contains("--export")) ExportPreviewPixels(args.Last());''')
s = s.replace('private static void Profile(bool avalon)', 'private static void Profile()')
s = s.replace('ProfileOne(fixture.Text, mode, avalon)', 'ProfileOne(fixture.Text, mode)')
s = s.replace('string text, string mode, bool avalon)', 'string text, string mode)')
s = s.replace('backend = avalon ? "avalon" : "legacy"', 'backend = "bounded"')
s = s.replace('IEdgeCapsulePreviewProvider provider = avalon ? AvalonEditEdgeCapsulePreviewProvider.Instance\n            : MarkdownEdgeCapsulePreviewProvider.Instance;',
              'IEdgeCapsulePreviewProvider provider = MarkdownEdgeCapsulePreviewProvider.Instance;')
s = s.replace('            if (element is AvalonEditEdgePreviewViewport av) return av.HasPreparedContent;\n', '')
s = s.replace('false, "avalon-experiment"', 'false, "edge-preview-check"')
s = s.replace('for (var i = 0; i < 9; i++)', 'for (var i = 0; i < 24; i++)')
s = s.replace('if (i >= 2) rows.Add(result);', 'if (i >= 3) rows.Add(result);')
s = s.replace('.Order().ElementAt(3)', '.Order().ElementAt(rows.Count / 2)')
s = s.replace('("short-rows",', '''("short-dense", string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd()),
            ("short-dense-rows", string.Join('\\n', Enumerable.Repeat(string.Concat(Enumerable.Repeat("**a** *b* `c` ~~d~~ ", 10)).TrimEnd(), 12))),
            ("short-rows",''')
if 'avalon' in s.lower():
    raise RuntimeError('Obsolete provider remains in harness')
save('tests/PaperTodo.EdgePreviewChecks/Program.cs', s)
old = root / 'tests/PaperTodo.EdgePreviewExperimentChecks'
for filename in ['SharedSemanticChecks.cs']:
    save('tests/PaperTodo.EdgePreviewChecks/' + filename, (old/filename).read_text(encoding='utf-8-sig'))
save('tests/PaperTodo.EdgePreviewChecks/PaperTodo.EdgePreviewChecks.csproj',
     (old/'PaperTodo.EdgePreviewExperimentChecks.csproj').read_text(encoding='utf-8-sig'))
shutil.rmtree(old)
(root/'src/EdgeCapsulePreview.AvalonEdit.cs').unlink()

host = root/'src/EdgeCapsuleHost.Preview.cs'
s = host.read_text(encoding='utf-8-sig')
a = s.index('        // Text renderers can reuse their own link hit testing without synthetic controls.')
b = s.index('        var current = source;', a)
save('src/EdgeCapsuleHost.Preview.cs', s[:a]+s[b:])

# Only bounded preparation belongs to the production backend; precise Describe measurement was rejected.
p = root/'src/EdgeCapsulePreview.Markdown.TextLayout.cs'
s = p.read_text(encoding='utf-8-sig')
a = s.index('internal static partial class MarkdownEdgeCapsulePreviewRenderer')
b = s.index('// Only the viewport-bounded', a)
s = s[:a]+s[b:]
a = s.index('    internal sealed class MeasureElement(')
b = s.index('    protected override void OnRender(', a)
s = s[:a]+s[b:]
s = s.replace('// Only the viewport-bounded long-paragraph path uses this element.',
              '// Bounded long or style-dense paragraphs use this element.')
save('src/EdgeCapsulePreview.Markdown.TextLayout.cs', s)

md = 'src/EdgeCapsulePreview.Markdown.cs'
edit(md, 'namespace PaperTodo;', '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PaperTodo.EdgePreviewChecks")]\n\nnamespace PaperTodo;')
edit(md, '    private Size? _renderedSize;', '''    private Size? _renderedSize;
    // A completed body may survive a brief retract/resume at unchanged content and geometry.
    // It is owned by this live view only; no per-note/global control or drawing cache exists.
    private Size? _publishedSize;''')
edit(md, 'IsVisibleChanged += (_, _) => InvalidateContentBuild();', 'IsVisibleChanged += (_, _) => CancelPendingBuild();')
edit(md, '''        _previewActive = active;
        // The host can still be visible while the old card retracts. Stop its pending work at
        // the existing preview visibility boundary, rather than waiting for WPF Unloaded.
        InvalidateContentBuild();''', '''        _previewActive = active;
        IsHitTestVisible = active && Opacity > 0;
        // Cancel unfinished work immediately, but keep a complete body at the same size.
        // A content/DPI invalidation or unload separately revokes that reuse permission.
        CancelPendingBuild();''')
edit(md, '''    private void InvalidateContentBuild()
    {
        _renderVersion++;
        _renderedSize = null;
        InvalidateArrange();
    }''', '''    private void InvalidateContentBuild()
    {
        _publishedSize = null;
        CancelPendingBuild();
    }

    private void CancelPendingBuild()
    {
        _renderVersion++;
        _renderedSize = _publishedSize;
        InvalidateArrange();
    }''')
edit(md, '''            _sourceTruncated = truncated;
            Opacity = 1;''', '''            _sourceTruncated = truncated;
            _publishedSize = size;
            Opacity = 1;''')
edit(md, 'if (viewportSize.HasValue && text.Length >= MarkdownEdgePreviewParagraph.MinimumSourceLength)',
'''if (viewportSize.HasValue && ShouldPrepareParagraph(text, mode, content.Inlines))''')
edit(md, '''    private const int MaximumInlineDepth = 6;\n''', '')
edit(md, 'if (element is MarkdownEdgePreviewParagraph or MarkdownEdgePreviewParagraph.MeasureElement) return;',
     'if (element is MarkdownEdgePreviewParagraph) return;')
edit(md, '// and code rows yield between visible lines; inline parsing also yields in batches.',
     '// and code rows yield between visible lines; copying prepared runs also yields in batches.')
# Complexity is decided inside the cooperative renderer, never while sizing the card.
edit(md, '    private static void AddEmptyState(Panel target)', '''    private static bool ShouldPrepareParagraph(string text, string mode, PreviewInlineCache cache)
    {
        if (text.Length >= MarkdownEdgePreviewParagraph.MinimumSourceLength) return true;
        // Tiny ordinary text stays on the simpler path. Reuse already-admitted inline values;
        // a short source can still contain many expensive styled elements.
        return text.Length >= 96 && mode != MarkdownRenderModes.Off &&
            cache.Get(text, mode).Pieces.Count >= 24;
    }

    private static void AddEmptyState(Panel target)''')

# Retain behavior checks using the actual render path instead of a dead measuring backend.
checks = 'tests/PaperTodo.MarkdownEditingChecks/EdgePreviewInlinePreparationChecks.cs'
edit(checks, 'check("Repeated row measurements retain natural height and current width/zoom", () =>',
     'check("Rendered repeated rows retain natural height and current width/zoom", () =>')
edit(checks, '''        check("Rendered repeated rows retain natural height and current width/zoom", () =>
        {
            foreach (var mode''', '''        check("Rendered repeated rows retain natural height and current width/zoom", () =>
        {
            double MeasureRows(MarkdownEdgeCapsulePreviewRenderer.PreviewContent content, double width, double zoom)
            {
                var panel = new StackPanel();
                foreach (var step in MarkdownEdgeCapsulePreviewRenderer.RenderSteps(panel, content, _ => { }, textZoom: zoom)) { }
                panel.Measure(new Size(width, double.PositiveInfinity));
                return panel.DesiredSize.Height;
            }
            foreach (var mode''')
p=root/checks;s=p.read_text(encoding='utf-8-sig')
s=s.replace('MarkdownEdgeCapsulePreviewRenderer.MeasureContentHeight(', 'MeasureRows(')
s=s.replace('                _ = MeasureRows(content, 360, 1);\n','')
s=s.replace('Preview width, height and both renderers reuse one inline preparation',
            'Preview width and both renderers reuse one inline preparation')
save(checks,s)
edit('tests/PaperTodo.MarkdownEditingChecks/EdgePreviewChecks.cs',
     'MarkdownEdgeCapsulePreviewRenderer.MeasureContentHeight(content, 400, 1) > NoteTypography.FontSize * 4',
     'MarkdownEdgeCapsulePreviewRenderer.EstimateVisualLines(content, 400) > 4')
# Small/dense path gets all existing pixel, link, zoom and typography parity coverage.
edit('tests/PaperTodo.MarkdownEditingChecks/EdgePreviewTextLayoutChecks.cs',
     '''foreach (var prefix in new[] { "", "## ", "> ", "- [x] ", "```\\n" })''',
     '''foreach (var prefix in new[] { "", "## ", "> ", "- [x] ", "```\\n" })
                            foreach (var shortDense in new[] { false, true })''')
edit('tests/PaperTodo.MarkdownEditingChecks/EdgePreviewTextLayoutChecks.cs',
     '''var source = prefix + string.Concat(Enumerable.Repeat("**粗体** `code` [链接](https://example.com) 中文 ", 12)).TrimEnd();''',
     '''if (shortDense && (mode == MarkdownRenderModes.Off || prefix == "```\\n")) continue;
                                var source = prefix + (shortDense
                                    ? string.Concat(Enumerable.Repeat("**a** *b* `c` ", 8)) + "[link](https://example.com)"
                                    : string.Concat(Enumerable.Repeat("**粗体** `code` [链接](https://example.com) 中文 ", 12))).TrimEnd();''')

# Normal CI continuously exercises the completed backend and lifecycle, not only ad-hoc experiments.
wf = '.github/workflows/pull-request-build.yml'
edit(wf, '      - name: Threading regression checks', '''      - name: Edge preview rendering and lifecycle checks
        run: dotnet run --project .\\tests\\PaperTodo.EdgePreviewChecks\\PaperTodo.EdgePreviewChecks.csproj -c Release

      - name: Threading regression checks''')

# Final knowledge owners: document the chosen current path, retain failed experiments in Git/PR.
p = root/'doc/ARCHITECTURE.md';s=p.read_text(encoding='utf-8-sig')
section='''### 内置笔记的边缘预览

边缘预览是有界导航内容，不是第二个可编辑正文。`MarkdownEdgeCapsulePreviewRenderer` 先捕获一次受限文本，尺寸估算与显示使用相同内容预算；行内语法调用正文已有的 `MarkdownSemanticSnapshot`，只把结果适配为该次预览拥有的纯数据样式段。块级预览保留有限近似，不为悬停读取预算外引用定义或解析整篇笔记。

普通短行使用精简的 WPF 文字元素；长行与短但样式密集的行使用系统 `TextFormatter`，只逐行准备可见区域并复用绘制结果。没有完整 AvalonEdit 预览控件、另一套行内正则解析器、精确同步排版尺寸后端或进程级排版缓存。

`MarkdownEdgeCapsulePreviewViewport` 唯一拥有这一视图的准备/取消/一次发布：首次正文完整发布后才启用正文显示与交互，收起立即关闭交互并取消未完成工作；同一视图、同一内容、同一尺寸的完整结果可在收起/恢复间复用。内容或主题/字体/缩放的正常失效、DPI 变化、卸载撤销复用资格；新尺寸必须重新准备，旧的迟到任务不可覆盖新版。它不拥有队列、外壳动画、窗口交接或持久化状态。

'''
if '### 内置笔记的边缘预览' in s: raise RuntimeError('Architecture already has a preview owner')
s=s.replace('## 6. Edge Capsule V3 Lite',section+'## 6. Edge Capsule V3 Lite')
save('doc/ARCHITECTURE.md',s)
edit('CHANGELOG.md', '按可见空间分批处理，减少对窗口响应的影响。',
     '按可见空间分批准备文字，密集样式短行也避免一次性构造大量文字对象；相同内容和尺寸的预览在短暂收起后恢复时复用已完成结果，内容、字体、缩放或屏幕变化后重新准备。行内语法沿用正文的识别实现，改善嵌套强调、转义与复杂链接的一致性。')
edit('doc/CHANGELOG.en.md',
     'Content is prepared in batches within the visible area; the card does not scroll and shows an ellipsis on overflow.',
     'Visible text is prepared in batches, including short rows with dense styles. A completed body can be reused when the same live preview briefly retracts and resumes without content or size changes; content, typography, zoom and display changes invalidate it. Inline syntax uses the note’s shared recognizer for nested emphasis, escaping and complex links. The card does not scroll and shows an ellipsis on overflow.')

save('tests/PaperTodo.EdgePreviewChecks/README.md', """# 边缘 Markdown 预览检查

测试实际使用的有界预览后端；不再包含完整 AvalonEdit 对照控件。架构边界见 `../../doc/ARCHITECTURE.md`。历史实验与负面结果保留在 #245 的讨论和提交 `abc314de` 的原实验目录，不作为应用中的可选后端。

```powershell
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Debug
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --profile
dotnet run --project tests/PaperTodo.EdgePreviewChecks -c Release -- --export output/edge-pixels
```

默认检查：四种显示档位、内容预算、首次一次发布与真实宿主取消、禁止滚动、链接回调、内容和缩放刷新、空状态、卸载/重挂；同一视图收起/恢复不重复准备，内容/宽度/主题/DPI 通知撤销复用；短密集文字按可见折行准备，普通短行保留简单路径。既有 MarkdownEditing 检查还验证字体、像素、取消、代码空行与准备结果复用。

DPI 检查调用实际通知边界，不模拟完整的多屏硬件环境。自动测试不能替代真实混合 DPI、鼠标捕获/跨胶囊手势或显卡最终呈现验证。

`--profile`：真实 Host/Presenter、固定 460×410 卡片、160ms 动画；每种输入每次进程 3 次预热、21 个新建视图样本。输出全部样本与中位数/最大值。指标包括尺寸计算、创建、挂载、布局就绪、应用动画回调间隔和 UI 线程分配。不是 GPU 呈现测量，分配不是常驻内存，21 样本不是线上 p95。重复悬停的复用资格由默认行为检查验证；新建视图探针不把这种复用当成冷启动收益。

比较版本须在同一 runner 使用相同探针与样本，按旧→新→新→旧交换顺序，保留负面结果。`--export` 输出四种档位、两种字体/清晰度、两档缩放、十个输入的 160 组原始像素；仅测试代码调用，不进入应用热路径。
""")

# Record changed source including deletions. This is an editing manifest, not a source-shape test.
import subprocess, json
paths=subprocess.check_output(['git','-C',str(root),'diff','--name-only'],encoding='utf-8').splitlines()
paths+=subprocess.check_output(['git','-C',str(root),'ls-files','--others','--exclude-standard'],encoding='utf-8').splitlines()
Path('results').mkdir(exist_ok=True)
Path('results/changed-paths.json').write_text(json.dumps(sorted(set(paths))),encoding='utf-8')
