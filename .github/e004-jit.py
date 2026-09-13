from pathlib import Path
import sys
root=Path(sys.argv[1])
def read(p):return (root/p).read_text(encoding='utf-8-sig')
def write(p,s): (root/p).write_text(s,encoding='utf-8',newline='\n')
def replace(p,a,b,n=1):
 s=read(p)
 if s.count(a)!=n:raise RuntimeError(f'{p}: expected {n}, saw {s.count(a)} for {a[:70]!r}')
 write(p,s.replace(a,b))
write('src/E004JitProbe.cs','''using System.Diagnostics;
using System.Text.Json;
namespace PaperTodo;
internal static class E004JitProbe
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    internal sealed record Entry(string Name, long Tick);
    internal static void Mark(string name)
    {
        var tick = Stopwatch.GetTimestamp();
        lock(Gate) Entries.Add(new(name,tick));
    }
    internal static void Report(string fixture)
    {
        Entry[] entries; lock(Gate) entries=Entries.ToArray();
        using var process=Process.GetCurrentProcess();
        Console.WriteLine("E004_JIT "+JsonSerializer.Serialize(new {fixture,entries,
            workingSet=process.WorkingSet64,privateBytes=process.PrivateMemorySize64,
            cpuMs=process.TotalProcessorTime.TotalMilliseconds,allocated=GC.GetTotalAllocatedBytes(false),
            frequency=Stopwatch.Frequency}));
    }
}
''')
p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };','''            var profileRoot = Environment.GetEnvironmentVariable("E004_JIT_PROFILE");
            if (!string.IsNullOrEmpty(profileRoot))
            {
                System.Runtime.ProfileOptimization.SetProfileRoot(profileRoot);
                System.Runtime.ProfileOptimization.StartProfile("startup.prof");
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };''')
replace(p,'            var baseline = args.Contains("--baseline");','''            var single = args.FirstOrDefault(arg => arg.StartsWith("--e004-case="));
            if (single != null) { RunIsolated(single["--e004-case=".Length..], false); return 0; }
            var baseline = args.Contains("--baseline");''')
replace(p,'            using var child = Process.Start(start)!;','''            var processStartedAt = Stopwatch.GetTimestamp();
            using var child = Process.Start(start)!;''')
replace(p,'            Console.Write(text); Console.Error.Write(error.GetAwaiter().GetResult());','''            Console.WriteLine("E004_EXTERNAL " + processStartedAt);
            Console.Write(text); Console.Error.Write(error.GetAwaiter().GetResult());''')
replace(p,'        var count = name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;','        var count = name == "expanded-1" ? 1 : name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;')
replace(p,'IsVisible = true, IsCollapsed = true, X = area.Left + 60','IsVisible = true, IsCollapsed = name != "expanded-1", X = area.Left + 60')
replace(p,'        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();','        E004JitProbe.Mark("controller.begin");\n        var started = Stopwatch.GetTimestamp();\n        var controller = new AppController();')
replace(p,'        var constructed = Stopwatch.GetTimestamp();','        var constructed = Stopwatch.GetTimestamp();\n        E004JitProbe.Mark("controller.ready");')
replace(p,'            await controller.StartAsync(createDefaultPaper: false);','''            EventHandler? rendered = null;
            rendered = (_, _) =>
            {
                if (windows.Values.Count(window => window.HasVisibleSurface) < count) return;
                E004JitProbe.Mark("rendering");
                System.Windows.Media.CompositionTarget.Rendering -= rendered;
            };
            System.Windows.Media.CompositionTarget.Rendering += rendered;
            await controller.StartAsync(createDefaultPaper: false);
            E004JitProbe.Mark("startup.returned");''')
replace(p,'            var ready = Stopwatch.GetTimestamp();','''            var ready = Stopwatch.GetTimestamp();
            E004JitProbe.Mark("ready");
            E004JitProbe.Report(name);''')
replace('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E004JitProbe.Mark("shell.built");')
replace('src/EdgeCapsulePreview.Preload.cs','        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        E004JitProbe.Mark("cache.stored");')
print('E004 minimal-marker JIT probe applied')
