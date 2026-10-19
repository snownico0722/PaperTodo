from pathlib import Path
import os
import subprocess
import sys
import json

ROOT = Path.cwd()
TEMP = Path(os.environ['RUNNER_TEMP']) / 'papertodo-review-followup'
TEMP.mkdir(parents=True, exist_ok=True)
OLD_SYSTEM = '97a65af962b6bde25dea0cfb52f44aa83d82e8da'
OLD_CAPSULE = 'a9b45477fab54c34afa3f56644e4c918bf567a91'
PAPER = '815de7a29964ef4900c2babfcc327b4a31c3c133'
BASE = '05a798582929cdb08fb57e023fa2fd5e9735965a'
SYSTEM_BRANCH = 'backport/3x-system-startup-1019'
CAPSULE_BRANCH = 'backport/3x-capsule-memory-1019'
MARKER = '.github/backport3x-review-capsule.tmp'


def run(*args):
    subprocess.run(list(args), check=True)


def git(*args):
    return subprocess.check_output(['git', *args], text=True, encoding='utf-8').strip()


def replace(path, old, new):
    p = ROOT / path
    text = p.read_text(encoding='utf-8')
    if text.count(old) != 1:
        raise RuntimeError(f'Expected one exact anchor in {path}: {text.count(old)}')
    p.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


def commit(message, *paths):
    run('git', 'add', '--', *paths)
    run('git', 'diff', '--cached', '--check')
    run('git', 'commit', '-m', message)
    return git('rev-parse', 'HEAD')


def combined(system, capsule):
    run('git', 'checkout', '--detach', system)
    run('git', 'merge', '--no-ff', '--no-edit', capsule)
    run('git', 'merge', '--no-ff', '--no-edit', PAPER)
    run('git', 'diff', BASE, 'HEAD', '--check')


def prepare():
    source_head = os.environ['SOURCE_HEAD']
    run('git', 'checkout', '--detach', source_head)
    wait = '''        var startupRestoreGeneration = _paperSurfaceRestoreGeneration;
        await WaitForStartupDisplayTopologyAsync();
        // A show/hide command or exit received during the wait owns the newer presentation.
        if (IsExiting || startupRestoreGeneration != _paperSurfaceRestoreGeneration) return;

'''
    replace('src/AppController.cs', wait, '')
    replace('src/AppController.cs', '''        StartupCommandKind initialVisibilityCommand = StartupCommandKind.None)
    {
        CreateTrayIcon();''', '''        StartupCommandKind initialVisibilityCommand = StartupCommandKind.None)
    {
        if (IsExiting) return;

        // Do not publish tray/hotkey entry points until display enumeration settles: their
        // show-all path performs off-screen rescue and would otherwise overwrite saved positions.
        var startupRestoreGeneration = _paperSurfaceRestoreGeneration;
        await WaitForStartupDisplayTopologyAsync();
        if (IsExiting || startupRestoreGeneration != _paperSurfaceRestoreGeneration) return;

        CreateTrayIcon();''')
    replace('CHANGELOG.md', '- 开机时副屏尚未就绪的情况下，短暂保留纸片原位置，避免提前移回主屏并覆盖原坐标；真正拔掉副屏时仍会恢复到可见区域。', '- 开机时副屏尚未就绪的情况下，先短暂等待再开放托盘和快捷键操作，避免提前移回主屏并覆盖纸片原坐标；真正拔掉副屏时仍会恢复到可见区域。')
    system = commit('fix(3.x): 显示器等待完成前不开放托盘和全局快捷键', 'src/AppController.cs', 'CHANGELOG.md')

    run('git', 'checkout', '--detach', OLD_CAPSULE)
    replace('src/AppController.cs', '''        if (!State.UseCapsuleCollapseAll)
        {
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
            ResetDeepCapsuleStartTopMargins();
        }''', '''        if (!State.UseCapsuleCollapseAll)
        {
            // Disabling the master only clears retraction, not the user's queue positions.
            State.CapsuleCollapseAllActive = false;
            State.CapsuleCollapseAllActiveQueues.Clear();
        }''')
    replace('CHANGELOG.md', '- 关闭胶囊或贴边功能后仍保留各队列调整过的高度，再次开启时恢复；明确恢复默认设置时才重置。', '- 关闭普通胶囊、贴边胶囊或主胶囊后，仍保留各队列调整过的高度，重启和再次开启时不丢失；明确恢复默认设置时才重置。')
    capsule_product = commit('fix(3.x): 关闭主胶囊不再清空队列高度记忆', 'src/AppController.cs', 'CHANGELOG.md')
    (ROOT / MARKER).write_text('Temporary validation marker; remove after verified fast-forward to trigger final-head CI.\n', encoding='utf-8')
    capsule = commit('test(3.x): 标记待清理的审查验证入口', MARKER)
    combined(system, capsule)
    result = dict(system=system, capsule=capsule, capsule_product=capsule_product, source_head=source_head, paper=PAPER, combined_tree=git('rev-parse', 'HEAD^{tree}'))
    (TEMP / 'commits.json').write_text(json.dumps(result), encoding='utf-8')
    print('REVIEW_COMMITS ' + json.dumps(result), flush=True)


CS = r'''using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

static class ReviewChecks
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object? Field(object obj, string name) => obj.GetType().GetField(name, Flags)!.GetValue(obj);
    static object? Call(object obj, string name, params object?[] args)
        => obj.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(obj, args);
    static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    static void Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        } while (Environment.TickCount64 < end);
    }
    static void Complete(Task task)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) Pump(20);
        Assert(task.IsCompleted, "startup completes within its bounded wait");
        task.GetAwaiter().GetResult();
    }
    static void Startup(App app, AppController c, bool before, bool exit)
    {
        c.State.UseCapsuleMode = false;
        c.State.UseDeepCapsuleMode = false;
        var paper = new PaperData { Id = "review-startup", Type = PaperTypes.Note, Content = "review", X = 100000, Y = 100000, Width = 300, Height = 240, IsVisible = true };
        c.State.Papers.Add(paper);
        var task = c.StartAsync(createDefaultPaper: false);
        Pump(300);
        if (before)
        {
            Assert(!task.IsCompleted && Field(c, "_trayIcon") != null && Field(c, "_globalHotkeys") != null,
                "BEFORE: tray and hotkey entry points exist during display wait");
            c.ShowAllPapers(); // This is the registered tray-double-click action.
            Assert(paper.X != 100000 || paper.Y != 100000,
                "BEFORE: tray show-all action bypasses the wait and rewrites off-screen coordinates");
            Complete(task);
            return;
        }
        Assert(!task.IsCompleted, "off-screen startup is still waiting");
        Assert(Field(c, "_trayIcon") == null && Field(c, "_globalHotkeys") == null,
            "AFTER: no tray or registered hotkey path can bypass display wait");
        Assert(paper.X == 100000 && paper.Y == 100000 && ((IDictionary)Field(c, "_windows")!).Count == 0,
            "waiting neither creates paper surfaces nor changes their coordinates");
        if (exit)
        {
            c.Dispose();
            Complete(task);
            Assert(Field(c, "_trayIcon") == null && Field(c, "_globalHotkeys") == null && ((IDictionary)Field(c, "_windows")!).Count == 0,
                "exit during wait does not create tray, hotkeys or paper windows afterwards");
            return;
        }
        typeof(App).GetField("_controller", Flags)!.SetValue(app, c);
        Call(app, "HandleSingleInstanceCommand", (object)new List<string> { "hide" });
        var queue = (ICollection)Field(app, "_pendingSingleInstanceCommands")!;
        Assert(queue.Count == 1 && paper.IsVisible, "secondary-instance hide stays queued during startup");
        Pump(200);
        Assert(paper.X == 100000 && Field(c, "_trayIcon") == null, "queued command does not bypass wait");
        Complete(task);
        Assert(Field(c, "_trayIcon") != null && Field(c, "_globalHotkeys") != null,
            "tray and hotkeys are published after wait");
        Assert(paper.X != 100000 || paper.Y != 100000,
            "a genuinely disconnected display still receives normal off-screen rescue");
        Call(app, "CompleteSingleInstanceStartup");
        Assert(queue.Count == 0 && !paper.IsVisible, "queued hide executes after startup without being overwritten");
    }
    static void Heights(AppController c, bool before)
    {
        string left = "|left", right = "|right";
        foreach (var toggle in before ? new[] { "ToggleCapsuleCollapseAll" } : new[] { "ToggleCapsuleMode", "ToggleDeepCapsuleMode", "ToggleCapsuleCollapseAll" })
        {
            c.State.UseCapsuleMode = true;
            c.State.UseDeepCapsuleMode = true;
            c.State.UseCapsuleCollapseAll = true;
            c.State.DeepCapsuleStartTopMargin = 93;
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[left] = 135;
            c.State.DeepCapsuleQueueStartTopMargins[right] = 245;
            c.State.CapsuleCollapseAllActive = true;
            c.State.CapsuleCollapseAllActiveQueues[left] = true;
            Call(c, toggle);
            if (before)
            {
                Assert(c.State.DeepCapsuleQueueStartTopMargins.Count == 0 && c.State.DeepCapsuleStartTopMargin != 93,
                    "BEFORE: disabling master capsule deletes global and per-queue heights");
                return;
            }
            void Check(AppState state, string stage)
            {
                Assert(state.DeepCapsuleStartTopMargin == 93 && state.DeepCapsuleQueueStartTopMargins.GetValueOrDefault(left) == 135 && state.DeepCapsuleQueueStartTopMargins.GetValueOrDefault(right) == 245,
                    toggle + " preserves heights " + stage);
            }
            Check(c.State, "immediately");
            Assert(!c.State.CapsuleCollapseAllActive && c.State.CapsuleCollapseAllActiveQueues.Count == 0,
                toggle + " still clears transient retraction state");
            c.SaveNow(sync: true);
            var loaded = new StateStore().Load();
            Check(loaded, "after synchronous save and reload while disabled");
            typeof(AppController).GetProperty("State", Flags)!.SetValue(c, loaded);
            Call(c, toggle);
            Check(c.State, "after re-enabling from reloaded settings");
            c.SaveNow(sync: true);
            Check(new StateStore().Load(), "after enabled-state save and reload");
        }
        Call(c, "RestoreGeneralSettingsPageDefaults");
        c.State.EnableAnimations = false;
        Assert(c.State.DeepCapsuleQueueStartTopMargins.Count == 0 && c.State.DeepCapsuleStartTopMargin == new AppState().DeepCapsuleStartTopMargin,
            "only explicit restore-defaults resets both height formats");
        c.SaveNow(sync: true);
        Assert(new StateStore().Load().DeepCapsuleQueueStartTopMargins.Count == 0, "explicit reset persists");
    }
    [STAThread] static int Main(string[] args)
    {
        AppController? controller = null;
        try
        {
            // The executable lives in a unique RUNNER_TEMP directory, never a user data folder.
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "data*.json*")) File.Delete(file);
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            controller = new AppController();
            controller.State.Papers.Clear();
            controller.State.EnableAnimations = false;
            controller.State.UsePersistentPowerShellProcess = false;
            bool before = args.Contains("before");
            if (args[0] == "heights") Heights(controller, before);
            else Startup(app, controller, before, args[0] == "startup-exit");
            Console.WriteLine("REVIEW REGRESSION PASSED: " + string.Join(" ", args));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { controller?.Dispose(); }
    }
}
'''


def checks(before=False):
    target = TEMP / ('before-checks' if before else 'after-checks')
    target.mkdir(exist_ok=True)
    project = target / 'ReviewChecks.csproj'
    project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT / "PaperTodo.csproj"}" /></ItemGroup></Project>', encoding='utf-8')
    (target / 'Program.cs').write_text(CS, encoding='utf-8')
    run('dotnet', 'build', str(project), '-c', 'Release')
    for case in (['startup', 'heights'] if before else ['startup', 'startup-exit', 'heights']):
        run('dotnet', 'run', '--no-build', '--project', str(project), '-c', 'Release', '--', case, *(['before'] if before else []))


def historical():
    sources = [('system', 'b2a4227ba0b129f9359f868fe07fc5e6306d3db0'), ('capsule', 'f0983faa88d0f44119a742c072400237f75e9d4e'), ('paper', 'fc5e5d9c11d5b4ff4140e5c6cea869e41a78cc71')]
    for name, sha in sources:
        text = git('show', f'{sha}:.github/backport3x-{name}.py')
        if name == 'system':
            text = text.replace('using System.Diagnostics;', 'using System.Diagnostics;\nusing System.IO;')
        if name == 'capsule':
            text = text.replace(')!==expected', ')! == expected')
            text = text.replace('c.State.DeepCapsuleStartTopMargin=93;', 'var leftKey="|left"; var rightKey="|right";\n            c.State.DeepCapsuleStartTopMargin=93;')
            text = text.replace('"check-left"', 'leftKey').replace('"check-right"', 'rightKey')
            text = text.replace('Deserialize<AppState>("{}")', 'Deserialize<AppState>("""{"Papers":[]}""")')
            text = text.replace('Assert(new StateStore().Load()', 'c.SaveNow(sync: true);\n            Assert(new StateStore().Load()')
        if name == 'paper':
            text = text.replace('var editor=(TextBox)refs["t0-50"];editor.Focus();', 'var editor=(TextBox)refs["t0-50"]; var beforeEdit=editor.Text; editor.Focus();')
            text = text.replace('Assert(editor.CanUndo,"native text undo survives visibility refresh");', 'Assert(editor.CanUndo,"native text undo survives visibility refresh");\n            editor.Undo(); Assert(editor.Text==beforeEdit,"Undo restores original text after visibility cycles");\n            editor.Redo(); Assert(editor.Text==text,"Redo restores edited text after visibility cycles");')
        path = TEMP / f'historical-{name}.py'
        path.write_text(text, encoding='utf-8')
        run(sys.executable, str(path), '--tests')


def publish():
    data = json.loads((TEMP / 'commits.json').read_text(encoding='utf-8'))
    for branch, expected in [(SYSTEM_BRANCH, data['source_head']), (CAPSULE_BRANCH, OLD_CAPSULE)]:
        actual = git('ls-remote', 'origin', 'refs/heads/' + branch).split()[0]
        if actual != expected:
            raise RuntimeError(f'{branch} changed during validation; refusing to overwrite')
    run('git', 'push', '--atomic', 'origin', data['system'] + ':refs/heads/' + SYSTEM_BRANCH, data['capsule'] + ':refs/heads/' + CAPSULE_BRANCH)
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
        summary.write('Two review defects reproduced before fixes; revised startup/height checks and three-PR regressions passed.\n\n```json\n' + json.dumps(data, indent=2) + '\n```\n')
    print('PUBLISHED_REVIEW_FIXES ' + json.dumps(data), flush=True)


mode = sys.argv[1]
if mode == 'before':
    combined(OLD_SYSTEM, OLD_CAPSULE)
    checks(before=True)
elif mode == 'prepare':
    prepare()
elif mode == 'after':
    checks()
    historical()
elif mode == 'publish':
    publish()
else:
    raise ValueError(mode)
