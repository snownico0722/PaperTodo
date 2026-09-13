from pathlib import Path
import sys
root = Path(sys.argv[1]); mode = sys.argv[2]

def read(p): return (root/p).read_text(encoding='utf-8-sig')
def write(p,s): (root/p).write_text(s,encoding='utf-8',newline='\n')
def replace(p,a,b):
 s=read(p)
 if s.count(a)!=1: raise RuntimeError(f'{p}: {a[:90]!r}: {s.count(a)} matches')
 write(p,s.replace(a,b,1))
def scope(p,signature,name):
 s=read(p)
 if s.count(signature)!=1: raise RuntimeError(f'{p}: signature {signature}: {s.count(signature)}')
 pos=s.index('{',s.index(signature))+1
 write(p,s[:pos]+f'\n        using var e003Scope = E003Probe.Measure("{name}");'+s[pos:])
def wrap(p,statement,name):
 replace(p,statement,f'using (E003Probe.Measure("{name}")) {{ {statement} }}')

if mode in ('early','defer','demand'):
 replace('src/AppController.cs','        ScheduleStartupShellPrewarm(papersToRestore, startPreviewPreload: true);',
  '        MarkdownEdgePreviewPreload.For(Application.Current.Dispatcher).StartStartupWork();\n'+
  ('        ScheduleStartupShellPrewarm(papersToRestore, startPreviewPreload: true);' if mode!='demand' else '        // Experiment only: first use owns Shell construction.'))
 if mode=='defer':
  replace('src/AppController.StartupPrewarm.cs','        var dispatcher = Application.Current.Dispatcher;',
  '        var dispatcher = Application.Current.Dispatcher;\n        if (startPreviewPreload) await Task.Delay(750);')

scope('src/AppController.cs','public AppController()','Controller.ctor')
scope('src/AppController.cs','public async Task StartAsync(','Startup.total')
scope('src/AppController.cs','private async Task RestorePaperSurfacesAsync(','Restore.total')
scope('src/AppController.cs','private PaperWindow GetOrCreatePaperWindow(','Paper.getOrCreate')
scope('src/AppController.cs','public void ArrangeDeepCapsules(','Queue.arrange')
scope('src/AppController.cs','private bool TrySaveNow(bool sync)','Save.total')
scope('src/AppController.cs','private void DisposeRuntimeResources()','Exit.resources')
scope('src/AppController.cs','private void DisposeTrayIcon()','Exit.tray')
for statement,name in [
 ('        CreateTrayIcon();','Startup.tray'),
 ('        InitializeGlobalHotkeys();','Startup.hotkeys'),
 ('        RefreshFullscreenAvoidanceRuntime();','Startup.fullscreen'),
 ('        RefreshTodoReminderSchedule();','Startup.reminders'),
 ('        RefreshExperimentalWindowRuntime();','Startup.experimental'),
 ('        DeferStartupPapersWithoutMonitor();','Startup.monitorDefer'),
 ('        CompleteDeferredStartupDisplayRestore();','Startup.monitorComplete')]:
 wrap('src/AppController.cs',statement,name)
replace('src/AppController.cs','        var rescuedPapers = EnsurePapersOnScreen();','        bool rescuedPapers;\n        using (E003Probe.Measure("Startup.rescue")) { rescuedPapers = EnsurePapersOnScreen(); }')
wrap('src/AppController.cs','            await Application.Current.Dispatcher.InvokeAsync(\n                static () => { },\n                DispatcherPriority.ApplicationIdle);','Restore.idleWait')
for statement,name in [
 ('        TryExitCleanup(DisposeEdgeCapsuleQueueCompositionProxies);','Exit.proxies'),
 ('        TryExitCleanup(DisposeGlobalHotkeys);','Exit.hotkeys'),
 ('        TryExitCleanup(DisposeMcpRuntime);','Exit.mcp'),
 ('        TryExitCleanup(DisposeExperimentalWindowRuntime);','Exit.experimental'),
 ('        TryExitCleanup(DisposePaperBodyPlugins);','Exit.plugins'),
 ('        TryExitCleanup(_imageStore.Dispose);','Exit.images'),
 ('        TryExitCleanup(() => scriptShutdown.GetAwaiter().GetResult());','Exit.scriptWait'),
 ('            TryExitCleanup(() => surface.Hide());','Exit.hideOne'),
 ('            TryExitCleanup(() => window.CloseForReal());','Exit.closePaper')]:
 wrap('src/AppController.cs',statement,name)
for p,s,n in [
 ('src/PaperWindow.cs','public PaperWindow(','Paper.ctor'),
 ('src/PaperWindow.cs','internal void EnsureShellBuilt()','Shell.ensure'),
 ('src/PaperWindow.cs','private void BuildShell()','Shell.build'),
 ('src/EdgeCapsuleHost.cs','public bool Apply(','Host.apply'),
 ('src/EdgeCapsuleHost.cs','private void RefreshNativeMetricsLayout()','Host.layout'),
 ('src/EdgeCapsuleHost.Preview.cs','private bool ApplyPreviewPresentation(','Host.preview'),
 ('src/WindowNative.cs','public static bool TrySetWindowDeviceBounds(','Native.setBounds'),
 ('src/WindowNative.cs','public static bool TryGetWindowDeviceBounds(','Native.getBounds'),
 ('src/PaperWindow.Lifecycle.cs','private void BeginPaperWindowClose()','Exit.paperBegin'),
 ('src/PaperWindow.Lifecycle.cs','private void CompletePaperWindowClose()','Exit.paperComplete')]: scope(p,s,n)
wrap('src/EdgeCapsuleHost.cs','            window.Show();','Host.show')
for statement,name in [('        BuildTopBar();','Shell.topBar'),('        BuildBody();','Shell.body'),('        BuildDragLayer();','Shell.dragLayer'),('        BuildCapsuleShell();','Shell.capsule')]:
 wrap('src/PaperWindow.cs',statement,name)
wrap('src/PaperWindow.cs','        _paperChrome.ContextMenu = BuildPaperContextMenu();','Shell.menu')
replace('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E003Probe.Mark("Shell.built");')
replace('src/EdgeCapsulePreview.Preload.cs','                WarmCompletions++;','                WarmCompletions++;\n                E003Probe.Mark("Artifact.stored");')
replace('src/PaperWindow.EdgeCapsulePreviewContent.cs','        _edgeCapsulePreviewInvalidationSource.Invalidate();','        E003Probe.Mark("Preview.invalidated");\n        _edgeCapsulePreviewInvalidationSource.Invalidate();')

p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'        try\n        {\n            var baseline = args.Contains("--baseline");','        if (args.Length == 2 && args[0] == "--probe-one")\n        {\n            RunIsolated(args[1], false);\n            return 0;\n        }\n        try\n        {\n            var baseline = args.Contains("--baseline");')
replace(p,'        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();',
 '''        E003Probe.Reset();
        var started = Stopwatch.GetTimestamp();
        var controller = new AppController();''')
replace(p,'        var constructed = Stopwatch.GetTimestamp();', '        var constructed = Stopwatch.GetTimestamp();\n        E003Probe.Mark("Controller.ready");')
replace(p,'            await controller.StartAsync(createDefaultPaper: false);', '''            EventHandler? rendering = null;
            rendering = (_, _) =>
            {
                var showing = windows.Values.Count(window => window.IsDeepCapsuleSlotVisible);
                if (showing < count) return;
                System.Windows.Media.CompositionTarget.Rendering -= rendering;
                E003Probe.Mark("Capsules.rendering");
                Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() =>
                {
                    E003Probe.FlushDwm();
                    E003Probe.Mark("Capsules.dwmBoundary");
                }), DispatcherPriority.Background);
            };
            System.Windows.Media.CompositionTarget.Rendering += rendering;
            await controller.StartAsync(createDefaultPaper: false);''')
replace(p,'            var returned = Stopwatch.GetTimestamp();','            var returned = Stopwatch.GetTimestamp();\n            E003Probe.Mark("Startup.returned");')
replace(p,'            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");', '''            if (Environment.GetEnvironmentVariable("E003_MODE") is "early" or "defer" or "demand")
            {
                await Until(() => cache.PendingCount == 0, "early artifact drain");
                E003Probe.Mark("Artifacts.earlyReady");
                E003Probe.Mark("Artifacts.count." + cache.ArtifactCount);
                if (Environment.GetEnvironmentVariable("E003_MODE") == "demand")
                {
                    using (E003Probe.Measure("Demand.firstShell")) windows["fixture-0"].EnsureShellBuilt();
                    foreach (var window in windows.Values) window.EnsureShellBuilt();
                    cache.StartStartupWork();
                }
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");''')
replace(p,'            var ready = Stopwatch.GetTimestamp();','            var ready = Stopwatch.GetTimestamp();\n            E003Probe.Mark("All.ready");')
replace(p,'            if (!baseline)\n            {\n                if (name.StartsWith("capsules-")', '            if (!baseline && !name.StartsWith("capsules-"))\n            {\n                if (name.StartsWith("capsules-")')
replace(p,'                controller.Exit();','                E003Probe.Mark("Exit.request");\n                controller.Exit();')
replace(p,'            var exitAt = Stopwatch.GetTimestamp();','            var exitAt = Stopwatch.GetTimestamp();\n            E003Probe.Mark("Dispose.request");')
replace(p,'            var disposedAt = Stopwatch.GetTimestamp();','            var disposedAt = Stopwatch.GetTimestamp();\n            E003Probe.Mark("Dispose.returned");\n            E003Probe.Dump();')

write('src/E003Probe.cs',r'''using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace PaperTodo;
internal static class E003Probe
{
    private static long _started;
    private static bool _dumped;
    private static readonly object Gate = new();
    private static readonly List<(string Name, double Start, double Ms)> Spans = new();
    private static readonly List<(string Name, double Ms)> Marks = new();
    internal static void Reset()
    {
        _started = Stopwatch.GetTimestamp();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }
    internal static void Mark(string name)
    {
        lock (Gate) Marks.Add((name, Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
    }
    internal static Stamp Measure(string name) => new(name);
    internal readonly struct Stamp : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        internal Stamp(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            var end = Stopwatch.GetTimestamp();
            lock (Gate) Spans.Add((_name, Stopwatch.GetElapsedTime(_started, _start).TotalMilliseconds, Stopwatch.GetElapsedTime(_start, end).TotalMilliseconds));
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    internal static void FlushDwm() { using var scope = Measure("Dwm.flush"); _ = DwmFlush(); }
    internal static void Dump()
    {
        lock(Gate)
        {
            if (_dumped) return;
            _dumped = true;
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var result = new {
                mode = Environment.GetEnvironmentVariable("E003_MODE") ?? "baseline",
                processId = Environment.ProcessId,
                workingSet = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                cpuMs = process.TotalProcessorTime.TotalMilliseconds,
                marks = Marks.Select(x => new { name=x.Name, ms=x.Ms }).ToArray(),
                metrics = Spans.GroupBy(x=>x.Name).ToDictionary(x=>x.Key,x=>new { count=x.Count(), totalMs=x.Sum(v=>v.Ms), maxMs=x.Max(v=>v.Ms) }),
                spans = Spans.Select(x=>new { name=x.Name,start=x.Start,ms=x.Ms }).ToArray()
            };
            Console.WriteLine("E003_PROFILE " + JsonSerializer.Serialize(result));
        }
    }
}
''')
