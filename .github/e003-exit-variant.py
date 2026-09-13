from pathlib import Path
import sys
root=Path(sys.argv[1])
p=root/'src/AppController.cs'
s=p.read_text(encoding='utf-8-sig')
a='''        try
        {
            Application.Current.Shutdown();
        }
        finally
        {
            Environment.Exit(0);
        }'''
b='''            Application.Current.Shutdown();'''
if s.count(a)!=1:raise RuntimeError('exit anchor missing')
p.write_text(s.replace(a,b,1),encoding='utf-8',newline='\n')
p=root/'tests/PaperTodo.LifecycleChecks/Program.cs'
s=p.read_text(encoding='utf-8-sig')
a='                controller.Exit();\n                throw new InvalidOperationException("Exit unexpectedly returned");'
b='                controller.Exit();\n                return;'
if s.count(a)!=1:raise RuntimeError('exit test anchor missing')
p.write_text(s.replace(a,b,1),encoding='utf-8',newline='\n')
