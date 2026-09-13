from pathlib import Path
import sys
root=Path(sys.argv[1])
def replace(path,old,new):
 p=root/path;s=p.read_text(encoding='utf-8-sig')
 if s.count(old)!=1:raise RuntimeError(f'{path}: anchor is not unique')
 p.write_text(s.replace(old,new),encoding='utf-8',newline='\n')
p=root/'src/StartupCompilationProfile.cs'
if p.exists():raise RuntimeError('candidate already applied')
p.write_text('''using System.IO;
using System.Runtime;

namespace PaperTodo;

internal static class StartupCompilationProfile
{
    internal static void Start(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // The CLR records method use and compiles likely methods on a background thread;
            // it never constructs WPF objects there. Missing/stale profiles remain optional.
            ProfileOptimization.SetProfileRoot(directory);
            ProfileOptimization.StartProfile("startup.prof");
        }
        catch (IOException)
        {
            // A disposable compilation cache must not prevent normal startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only/restricted profiles fall back to ordinary JIT.
        }
    }
}
''',encoding='utf-8',newline='\n')
replace('App.xaml.cs','''        _singleInstance.StartListener(HandleSingleInstanceCommand);

        base.OnStartup(e);''','''        _singleInstance.StartListener(HandleSingleInstanceCommand);

        // Only the GUI owner records startup. Secondary commands and the MCP bridge must not
        // overwrite its profile; this cache is independent of portable paper/user data.
        if (startupCommand.Kind != StartupCommandKind.Exit)
        {
            StartupCompilationProfile.Start(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaperTodo", "Cache", "StartupCompilation"));
        }

        base.OnStartup(e);''')
replace('tests/PaperTodo.PersistenceChecks/Program.cs','''    ("replace-retries-transient-sharing-failures", ReplaceRetriesTransientSharingFailures)
''','''    ("replace-retries-transient-sharing-failures", ReplaceRetriesTransientSharingFailures),
    ("optional-compilation-cache-never-blocks-state", OptionalCompilationCacheNeverBlocksState)
''')
replace('tests/PaperTodo.PersistenceChecks/Program.cs','''static void PrimarySaveFaultsKeepALoadableGeneration()
''','''static void OptionalCompilationCacheNeverBlocksState()
{
    using var scope = new TempDirectory();
    var blocked = Path.Combine(scope.Path, "not-a-directory");
    File.WriteAllText(blocked, "keep this file");
    StartupCompilationProfile.Start(blocked);
    Assert(File.ReadAllText(blocked) == "keep this file", "profile setup changed an unrelated file");

    var cache = Path.Combine(scope.Path, "compilation");
    Directory.CreateDirectory(cache);
    File.WriteAllText(Path.Combine(cache, "startup.prof"), "not a valid CLR profile");
    try
    {
        StartupCompilationProfile.Start(cache);
        // A bad optional profile must neither throw nor bypass the ordinary persistence path.
        var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        store.SaveJsonSync(store.SerializeState(NewState("dark")), version: 1);
        Assert(store.Load().Theme == "dark", "profile failure affected main state");
    }
    finally
    {
        System.Runtime.ProfileOptimization.StartProfile(null);
    }
}

static void PrimarySaveFaultsKeepALoadableGeneration()
''')
print('E004 production candidate applied (no measurement hooks)')
