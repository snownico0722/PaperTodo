from pathlib import Path
import json
import sys
root=Path(sys.argv[1]);action=sys.argv[2]
backup=root/'.e003-smoke-originals.json'
paths=['App.xaml.cs','src/AppController.cs']
if action=='restore':
 for name,text in json.loads(backup.read_text(encoding='utf-8')).items():
  (root/name).write_text(text,encoding='utf-8',newline='\n')
 (root/'src/E003AppSmoke.cs').unlink()
 backup.unlink()
 raise SystemExit(0)
if action!='instrument' or backup.exists(): raise SystemExit('invalid smoke state')
original={name:(root/name).read_text(encoding='utf-8-sig') for name in paths}
backup.write_text(json.dumps(original),encoding='utf-8')
def replace(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig')
 if s.count(old)!=1: raise SystemExit('smoke anchor '+name+old[:90])
 p.write_text(s.replace(old,new),encoding='utf-8',newline='\n')
replace('App.xaml.cs','        CompleteSingleInstanceStartup();','        CompleteSingleInstanceStartup();\n        E003AppSmoke.Mark("Ready");')
replace('App.xaml.cs','        base.OnExit(e);','        base.OnExit(e);\n        E003AppSmoke.Mark("OnExitComplete");')
replace('src/AppController.cs','    public void Exit()\n    {','    public void Exit()\n    {\n        E003AppSmoke.Mark("ExitRequest");')
(root/'src/E003AppSmoke.cs').write_text('''using System.Diagnostics;
using System.IO;
namespace PaperTodo;
internal static class E003AppSmoke
{
    internal static void Mark(string name)
    {
        var path = Environment.GetEnvironmentVariable("PAPERTODO_E003_SMOKE");
        if (string.IsNullOrEmpty(path)) return;
        File.AppendAllText(path, $"{Environment.ProcessId}|{name}|{Stopwatch.GetTimestamp()}|{Stopwatch.Frequency}\\n");
    }
}
''',encoding='utf-8')
print('Real App smoke instrumentation applied')
