from pathlib import Path
import sys
root=Path(sys.argv[1]); mode=sys.argv[2]

def replace(path, old, new, n=1):
 p=root/path; s=p.read_text(encoding='utf-8-sig')
 if s.count(old)!=n: raise SystemExit(f'{path}: expected {n}, got {s.count(old)}: {old[:100]!r}')
 p.write_text(s.replace(old,new),encoding='utf-8',newline='\n')

def scoped_priority(path, declaration, next_declaration):
 p=root/path;s=p.read_text(encoding='utf-8-sig'); start=s.index(declaration);end=s.index(next_declaration,start+1)
 section=s[start:end]
 if section.count('DispatcherPriority.ApplicationIdle')!=1: raise SystemExit('priority anchor '+path)
 section=section.replace('DispatcherPriority.ApplicationIdle','DispatcherPriority.SystemIdle')
 p.write_text(s[:start]+section+s[end:],encoding='utf-8',newline='\n')

if mode in ('preview','combined'):
 p='src/EdgeCapsulePreview.Preload.cs'
 replace(p,'    private CancellationTokenSource? _work;', '    private Task _drainTask = Task.CompletedTask;\n    private CancellationTokenSource? _work;')
 replace(p,'_debounce.Interval = TimeSpan.FromMilliseconds(500); Drain();', '_debounce.Interval = TimeSpan.FromMilliseconds(500); _ = DrainPendingAsync();')
 replace(p,'''    internal void StartStartupWork()
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted || RunnableCount == 0) return;
        // Restoration supplies a stable batch, not a keystroke stream. Reuse the same one-shot
        // timer/owner but do not impose the editing debounce on the first startup batch.
        _debounce.Stop();
        _debounce.Interval = TimeSpan.Zero;
        _debounce.Start();
    }''', '''    internal Task StartStartupWork()
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted || RunnableCount == 0) return Task.CompletedTask;
        // The first stable batch uses the normal renderer/drain, without the editing debounce.
        // Await only this pass: a user edit can cancel it and retain its ordinary 500ms delay.
        _debounce.Stop();
        return DrainPendingAsync();
    }

    private Task DrainPendingAsync()
    {
        if (!_drainTask.IsCompleted) return _drainTask;
        return _drainTask = DrainAsync();
    }''')
 replace(p,'    private async void Drain()', '    private async Task DrainAsync()')
 p='src/AppController.StartupPrewarm.cs'
 replace(p,'        try\n        {\n            while (pending.Count', '''        try
        {
            // Collapsed notes already have their edge host and model. Prepare their preview
            // before paying the optional editor/Shell first-use cost; demand can still build
            // one Shell immediately through EnsureShellBuilt.
            if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
                await MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();
            while (pending.Count''')
 replace(p,'                }, DispatcherPriority.ApplicationIdle);', '                }, startPreviewPreload ? DispatcherPriority.SystemIdle : DispatcherPriority.ApplicationIdle);')
 replace(p,'''        // Only the startup batch bypasses the editor debounce. A runtime show/restore must not
        // shorten another note's typing coalescing window. The existing cache still owns the drain.
        if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
            MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();
''','')
 p='src/PaperWindow.Capsule.cs'
 replace(p,'''    private void RefreshCapsuleLabel()
    {
        InvalidateEdgeCapsulePreviewContent();''','''    private void RefreshCapsuleLabel(bool invalidatePreview = true)
    {
        if (invalidatePreview) InvalidateEdgeCapsulePreviewContent();''')
 replace(p,'''        RefreshCapsuleLabel();
        leftStack.Children.Add(_capsuleLabelText);''','''        // Materializing the first Shell is not a model edit. Preserve an existing edge
        // artifact when the Markdown editor loaded the same text; normalization still invalidates.
        RefreshCapsuleLabel(invalidatePreview:
            !IsCurrentBodyProviderMarkdown ||
            !string.Equals(CurrentMarkdownTextForEdgeCapsulePreview(), _paper.Content ?? string.Empty,
                StringComparison.Ordinal));
        leftStack.Children.Add(_capsuleLabelText);''')
 p='src/AppController.cs'
 replace(p,'''        // Shell construction can invalidate preview resources. Queue every reader now, but
        // release the startup debounce only after those shells finish, not just before they reset it.''','''        // Preview artifacts use the existing edge host and model; their first pass precedes
        // optional collapsed Shell/editor construction rather than waiting behind it.''')

if mode in ('idle','combined'):
 scoped_priority('src/PaperWindow.EdgeCapsulePreview.cs','    private void ScheduleEdgeCapsuleCompositionPrewarm()', '    internal void RefreshEdgeCapsuleHoverIntentSettings()')
 replace('src/App.EdgeCapsuleComposition.cs','DispatcherPriority.ApplicationIdle','DispatcherPriority.SystemIdle')
 replace('src/PaperWindow.EdgeCapsulePlacement.cs','System.Windows.Threading.DispatcherPriority.ApplicationIdle,\n            requireActiveInteraction: false','System.Windows.Threading.DispatcherPriority.SystemIdle,\n            requireActiveInteraction: false',2)

if mode == 'measure':
 p='src/E003Probe.cs'
 replace(p,'    private static long _origin', '''    internal static int ExpectedArtifacts;
    private static int _shellsBuilt;
    private static bool _artifactsReady;
    internal static void ArtifactStored(int count)
    {
        if (!_artifactsReady && count == ExpectedArtifacts)
        {
            _artifactsReady = true;
            Mark("cache.initialReady");
        }
    }
    internal static void ShellBuilt()
    {
        if (++_shellsBuilt == ExpectedArtifacts) Mark("shell.allBuilt");
    }
    private static long _origin''')
 p='src/EdgeCapsulePreview.Preload.cs'
 replace(p,'        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        E003Probe.ArtifactStored(_artifacts.Count);')
 p='src/PaperWindow.cs'
 replace(p,'        ReplayPluginRuntimePresentation();\n    }\n\n    private void HandleWindowGeometryChanged()', '        ReplayPluginRuntimePresentation();\n        E003Probe.ShellBuilt();\n    }\n\n    private void HandleWindowGeometryChanged()')
 p='tests/PaperTodo.LifecycleChecks/Program.cs'
 replace(p,'        E003Probe.Reset();','        E003Probe.Reset();\n        E003Probe.ExpectedArtifacts = count;')
 replace(p,'            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");', '''            if (name == "early-expand")
            {
                await Until(() => cache.ArtifactCount == count, "early preview readiness");
                Console.WriteLine("EARLY_EXPAND_WAS_BUILT " + windows["fixture-0"].IsShellBuilt);
                using (E003Probe.Measure("demand.expand")) windows["fixture-0"].ActivateFromEdgeShortcut();
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");''')
 replace(p,'            E003Probe.Mark("preview.readyObserved");','            E003Probe.Mark("preview.readyObserved");\n            Console.WriteLine("STARTUP_WARM_COMPLETIONS " + cache.WarmCompletions);')
 replace(p,'                catch (Exception ex) { Console.Error.WriteLine(ex); }','                catch (Exception ex) { E003Probe.Report("failed-" + args[1]); Console.Error.WriteLine(ex); }')
print('E003 variant applied:',mode)
