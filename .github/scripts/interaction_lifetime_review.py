from pathlib import Path
import os

def edit(path, before, after):
    p = Path(path)
    s = p.read_text(encoding='utf-8-sig')
    assert s.count(before) == 1, (path, before[:80], s.count(before))
    p.write_text(s.replace(before, after), encoding='utf-8', newline='\n')

edit('tests/PaperTodo.LifecycleChecks/Program.cs', '"real-exit", "early-expand"',
     '"lifetime-body-visibility", "lifetime-body-failure", "lifetime-mini-reset", "lifetime-reminder-flash", "real-exit", "early-expand"')
edit('tests/PaperTodo.LifecycleChecks/Program.cs', '''            if (name == "missing-monitor")
''', '''            if (name.StartsWith("lifetime-", StringComparison.Ordinal))
            {
                await InteractionLifetimeChecks.Run(name, windows.Values.First());
                return;
            }
            if (name == "missing-monitor")
''')
if os.environ.get('REVIEW_FIXED') != 'true':
    raise SystemExit(0)

edit('src/PaperWindow.PluginBodies.cs', '''        var failure = _paperBodyHost.Invoke(callback);
''', '''        var session = _paperBodyHost.Current;
        var generation = _bodySessionGeneration;
        var failure = _paperBodyHost.Invoke(callback);
''')
edit('src/PaperWindow.PluginBodies.cs', '''            if (!disableOnFailure ||
                _windowLifecycle != PaperWindowLifecycleState.Alive)
''', '''            if (!disableOnFailure ||
                _windowLifecycle != PaperWindowLifecycleState.Alive ||
                generation != _bodySessionGeneration ||
                !ReferenceEquals(session, _paperBodyHost.Current))
''')
edit('src/PaperWindow.PluginBodies.cs', '''            item.OnPresentationChanged(visible);
            item.OnVisibilityChanged(runtimeVisible);
''', '''            item.OnPresentationChanged(visible);
            // Plugin callbacks can synchronously hide, reload or replace their own body. The
            // second notification must not resurrect an obsolete state or call a retired session.
            if (ReferenceEquals(item, _paperBodyHost.Current) &&
                _bodyRuntimeVisible == runtimeVisible)
            {
                item.OnVisibilityChanged(runtimeVisible);
            }
''')
p = Path('src/PaperWindow.PluginMiniView.cs')
s = p.read_text(encoding='utf-8-sig')
a = s.index('    private void ResetPluginMiniViewCache()')
b = s.index('    private EdgeCapsulePreviewDescriptor DescribePluginCapsuleFallback', a)
s = s[:a] + '''    private void ResetPluginMiniViewCache()
    {
        var visibleProvider = _pluginMiniViewVisible ? _pluginMiniViewProvider : null;
        // Revoke this cache before notifying foreign code. A callback may close/reload the body
        // or establish a replacement mini; it must not recursively retire the same generation.
        _pluginMiniViewGeneration = -1;
        _pluginMiniViewProvider = null;
        _pluginMiniView = null;
        _pluginMiniViewSize = default;
        _pluginMiniViewAttempted = false;
        _pluginMiniViewActive = false;
        _pluginMiniViewVisible = false;
        try
        {
            visibleProvider?.OnMiniViewVisibilityChanged(false);
        }
        catch
        {
            // Optional mini cleanup cannot block body retirement.
        }
    }

''' + s[b:]
p.write_text(s, encoding='utf-8', newline='\n')
edit('src/AnimationHelper.cs', '''        var originalBg = element.Background;
        var highlightBrush = new SolidColorBrush(Colors.Transparent);
        element.Background = highlightBrush;
''', '''        var highlightBrush = new SolidColorBrush(Colors.Transparent);
''')
edit('src/AnimationHelper.cs', '''        flashAnim.Completed += (s, e) => element.Background = originalBg;

        highlightBrush.BeginAnimation(SolidColorBrush.ColorProperty, flashAnim);
''', '''        // The flash owns an animation, not the base brush/binding. WPF replaces an older
        // flash clock and restores the CURRENT base at completion, including intervening themes.
        var flashBackground = new ObjectAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(duration * 2),
            FillBehavior = FillBehavior.Stop
        };
        flashBackground.KeyFrames.Add(new DiscreteObjectKeyFrame(highlightBrush, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        element.BeginAnimation(Border.BackgroundProperty, flashBackground);
        highlightBrush.BeginAnimation(SolidColorBrush.ColorProperty, flashAnim);
''')
