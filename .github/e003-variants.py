from pathlib import Path
import sys, re
root=Path(sys.argv[1]); variant=sys.argv[2]
def read(p): return (root/p).read_text(encoding='utf-8-sig')
def write(p,t): (root/p).write_text(t,encoding='utf-8',newline='\n')
def one(p,a,b):
 t=read(p)
 if t.count(a)!=1: raise RuntimeError(f'{p} expected one {a[:60]} got {t.count(a)}')
 write(p,t.replace(a,b,1))
def method(p,name):
 t=read(p);m=re.search(r'(?m)^    (?:public|private|internal|protected)[^\n]*\b'+name+r'\(',t)
 if not m: raise RuntimeError(name)
 pos=t.index('\n    {',m.start())+6
 write(p,t[:pos]+f'\n        using var __e003 = E003.Span("{Path(p).stem}.{name}");'+t[pos:])
method('src/EdgeCapsuleDragWindow.cs','TryPrewarmInfrastructure')
method('src/EdgeCapsuleDragWindow.cs','ReturnToPool')
if variant=='no-entry-animation':
 p='src/AppController.cs';t=read(p);a=t.index('    private async Task RestorePaperSurfacesAsync(');b=t.index('    private void ApplyInitialStartupVisibility',a)
 part=t[a:b]
 if part.count('animate: State.EnableAnimations')!=2:raise RuntimeError('animation count')
 write(p,t[:a]+part.replace('animate: State.EnableAnimations','animate: false')+t[b:])
elif variant=='drag-idle':
 p='src/PaperWindow.EdgeCapsulePlacement.cs';t=read(p)
 if t.count('DispatcherPriority.ApplicationIdle')!=2:raise RuntimeError('drag priority count')
 write(p,t.replace('DispatcherPriority.ApplicationIdle','DispatcherPriority.SystemIdle'))
elif variant=='delayed-shell':
 one('src/AppController.StartupPrewarm.cs','        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;',
 '''        var batchLimit = pending.Count <= SmallPrewarmPaperLimit ? SmallPrewarmPaperLimit : 1;
        if (startPreviewPreload)
        {
            MarkdownEdgePreviewPreload.For(dispatcher).StartStartupWork();
            await Task.Delay(500);
        }''')
elif variant=='graceful-exit':
 one('src/AppController.cs','''        try
        {
            Application.Current.Shutdown();
        }
        finally
        {
            E003.Mark("environment-exit");
            Environment.Exit(0);
        }''','''        E003.Mark("normal-shutdown-request");
        Application.Current.Shutdown();''')
 one('tests/PaperTodo.LifecycleChecks/Program.cs','''                controller.Exit();
                throw new InvalidOperationException("Exit unexpectedly returned");''','''                controller.Exit();
                return;''')
elif variant!='baseline': raise RuntimeError(variant)
print('Prepared variant '+variant)
