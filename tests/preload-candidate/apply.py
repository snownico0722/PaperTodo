from pathlib import Path
import shutil

def edit(path, old, new):
    p=Path(path); s=p.read_text(encoding='utf-8-sig')
    assert s.count(old)==1,(path,old[:100],s.count(old))
    p.write_text(s.replace(old,new),encoding='utf-8',newline='\n')

for name in ['EdgeCapsulePreview.Preload.cs','PaperWindow.EdgePreviewPreload.cs']:
    shutil.copyfile('tests/preload-candidate/'+name+'.txt','src/'+name)
shutil.copyfile('tests/preload-candidate/PreloadChecks.cs.txt','tests/PaperTodo.EdgePreviewChecks/PreloadChecks.cs')
edit('src/EdgeCapsulePreview.cs','    public void Invalidate() => Invalidated?.Invoke();', '''    private long _version;
    internal long Version => Interlocked.Read(ref _version);
    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        Invalidated?.Invoke();
    }''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        var content = MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
            context.ReadMarkdownText(), context.ReadMarkdownRenderMode());''', '''        var content = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher).Capture(context);''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''    internal void SetPreviewActive(bool active) => _viewport.SetPreviewActive(active);''', '''    internal void SetPreviewActive(bool active) => _viewport.SetPreviewActive(active);
    internal MarkdownEdgeCapsulePreviewViewport PreloadViewport => _viewport;''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        var content = _initialContent ?? MarkdownEdgeCapsulePreviewRenderer.CaptureContent(
            Context.ReadMarkdownText(), Context.ReadMarkdownRenderMode());''', '''        var content = _initialContent ?? MarkdownEdgePreviewPreload.For(Dispatcher).Capture(Context);''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''            target, content, Context.OpenExternal, size, textZoom));''', '''            target, content, Context.OpenExternal, size, textZoom),
            MarkdownEdgePreviewPreload.For(Dispatcher).Bind(Context, content, textZoom));''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''    private long _renderVersion;''', '''    private long _renderVersion;
    private MarkdownEdgePreviewPreload.Binding? _preloadBinding;
    private MarkdownEdgePreviewPreload.Key? _publishedKey;
    internal Func<bool>? PreloadStillCurrent { get; set; }
    internal event Action<bool>? PreparationFinished;''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        Unloaded += (_, _) => InvalidateContentBuild();''', '''        Unloaded += (_, _) => { ReturnBodyToPreload(); InvalidateContentBuild(); };''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''    public void SetContent(Func<Panel, Size, IEnumerable<bool>> renderContent)
    {
        _renderContent = renderContent;
        InvalidateContentBuild();
    }''', '''    public void SetContent(Func<Panel, Size, IEnumerable<bool>> renderContent,
        MarkdownEdgePreviewPreload.Binding? preloadBinding = null)
    {
        _renderContent = renderContent;
        _preloadBinding = preloadBinding;
        InvalidateContentBuild();
    }

    internal bool ReturnBodyToPreload()
    {
        if (_publishedKey is not { } key || _publishedSize == null || !key.Binding.Current) return false;
        var body = _body;
        Children.Remove(body);
        body.Clip = null;
        body.IsHitTestVisible = false;
        _body = new StackPanel { Clip = _bodyClip };
        Children.Add(_body);
        key.Binding.Owner.Store(new(key, body, _sourceTruncated));
        _sourceTruncated = false;
        _publishedKey = null;
        _publishedSize = _renderedSize = null;
        _renderVersion++;
        Opacity = 0;
        IsHitTestVisible = false;
        return true;
    }''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        _publishedSize = null;
        CancelPendingBuild();''', '''        _publishedSize = null;
        _publishedKey = null;
        CancelPendingBuild();''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        !Dispatcher.HasShutdownStarted;''', '''        !Dispatcher.HasShutdownStarted && _preloadBinding?.Current != false &&
        PreloadStillCurrent?.Invoke() != false;''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflow''', '''    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_renderedSize != finalSize && _previewActive && IsLoaded && IsVisible &&
            finalSize.Width > 0 && finalSize.Height > 0 && PreloadStillCurrent?.Invoke() != false &&
            MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, finalSize) is { } cachedKey &&
            cachedKey.Binding.Owner.TryTake(cachedKey, out var cached, PreloadStillCurrent == null))
        {
            Children.Remove(_body);
            _body = cached!.Panel;
            Children.Add(_body);
            _body.Clip = _bodyClip;
            _body.Opacity = 1;
            _body.IsHitTestVisible = true;
            _body.Measure(new Size(finalSize.Width, double.PositiveInfinity));
            _sourceTruncated = cached.Truncated;
            _publishedSize = _renderedSize = finalSize;
            _publishedKey = cachedKey;
            _renderVersion++;
            Opacity = 1;
            IsHitTestVisible = true;
            PreparationFinished?.Invoke(true);
        }
        var overflow''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''        var published = false;
        try''', '''        var published = false;
        var preparedKey = MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, size);
        try''')
p=Path('src/EdgeCapsulePreview.Markdown.cs');s=p.read_text();assert s.count('await Dispatcher.Yield(DispatcherPriority.Background);')==2
s=s.replace('await Dispatcher.Yield(DispatcherPriority.Background);','await Dispatcher.Yield(PreloadStillCurrent == null\n                ? DispatcherPriority.Background : DispatcherPriority.ContextIdle);')
p.write_text(s,encoding='utf-8',newline='\n')
edit('src/EdgeCapsulePreview.Markdown.cs', '''            _publishedSize = size;
            Opacity = 1;''', '''            _publishedSize = size;
            // A resource/DPI change during preparation makes this result non-cacheable.
            _publishedKey = preparedKey == MarkdownEdgePreviewPreload.MakeKey(_preloadBinding, this, size)
                ? preparedKey : null;
            Opacity = 1;''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''                Children.Remove(staging);
            }
        }
    }
}''', '''                Children.Remove(staging);
            }
            PreparationFinished?.Invoke(published);
        }
    }
}''')
edit('src/EdgeCapsulePreview.Markdown.cs', '''    public static string MeasureText(PreviewContent content)''', '''    internal static IEnumerable<bool> WarmInlineSteps(PreviewContent content)
    {
        foreach (var line in content.Lines)
        {
            if (content.RenderMode == MarkdownRenderModes.Off || line.WasInsideFence ||
                line.FenceKind != MarkdownFenceLineKind.None) { yield return false; continue; }
            var raw = content.RenderMode == MarkdownRenderModes.Full ? line.Text.Trim() : line.Text;
            var trimmed = raw.TrimStart();
            var prefix = raw.Length - trimmed.Length;
            var heading = HeadingPattern.Match(trimmed);
            var task = TaskListPattern.Match(trimmed);
            var ordered = OrderedListPattern.Match(trimmed);
            var unordered = UnorderedListPattern.Match(trimmed);
            if (heading.Success) prefix += heading.Groups[2].Index;
            else if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                prefix++;
                if (content.RenderMode == MarkdownRenderModes.Full)
                    while (prefix < raw.Length && char.IsWhiteSpace(raw[prefix])) prefix++;
            }
            else if (task.Success || ordered.Success) prefix += (task.Success ? task : ordered).Groups[2].Index;
            else if (unordered.Success) prefix += unordered.Groups[1].Index;
            if (!HorizontalRulePattern.IsMatch(trimmed)) content.Inlines.Get(raw[prefix..], content.RenderMode);
            if (content.RenderMode == MarkdownRenderModes.Full)
                content.Inlines.Get(StripBlockPrefix(raw), MarkdownRenderModes.Full);
            yield return false;
        }
    }

    public static string MeasureText(PreviewContent content)''')
edit('src/EdgeCapsuleHost.Preview.cs', '''    public bool HasPreviewContent => !_disposed && _previewContent != null;''', '''    public bool HasPreviewContent => !_disposed && _previewContent != null;
    internal Panel? MarkdownPreloadAnchor => !_disposed && IsVisible ? ContentHost : null;''')
edit('src/PaperWindow.EdgeCapsulePreviewContent.cs', '''    private void InvalidateEdgeCapsulePreviewContent() =>
        _edgeCapsulePreviewInvalidationSource.Invalidate();''', '''    private void InvalidateEdgeCapsulePreviewContent()
    {
        _edgeCapsulePreviewInvalidationSource.Invalidate();
        ScheduleMarkdownPreviewPreload();
    }''')
edit('src/PaperWindow.EdgeCapsulePreview.cs', '''        var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();''', '''        MarkdownEdgePreviewPreload.For(Dispatcher).BeginDemand();
        var prepareStartedAt = EdgeCapsulePerformanceDiagnostics.Timestamp();''')
edit('src/PaperWindow.EdgeCapsulePreview.cs', '''            content.HorizontalAlignment = HorizontalAlignment.Stretch;''', '''            if (content is MarkdownEdgeCapsulePreviewView markdownView)
            {
                void Prepared(bool success)
                {
                    markdownView.PreloadViewport.PreparationFinished -= Prepared;
                    if (success && _windowLifecycle == PaperWindowLifecycleState.Alive)
                        _controller.ScheduleMarkdownPreviewNeighbors(this);
                }
                markdownView.PreloadViewport.PreparationFinished += Prepared;
            }
            content.HorizontalAlignment = HorizontalAlignment.Stretch;''')
edit('src/AppController.EdgeCapsulePreview.cs', '''        if (!pointerOver)
''', '''        if (pointerOver) ScheduleMarkdownPreviewNeighbors(window);

        if (!pointerOver)
''')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs', '''            if (args.Contains("--profile")) Profile();''', '''            if (args.Contains("--preload-profile")) ProfilePreload(args.Contains("--reverse"));
            else if (args.Contains("--preload-memory")) PreloadMemory();
            else if (args.Contains("--profile")) Profile();''')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs', '''else { SharedPreviewSemanticChecks.Run(); Checks(); }''', '''else { SharedPreviewSemanticChecks.Run(); Checks(); PreloadChecks(); }''')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs', '''    private static double[] ProfileOne(string text, string mode)''', '''    private static double[] ProfileOne(string text, string mode, string preparation = "cold")''')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs', '''        var allocation = GC.GetAllocatedBytesForCurrentThread();''', '''        var preload = MarkdownEdgePreviewPreload.For(dispatcher);
        preload.SetEnabledForChecks(preparation != "cold");
        var warmStarted = Stopwatch.GetTimestamp();
        if (preparation == "text")
            foreach (var step in MarkdownEdgeCapsulePreviewRenderer.WarmInlineSteps(preload.Capture(context))) { }
        if (preparation == "layout")
            Require(AwaitPreload(preload.WarmLayoutAsync(new(context, host.MarkdownPreloadAnchor!, fixedSize, () => true))),
                "profile prelayout completes on the real host without opening it");
        var warmMs = preparation == "cold" ? 0 : Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
        var hitsBefore = preload.BodyHits;
        var allocation = GC.GetAllocatedBytesForCurrentThread();''')
edit('tests/PaperTodo.EdgePreviewChecks/Program.cs', '''(GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024.0 };''', '''(GC.GetAllocatedBytesForCurrentThread() - allocation) / 1024.0, warmMs, preload.BodyHits - hitsBefore };''')
