from pathlib import Path
import sys
root=Path(sys.argv[1])
def one(p,a,b):
 f=root/p;t=f.read_text(encoding='utf-8-sig')
 if t.count(a)!=1:raise RuntimeError(f'{p} count {t.count(a)} {a[:80]}')
 f.write_text(t.replace(a,b,1),encoding='utf-8',newline='\n')
(root/'src/E003AppProbe.cs').write_text(r'''using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
namespace PaperTodo;
// Temporary experiment recorder. Not retained in the production PR.
internal static class E003App
{
    private static readonly List<(string Name, long Tick)> Marks = new();
    private static readonly string? Output = Environment.GetEnvironmentVariable("PAPERTODO_E003_DIR");
    [ModuleInitializer]
    internal static void Init()
    {
        Mark("managed");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Mark("process-exit-callback");
            if (Output == null) return;
            using var writer = new StreamWriter(Path.Combine(Output, "app-trace.tsv"));
            writer.WriteLine("name\ttick\tfrequency");
            lock (Marks) foreach (var row in Marks)
                writer.WriteLine($"{row.Name}\t{row.Tick}\t{Stopwatch.Frequency}");
        };
    }
    internal static void Mark(string name)
    {
        if (Output == null) return;
        var tick = Stopwatch.GetTimestamp();
        lock (Marks) Marks.Add((name, tick));
    }
    internal static void Observe(AppController controller)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!controller.E003Visible) return;
            CompositionTarget.Rendering -= handler;
            Mark("visible-render-callback");
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                WindowNative.FlushDesktopComposition();
                Mark("dwm-proxy");
                if (Output != null) File.WriteAllText(Path.Combine(Output, "ready"), "ready");
            }));
        };
        CompositionTarget.Rendering += handler;
        Application.Current.Exit += (_, _) => Mark("app-exit");
        Application.Current.Dispatcher.ShutdownFinished += (_, _) => Mark("dispatcher-finished");
    }
}
public sealed partial class AppController
{
    internal bool E003Visible => _windows.Count == 10 && _windows.Values.All(window => window.E003Visible);
}
public sealed partial class PaperWindow
{
    internal bool E003Visible => IsVisible && Opacity > 0 ||
        _edgeCapsuleHost is { IsVisible: true } host && host.E003Revealed;
}
''',encoding='utf-8')
one('src/EdgeCapsuleHost.cs','    private Window Window { get; }','    private Window Window { get; }\n    internal bool E003Revealed => Window.Opacity > 0;')
one('App.xaml.cs','            _controller = new AppController();','            E003App.Mark("controller-begin");\n            _controller = new AppController();\n            E003App.Mark("controller-ready");\n            E003App.Observe(_controller);')
one('App.xaml.cs','        CompleteSingleInstanceStartup();','        CompleteSingleInstanceStartup();\n        E003App.Mark("app-startup-complete");')
one('src/AppController.cs','        await RestorePaperSurfacesAsync(papersToRestore);','        await RestorePaperSurfacesAsync(papersToRestore);\n        E003App.Mark("restore-return");')
one('src/AppController.cs','        CommitSettingsExternalMarkdownEditor(saveImmediately: false);','        E003App.Mark("exit-request");\n        CommitSettingsExternalMarkdownEditor(saveImmediately: false);')
one('src/AppController.cs','        ClearPaperLinkDropTarget();\n        _deepCapsuleContextMenuOwners.Clear();','        E003App.Mark("ui-hidden");\n        ClearPaperLinkDropTarget();\n        _deepCapsuleContextMenuOwners.Clear();')
one('src/PaperWindow.cs','        _isShellBuilt = true;','        _isShellBuilt = true;\n        E003App.Mark("shell-built");')
one('src/EdgeCapsulePreview.Preload.cs','        _artifacts[key.Binding.Source] = new(key, artifact);','        _artifacts[key.Binding.Source] = new(key, artifact);\n        if (_artifacts.Count == 10) E003App.Mark("artifacts-full");')
print('Applied temporary real-app observer')
