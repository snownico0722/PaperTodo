from pathlib import Path
import startup_intent_review
from interaction_lifetime_review import edit

# A built-in test provider shares its instance limit with all five isolated fixture notes.
# Permit another instance so deletion tests exercise startup, not an unrelated quota rejection.
edit('tests/PaperTodo.LifecycleChecks/StartupDeferredIntentChecks.cs',
     '        var manifest = new PaperBodyPluginManifest\n        {',
     '        var manifest = new PaperBodyPluginManifest\n        {\n            MaxPaperInstances = 0,')
edit('tests/PaperTodo.LifecycleChecks/Program.cs', '"lifetime-mini-reset", "lifetime-reminder-flash"',
     '"lifetime-mini-reset", "lifetime-mini-create", "lifetime-reminder-flash"')
edit('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs',
     '        var original = host.Current;',
     '        var original = host.Current;\n        var originalDescriptor = (PaperBodyPluginDescriptor)Part(window, "_bodyDescriptor");')
edit('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs',
     '            else if (name == "lifetime-mini-reset")', '''            else if (name == "lifetime-mini-create")
            {
                Set(window, "_bodyDescriptor", originalDescriptor with { Kind = PaperBodyPluginKind.Native });
                var context = new EdgeCapsulePreviewContext(paper, () => "lifetime", false,
                    () => "text", () => MarkdownRenderModes.Full, (_, _) => false, _ => false,
                    () => new Style(), () => "", _ => { }, new());
                var size = new EdgeCapsulePreviewSize(240, 180);
                var descriptor = window.DescribePluginEdgeCapsulePreview(context);
                var replacement = new BodyProbe();
                host.CommitCancelDispose(false);
                host.Attach(replacement);
                descriptor.CreateContent(size);
                Require(probe.MiniCreates == 0,
                    "MINI_STALE_CREATE: an old preview descriptor called a retired factory");

                host.CommitCancelDispose(false);
                probe = new BodyProbe();
                host.Attach(probe);
                ResetMini(window);
                var staleView = new Border();
                var replacementView = new Border();
                probe.MiniFactory = () =>
                {
                    ResetMini(window);
                    host.CommitCancelDispose(false);
                    host.Attach(replacement);
                    Set(window, "_pluginMiniViewProvider", replacement);
                    Set(window, "_pluginMiniView", replacementView);
                    return staleView;
                };
                var result = window.DescribePluginEdgeCapsulePreview(context).CreateContent(size);
                Require(!ReferenceEquals(result, staleView) &&
                    ReferenceEquals(Part(window, "_pluginMiniViewProvider"), replacement) &&
                    ReferenceEquals(Part(window, "_pluginMiniView"), replacementView),
                    "MINI_STALE_CREATE: a factory's late return overwrote the replacement preview");
            }
            else if (name == "lifetime-mini-reset")''')
edit('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs',
     '            probe.MiniVisibility = null;',
     '            probe.MiniVisibility = null;\n            probe.MiniFactory = null;\n            Set(window, "_bodyDescriptor", originalDescriptor);')
edit('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs',
     '        public int Disposals;',
     '        public int Disposals, MiniCreates;\n        public Func<FrameworkElement>? MiniFactory;')
edit('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs',
     '        public FrameworkElement CreateMiniView(PaperMiniViewContext context) => new Border();',
     '        public FrameworkElement CreateMiniView(PaperMiniViewContext context) { MiniCreates++; return MiniFactory?.Invoke() ?? new Border(); }')

# An object-keyframe brush is frozen by the animation clock; independently animating the original
# brush was invisible. A small single-clock brush animation owns only the transient DP value.
p = Path('src/AnimationHelper.cs')
s = p.read_text(encoding='utf-8-sig')
a = s.index('    // 闪烁高亮')
s = s[:a] + '''    // 提醒高亮只拥有动画值，不修改背景的基础值或绑定。
    public static void FlashHighlight(Border element, Color highlightColor, double duration = 120)
    {
        element.BeginAnimation(Border.BackgroundProperty, new FlashBrushAnimation(highlightColor)
        {
            Duration = TimeSpan.FromMilliseconds(duration),
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop
        });
    }

    private sealed class FlashBrushAnimation(Color color) : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(Brush);
        public override bool IsDestinationDefault => false;
        protected override Freezable CreateInstanceCore() => new FlashBrushAnimation(color);
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
        {
            if (clock.CurrentState == ClockState.Stopped) return defaultDestinationValue;
            var progress = QuickEase.Ease(clock.CurrentProgress ?? 0);
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)(color.A * 0.4 * progress), color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
'''
p.write_text(s, encoding='utf-8', newline='\n')

p = Path('src/PaperWindow.PluginMiniView.cs')
s = p.read_text(encoding='utf-8-sig')
s = s.replace('            var preferred = ReadPreferredMiniSize(',
              '            var generation = _bodySessionGeneration;\n            var preferred = ReadPreferredMiniSize(', 1)
s = s.replace('                    nativeProvider,\n                    context,\n                    size),',
              '                    nativeProvider,\n                    generation,\n                    context,\n                    size),', 1)
s = s.replace('                    nativeProvider,\n                    visible));',
              '                    nativeProvider,\n                    generation,\n                    visible));', 1)
s = s.replace('        IPaperMiniViewProvider provider,\n        EdgeCapsulePreviewContext context,',
              '        IPaperMiniViewProvider provider,\n        int generation,\n        EdgeCapsulePreviewContext context,', 1)
s = s.replace('''        EnsurePluginMiniViewGeneration(provider, size);
        if (_pluginMiniViewAttempted)''', '''        if (!EnsurePluginMiniViewGeneration(provider, generation, size))
        {
            return BuildPluginCapsuleEdgePreviewContent(context, size);
        }
        bool OwnsMiniRequest() => generation == _bodySessionGeneration &&
            ReferenceEquals(provider, _paperBodyHost.Current) &&
            ReferenceEquals(provider, _pluginMiniViewProvider) &&
            _pluginMiniViewGeneration == generation && _pluginMiniViewSize == size;
        if (_pluginMiniViewAttempted)''', 1)
s = s.replace('''                CurrentPaperBodyTheme()));
            if (view == null ||''', '''                CurrentPaperBodyTheme()));
            // A factory can synchronously replace its body; never publish the old result.
            if (!OwnsMiniRequest()) return BuildPluginCapsuleEdgePreviewContent(context, size);
            if (view == null ||''', 1)
s = s.replace('''        catch
        {
            _pluginMiniView = null;
            _pluginMiniViewActive = false;
            return BuildPluginCapsuleEdgePreviewContent(context, size);
        }
    }

    private void EnsurePluginMiniViewGeneration(
        IPaperMiniViewProvider provider,
        EdgeCapsulePreviewSize size)
    {
''', '''        catch
        {
            if (OwnsMiniRequest())
            {
                _pluginMiniView = null;
                _pluginMiniViewActive = false;
            }
            return BuildPluginCapsuleEdgePreviewContent(context, size);
        }
    }

    private bool EnsurePluginMiniViewGeneration(
        IPaperMiniViewProvider provider,
        int generation,
        EdgeCapsulePreviewSize size)
    {
        if (generation != _bodySessionGeneration || !ReferenceEquals(provider, _paperBodyHost.Current))
            return false;
''', 1)
s = s.replace('''        {
            return;
        }

        ResetPluginMiniViewCache();
        _pluginMiniViewGeneration = _bodySessionGeneration;
        _pluginMiniViewProvider = provider;
        _pluginMiniViewSize = size;
    }

    private void NotifyNativePluginMiniViewVisibility(
        IPaperMiniViewProvider provider,
        bool visible)
    {
        if (!_pluginMiniViewActive ||''', '''        {
            return true;
        }

        ResetPluginMiniViewCache();
        if (generation != _bodySessionGeneration || !ReferenceEquals(provider, _paperBodyHost.Current) ||
            _pluginMiniViewProvider != null)
            return false;
        _pluginMiniViewGeneration = generation;
        _pluginMiniViewProvider = provider;
        _pluginMiniViewSize = size;
        return true;
    }

    private void NotifyNativePluginMiniViewVisibility(
        IPaperMiniViewProvider provider,
        int generation,
        bool visible)
    {
        if (generation != _bodySessionGeneration || !ReferenceEquals(provider, _paperBodyHost.Current) ||
            !_pluginMiniViewActive ||''', 1)
p.write_text(s, encoding='utf-8', newline='\n')
