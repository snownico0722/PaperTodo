from pathlib import Path
import runpy
import sys
root=Path(sys.argv[1]); mode=sys.argv[2]
ns=runpy.run_path(str(root/'.github/e003-detail.py'))
read,write,replace=(ns[k] for k in ('read','write','replace'))
p='tests/PaperTodo.LifecycleChecks/Program.cs'
s=read(p).replace('name == "real-exit"','name.StartsWith("real-exit")').replace('name == "scripts"','(name == "scripts" || name == "real-exit-scripts")')
s=s.replace('throw new InvalidOperationException("Exit unexpectedly returned");','return;')
s=s.replace('            app.Run();','''            app.Exit += (_, _) => Console.WriteLine("WPF_EXIT " + Stopwatch.GetTimestamp());
            app.Dispatcher.ShutdownFinished += (_, _) => Console.WriteLine("DISPATCHER_STOPPED " + Stopwatch.GetTimestamp());
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.WriteLine("RUNTIME_EXIT " + Stopwatch.GetTimestamp());
            app.Run();
            Console.WriteLine("APP_RUN_RETURN " + Stopwatch.GetTimestamp());''')
write(p,s)
if mode == 'natural':
 p='src/AppController.cs';s=read(p)
 start=s.index('        try\n',s.index('        _lifecycleState = AppLifecycleState.Disposed;',s.index('    public void Exit()')))
 end=s.index('    private static void TryExitCleanup',start)
 s=s[:start]+'''        E003Probe.Mark("exit.beforeShutdown");
        Application.Current.Shutdown();
        E003Probe.Mark("exit.afterShutdown");
        E003Probe.Report("real-exit");
        Console.WriteLine("BEFORE_ENV_EXIT " + System.Diagnostics.Stopwatch.GetTimestamp());
    }

'''+s[end:]
 write(p,s)
print('Exit variant',mode)
