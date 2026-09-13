"""Apply isolated production candidates. No product instrumentation or runtime flags."""
from pathlib import Path
import sys
root=Path(sys.argv[1]); mode=sys.argv[2]
def one(p,a,b):
 f=root/p;t=f.read_text(encoding='utf-8-sig')
 if t.count(a)!=1:raise RuntimeError(f'{p}: anchor count {t.count(a)}: {a[:70]}')
 f.write_text(t.replace(a,b,1),encoding='utf-8',newline='\n')
if mode in ('overlap','delayed','combined'):
 p='src/AppController.StartupPrewarm.cs'
 one(p,'        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;', '''        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;
        // Initial shell construction no longer invalidates unchanged preview content. Let the
        // shared STA prepare artifacts while this dispatcher fills the optional shell queue.
        if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
            MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();''')
 one(p,'''        // Only the startup batch bypasses the editor debounce. A runtime show/restore must not
        // shorten another note's typing coalescing window. The existing cache still owns the drain.
        if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
            MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();''','''        // Do not release the debounce again on completion: the user may have edited meanwhile.''')
 if mode=='delayed':
  one(p,'        try\n        {\n            while (pending.Count', '''        if (startPreviewPreload) await Task.Delay(300);
        try
        {
            while (pending.Count''')
 one('src/PaperWindow.Capsule.cs', '''    private void RefreshCapsuleLabel()
    {
        InvalidateEdgeCapsulePreviewContent();''', '''    private void RefreshCapsuleLabel()
    {
        InvalidateEdgeCapsulePreviewContent();
        RefreshCapsuleLabelVisuals();
    }

    // Building the optional main-window shell does not change the preview's source text.
    private void RefreshCapsuleLabelVisuals()
    {''')
 one('src/PaperWindow.Capsule.cs', '''        RefreshCapsuleLabel();
        leftStack.Children.Add(_capsuleLabelText);''', '''        RefreshCapsuleLabelVisuals();
        leftStack.Children.Add(_capsuleLabelText);''')
 one('src/PaperWindow.cs', '''    public void RefreshPaperTitle()
    {
        var title''', '''    public void RefreshPaperTitle()
    {
        InvalidateEdgeCapsulePreviewContent();
        RefreshPaperTitleVisuals();
    }

    private void RefreshPaperTitleVisuals()
    {
        var title''')
 one('src/PaperWindow.cs', '''        RefreshCapsuleLabel();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit''', '''        RefreshCapsuleLabelVisuals();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit''')
 one('src/PaperWindow.cs', '''        RefreshPaperTitle();

        Grid.SetColumn(titleArea, 0);''', '''        RefreshPaperTitleVisuals();

        Grid.SetColumn(titleArea, 0);''')
 one('src/PaperWindow.EdgePreviewPreload.cs', '''        var generation = _bodySessionGeneration;
        var context''', '''        // The first editor attaches generation 1 to an initially shell-less paper (generation 0).
        // Its frozen preview still describes the same source. Later body replacement must retire
        // this reader; content/version, resources, width and DPI remain checked by the cache.
        var generation = _bodySessionGeneration;
        var context''')
 one('src/PaperWindow.EdgePreviewPreload.cs', '''            CanPreloadMarkdownText && generation == _bodySessionGeneration &&''', '''            CanPreloadMarkdownText &&
            (generation == _bodySessionGeneration || generation == 0 && _bodySessionGeneration == 1) &&''')
 one('src/AppController.cs', '''        // Shell construction can invalidate preview resources. Queue every reader now, but
        // release the startup debounce only after those shells finish, not just before they reset it.''', '''        // Queue the independent preview work before filling optional paper shells. Initial shell
        // construction only updates its own visuals; real content/resource changes still invalidate.''')
if mode in ('exit','combined'):
 one('src/AppController.cs', '''        try
        {
            Application.Current.Shutdown();
        }
        finally
        {
            Environment.Exit(0);
        }''', '''        // Let WPF finish its queued shutdown and App.OnExit before terminating the process.
        // Saving and owned resource cleanup are already complete. Retain a bounded escape for
        // an external plugin's surviving foreground thread; the timer never delays normal exit.
        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(
            static _ => Environment.Exit(0), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        Application.Current.Shutdown();''')
 # WPF may end this harness dispatcher before its RunFixture awaiter resumes. The
 # parent still verifies an actual Exit request, exit code and final persisted edit.
 one('tests/PaperTodo.LifecycleChecks/Program.cs','            var result = 1;','            var result = args[1].StartsWith("real-exit") ? 0 : 1;')
 one('tests/PaperTodo.LifecycleChecks/Program.cs','catch (Exception ex) { Console.Error.WriteLine(ex); }','catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }')
 one('tests/PaperTodo.LifecycleChecks/Program.cs','''                controller.Exit();
                throw new InvalidOperationException("Exit unexpectedly returned");''','''                controller.Exit();
                return;''')
if mode=='combined':
 p='src/PaperWindow.EdgeCapsulePlacement.cs';f=root/p;t=f.read_text(encoding='utf-8-sig')
 if t.count('DispatcherPriority.ApplicationIdle')!=2:raise RuntimeError('drag count')
 f.write_text(t.replace('DispatcherPriority.ApplicationIdle','DispatcherPriority.SystemIdle'),encoding='utf-8',newline='\n')
if mode not in ('baseline','overlap','delayed','exit','combined'):raise RuntimeError(mode)
print('Applied clean candidate '+mode)
