from pathlib import Path
import runpy, sys
root=Path(sys.argv[1]);variant=sys.argv[2]
base='delayed-shell' if variant in ('preview-first','combined') else variant
sys.argv[2]=base
runpy.run_path(str(Path(__file__).with_name('e003-variants.py')),run_name='__main__')
def read(p): return (root/p).read_text(encoding='utf-8-sig')
def write(p,t): (root/p).write_text(t,encoding='utf-8',newline='\n')
def one(p,a,b):
 t=read(p)
 if t.count(a)!=1:raise RuntimeError(f'{p} count {t.count(a)} {a[:50]}')
 write(p,t.replace(a,b,1))
# A normal WPF shutdown can stop this harness dispatcher before its async awaiter
# observes RunFixture returning. Exceptions still explicitly set failure status;
# the external parent independently checks exit status and final persisted edits.
if variant=='graceful-exit':
 one('tests/PaperTodo.LifecycleChecks/Program.cs','            var result = 1;','            var result = args[1] == "real-exit" ? 0 : 1;')
 one('tests/PaperTodo.LifecycleChecks/Program.cs','catch (Exception ex) { Console.Error.WriteLine(ex); }','catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }')
if variant in ('preview-first','combined'):
 one('src/AppController.StartupPrewarm.cs','await Task.Delay(500);','await Task.Delay(300);')
 one('src/AppController.StartupPrewarm.cs','''        if (startPreviewPreload && !IsExiting && generation == _startupShellPrewarmGeneration)
            MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();''','''        // No second startup release: preserve the normal debounce if the user edited meanwhile.''')
 one('src/PaperWindow.Capsule.cs','''    private void RefreshCapsuleLabel()
    {
        InvalidateEdgeCapsulePreviewContent();''','''    private void RefreshCapsuleLabel()
    {
        InvalidateEdgeCapsulePreviewContent();
        RefreshCapsuleLabelVisuals();
    }

    private void RefreshCapsuleLabelVisuals()
    {''')
 one('src/PaperWindow.Capsule.cs','''        RefreshCapsuleLabel();
        leftStack.Children.Add(_capsuleLabelText);''','''        RefreshCapsuleLabelVisuals();
        leftStack.Children.Add(_capsuleLabelText);''')
 one('src/PaperWindow.cs','''    public void RefreshPaperTitle()
    {
        var title''','''    public void RefreshPaperTitle()
    {
        InvalidateEdgeCapsulePreviewContent();
        RefreshPaperTitleVisuals();
    }

    private void RefreshPaperTitleVisuals()
    {
        var title''')
 one('src/PaperWindow.cs','''        RefreshCapsuleLabel();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit''','''        RefreshCapsuleLabelVisuals();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit''')
 one('src/PaperWindow.cs','''        RefreshPaperTitle();

        Grid.SetColumn(titleArea, 0);''','''        RefreshPaperTitleVisuals();

        Grid.SetColumn(titleArea, 0);''')
 one('src/PaperWindow.EdgePreviewPreload.cs','''            CanPreloadMarkdownText && generation == _bodySessionGeneration &&''','''            CanPreloadMarkdownText &&
            (generation == _bodySessionGeneration || generation == 0 && _bodySessionGeneration == 1) &&''')
 if variant=='combined':
  p='src/PaperWindow.EdgeCapsulePlacement.cs';t=read(p)
  if t.count('DispatcherPriority.ApplicationIdle')!=2:raise RuntimeError('drag count')
  write(p,t.replace('DispatcherPriority.ApplicationIdle','DispatcherPriority.SystemIdle'))
one('src/EdgeCapsuleHost.cs', '    private Window Window { get; }', '    private Window Window { get; }\n    internal bool E003Revealed => Window.Opacity > 0;')
# Visible becomes true inside Show, before host opacity is revealed. Observe a
# subsequent render with nonzero opacity; this is still not physical scanout.
one('src/LifecycleProbe.E003.cs','window => window.HasVisibleSurface','window => window.E003VisuallyReady')
with (root/'src/LifecycleProbe.E003.cs').open('a',encoding='utf-8') as f:
 f.write('''\npublic sealed partial class PaperWindow
{
    public bool E003VisuallyReady => IsVisible && Opacity > 0 ||
        _edgeCapsuleHost is { IsVisible: true } host && host.E003Revealed;
}
''')
one('tests/PaperTodo.LifecycleChecks/Program.cs','windows.Values.All(window => window.HasVisibleSurface));','windows.Values.All(window => window.E003VisuallyReady));')
print('Prepared extended candidate '+variant)
