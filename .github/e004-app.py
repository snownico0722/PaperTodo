from pathlib import Path
import sys
root=Path(sys.argv[1])
def patch(path,old,new):
 p=root/path;s=p.read_text(encoding='utf-8-sig')
 if s.count(old)!=1:raise RuntimeError(f'{path}: {old[:65]!r} count {s.count(old)}')
 p.write_text(s.replace(old,new),encoding='utf-8',newline='\n')
(root/'src/E004AppProbe.cs').write_text('''using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
namespace PaperTodo;
internal static class E004AppProbe
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = new();
    private static readonly string? PathName = Environment.GetEnvironmentVariable("E004_APP_LOG");
    internal static void Mark(string name)
    {
        var tick = Stopwatch.GetTimestamp();
        if (PathName == null) return;
        lock (Gate) Pending.Add($"{Environment.ProcessId}|{name}|{tick}|{Stopwatch.Frequency}");
    }
    internal static void Flush()
    {
        if (PathName == null) return;
        lock (Gate) { File.AppendAllLines(PathName, Pending); Pending.Clear(); }
    }
    internal static void ExitSample()
    {
        Mark("ExitRequest");
        using var p=Process.GetCurrentProcess();
        lock(Gate) Pending.Add($"{Environment.ProcessId}|IdleMetrics|{p.WorkingSet64}|{p.PrivateMemorySize64}|{p.TotalProcessorTime.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Flush();
    }
    internal static void Observe(AppController controller)
    {
        var windows=(Dictionary<string,PaperWindow>)typeof(AppController)
            .GetField("_windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
        var count=controller.State.Papers.Count(p=>p.IsVisible);
        EventHandler? handler=null;
        handler=(_,_)=>
        {
            if(windows.Count<count || windows.Values.Count(w=>w.HasVisibleSurface)<count) return;
            Mark("Rendering");
            CompositionTarget.Rendering-=handler;
        };
        CompositionTarget.Rendering+=handler;
    }
}
''',encoding='utf-8',newline='\n')
patch('App.xaml.cs','            _controller = new AppController();','            _controller = new AppController();\n            E004AppProbe.Observe(_controller);')
patch('App.xaml.cs','                    _singleInstanceCommandsReady = true;\n                    return;','                    _singleInstanceCommandsReady = true;\n                    E004AppProbe.Mark("Ready");\n                    E004AppProbe.Flush();\n                    return;')
patch('App.xaml.cs','        base.OnExit(e);','        base.OnExit(e);\n        E004AppProbe.Mark("OnExitComplete");\n        E004AppProbe.Flush();')
patch('src/AppController.cs','    public void Exit()\n    {','    public void Exit()\n    {\n        E004AppProbe.ExitSample();')
patch('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E004AppProbe.Mark("Shell");')
patch('src/EdgeCapsulePreview.Preload.cs','        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        E004AppProbe.Mark("Cache");')
if (root/'src/StartupCompilationProfile.cs').exists():
 patch('App.xaml.cs','''StartupCompilationProfile.Start(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaperTodo", "Cache", "StartupCompilation"));''','''StartupCompilationProfile.Start(Environment.GetEnvironmentVariable("E004_APP_PROFILE") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaperTodo", "Cache", "StartupCompilation"));''')
 patch('src/StartupCompilationProfile.cs','            ProfileOptimization.StartProfile("startup.prof");','            ProfileOptimization.StartProfile("startup.prof");\n            E004AppProbe.Mark("ProfileStarted");')
print('Real App timing hooks applied; profile path isolated for this experiment')
