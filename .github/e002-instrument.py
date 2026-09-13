from pathlib import Path

ROOT = Path('target')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    p = ROOT / path
    p.write_text(text, encoding='utf-8', newline='\n')

def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one match, got {count}: {old[:100]!r}')
    write(path, text.replace(old, new, 1))

write('src/StartupHostProbe.cs', r'''using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PaperTodo;

internal static class StartupHostProbe
{
    private sealed class Metric
    {
        public int Count;
        public double TotalMilliseconds;
        public double MaxMilliseconds;
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _startedAt;
        private bool _disposed;

        internal Scope(string name)
        {
            _name = name;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Record(_name, _startedAt);
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Metric> Metrics = new(StringComparer.Ordinal);
    private static readonly string? OutputPrefix = Environment.GetEnvironmentVariable("PAPERTODO_E002_PROBE");
    private static long _moduleTimestamp;

    [ModuleInitializer]
    internal static void ModuleInit() => _moduleTimestamp = Stopwatch.GetTimestamp();

    internal static IDisposable Measure(string name) =>
        string.IsNullOrWhiteSpace(OutputPrefix) ? NullScope.Instance : new Scope(name);

    internal static T Measure<T>(string name, Func<T> action)
    {
        if (string.IsNullOrWhiteSpace(OutputPrefix)) return action();
        var startedAt = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { Record(name, startedAt); }
    }

    internal static void Measure(string name, Action action)
    {
        if (string.IsNullOrWhiteSpace(OutputPrefix))
        {
            action();
            return;
        }
        var startedAt = Stopwatch.GetTimestamp();
        try { action(); }
        finally { Record(name, startedAt); }
    }

    private static void Record(string name, long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        lock (Gate)
        {
            if (!Metrics.TryGetValue(name, out var metric))
            {
                metric = new Metric();
                Metrics[name] = metric;
            }
            metric.Count++;
            metric.TotalMilliseconds += elapsed;
            metric.MaxMilliseconds = Math.Max(metric.MaxMilliseconds, elapsed);
        }
    }

    internal static void Dump(string phase)
    {
        if (string.IsNullOrWhiteSpace(OutputPrefix)) return;
        Dictionary<string, object> metrics;
        lock (Gate)
        {
            metrics = Metrics.ToDictionary(
                pair => pair.Key,
                pair => (object)new
                {
                    count = pair.Value.Count,
                    totalMs = pair.Value.TotalMilliseconds,
                    maxMs = pair.Value.MaxMilliseconds,
                    averageMs = pair.Value.Count == 0 ? 0 : pair.Value.TotalMilliseconds / pair.Value.Count
                },
                StringComparer.Ordinal);
        }
        var payload = new
        {
            phase,
            pid = Environment.ProcessId,
            frequency = Stopwatch.Frequency,
            moduleTimestamp = _moduleTimestamp,
            dumpTimestamp = Stopwatch.GetTimestamp(),
            workingSet = Environment.WorkingSet,
            metrics
        };
        try
        {
            File.WriteAllText($"{OutputPrefix}-{phase}.json", JsonSerializer.Serialize(payload));
        }
        catch { }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
''')

# PaperWindow construction cost, including the intentionally light deferred-shell constructor.
replace_once(
    'src/PaperWindow.cs',
    '''    {\n        _paper = paper;\n        _controller = controller;''',
    '''    {\n        using var startupHostProbe = StartupHostProbe.Measure("paperWindow.ctor");\n        _paper = paper;\n        _controller = controller;''')

# Split the first host initialization into the major pieces that are candidates for post-start work.
text = read('src/PaperWindow.EdgeCapsule.cs')
text = text.replace(
    '''        _edgeCapsuleHost = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(''',
    '''        _edgeCapsuleHost = StartupHostProbe.Measure("host.create", () => EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(''',
    1)
text = text.replace(
    '''            EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id)));\n        var host = _edgeCapsuleHost;''',
    '''            EdgeCapsulePerformanceDiagnostics.ShortId(_paper.Id))));\n        var host = _edgeCapsuleHost;''',
    1)
text = text.replace(
    '''        _ = MeasureDeepCapsuleIconSlotWidth(DeepCapsuleSlotDpi().PixelsPerDip);''',
    '''        StartupHostProbe.Measure(\n            "host.iconMeasure",\n            () => _ = MeasureDeepCapsuleIconSlotWidth(DeepCapsuleSlotDpi().PixelsPerDip));''',
    1)
text = text.replace(
    '''        AttachDeepCapsuleSlotHostInput();\n        host.AttachNativeHooks(\n            OnDeepCapsuleSlotHostMessage,\n            CloseDeepCapsuleSlotContextMenu);\n        UpdateDeepCapsuleSlotHostTheme();''',
    '''        StartupHostProbe.Measure("host.attachInput", AttachDeepCapsuleSlotHostInput);\n        StartupHostProbe.Measure(\n            "host.attachNativeHooks",\n            () => host.AttachNativeHooks(\n                OnDeepCapsuleSlotHostMessage,\n                CloseDeepCapsuleSlotContextMenu));\n        StartupHostProbe.Measure("host.initialTheme", UpdateDeepCapsuleSlotHostTheme);''',
    1)
write('src/PaperWindow.EdgeCapsule.cs', text)

# Physical first-show apply / Show / forced layout.
replace_once(
    'src/EdgeCapsuleHost.cs',
    '''    {\n        if (_disposed || !frame.IsUsable)''',
    '''    {\n        using var startupHostProbe = StartupHostProbe.Measure("host.apply.total");\n        if (_disposed || !frame.IsUsable)''')
replace_once(
    'src/EdgeCapsuleHost.cs',
    '''            window.Show();''',
    '''            StartupHostProbe.Measure("host.windowShow", window.Show);''')
replace_once(
    'src/EdgeCapsuleHost.cs',
    '''            Root.UpdateLayout();''',
    '''            StartupHostProbe.Measure("host.forcedUpdateLayout", Root.UpdateLayout);''')

# Menu cost is intentionally moved below first-frame work.
replace_once(
    'src/PaperWindow.EdgeCapsuleContextMenu.cs',
    '''    {\n        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);''',
    '''    {\n        using var startupHostProbe = StartupHostProbe.Measure("menu.build");\n        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);''')

# Batch-specific boundaries plus two snapshots: visible first-frame work, then SystemIdle after the
# per-host menu callbacks that were queued earlier.
text = read('src/AppController.StartupEdgePresentation.cs')
text = text.replace(
    '''    {\n        var staged = new List<PaperWindow>();''',
    '''    {\n        using var startupBatchProbe = StartupHostProbe.Measure("startup.batch.total");\n        var staged = new List<PaperWindow>();''',
    1)
text = text.replace(
    '''        dispatcher.Invoke(DispatcherPriority.Render, static () => { });\n\n        foreach (var window in staged)''',
    '''        StartupHostProbe.Measure(\n            "startup.batch.hiddenRender",\n            () => dispatcher.Invoke(DispatcherPriority.Render, static () => { }));\n\n        using (StartupHostProbe.Measure("startup.batch.revealLoop"))\n        {\n        foreach (var window in staged)''',
    1)
text = text.replace(
    '''                window.RecoverStartupDeepCapsulePresentation();\n            }\n        }\n\n        // This is the only visible startup Render boundary''',
    '''                window.RecoverStartupDeepCapsulePresentation();\n            }\n        }\n        }\n\n        // This is the only visible startup Render boundary''',
    1)
text = text.replace(
    '''        dispatcher.Invoke(DispatcherPriority.Render, static () => { });\n    }''',
    '''        StartupHostProbe.Measure(\n            "startup.batch.visibleRender",\n            () => dispatcher.Invoke(DispatcherPriority.Render, static () => { }));\n        StartupHostProbe.Dump("visible");\n        dispatcher.BeginInvoke(\n            (Action)(() => StartupHostProbe.Dump("idle")),\n            DispatcherPriority.SystemIdle);\n    }''',
    1)
write('src/AppController.StartupEdgePresentation.cs', text)

print('E-002 temporary instrumentation applied')
