from pathlib import Path
import sys,subprocess
root=Path(sys.argv[1]);mode=sys.argv[2];tools=Path(__file__).parent
subprocess.run([sys.executable,str(tools/'e003-candidate.py'),str(root),mode],check=True)
probe=(tools/'e003-probe.py').read_text(encoding='utf-8')
probe=probe.replace('def wrap(p,statement,name):\n replace(p,statement,', '''def wrap(p,statement,name):
 if name == 'Restore.idleWait' and statement not in read(p):
  statement=statement.replace('DispatcherPriority.ApplicationIdle','DispatcherPriority.ContextIdle')
 if name == 'Startup.tray' and statement not in read(p):
  statement=statement.strip()
 replace(p,statement,''')
ns={'__name__':'__main__'}
sys.argv=[str(tools/'e003-probe.py'),str(root),'baseline']
exec(compile(probe,str(tools/'e003-probe.py'),'exec'),ns)
scope=ns['scope'];wrap=ns['wrap'];replace=ns['replace']
scope('src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs','internal static void PrewarmLightweight(','Composition.prewarm')
scope('src/EdgeCapsuleQueueCompositionProxy.LightPrewarm.cs','private static void RunLightweightPrewarmProbe(','Composition.probe')
replace('src/AppController.Tray.cs','        var trayIcon = new TaskbarIcon();','        TaskbarIcon trayIcon;\n        using (E003Probe.Measure("Tray.constructor")) { trayIcon = new TaskbarIcon(); }')
wrap('src/AppController.Tray.cs','        trayIcon.IconSource = LoadTrayIconSource();','Tray.icon')
wrap('src/AppController.Tray.cs','        _trayMenu = CreateTrayMenu();','Tray.menuShell')
wrap('src/AppController.Tray.cs','        trayIcon.Visibility = Visibility.Visible;','Tray.show')
wrap('src/AppController.cs','            Application.Current.Shutdown();','Exit.applicationShutdown')
# Do not serialize full diagnostics inside ProcessExit: that can distort external exit timing.
replace('src/E003Probe.cs','        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();', '''        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("E003_NO_EXIT_DUMP") != "1") Dump();
        };''')
if mode!='baseline':
 replace('tests/PaperTodo.LifecycleChecks/Program.cs','            E003Probe.Mark("All.ready");', '''            E003Probe.Mark("All.ready");
            if (name.StartsWith("capsules-") && count <= 10)
            {
                Require(cache.ArtifactCount == count, "preview-first startup lost artifacts");
                Require(cache.WarmCompletions == count, "initial Shell rebuilt completed artifacts");
            }''')
