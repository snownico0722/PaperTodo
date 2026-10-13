from pathlib import Path
import subprocess
import sys
import tempfile

MAIN = 'aa78745cdc5389ec012e4d7b3fb5173e90ad6d77'
COMMON = '3ba1240a457d5554d9580dfc07d292bf204b0701'
TEST = Path('tests/PaperTodo.MicaChecks/FormTransitionRenderingChecks.cs')
NATIVE = Path('src/NativeMicaBackdrop.cs')
SKIN = Path('src/SkinBorder.cs')

def replace(path, old, new, count=1):
    text = path.read_text(encoding='utf-8')
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f'{path}: expected {count} anchors, got {actual}: {old[:100]}')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

REGRESSIONS = r'''
    private static void CheckLiveHandoffRegressions(AppState state)
    {
        var saved = (state.PaperSkin, state.MatchAuxiliaryMaterialStrength);
        var failures = new List<string>();
        try
        {
            state.PaperSkin = PaperSkins.Aero;
            state.MatchAuxiliaryMaterialStrength = true;
            Theme.Invalidate();
            foreach (var scenario in new[] { "opaque-frame", "fresh-alpha-failure", "hidden-endpoint" })
            {
                var surface = new SkinBorder
                {
                    Background = Theme.PaperBrush, CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1)
                };
                var window = new Window
                {
                    Content = surface, Width = 320, Height = 240, Left = 20, Top = 20,
                    WindowStyle = WindowStyle.None, AllowsTransparency = false, ShowInTaskbar = false
                };
                var api = new NativeProbe();
                var eligible = true;
                using var backdrop = new NativeMicaBackdrop(window, () => surface, () => eligible,
                    brush => surface.Background = brush, api, tryLiveFormMorph: true);
                try
                {
                    backdrop.Refresh(true, false, NativeMicaBackdrop.AeroGlassMaterial);
                    window.Show();
                    Until(() => backdrop.IsActive && surface.ActualWidth > 0,
                        "review fixture starts on an alpha-aware native recipe");
                    Program.Pump();
                    surface.SetSampledBackgroundCaptureSuspended(true);
                    eligible = false;
                    if (scenario == "fresh-alpha-failure")
                    {
                        // The capsule's alpha installation succeeded, but the new native
                        // installation fails. Testing a second Refresh before admission
                        // would miss the actual admission-to-installation boundary.
                        backdrop.Refresh(true, false, force: true);
                        api.RedirectionSupported = false;
                        surface.SetFormTransition(true);
                        Program.Assert(!backdrop.TryBeginLiveFormMorph() &&
                            !backdrop.IsLiveFormMorph && !surface.IsLiveNativeMorph && !backdrop.IsActive,
                            "a failed NEW alpha installation must reject live morph and keep fallback");
                    }
                    else
                    {
                        surface.SetFormTransition(true);
                        Program.Assert(backdrop.TryBeginLiveFormMorph(), "review fixture enters live morph");
                        surface.IsCapsule = true;
                        surface.SetFormTransition(false);
                        backdrop.FinishLiveFormMorph(collapsed: true, visible: true);
                        if (scenario == "opaque-frame")
                        {
                            // Read the actual WPF paint, not just Background.A. A full
                            // Aero capsule normally transmits through its own material fill.
                            var size = new Size(surface.ActualWidth, surface.ActualHeight);
                            var staged = Render(surface, size, 1).At(size.Width / 2, size.Height / 2);
                            Program.Assert(staged.A == 255,
                                $"outgoing full-Aero handoff must record opaque pixels, actual alpha={staged.A}");
                            Until(() => !backdrop.IsLiveFormMorph && !backdrop.IsActive,
                                "opaque frame completes outgoing native handoff");
                            var resting = Render(surface, size, 1).At(size.Width / 2, size.Height / 2);
                            Program.Assert(resting.A < 255,
                                "temporary backing is removed so resting full Aero transmits again");
                        }
                        else
                        {
                            var completed = 0;
                            backdrop.StartFormAnimation(() => completed++);
                            var stale = surface.FormFrameRendered;
                            window.Hide();
                            Program.Assert(!backdrop.IsLiveFormMorph && !backdrop.IsActive &&
                                surface.FormFrameRendered == null && completed == 1,
                                "hidden endpoint must release native handoff and its capture continuation without paint");
                            stale?.Invoke();
                            Program.Pump();
                            window.Show();
                            Program.Pump();
                            Program.Assert(completed == 1 && !backdrop.IsLiveFormMorph && !backdrop.IsActive,
                                "a stale hidden-frame notification cannot replay after showing the capsule");
                        }
                    }
                    Console.WriteLine("LIVE_REVIEW_PASS " + scenario);
                }
                catch (Exception error)
                {
                    failures.Add(scenario + ": " + error.Message);
                    Console.WriteLine("LIVE_REVIEW_FAIL " + scenario + ": " + error.Message);
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            (state.PaperSkin, state.MatchAuxiliaryMaterialStrength) = saved;
            Theme.Invalidate();
        }
        Program.Assert(failures.Count == 0, string.Join("; ", failures));
    }

'''

REAL_HIDDEN = r'''
                // Complete the geometry, then hide BEFORE the outgoing WPF frame.
                // This is also how an ordinary paper leaves for its resting Edge host.
                frames = surface.BackgroundFrameCount;
                window.SetCollapsedState(true, animate: true, saveGeometry: false);
                window.SettleAnimationsForDisabledSetting();
                Program.Assert(!IsTransitioning(window) && native.IsLiveFormMorph,
                    "real fixture is at the pending native-to-capsule handoff");
                fixture.Paper.IsVisible = false;
                window.PrepareForHide();
                window.HideWithoutGeometrySave();
                Program.Pump();
                Program.Assert(!native.IsLiveFormMorph && !window.IsNativeMicaEffective &&
                    !surface.HasBackgroundCapture && surface.FormFrameRendered == null,
                    "hidden real endpoint has no stranded native handoff or capture");
                fixture.Paper.IsVisible = true;
                window.PrepareForShow();
                window.Show();
                AwaitSnapshot(fixture, frames, "show after hidden live endpoint");
'''

mode = sys.argv[1]
if mode == 'sync':
    subprocess.run(['git', 'merge-base', '--is-ancestor', COMMON, 'HEAD'], check=True)
    result = subprocess.run(['git', 'merge', '--no-commit', '--no-ff', MAIN])
    conflicts = subprocess.check_output(['git', 'diff', '--name-only', '--diff-filter=U'], text=True).splitlines()
    if result.returncode and not conflicts:
        raise RuntimeError('merge failed without resolvable text conflicts')
    # #346 was squash-merged. Resolve using its exact final unsquashed tree as
    # the semantic common base, not by selecting ours/theirs wholesale.
    for name in conflicts:
        if not (name.startswith('src/') or name.startswith('tests/') or name in
                ('CHANGELOG.md', 'CHANGELOG.zh.md', 'doc/ARCHITECTURE.md')):
            raise RuntimeError('unexpected merge conflict: ' + name)
        with tempfile.TemporaryDirectory() as directory:
            paths = []
            for label, ref in [('ours', 'HEAD'), ('common', COMMON), ('main', MAIN)]:
                file = Path(directory) / label
                file.write_bytes(subprocess.check_output(['git', 'show', f'{ref}:{name}']))
                paths.append(str(file))
            merged = subprocess.run(['git', 'merge-file', '-p', *paths], capture_output=True)
            if merged.returncode:
                raise RuntimeError('semantic merge needs review: ' + name)
            Path(name).write_bytes(merged.stdout)
            subprocess.run(['git', 'add', '--', name], check=True)
    print('Synchronized squash-merged #346 and main without rewriting PR history.')
elif mode == 'tests':
    replace(TEST, '            CheckContentViewport();',
        '            CheckLiveHandoffRegressions(state);\n            CheckContentViewport();')
    replace(TEST, '    private static void CheckContentViewport()',
        REGRESSIONS + '    private static void CheckContentViewport()')
elif mode == 'fix':
    replace(SKIN, 'internal bool FormFrameNeedsOpaque { get; set; } = true;',
        '// null: no prepared frame; true: explicit opaque backing; false: native alpha frame.\n'
        '    internal bool? FormFrameNeedsOpaque { get; set; }')
    replace(SKIN, '        RenderSurface(dc);',
        '        // A solid Background property is not proof of opaque paint: full Aero\n'
        '        // draws its own transmitted fill. Keep a real backing until the adapter\n'
        '        // commits/cancels this handoff, including repaints after OnRender.\n'
        '        if (FormFrameNeedsOpaque == true && !IsOutline && ActualWidth > 0 && ActualHeight > 0)\n'
        '        {\n'
        '            EnsureGeometry();\n'
        '            dc.DrawGeometry(Theme.PaperBrush, null, _shape);\n'
        '        }\n'
        '        RenderSurface(dc);')
    replace(NATIVE, '                surface.FormFrameNeedsOpaque = true;',
        '                surface.FormFrameRendered = null;\n'
        '                surface.FormFrameNeedsOpaque = null;\n'
        '                surface.InvalidateVisual();')
    replace(NATIVE, '        preparation.Surface.FormFrameNeedsOpaque = true;',
        '        preparation.Surface.FormFrameNeedsOpaque = null;\n'
        '        preparation.Surface.InvalidateVisual();')
    replace(NATIVE, '        if (!IsActive)\n        {\n            _liveFormMorph = false;',
        '        if (!IsActive || !_redirectionAlphaReady)\n        {\n            _liveFormMorph = false;')
    replace(NATIVE, '        if (_formPreparation is { } pending)\n        {',
        '        if (_formPreparation is { } pending)\n'
        '        {\n'
        '            if (pending.FinishLive && (!_window.IsVisible ||\n'
        '                _window.WindowState == WindowState.Minimized))\n'
        '            {\n'
        '                // The geometry is already at its endpoint. A hidden paper\n'
        '                // cannot publish that frame; release DWM now, then complete\n'
        '                // the existing capture continuation once (it checks generation).\n'
        '                var completed = pending.Start;\n'
        '                CancelLiveFormMorph();\n'
        '                Refresh(_requested, _dark, force: true);\n'
        '                completed?.Invoke();\n'
        '                return;\n'
        '            }')
    replace(TEST, 'backdrop.IsActive && surface.FormFrameNeedsOpaque &&',
        'backdrop.IsActive && surface.FormFrameNeedsOpaque == true &&')
    replace(TEST, '!surface.FormFrameNeedsOpaque, "expansion stages first native visual"',
        'surface.FormFrameNeedsOpaque == false, "expansion stages first native visual"')
    paper_test = Path('tests/PaperTodo.MicaChecks/PaperFormMaterialChecks.cs')
    replace(paper_test,
        '                    "real native morph traversed intermediate frames, then released its temporary state");',
        '                    "real native morph traversed intermediate frames, then released its temporary state");\n' + REAL_HIDDEN)
    architecture = Path('doc/ARCHITECTURE.md')
    replace(architecture, '收起到胶囊端点后才在不透明新帧交接原生背景',
        '收起到胶囊端点后先绘制明确的不透明背衬，再交接原生背景并撤掉临时背衬；'
        '端点已隐藏或最小化时直接完成材质释放和采样恢复，不等待不可见帧')
else:
    raise RuntimeError('unknown mode: ' + mode)
