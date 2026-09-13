from pathlib import Path
import re, sys
root = Path(sys.argv[1])
mode = sys.argv[2]
def read(p): return (root/p).read_text(encoding='utf-8-sig')
def write(p,s): (root/p).write_text(s, encoding='utf-8', newline='\n')
def replace(p,a,b,n=1):
 s=read(p)
 if s.count(a)!=n: raise RuntimeError(f'{p}: {a[:70]!r}: {s.count(a)} != {n}')
 write(p,s.replace(a,b))
def method(p,name,label):
 s=read(p); hits=list(re.finditer(r'^    (?:public|private|internal|protected)[^\n]*\b'+re.escape(name)+r'\(',s,re.M))
 if len(hits)!=1: raise RuntimeError(f'{p}: {name}: {len(hits)}')
 i=s.index('{',hits[0].end())
 if '=>' in s[hits[0].end():i]: raise RuntimeError(name)
 write(p,s[:i+1]+f'\n        using var e004Scope = E004Probe.Measure("{label}");'+s[i+1:])
# Product-only variants. Never applied to the PR without separate validation.
if mode in ('tray','both'):
 p='src/AppController.cs'
 replace(p,'        CreateTrayIcon();\n        InitializeGlobalHotkeys();','''        // Experimental first-frame ordering: retain a tray immediately for hidden/empty starts.
        var postponeTray = State.Papers.Any(paper => paper.IsVisible) &&
            initialVisibilityCommand is not (StartupCommandKind.Hide or StartupCommandKind.Toggle);
        if (!postponeTray) CreateTrayIcon();
        InitializeGlobalHotkeys();''')
 replace(p,'        await RestorePaperSurfacesAsync(papersToRestore);','''        await RestorePaperSurfacesAsync(papersToRestore);
        if (postponeTray && !IsExiting) CreateTrayIcon();''')
if mode in ('styles','both'):
 p='src/PaperWindow.cs'
 replace(p,'    private static Style BuildIconButtonStyle()','''    [ThreadStatic]
    private static Style? _sharedIconButtonStyle;
    [ThreadStatic]
    private static double _sharedIconButtonStyleScale;

    private static Style SharedIconButtonStyle
    {
        get
        {
            var scale = AppTypography.ScaleFactor;
            if (_sharedIconButtonStyle == null || _sharedIconButtonStyleScale != scale)
            {
                _sharedIconButtonStyle = BuildIconButtonStyle();
                _sharedIconButtonStyleScale = scale;
            }
            return _sharedIconButtonStyle;
        }
    }

    private static Style BuildIconButtonStyle()''')
 replace(p,'            Style = BuildIconButtonStyle()','            Style = SharedIconButtonStyle')
if '--product' in sys.argv: raise SystemExit(0)
write('src/E004Probe.cs','''using System.Diagnostics;
using System.Text.Json;
namespace PaperTodo;
internal static class E004Probe
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _origin = Stopwatch.GetTimestamp();
    internal sealed record Entry(string Name, double StartMs, double DurationMs, int Thread);
    internal static void Reset() { lock (Gate) { Entries.Clear(); _origin = Stopwatch.GetTimestamp(); } }
    internal static Scope Measure(string name) => new(name);
    internal readonly struct Scope : IDisposable
    {
        private readonly string _name; private readonly long _start;
        internal Scope(string name) { _name=name; _start=Stopwatch.GetTimestamp(); }
        public void Dispose() { Add(_name,_start,Stopwatch.GetTimestamp()); }
    }
    private static void Add(string name,long start,long end)
    {
        lock(Gate) Entries.Add(new(name,Stopwatch.GetElapsedTime(_origin,start).TotalMilliseconds,
            Stopwatch.GetElapsedTime(start,end).TotalMilliseconds,Environment.CurrentManagedThreadId));
    }
    internal static void Mark(string name) { var now=Stopwatch.GetTimestamp(); Add(name,now,now); }
    internal static void Report(string fixture)
    {
        Entry[] entries; lock(Gate) entries=Entries.ToArray();
        using var process=Process.GetCurrentProcess();
        Console.WriteLine("E004_PROFILE "+JsonSerializer.Serialize(new {fixture,entries,
            workingSet=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,
            cpuMs=process.TotalProcessorTime.TotalMilliseconds,allocated=GC.GetTotalAllocatedBytes(false)}));
    }
}
''')
for p,n,l in [
 ('src/AppController.cs','AppController','controller.ctor'),
 ('src/AppController.cs','StartAsync','startup.total'),
 ('src/AppController.cs','RestorePaperSurfacesAsync','restore.total'),
 ('src/AppController.cs','ArrangeDeepCapsules','arrange.total'),
 ('src/AppController.cs','DisposeRuntimeResources','exit.resources'),
 ('src/AppController.Tray.cs','CreateTrayIcon','tray.total'),
 ('src/AppController.Tray.cs','CreateTrayMenu','tray.menu'),
 ('src/AppController.Tray.cs','LoadTrayIconSource','tray.icon'),
 ('src/PaperWindow.cs','PaperWindow','paper.ctor'),
 ('src/PaperWindow.cs','EnsureShellBuilt','shell.ensure'),
 ('src/PaperWindow.cs','BuildShell','shell.build'),
 ('src/PaperWindow.cs','BuildTopBar','shell.topbar'),
 ('src/PaperWindow.cs','BuildBody','shell.body'),
 ('src/PaperWindow.cs','BuildIconButtonStyle','shell.iconStyle'),
 ('src/PaperWindow.cs','BuildPaperContextMenu','shell.paperMenu'),
 ('src/PaperWindow.Note.cs','BuildNoteBody','note.body'),
 ('src/MarkdownTextBox.cs','MarkdownTextBox','editor.ctorAfterBase'),
 ('src/MarkdownTextBox.cs','RefreshTextView','editor.refresh'),
 ('src/MarkdownTextBox.cs','ConfigureNoteImages','editor.images'),
 ('src/PaperWindow.Capsule.cs','BuildCapsuleShell','shell.capsule'),
 ('src/MasterCapsuleWindow.cs','MasterCapsuleWindow','master.ctor'),
 ('src/EdgeCapsuleHost.cs','Apply','host.apply'),
 ('src/EdgeCapsuleHost.cs','RefreshNativeMetricsLayout','host.layout'),
 ('src/WindowNative.cs','TrySetWindowDeviceBounds','native.bounds'),
 ('src/EdgeCapsulePreview.Preload.cs','WarmLayoutAsync','preview.warm'),
]: method(p,n,l)
replace('src/PaperWindow.Note.cs','        _noteBox = new MarkdownTextBox','        E004Probe.Mark("editor.beforeNew");\n        _noteBox = new MarkdownTextBox')
replace('src/PaperWindow.Note.cs','        var box = _noteBox;','        var box = _noteBox;\n        E004Probe.Mark("editor.afterNew");')
replace('src/WindowNative.cs','        var handle = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();','''        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            using var handleScope = E004Probe.Measure("native.ensureHandle");
            handle = helper.EnsureHandle();
        }''')
replace('src/EdgeCapsuleHost.cs','            window.Show();','            using (E004Probe.Measure("host.show")) window.Show();')
replace('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E004Probe.Mark("shell.built");')
replace('src/EdgeCapsulePreview.Preload.cs','        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        E004Probe.Mark("cache.stored");')
p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'            var baseline = args.Contains("--baseline");','''            var single = args.FirstOrDefault(arg => arg.StartsWith("--e004-case="));
            if (single != null) { RunIsolated(single["--e004-case=".Length..], false); return 0; }
            var baseline = args.Contains("--baseline");''')
replace(p,'        var count = name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;','        var count = name == "expanded-1" ? 1 : name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;')
replace(p,'IsVisible = true, IsCollapsed = true, X = area.Left + 60','IsVisible = true, IsCollapsed = name != "expanded-1", X = area.Left + 60')
replace(p,'        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();','        E004Probe.Reset();\n        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();')
replace(p,'        var constructed = Stopwatch.GetTimestamp();','        var constructed = Stopwatch.GetTimestamp();\n        E004Probe.Mark("controller.ready");')
replace(p,'            await controller.StartAsync(createDefaultPaper: false);','''            EventHandler? rendered = null;
            rendered = (_, _) =>
            {
                if(windows.Values.Count(window => window.HasVisibleSurface)<count) return;
                E004Probe.Mark("capsules.rendering");
                System.Windows.Media.CompositionTarget.Rendering -= rendered;
            };
            System.Windows.Media.CompositionTarget.Rendering += rendered;
            await controller.StartAsync(createDefaultPaper: false);
            E004Probe.Mark("startup.returned");''')
replace(p,'            var ready = Stopwatch.GetTimestamp();','            var ready = Stopwatch.GetTimestamp();\n            E004Probe.Mark("ready.observed");')
replace(p,'            var disposedAt = Stopwatch.GetTimestamp();','            var disposedAt = Stopwatch.GetTimestamp();\n            E004Probe.Report(name);')
replace('src/AppController.cs','        Application.Current.Shutdown();','        E004Probe.Report("real-exit");\n        Application.Current.Shutdown();')
print('E004 ready',mode)
