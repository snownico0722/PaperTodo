from pathlib import Path
import sys
root=Path(sys.argv[1])
def replace(path,a,b):
 p=root/path;s=p.read_text(encoding='utf-8-sig')
 if s.count(a)!=1:raise RuntimeError(f'{path}: {a[:80]}: {s.count(a)}')
 p.write_text(s.replace(a,b,1),encoding='utf-8',newline='\n')
replace('App.xaml.cs','    protected override async void OnStartup(StartupEventArgs e)\n    {','    protected override async void OnStartup(StartupEventArgs e)\n    {\n        E003AppProbe.Attach(this);')
replace('App.xaml.cs','new SingleInstanceHelper("PaperTodo-SingleInstance-Mutex", "PaperTodo-SingleInstance-Activate")','new SingleInstanceHelper("E003-Mutex-" + Environment.GetEnvironmentVariable("E003_RUN_ID"), "E003-Pipe-" + Environment.GetEnvironmentVariable("E003_RUN_ID"))')
replace('App.xaml.cs','            _controller = new AppController();','            _controller = new AppController();\n            E003AppProbe.Mark("controller_ready");')
replace('App.xaml.cs','        if (!_controller.IsRunning)\n        {','        E003AppProbe.Mark("startup_return");\n        if (!_controller.IsRunning)\n        {')
replace('App.xaml.cs','    protected override void OnExit(ExitEventArgs e)\n    {','    protected override void OnExit(ExitEventArgs e)\n    {\n        E003AppProbe.Mark("app_on_exit");')
replace('src/PaperWindow.cs','        ReplayPluginRuntimePresentation();\n    }\n\n    private void HandleWindowGeometryChanged()', '        ReplayPluginRuntimePresentation();\n        AppController.Current.RecordE003Shells();\n    }\n\n    private void HandleWindowGeometryChanged()')
replace('src/EdgeCapsulePreview.Preload.cs','                WarmCompletions++;','                WarmCompletions++;\n                if (ArtifactCount == 10) E003AppProbe.Mark("artifacts_ready");')
replace('src/AppController.Tray.cs','        trayIcon.Visibility = Visibility.Visible;','        trayIcon.Visibility = Visibility.Visible;\n        E003AppProbe.Mark("tray_ready");')
replace('src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs','        state.Attempted = true;','        state.Attempted = true;\n        using var e003Composition = new E003AppProbe.EndMark("composition_ready");')
replace('src/AppController.cs','    public void Exit()\n    {','    public void Exit()\n    {\n        E003AppProbe.Mark("exit_enter");')
replace('src/AppController.cs','            TryExitCleanup(() => surface.Hide());\n        ClearPaperLinkDropTarget();','            TryExitCleanup(() => surface.Hide());\n        E003AppProbe.Mark("exit_ui_hidden");\n        ClearPaperLinkDropTarget();')
p=root/'src/AppController.cs';s=p.read_text(encoding='utf-8-sig');i=s.index('    public void Exit()');j=s.index('    private static void TryExitCleanup',i);part=s[i:j]
a='        DisposeRuntimeResources();'
if part.count(a)!=1:raise RuntimeError('Exit cleanup marker')
part=part.replace(a,'        E003AppProbe.Mark("exit_saved");\n'+a+'\n        E003AppProbe.Mark("exit_cleanup_done");',1)
p.write_text(s[:i]+part+s[j:],encoding='utf-8',newline='\n')
(root/'src/E003AppProbe.cs').write_text(r'''using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
namespace PaperTodo;
internal static class E003AppProbe
{
    private static readonly Dictionary<string, long> Marks = new();
    private static readonly object Gate = new();
    private static bool _ready;
    [ModuleInitializer]
    internal static void Initialize()
    {
        Mark("module");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Mark("process_exit_event"); Flush(); };
    }
    internal static void Attach(Application app)
    {
        Mark("app_startup");
        EventHandler? rendering = null;
        rendering = (_, _) =>
        {
            if (AppController.Current?.E003CapsulesVisible != true) return;
            CompositionTarget.Rendering -= rendering;
            Mark("capsules_rendering");
            _ = app.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (DwmFlush() != 0) throw new InvalidOperationException("DwmFlush failed");
                Mark("capsules_dwm_boundary");
            }));
        };
        CompositionTarget.Rendering += rendering;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    internal readonly struct EndMark(string name) : IDisposable
    {
        public void Dispose() => Mark(name);
    }
    internal static void Mark(string name)
    {
        var ticks = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            Marks.TryAdd(name, ticks);
            if (!_ready && new[] {"startup_return", "shells_ready", "artifacts_ready", "capsules_dwm_boundary", "composition_ready", "tray_ready"}.All(Marks.ContainsKey))
            {
                _ready = true;
                using var p = Process.GetCurrentProcess(); p.Refresh();
                Marks["ready_working_set_bytes"] = p.WorkingSet64;
                Marks["ready_private_bytes"] = p.PrivateMemorySize64;
                Flush();
                File.WriteAllText(Environment.GetEnvironmentVariable("E003_READY")!, "ready");
            }
        }
    }
    private static void Flush()
    {
        lock (Gate)
        {
            var path = Environment.GetEnvironmentVariable("E003_EVENTS");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllLines(path, new[] {"stage,ticks"}.Concat(Marks.Select(p => p.Key + "," + p.Value.ToString(CultureInfo.InvariantCulture))));
        }
    }
}
public sealed partial class AppController
{
    internal bool E003CapsulesVisible => _windows.Count == 10 && _windows.Values.All(w => w.IsDeepCapsuleSlotVisible);
    internal void RecordE003Shells()
    {
        if (_windows.Count == 10 && _windows.Values.All(w => w.IsShellBuilt)) E003AppProbe.Mark("shells_ready");
    }
}
''',encoding='utf-8',newline='\n')
