from pathlib import Path

def edit(path, old, new):
    p = Path(path)
    text = p.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, (path, old[:80], text.count(old))
    p.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

core = 'src/EdgeCapsulePreview.Preload.cs'
edit(core, '            NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,',
'''            NoteTypography.FontWeight, NoteTypography.FontStyle, NoteTypography.FontStretch,
            NoteTypography.Language.IetfLanguageTag, NoteTypography.HeadingFontWeight, AppTypography.TextFormattingMode,''')
edit(core, '    internal void Store(Body body)', '    internal bool Store(Body body)')
edit(core, '            !ReferenceEquals(current.Content, body.Key.Binding.Content)) return;',
     '            !ReferenceEquals(current.Content, body.Key.Binding.Content)) return false;')
edit(core, '        while (_bodies.Count > MaximumBodies) _bodies.RemoveLast();',
'''        while (_bodies.Count > MaximumBodies) _bodies.RemoveLast();
        return true;''')
edit(core, '        viewport.PreloadStillCurrent = Current;', '''        viewport.PreloadStillCurrent = Current;
        void Abandoned(object? sender, EventArgs args)
        {
            if (!Current()) complete.TrySetResult(false);
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Abandoned(sender, EventArgs.Empty);
        void Invalidated() => complete.TrySetResult(false);
        target.Anchor.Unloaded += Abandoned;
        target.Anchor.IsVisibleChanged += VisibilityChanged;
        target.Context.InvalidationSource.Invalidated += Invalidated;''')
edit(core, '            viewport.PreparationFinished -= Finished;', '''            target.Anchor.Unloaded -= Abandoned;
            target.Anchor.IsVisibleChanged -= VisibilityChanged;
            target.Context.InvalidationSource.Invalidated -= Invalidated;
            viewport.PreparationFinished -= Finished;''')
md='src/EdgeCapsulePreview.Markdown.cs'
edit(md,'        key.Binding.Owner.Store(new(key, body, _sourceTruncated));',
     '        var retained = key.Binding.Owner.Store(new(key, body, _sourceTruncated));')
edit(md,'''        IsHitTestVisible = false;
        return true;
    }

    internal void SetPreviewActive''', '''        IsHitTestVisible = false;
        return retained;
    }

    internal void SetPreviewActive''')
edit(md, '''    // It is owned by this live view only; no per-note/global control or drawing cache exists.''',
'''    // Published bodies stay view-owned while mounted. On detach the optional bounded preload
    // cache may take exclusive ownership; no body can belong to two live trees.''')
edit(md, '''        var content = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher).Capture(context);''',
'''        var initialVersion = context.InvalidationSource.Version;
        var content = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher).Capture(context);''')
edit(md, 'size => view = new MarkdownEdgeCapsulePreviewView(context, size, content),',
     'size => view = new MarkdownEdgeCapsulePreviewView(context, size, content, initialVersion),')
edit(md, '''    private MarkdownEdgeCapsulePreviewRenderer.PreviewContent? _initialContent;''',
'''    private MarkdownEdgeCapsulePreviewRenderer.PreviewContent? _initialContent;
    private readonly long _initialVersion;''')
edit(md, '''        MarkdownEdgeCapsulePreviewRenderer.PreviewContent initialContent)
        : base(context, size)''',
'''        MarkdownEdgeCapsulePreviewRenderer.PreviewContent initialContent,
        long initialVersion = 0)
        : base(context, size)''')
edit(md, '        _initialContent = initialContent;',
'''        _initialContent = initialContent;
        _initialVersion = initialVersion;''')
edit(md, '''        var content = _initialContent ?? MarkdownEdgePreviewPreload.For(Dispatcher).Capture(Context);''',
'''        var content = _initialContent != null && _initialVersion == Context.InvalidationSource.Version
            ? _initialContent : MarkdownEdgePreviewPreload.For(Dispatcher).Capture(Context);''')
test='tests/PaperTodo.EdgePreviewChecks/PreloadChecks.cs'
edit(test, 'using System.Windows.Media;','using System.Windows.Media;\nusing System.Windows.Media.Imaging;')
edit(test, '''            Console.WriteLine("PASS preload A-B-A and detached return: three demand hits");''', '''            Console.WriteLine("PASS preload A-B-A and detached return: three demand hits");
            var pixelFixtures = new[] {
                "plain **strong** *italic* `code` [link](https://example.com)",
                string.Concat(Enumerable.Repeat("**粗体** ~~删除~~ `code` [链接](https://example.com) 文 ", 80)),
                "## heading\\n- [x] **done**\\n> quote *text*\\n```\\n\\ncode\\n```"
            };
            var pixelCases = 0;
            var exactCases = 0;
            var maximumDifference = 0;
            byte[] Pixels(FrameworkElement element)
            {
                var dpi = VisualTreeHelper.GetDpi(element);
                int width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
                int height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
                var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var bytes = new byte[width * height * 4];
                bitmap.CopyPixels(bytes, width * 4, 0);
                return bytes;
            }
            foreach (var testMode in new[] { MarkdownRenderModes.Off, MarkdownRenderModes.Basic, MarkdownRenderModes.Enhanced, MarkdownRenderModes.Full })
            foreach (var zoom in new[] { 0.7, 1.3 })
            foreach (var fixture in pixelFixtures)
            {
                mode = testMode; text = fixture; cache.Clear();
                var context = Context(new()); context.Paper.TextZoom = zoom;
                Require(Warm(context), "pixel reference is actually preloaded");
                var previousHits = cache.BodyHits;
                var hot = Demand(context); var hotPixels = Pixels(hot); Release(hot);
                Require(cache.BodyHits == previousHits + 1, "pixel comparison traverses the cached-body path");
                cache.Clear();
                var cold = Demand(context); var coldPixels = Pixels(cold); Release(cold);
                Require(hotPixels.Length == coldPixels.Length, "cache preserves pixel dimensions");
                var differences = hotPixels.Zip(coldPixels).Select(pair => Math.Abs(pair.First - pair.Second)).ToArray();
                maximumDifference = Math.Max(maximumDifference, differences.Max());
                if (hotPixels.SequenceEqual(coldPixels)) exactCases++;
                Require(differences.All(delta => delta <= 32), "cache preserves visible text/decoration pixels");
                pixelCases++;
            }
            Console.WriteLine($"PRELOAD_PIXELS cases={pixelCases} exact={exactCases} maximumChannelDifference={maximumDifference}");
            mode = MarkdownRenderModes.Full;
            cache.Clear();
            var descriptorBeforeEdit = MarkdownEdgeCapsulePreviewProvider.Instance.Describe(a);
            text = "edited between describe and mount";
            source.Invalidate();
            var editedView = descriptorBeforeEdit.CreateContent(size);
            var editedBorder = new Border { Width = size.WidthDip - 22, Height = size.HeightDip, Child = editedView };
            root.Children.Add(editedBorder);
            ((EdgeCapsuleLivePreviewView)editedView).PrepareForFirstDisplay();
            Pump();
            Require(PreviewText(editedView).Contains(text), "deferred first display never binds an old excerpt to the new version");
            Release(editedBorder);
            var binding = cache.Bind(a, cache.Capture(a), a.Paper.TextZoom)!;
            var key = MarkdownEdgePreviewPreload.MakeKey(binding, root, new Size(200, 100))!;
            Require(cache.Store(new(key, new StackPanel(), false)), "cache key fixture retained");
            Require(!cache.TryTake(key with { Dpi = new DpiScale(key.Dpi.DpiScaleX * 1.5, key.Dpi.DpiScaleY * 1.5) }, out _), "different DPI cannot reuse old drawing");
            Require(cache.TryTake(key, out _, demand: false), "matching DPI key remains usable");
            Console.WriteLine("PASS first-display generation and DPI-key rejection");''')
