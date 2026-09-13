from pathlib import Path
import sys
root=Path(sys.argv[1]); mode=sys.argv[2]
def read(p):return (root/p).read_text(encoding='utf-8-sig')
def write(p,s):(root/p).write_text(s,encoding='utf-8',newline='\n')
def replace(p,a,b):
 s=read(p)
 if s.count(a)!=1:raise RuntimeError(f'{p} {a[:70]!r}: {s.count(a)}')
 write(p,s.replace(a,b,1))
if mode!='baseline':
 p='src/EdgeCapsulePreview.Preload.cs'
 replace(p,'    private CancellationTokenSource? _work;','    private CancellationTokenSource? _work;\n    private Task _drainTask = Task.CompletedTask;')
 replace(p,'_debounce.Interval = TimeSpan.FromMilliseconds(500); Drain();','_debounce.Interval = TimeSpan.FromMilliseconds(500); _drainTask = DrainAsync();')
 replace(p,'    private async void Drain()', '''    // Startup may await this one drain before preconstructing optional editors. Unavailable
    // hosts remain dormant and user input can cancel the drain; this is not an all-sources barrier.
    internal Task StartStartupWorkAsync()
    {
        _dispatcher.VerifyAccess();
        if (!_enabled || _dispatcher.HasShutdownStarted || RunnableCount == 0)
            return Task.CompletedTask;
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(500);
        if (_work == null) _drainTask = DrainAsync();
        return _drainTask;
    }

    private async Task DrainAsync()''')
 p='src/AppController.StartupPrewarm.cs'
 replace(p,'        try\n        {\n            while', '''        try
        {
            // Folded papers can render their preview directly from the model. Finish the small
            // initial artifact batch first; optional full editors follow on the same idle queue.
            if (startPreviewPreload && pending.Count <= SmallPrewarmPaperLimit)
                await MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWorkAsync();
            while''')
 p='src/PaperWindow.Capsule.cs'
 replace(p,'    private void RefreshCapsuleLabel()\n    {\n        InvalidateEdgeCapsulePreviewContent();', '''    private void RefreshCapsuleLabel(bool invalidatePreview = true)
    {
        if (invalidatePreview) InvalidateEdgeCapsulePreviewContent();''')
 replace(p,'        RefreshCapsuleLabel();\n        leftStack.Children.Add(_capsuleLabelText);', '''        // Constructing a hidden Shell copies the existing model; it is not a content change.
        RefreshCapsuleLabel(invalidatePreview: false);
        leftStack.Children.Add(_capsuleLabelText);''')
 p='src/PaperWindow.cs'
 replace(p,'    public void RefreshPaperTitle()\n    {', '''    public void RefreshPaperTitle() => RefreshPaperTitle(invalidatePreview: true);

    private void RefreshPaperTitle(bool invalidatePreview)
    {''')
 replace(p,'        RefreshCapsuleLabel();\n        RefreshPaperContextMenus();', '        RefreshCapsuleLabel(invalidatePreview);\n        RefreshPaperContextMenus();')
 replace(p,'        RefreshPaperTitle();\n\n        Grid.SetColumn(titleArea, 0);', '''        RefreshPaperTitle(invalidatePreview: false);

        Grid.SetColumn(titleArea, 0);''')
 p='src/AppController.cs'
 replace(p,'            // The continuation runs below Render/Loaded so edge hosts reach the compositor\n            // before full paper shells begin their sequential construction.\n            await Application.Current.Dispatcher.InvokeAsync(\n                static () => { },\n                DispatcherPriority.ApplicationIdle);', '''            // Continue after Render/Loaded, but ahead of optional ApplicationIdle prewarm.
            // Compositor prewarm and hidden Shells must not hold startup command delivery hostage.
            await Application.Current.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);''')
 if mode=='latecomp':
  for p in ('src/App.EdgeCapsuleComposition.cs','src/PaperWindow.EdgeCapsulePreview.cs'):
   replace(p,'            DispatcherPriority.ApplicationIdle,','            DispatcherPriority.SystemIdle,')
 if mode=='latetray':
  replace('src/AppController.cs','        CreateTrayIcon();', '''        _ = Application.Current.Dispatcher.BeginInvoke(
            (Action)(() => { if (!IsExiting) CreateTrayIcon(); }),
            DispatcherPriority.SystemIdle);''')
