from pathlib import Path
import runpy
import sys

root = Path(sys.argv[1])
ns = runpy.run_path(str(root / '.github/e003-profile.py'))
method, replace, read, write = (ns[k] for k in ('method','replace','read','write'))

for path, name, label in [
 ('src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs','PrewarmLightweight','warm.composition'),
 ('src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs','RunLightweightPrewarmProbe','warm.compositionProbe'),
 ('src/EdgeCapsuleDragWindow.cs','TryPrewarmInfrastructure','warm.drag'),
 ('src/EdgeCapsuleDragWindow.cs','PrewarmInfrastructureAndPark','warm.dragPark'),
 ('src/AppController.Tray.cs','LoadTrayIconSource','tray.iconSource'),
 ('src/AppController.Tray.cs','CreateTrayMenu','tray.menuShell')
]:
 method(path,name,label)

p='src/AppController.Tray.cs'
replace(p, '        var trayIcon = new TaskbarIcon();', '        E003Probe.Mark("tray.beforeIcon");\n        var trayIcon = new TaskbarIcon();\n        E003Probe.Mark("tray.iconCreated");')
replace(p, '        trayIcon.Visibility = Visibility.Visible;', '        E003Probe.Mark("tray.beforeVisible");\n        trayIcon.Visibility = Visibility.Visible;\n        E003Probe.Mark("tray.visible");')

p='src/AppController.cs'
replace(p,'            Application.Current.Shutdown();', '            E003Probe.Mark("exit.beforeShutdown");\n            Application.Current.Shutdown();\n            E003Probe.Mark("exit.afterShutdown");')
replace(p,'            E003Probe.Report("real-exit");','            E003Probe.Mark("exit.beforeReport");\n            E003Probe.Report("real-exit");\n            Console.WriteLine("BEFORE_ENV_EXIT " + System.Diagnostics.Stopwatch.GetTimestamp());')
p='src/E003Probe.cs'
replace(p,'new { fixture, entries,','new { fixture, entries, originTicks = _origin, reportTicks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,')
p='tests/PaperTodo.LifecycleChecks/Program.cs'
replace(p,'            var endedAt = Stopwatch.GetTimestamp();','            var endedAt = Stopwatch.GetTimestamp();\n            Console.WriteLine("PROCESS_ENDED " + endedAt);')
print('Detailed idle/tray/exit instrumentation applied')
