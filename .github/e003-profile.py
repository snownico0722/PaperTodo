from pathlib import Path
import re
import sys

root = Path(sys.argv[1])

def read(path):
    return (root / path).read_text(encoding='utf-8-sig')

def write(path, text):
    (root / path).write_text(text, encoding='utf-8', newline='\n')

def replace(path, old, new, count=1):
    text = read(path)
    if text.count(old) != count:
        raise SystemExit(f'{path}: expected {count} matches for {old[:90]!r}, got {text.count(old)}')
    write(path, text.replace(old, new))

def method(path, name, label):
    text = read(path)
    matches = list(re.finditer(r'^    (?:public|private|internal|protected)[^\n]*\b' + re.escape(name) + r'\(', text, re.M))
    if len(matches) != 1:
        raise SystemExit(f'{path}: {name}: expected one declaration, got {len(matches)}')
    start = text.find('{', matches[0].end())
    if '=>' in text[matches[0].end():start] or ';' in text[matches[0].end():start]:
        raise SystemExit(f'{name}: expression body unsupported')
    write(path, text[:start+1] + f'\n        using var e003Scope = E003Probe.Measure("{label}");' + text[start+1:])

write('src/E003Probe.cs', '''using System.Diagnostics;
using System.Text.Json;
namespace PaperTodo;
internal static class E003Probe
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _origin = Stopwatch.GetTimestamp();
    internal sealed record Entry(string Name, double StartMs, double DurationMs, int ThreadId);
    internal static void Reset() { lock (Gate) { Entries.Clear(); _origin = Stopwatch.GetTimestamp(); } }
    internal static Scope Measure(string name) => new(name);
    internal readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        internal Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose() { Add(_name, _start, Stopwatch.GetTimestamp()); }
    }
    private static void Add(string name, long start, long end)
    {
        lock (Gate) Entries.Add(new(name, Stopwatch.GetElapsedTime(_origin, start).TotalMilliseconds,
            Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, Environment.CurrentManagedThreadId));
    }
    internal static void Mark(string name) { var now = Stopwatch.GetTimestamp(); Add(name, now, now); }
    internal static void Report(string fixture)
    {
        Entry[] entries;
        lock (Gate) entries = Entries.ToArray();
        Console.WriteLine("E003_PROFILE " + JsonSerializer.Serialize(new { fixture, entries,
            workingSet = Environment.WorkingSet, allocated = GC.GetTotalAllocatedBytes(false) }));
    }
}
''')

for path, name, label in [
    ('src/AppController.cs', 'StartAsync', 'startup.total'),
    ('src/AppController.cs', 'RestorePaperSurfacesAsync', 'restore.total'),
    ('src/AppController.cs', 'GetOrCreatePaperWindow', 'paper.getOrCreate'),
    ('src/AppController.cs', 'ArrangeDeepCapsules', 'arrange.total'),
    ('src/AppController.cs', 'EnsurePapersOnScreen', 'startup.rescue'),
    ('src/AppController.cs', 'Exit', 'exit.total'),
    ('src/AppController.cs', 'DisposeRuntimeResources', 'exit.disposeResources'),
    ('src/AppController.Tray.cs', 'CreateTrayIcon', 'startup.tray'),
    ('src/AppController.Tray.cs', 'DisposeTrayIcon', 'exit.tray')
]:
    if name == 'DisposeTrayIcon':
        path = 'src/AppController.cs'
    method(path, name, label)

for path, name, label in [
    ('src/PaperWindow.cs', 'PaperWindow', 'paper.ctor'),
    ('src/PaperWindow.cs', 'EnsureShellBuilt', 'shell.ensure'),
    ('src/PaperWindow.cs', 'BuildShell', 'shell.build'),
    ('src/PaperWindow.cs', 'BuildTopBar', 'shell.topbar'),
    ('src/PaperWindow.cs', 'BuildBody', 'shell.body'),
    ('src/PaperWindow.cs', 'BuildDragLayer', 'shell.dragLayer'),
    ('src/PaperWindow.cs', 'CloseForReal', 'exit.paperClose'),
    ('src/PaperWindow.Lifecycle.cs', 'CompletePaperWindowClose', 'exit.paperComplete'),
    ('src/PaperWindow.Lifecycle.cs', 'BeginPaperWindowClose', 'exit.paperBegin'),
    ('src/PaperWindow.EdgeCapsulePreviewContent.cs', 'InvalidateEdgeCapsulePreviewContent', 'preview.invalidate'),
    ('src/MasterCapsuleWindow.cs', 'MasterCapsuleWindow', 'master.ctor'),
    ('src/EdgeCapsuleHost.cs', 'Apply', 'host.apply'),
    ('src/EdgeCapsuleHost.cs', 'RefreshNativeMetricsLayout', 'host.layout'),
    ('src/WindowNative.cs', 'TrySetWindowDeviceBounds', 'native.setBounds'),
    ('src/EdgeCapsulePreview.Preload.cs', 'WarmLayoutAsync', 'preview.warm')
]:
    method(path, name, label)

p = 'src/AppController.cs'
for statement, name in [
    ('        CreateTrayIcon();', 'start.trayDone'),
    ('        InitializeGlobalHotkeys();', 'start.hotkeysDone'),
    ('        RefreshFullscreenAvoidanceRuntime();', 'start.fullscreenDone'),
    ('        RefreshTodoReminderSchedule();', 'start.remindersDone'),
    ('        RefreshExperimentalWindowRuntime();', 'start.experimentalDone'),
    ('        DeferStartupPapersWithoutMonitor();', 'start.displayDone'),
    ('        EnablePluginRuntimeReconciliation();', 'start.pluginsDone'),
    ('        await RestorePaperSurfacesAsync(papersToRestore);', 'start.restoreReturned')
]:
    text = read(p)
    begin = text.index('    public async Task StartAsync(')
    end = text.index('    private async Task RestorePaperSurfacesAsync(', begin)
    section = text[begin:end]
    if statement not in section:
        raise SystemExit(f'missing startup anchor {statement}')
    section = section.replace(statement, statement + f'\n        E003Probe.Mark("{name}");', 1)
    write(p, text[:begin] + section + text[end:])
replace(p, '                static () => { },\n                DispatcherPriority.ApplicationIdle);\n            if (restoreGeneration',
    '                static () => E003Probe.Mark("restore.idleResumed"),\n                DispatcherPriority.ApplicationIdle);\n            if (restoreGeneration')
replace(p, '            RefreshTrayMenu();\n\n            // The continuation',
    '            RefreshTrayMenu();\n            E003Probe.Mark("restore.firstArrangeDone");\n\n            // The continuation')
replace(p, '            Environment.Exit(0);', '            E003Probe.Report("real-exit");\n            Environment.Exit(0);')
for statement, name in [
    ('        TryExitCleanup(DisposeEdgeCapsuleQueueCompositionProxies);', 'exit.proxiesDone'),
    ('        ClearPaperLinkDropTarget();', 'exit.uiHidden'),
    ('        TryExitCleanup(DisposeGlobalHotkeys);', 'exit.hotkeysDone'),
    ('        TryExitCleanup(DisposeMcpRuntime);', 'exit.mcpDone'),
    ('        TryExitCleanup(DisposeExperimentalWindowRuntime);', 'exit.experimentalDone'),
    ('        TryExitCleanup(DisposeTrayIcon);', 'exit.trayDone'),
    ('        _windows.Clear();', 'exit.papersDone'),
    ('        TryExitCleanup(DisposePaperBodyPlugins);', 'exit.pluginsDone'),
    ('        _masterCapsules.Clear();', 'exit.mastersDone'),
    ('        TryExitCleanup(_imageStore.Dispose);', 'exit.imagesDone'),
    ('        TryExitCleanup(() => scriptShutdown.GetAwaiter().GetResult());', 'exit.scriptsDone')
]:
    text = read(p)
    begin = text.index('    private void DisposeRuntimeResources()')
    section = text[begin:]
    if section.count(statement) != 1: raise SystemExit(f'exit anchor {name}')
    section = section.replace(statement, statement + f'\n        E003Probe.Mark("{name}");')
    write(p, text[:begin] + section)

replace('src/EdgeCapsuleHost.cs', '            window.Show();',
    '            using (E003Probe.Measure("host.show")) window.Show();')
replace('src/WindowNative.cs', '    public static void FlushDesktopComposition() => _ = DwmFlush();',
    '    public static void FlushDesktopComposition() { using var scope = E003Probe.Measure("native.dwmFlush"); _ = DwmFlush(); }')

p = 'tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p, '            var baseline = args.Contains("--baseline");',
    '            var singleCase = args.FirstOrDefault(arg => arg.StartsWith("--e003-case="));\n            if (singleCase != null) { RunIsolated(singleCase["--e003-case=".Length..], false); return 0; }\n            var baseline = args.Contains("--baseline");')
replace(p, '        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();',
    '        E003Probe.Reset();\n        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();')
replace(p, '        var constructed = Stopwatch.GetTimestamp();',
    '        var constructed = Stopwatch.GetTimestamp();\n        E003Probe.Mark("controller.ready");')
replace(p, '            await controller.StartAsync(createDefaultPaper: false);',
    '''            EventHandler? rendered = null;
            rendered = (_, _) =>
            {
                if (windows.Values.Count(window => window.HasVisibleSurface) < count) return;
                E003Probe.Mark("capsules.renderingObserved");
                System.Windows.Media.CompositionTarget.Rendering -= rendered;
            };
            System.Windows.Media.CompositionTarget.Rendering += rendered;
            await controller.StartAsync(createDefaultPaper: false);''')
replace(p, '            var ready = Stopwatch.GetTimestamp();',
    '            var ready = Stopwatch.GetTimestamp();\n            E003Probe.Mark("preview.readyObserved");')
replace(p, '            var disposedAt = Stopwatch.GetTimestamp();',
    '            var disposedAt = Stopwatch.GetTimestamp();\n            E003Probe.Report(name);')
print('Temporary E003 instrumentation applied')
