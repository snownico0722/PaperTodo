from pathlib import Path
import os, sys, subprocess, textwrap

ROOT = Path.cwd()
def read(p): return Path(p).read_text(encoding='utf-8')
def write(p, s):
    p = Path(p); p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(s, encoding='utf-8', newline='\n')
def replace(p, old, new):
    s = read(p)
    if s.count(old) != 1: raise RuntimeError(f'{p}: expected one anchor, got {s.count(old)}: {old[:100]}')
    write(p, s.replace(old, new, 1))
def method(p, signature, new):
    s = read(p); start = s.index('    '+signature)
    end = s.index('\n    }', start)+6
    write(p, s[:start]+textwrap.dedent(new).strip('\n')+s[end:])
def commit(message, *paths):
    subprocess.run(['git','add','--',*paths], check=True)
    subprocess.run(['git','diff','--cached','--check'], check=True)
    subprocess.run(['git','commit','-m',message], check=True)

if '--tests' not in sys.argv:
    write('src/AppController.StartupDisplayTopology.cs', '''using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    internal async Task WaitForStartupDisplayTopologyAsync()
    {
        if (!HasPaperGeometryOutsideConnectedWorkAreas()) return;

        // A late secondary display is not the same as a removed display. Preserve saved
        // coordinates until enumeration settles; never save a temporary primary-screen rescue.
        var deadline = Environment.TickCount64 + 5000;
        var readySamples = 0;
        while (!IsExiting && Environment.TickCount64 < deadline)
        {
            await Task.Delay(150);
            WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
            if (HasPaperGeometryOutsideConnectedWorkAreas()) readySamples = 0;
            else if (++readySamples >= 2) return;
        }
        // A genuinely disconnected monitor still uses the existing bounded rescue afterwards.
        WindowWorkAreaHelper.InvalidateMonitorGeometryCache();
    }

    private bool HasPaperGeometryOutsideConnectedWorkAreas()
    {
        var workAreas = WindowWorkAreaHelper.ConnectedMonitorGeometries()
            .Select(monitor => WindowWorkAreaHelper.WorkAreaForDevice(monitor.DeviceName))
            .Where(area => area.HasValue && !area.Value.IsEmpty && area.Value.Width > 0 && area.Value.Height > 0)
            .Select(area => area!.Value).ToList();
        foreach (var paper in State.Papers)
        {
            if (paper.IsCollapsed && State.UseCapsuleMode && State.UseDeepCapsuleMode &&
                CanPaperDisplayAsCapsule(paper)) continue;
            if (!IsFinite(paper.X) || !IsFinite(paper.Y) || !IsFinite(paper.Width) ||
                !IsFinite(paper.Height) || paper.Width <= 0 || paper.Height <= 0) continue;
            var center = new Point(paper.X + paper.Width / 2, paper.Y + paper.Height / 2);
            if (!workAreas.Any(area => area.Contains(center))) return true;
        }
        return false;
    }
}
''')
    replace('src/AppController.cs', '        ApplyInitialStartupVisibility(initialVisibilityCommand);\n        var rescuedPapers = EnsurePapersOnScreen();', '''        var startupRestoreGeneration = _paperSurfaceRestoreGeneration;
        await WaitForStartupDisplayTopologyAsync();
        // A show/hide command or exit received during the wait owns the newer presentation.
        if (IsExiting || startupRestoreGeneration != _paperSurfaceRestoreGeneration) return;

        ApplyInitialStartupVisibility(initialVisibilityCommand);
        var rescuedPapers = EnsurePapersOnScreen();''')
    commit('fix(3.x): 开机显示器延迟枚举时保留副屏坐标', 'src/AppController.cs', 'src/AppController.StartupDisplayTopology.cs')

    p='src/SystemSettingsHelper.cs'
    replace(p, '    private const string AppKeyName = "PaperTodo";', '''    private const string AppKeyName = "PaperTodo";
    private const string StartupApprovedRunPath =
        @"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";''')
    method(p, 'private static bool IsLegacyRunStartupEnabled()', '''
    private static bool IsLegacyRunStartupEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, false);
            return IsStartupEnabled(runKey?.GetValue(AppKeyName),
                approvalKey?.GetValue(AppKeyName), Environment.ProcessPath ?? "");
        }
        catch { return false; }
    }''')
    method(p, 'private static bool ToggleLegacyRunStartup(bool enable)', '''
    private static bool ToggleLegacyRunStartup(bool enable)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (enable && string.IsNullOrEmpty(processPath)) return false;
            using var runKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true);
            if (runKey == null) return false;
            if (!enable)
            {
                runKey.DeleteValue(AppKeyName, false);
                return runKey.GetValue(AppKeyName) == null;
            }
            runKey.SetValue(AppKeyName, $"\\\"{processPath}\\\"", RegistryValueKind.String);
            using var approvalKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, true);
            var approval = approvalKey?.GetValue(AppKeyName);
            // Only an explicit enable click may clear our own disabled entry. Reading external
            // Startup Apps state never silently re-enables an entry disabled by the user.
            if (approval != null && !StartupApprovalAllowsLaunch(approval))
                approvalKey!.DeleteValue(AppKeyName, false);
            return IsStartupEnabled(runKey.GetValue(AppKeyName),
                approvalKey?.GetValue(AppKeyName), processPath!);
        }
        catch { return false; }
    }''')
    replace(p, '    private static string? TryGetLegacyRunExecutablePath()', '''    internal static bool IsStartupEnabled(object? runValue, object? approvalValue, string processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        var value = runValue?.ToString();
        return (value == processPath || value == $"\\\"{processPath}\\\"") &&
            StartupApprovalAllowsLaunch(approvalValue);
    }

    internal static bool StartupApprovalAllowsLaunch(object? approvalValue)
    {
        if (approvalValue == null) return true;
        if (approvalValue is not byte[] data || data.Length < 12) return false;
        // Explorer-owned, undocumented format: the first DWORD is the approval state;
        // the FILETIME records history and is not evidence that an entry is disabled.
        return data[1] == 0 && data[2] == 0 && data[3] == 0 && data[0] is 0x00 or 0x02 or 0x06;
    }

    private static string? TryGetLegacyRunExecutablePath()''')
    write('src/AppController.StartupSettings.cs', '''using System.Windows.Controls;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private CheckBox? _settingsStartupCheckBox;
    private DispatcherTimer? _settingsStartupRefreshTimer;

    private void StartSettingsStartupStateRefresh()
    {
        if (_settingsStartupRefreshTimer == null)
        {
            _settingsStartupRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _settingsStartupRefreshTimer.Tick += (_, _) => RefreshSettingsStartupStateFromWindows();
        }
        _settingsStartupRefreshTimer.Start();
        RefreshSettingsStartupStateFromWindows();
    }

    private void RefreshSettingsStartupStateFromWindows()
    {
        if (_settingsWindow is not { IsVisible: true } ||
            _settingsStartupCheckBox is not { IsVisible: true } checkBox) return;
        // SettingsToggle dispatches Click, not Checked: this read-only refresh cannot register
        // startup, rebuild the settings page or disturb another editor's caret/selection.
        checkBox.IsChecked = SystemSettingsHelper.IsStartupEnabled();
    }
}
''')
    p='src/AppController.Settings.cs'
    replace(p, '        leftColumn.Children.Add(WrapWithHint(SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup), "TipStartup"));', '''        _settingsStartupCheckBox = SettingsToggle(Strings.Get("TrayStartup"), SystemSettingsHelper.IsStartupEnabled(), ToggleStartup);
        leftColumn.Children.Add(WrapWithHint(_settingsStartupCheckBox, "TipStartup"));''')
    replace(p, '        window.Deactivated += (_, _) => CommitSettingsExternalMarkdownEditor();', '''        window.Deactivated += (_, _) => CommitSettingsExternalMarkdownEditor();
        window.Activated += (_, _) => RefreshSettingsStartupStateFromWindows();
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible) StartSettingsStartupStateRefresh();
            else _settingsStartupRefreshTimer?.Stop();
        };''')
    replace(p, '        window.Closed += (_, _) =>\n        {\n            CommitSettingsExternalMarkdownEditor();', '''        window.Closed += (_, _) =>
        {
            _settingsStartupRefreshTimer?.Stop();
            _settingsStartupRefreshTimer = null;
            _settingsStartupCheckBox = null;
            CommitSettingsExternalMarkdownEditor();''')
    commit('fix(3.x): 同步 Windows 自启动批准状态并保留 Store 启动路径', 'src/SystemSettingsHelper.cs', 'src/AppController.StartupSettings.cs', p)

    for name, field in [('TogglePreferPowerShell7','PreferPowerShell7'),('ToggleHideScriptRunWindow','HideScriptRunWindow')]:
        method(p, f'private void {name}()', f'''
    private void {name}()
    {{
        State.{field} = !State.{field};
        // Preferences select the session for future submissions, not the lifetime of running work.
        // The existing executable/window-mode keyed pool also reuses sessions when switching back.
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        SaveNow();
        RefreshSettingsWindowContent();
    }}''')
    commit('fix(3.x): PowerShell 偏好切换不终止已运行的脚本', p)

    p='src/PaperWindow.Note.cs'
    replace(p, 'using System;\n', 'using System;\nusing System.ComponentModel;\n')
    replace(p, '''            var path = WriteExternalMarkdownFile();
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });''', '''            var path = WriteExternalMarkdownFile();
            OpenExternalNoteFile(path);''')
    replace(p, '    public void UpdateExternalMarkdownExtension()', '''    private static void OpenExternalNoteFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // ERROR_NO_ASSOCIATION only
        {
            var startInfo = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
    }

    public void UpdateExternalMarkdownExtension()''')
    commit('fix(3.x): 外部笔记无关联应用时使用记事本', p)
    replace('CHANGELOG.md', '**优化与修复**\n', '''**优化与修复**

- 开机时副屏尚未就绪的情况下，短暂保留纸片原位置，避免提前移回主屏并覆盖原坐标；真正拔掉副屏时仍会恢复到可见区域。
- 开机自启动状态与 Windows 启动应用中的启用状态保持一致；用户主动重新开启时校验注册结果，商店版继续使用原有启动任务。
- 修改 PowerShell 版本偏好或运行窗口显示偏好时不再终止正在运行的常驻脚本；新任务按新偏好选择会话，明确关闭常驻模式时仍会结束常驻进程。
- 外部打开笔记时，若所选后缀没有关联应用，自动使用记事本打开；其他错误仍正常提示。
''')
    commit('docs(3.x): 记录系统与启动修复', 'CHANGELOG.md')
    subprocess.run(['git','diff','05a798582929cdb08fb57e023fa2fd5e9735965a','--stat'], check=True)
else:
    target=Path(os.environ['RUNNER_TEMP'])/'paper-backport-check-system'
    target.mkdir(exist_ok=True)
    write(target/'Check.csproj', f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT / 'PaperTodo.csproj'}" /></ItemGroup></Project>''')
    write(target/'Program.cs', r'''using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

static class Check
{
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object? Call(object obj, string name, params object?[] args)
    {
        var t = obj as Type ?? obj.GetType();
        var m = t.GetMethods(Flags).Single(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m.Invoke(obj is Type ? null : obj, args);
    }
    static void Assert(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    static void Pump(Task task)
    {
        var end = Environment.TickCount64 + 10000;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
        Assert(task.IsCompleted, "display wait has a bounded deadline");
        task.GetAwaiter().GetResult();
    }
    [STAThread] static int Main()
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            var helper = typeof(SystemSettingsHelper);
            foreach (byte status in new byte[]{0,2,3,6,7,255})
            {
                var record = new byte[12]; record[0]=status;
                Assert((bool)Call(helper,"StartupApprovalAllowsLaunch",record)! == (status is 0 or 2 or 6), "startup state " + status);
                record[4]=117;
                Assert((bool)Call(helper,"StartupApprovalAllowsLaunch",record)! == (status is 0 or 2 or 6), "timestamp does not override state " + status);
            }
            Assert(!(bool)Call(helper,"StartupApprovalAllowsLaunch",new byte[4])!, "short approval record rejected");
            Assert((bool)Call(helper,"StartupApprovalAllowsLaunch",new object?[]{null})!, "missing approval keeps legacy startup available");
            Assert((bool)Call(helper,"IsStartupEnabled","\"C:\\Program Files\\PaperTodo.exe\"",null,"C:\\Program Files\\PaperTodo.exe")!, "quoted current path accepted");
            Assert(!(bool)Call(helper,"IsStartupEnabled","C:\\old.exe",null,"C:\\new.exe")!, "old executable path rejected");
            var controller = new AppController();
            controller.State.Papers.Clear();
            controller.State.UseCapsuleMode=false;
            var a = SystemParameters.WorkArea;
            var paper = new PaperData { Id="topology-check", Type=PaperTypes.Note, X=a.Left+40, Y=a.Top+40, Width=200, Height=160, IsVisible=true };
            controller.State.Papers.Add(paper);
            Assert(!(bool)Call(controller,"HasPaperGeometryOutsideConnectedWorkAreas")!, "connected paper starts without waiting");
            var sw=Stopwatch.StartNew(); Pump((Task)Call(controller,"WaitForStartupDisplayTopologyAsync")!);
            Assert(sw.ElapsedMilliseconds<1000, "normal startup has no deliberate delay");
            paper.X=100000; paper.Y=100000;
            Assert((bool)Call(controller,"HasPaperGeometryOutsideConnectedWorkAreas")!, "unavailable screen is detected");
            sw.Restart(); Pump((Task)Call(controller,"WaitForStartupDisplayTopologyAsync")!);
            Assert(sw.ElapsedMilliseconds>=4800 && sw.ElapsedMilliseconds<9000 && paper.X==100000 && paper.Y==100000, "timeout does not mutate saved geometry");
            paper.X=double.NaN;
            Assert(!(bool)Call(controller,"HasPaperGeometryOutsideConnectedWorkAreas")!, "invalid geometry is left for normal rescue");
            controller.State.Papers.Clear();
            controller.State.UsePersistentPowerShellProcess=true;
            controller.State.PreferPowerShell7=false;
            controller.State.HideScriptRunWindow=true;
            var process=(Process)Call(typeof(PaperWindow),"EnsurePersistentScriptProcess","powershell.exe",true)!;
            int pid=process.Id;
            var marker=Path.Combine(AppContext.BaseDirectory,"script-completed.txt");
            process.StandardInput.WriteLine("Start-Sleep -Milliseconds 1200; Set-Content -LiteralPath '"+marker.Replace("'","''")+"' -Value completed");
            process.StandardInput.Flush();
            Call(controller,"TogglePreferPowerShell7");
            Assert(!process.HasExited && process.Id==pid,"version preference preserves running process");
            Call(controller,"ToggleHideScriptRunWindow");
            Assert(!process.HasExited && process.Id==pid,"window preference preserves running process");
            var same=(Process)Call(typeof(PaperWindow),"EnsurePersistentScriptProcess","powershell.exe",true)!;
            Assert(same.Id==pid,"switching back reuses existing session");
            var deadline=Environment.TickCount64+8000;
            while(!File.Exists(marker) && Environment.TickCount64<deadline) Thread.Sleep(50);
            Assert(File.Exists(marker),"in-flight script reaches completion after preference changes");
            Call(controller,"TogglePersistentPowerShellProcess");
            Assert(!controller.State.UsePersistentPowerShellProcess,"explicit persistent-mode disable retained");
            Console.WriteLine("SYSTEM BACKPORT CHECKS PASSED");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Call(typeof(PaperWindow),"StopPersistentScriptProcesses"); } catch {} }
    }
}
''')
    subprocess.run(['dotnet','run','--project',str(target/'Check.csproj'),'-c','Release'],check=True)
