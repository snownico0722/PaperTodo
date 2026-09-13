"""Temporary E-003 instrumentation. Applied only in an isolated CI checkout."""
from pathlib import Path
import re, sys
root = Path(sys.argv[1])

def read(p): return (root / p).read_text(encoding='utf-8-sig')
def write(p,t): (root/p).write_text(t, encoding='utf-8', newline='\n')
def replace(p,a,b):
    t=read(p)
    if t.count(a)!=1: raise RuntimeError(f'{p}: anchor count {t.count(a)}: {a[:80]}')
    write(p,t.replace(a,b,1))
def span(p,name,label=None):
    t=read(p)
    matches=list(re.finditer(r'(?m)^    (?:public|private|internal|protected)[^\n]*\b'+re.escape(name)+r'\(',t))
    n=0
    for m in reversed(matches):
        pos=t.find('\n    {',m.start())
        if pos<0 or '=>' in t[m.start():pos] or ';' in t[m.start():pos]: continue
        pos+=len('\n    {')
        t=t[:pos]+f'\n        using var __e003 = E003.Span("{label or name}");'+t[pos:]
        n+=1
    if n==0: raise RuntimeError(f'{p}: no block method {name}')
    write(p,t)

def statement(p,a,label):
    replace(p,a, f'var __e003_{label.replace(".","_")} = System.Diagnostics.Stopwatch.GetTimestamp();\n        '+a+f'\n        E003.End("{label}", __e003_{label.replace(".","_")});')

write('src/LifecycleProbe.E003.cs', r'''using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
namespace PaperTodo;
// Temporary CI-only recorder. No console/disk I/O in timed methods.
public static class E003
{
    private readonly record struct Entry(string Name, long Start, long End, int Thread);
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new(4096);
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("PAPERTODO_E003_DIR");
    public static int ExpectedCount = 10;
    [ModuleInitializer]
    public static void Initialize()
    {
        Mark("managed-module");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }
    public readonly struct Scope(string name, long start) : IDisposable
    {
        public void Dispose() => End(name, start);
    }
    public static Scope Span(string name) => new(name, Stopwatch.GetTimestamp());
    public static void End(string name, long start)
    {
        if (DirectoryPath == null) return;
        var end = Stopwatch.GetTimestamp();
        lock (Gate) Entries.Add(new(name, start, end, Environment.CurrentManagedThreadId));
    }
    public static void Mark(string name) => End(name, Stopwatch.GetTimestamp());
    public static void Observe(Func<bool> ready)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!ready()) return;
            CompositionTarget.Rendering -= handler;
            Mark("all-visible-render-callback");
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                Mark("post-render-dispatch");
                WindowNative.FlushDesktopComposition();
                Mark("dwmflush-proxy");
                if (DirectoryPath != null)
                    File.WriteAllText(Path.Combine(DirectoryPath, "ready-" + Environment.ProcessId), "ready");
            }));
        };
        CompositionTarget.Rendering += handler;
    }
    public static void Dump()
    {
        if (DirectoryPath == null) return;
        Entry[] entries;
        lock (Gate) entries = Entries.ToArray();
        using var writer = new StreamWriter(Path.Combine(DirectoryPath, "trace-" + Environment.ProcessId + ".tsv"));
        writer.WriteLine("name\tstart\tend\tthread\tfrequency");
        foreach (var e in entries)
            writer.WriteLine($"{e.Name}\t{e.Start}\t{e.End}\t{e.Thread}\t{Stopwatch.Frequency}");
    }
}
public sealed partial class AppController
{
    internal bool E003SurfacesVisible => _windows.Count >= E003.ExpectedCount &&
        _windows.Values.All(window => window.HasVisibleSurface);
}
''')

methods={
 'src/AppController.cs': ['AppController','StartAsync','RestorePaperSurfacesAsync','GetOrCreatePaperWindow','ArrangeDeepCapsules','DisposeRuntimeResources','DisposeTrayIcon','TrySaveNow'],
 'src/AppController.Tray.cs': ['CreateTrayIcon','LoadTrayIconSource','CreateTrayMenu','RefreshTrayMenu'],
 'src/AppController.StartupPrewarm.cs': ['PrewarmShellsAsync'],
 'src/PaperWindow.cs': ['PaperWindow','ConfigureWindow','EnsureShellBuilt','BuildShell','BuildBody','BuildTopBar','BuildDragLayer','CloseForReal'],
 'src/PaperWindow.Note.cs': ['BuildNoteBody','UpdateTextZoom'],
 'src/PaperWindow.Capsule.cs': ['BuildCapsuleShell'],
 'src/PaperWindow.EdgeCapsuleContextMenu.cs':['BuildDeepCapsuleSlotContextMenu'],
 'src/EdgeCapsuleHost.cs': ['Apply','RefreshNativeMetricsLayout'],
 'src/WindowNative.cs':['TrySetWindowDeviceBounds','TryGetWindowDeviceBounds'],
 'src/EdgeCapsulePreview.Preload.cs':['WarmLayoutAsync'],
 'src/PaperWindow.PluginBodies.cs':['CreateAndAttachInitialPaperBody','CommitDisposeAndInvalidateCurrentBody'],
 'src/PaperWindow.Lifecycle.cs':['BeginPaperWindowClose','CompletePaperWindowClose'],
 'src/StateStore.cs':['SaveJsonSync'],
}
for p,names in list(methods.items()):
 for name in names:
    files=[p] if (root/p).exists() and re.search(r'(?m)^    (?:public|private|internal|protected)[^\n]*\b'+name+r'\(',read(p)) else []
    if not files:
        files=[str(f.relative_to(root)) for f in (root/'src').glob('*.cs') if re.search(r'(?m)^    (?:public|private|internal|protected)[^\n]*\b'+name+r'\(',f.read_text(encoding='utf-8-sig'))]
    if len(files)!=1: raise RuntimeError(f'{name}: {files}')
    span(files[0],name,Path(files[0]).stem+'.'+name)

p='src/AppController.cs'
replace(p,'        CreateTrayIcon();','        E003.Mark("start-enter");\n        CreateTrayIcon();')
replace(p,'        ApplyInitialStartupVisibility(initialVisibilityCommand);','        E003.Mark("startup-services-ready");\n        ApplyInitialStartupVisibility(initialVisibilityCommand);')
replace(p,'        await RestorePaperSurfacesAsync(papersToRestore);','        E003.Mark("restore-enter");\n        await RestorePaperSurfacesAsync(papersToRestore);\n        E003.Mark("restore-return");')
replace(p,'            RefreshTrayMenu();\n\n            // The continuation','            E003.Mark("first-arrange-return");\n            RefreshTrayMenu();\n\n            // The continuation')
replace(p,'            if (restoreGeneration != _paperSurfaceRestoreGeneration)','            E003.Mark("restore-idle-resumed");\n            if (restoreGeneration != _paperSurfaceRestoreGeneration)')
replace(p,'        CommitSettingsExternalMarkdownEditor(saveImmediately: false);','        E003.Mark("exit-request");\n        CommitSettingsExternalMarkdownEditor(saveImmediately: false);')
replace(p,'        _lifecycleState = AppLifecycleState.Exiting;\n        _saveTimer.Stop();','        E003.Mark("exit-editors-committed");\n        _lifecycleState = AppLifecycleState.Exiting;\n        _saveTimer.Stop();')
replace(p,'        DisposeRuntimeResources();\n        _lifecycleState = AppLifecycleState.Disposed;\n        try','        E003.Mark("exit-save-done");\n        DisposeRuntimeResources();\n        E003.Mark("exit-dispose-done");\n        _lifecycleState = AppLifecycleState.Disposed;\n        try')
replace(p,'        ClearPaperLinkDropTarget();\n        _deepCapsuleContextMenuOwners.Clear();','        E003.Mark("all-ui-hidden");\n        ClearPaperLinkDropTarget();\n        _deepCapsuleContextMenuOwners.Clear();')
replace(p,'            Environment.Exit(0);','            E003.Mark("environment-exit");\n            Environment.Exit(0);')
statement('src/EdgeCapsuleHost.cs','window.Show();','host.show')
statement('src/EdgeCapsuleHost.cs','var previewApplied = ApplyPreviewPresentation(frame);','host.preview')
statement('src/WindowNative.cs','var handle = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();','native.ensureHandle')
replace('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E003.Mark("shell-built");')
replace('src/EdgeCapsulePreview.Preload.cs','        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        E003.Mark("artifact-store");\n        if (_artifacts.Count == E003.ExpectedCount) E003.Mark("artifacts-full");')
replace('src/PaperWindow.EdgeCapsulePreviewContent.cs','        _edgeCapsulePreviewInvalidationSource.Invalidate();','        E003.Mark("preview-invalidated");\n        _edgeCapsulePreviewInvalidationSource.Invalidate();')
replace('App.xaml.cs','            _controller = new AppController();','            _controller = new AppController();\n            E003.Mark("controller-ready");\n            E003.Observe(() => _controller.E003SurfacesVisible);')
replace('App.xaml.cs','        CompleteSingleInstanceStartup();','        CompleteSingleInstanceStartup();\n        E003.Mark("app-startup-complete");')

p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'                : Cases;','                : args.Length == 2 && args[0] == "--case" ? new[] { args[1] } : Cases;')
replace(p,'        var constructed = Stopwatch.GetTimestamp();','        var constructed = Stopwatch.GetTimestamp();\n        E003.Mark("controller-ready");\n        E003.ExpectedCount = count;')
replace(p,'            await controller.StartAsync(createDefaultPaper: false);','            E003.Observe(() => windows.Count >= count && windows.Values.All(window => window.HasVisibleSurface));\n            await controller.StartAsync(createDefaultPaper: false);')
replace(p,'            var returned = Stopwatch.GetTimestamp();','            var returned = Stopwatch.GetTimestamp();\n            E003.Mark("test-start-return");')
replace(p,'            var shells = Stopwatch.GetTimestamp();','            var shells = Stopwatch.GetTimestamp();\n            E003.Mark("test-shells-observed");')
replace(p,'            var ready = Stopwatch.GetTimestamp();','            var ready = Stopwatch.GetTimestamp();\n            E003.Mark("test-preload-observed");')
print('E-003 temporary instrumentation applied')
