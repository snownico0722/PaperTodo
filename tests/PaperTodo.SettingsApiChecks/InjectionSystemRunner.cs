using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

// Each scenario owns a clean executable directory and a real Dispatcher/controller.
// Never load a developer's papers, plugins or user.json.
internal static partial class InjectionSystemRunner
{
    private const string Marker = ".papertodo-injection-system-fixture";
    private const string Result = "restart-result.txt";
    private static int _assertions;
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) throw new InvalidOperationException(message);
    }
    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));
    private static object? Call(object target, string name, params object?[] args)
    {
        try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Error<T>(Action action, string code) where T : Exception
    {
        try { action(); }
        catch (T ex)
        {
            var actual = ex.GetType().GetProperty("Code")!.GetValue(ex) as string;
            Check(actual == code, $"Expected {code}, got {actual}."); return;
        }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Settle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }
    private static Dictionary<string, PaperWindow> Windows(AppController c) => Field<Dictionary<string, PaperWindow>>(c, "_windows");
    private static PaperInjectionApplyResult Set(AppController c, params (string Id, object Value)[] values) =>
        c.ApplyUserInjection(new PaperInjectionPatch
        {
            ExpectedRevision = UserInjection.Read().Revision,
            Set = values.ToDictionary(v => v.Id, v => J(v.Value), StringComparer.Ordinal)
        });
    private static void Remove(AppController c, params string[] ids) => c.ApplyUserInjection(new PaperInjectionPatch
    {
        ExpectedRevision = UserInjection.Read().Revision, Remove = ids
    });

    [STAThread]
    private static int Main(string[] args)
    {
        var journeys = args.SequenceEqual(new[] { "--user-journeys" });
        if (args.Length > 0 && !journeys) return Child(args);
        var failures = new List<string>();
        var scenarios = journeys
            ? new[] { "user-editor", "user-paste", "user-drag", "user-preview" }
            : new[] { "timers", "typography", "settings", "menus", "next-use", "good-lock", "conflict", "lifetime", "restart-cancel", "restart" };
        foreach (var mode in new[] { "normal", "edge" })
        foreach (var scenario in scenarios)
        {
            var root = Path.Combine(Path.GetTempPath(), "PaperTodo.InjectionSystem", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
                    if (relative.Split(Path.DirectorySeparatorChar)[0] is "plugins" or "data") continue;
                    if (Path.GetExtension(file) is not (".exe" or ".dll" or ".pdb") &&
                        !file.EndsWith(".deps.json") && !file.EndsWith(".runtimeconfig.json")) continue;
                    var target = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                File.WriteAllText(Path.Combine(root, Marker), mode);
                var start = new ProcessStartInfo(Path.Combine(root, "PaperTodo.SettingsApiChecks.exe"))
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add("--injection-fixture"); start.ArgumentList.Add(scenario);
                using var child = Process.Start(start)!;
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(90_000))
                {
                    child.Kill(entireProcessTree: true); child.WaitForExit();
                    Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
                    throw new TimeoutException("Fixture timed out.");
                }
                Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
                Check(child.ExitCode == 0, "Fixture exited " + child.ExitCode);
                if (scenario is "restart" or "user-preview")
                {
                    var watch = Stopwatch.StartNew();
                    var result = Path.Combine(root, Result);
                    while (!File.Exists(result) && watch.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(100);
                    Check(File.Exists(result), "The production restart did not finish its successor journey.");
                    var text = File.ReadAllText(result);
                    Check(text.StartsWith("PASS ", StringComparison.Ordinal), text);
                    Console.WriteLine(text);
                }
                Console.WriteLine($"PASS {(journeys ? "USER_JOURNEY" : "SYSTEM_UI")} {mode}/{scenario}");
            }
            catch (Exception ex) { failures.Add($"{mode}/{scenario}: {ex.GetBaseException().Message}"); }
            finally
            {
                var journal = Path.Combine(root, "journey.log");
                if (File.Exists(journal)) Console.Write(File.ReadAllText(journal));
                var pidFile = Path.Combine(root, "successor.pid");
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var pid))
                {
                    try { using var p = Process.GetProcessById(pid); if (!p.WaitForExit(5000)) p.Kill(entireProcessTree: true); }
                    catch (ArgumentException) { }
                }
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
        Console.WriteLine($"{(journeys ? "USER_JOURNEY_RESULT" : "SYSTEM_UI_RESULT")} fixtures={scenarios.Length * 2} failures={failures.Count}");
        foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static int Child(string[] args)
    {
        var markerPath = Path.Combine(AppContext.BaseDirectory, Marker);
        if (!File.Exists(markerPath)) throw new InvalidOperationException("Refusing to use non-fixture data.");
        var resumed = args.Length == 2 && args[0] == "--return-paper";
        var scenario = resumed ? "resumed" : args.Length == 2 && args[0] == "--injection-fixture" ? args[1] : throw new ArgumentException("Invalid fixture arguments.");
        if (resumed && File.Exists(Path.Combine(AppContext.BaseDirectory, "preview-stage.txt"))) scenario = "user-preview";
        var journey = scenario.StartsWith("user-", StringComparison.Ordinal);
        var edge = File.ReadAllText(markerPath) == "edge";
        if (!resumed)
        {
            var state = new AppState
            {
                TelemetryEnabled = false, EnableAnimations = false, UseCapsuleMode = true,
                UseDeepCapsuleMode = edge, UsePersistentPowerShellProcess = false, McpEnabled = false,
                HidePapersFromTaskbar = false, HidePapersFromWindowSwitcher = false
            };
            state.Papers.Add(new PaperData { Id = "note", Type = PaperTypes.Note, Content = "original note",
                IsVisible = true, X = 80, Y = 80, Width = 360, Height = 260 });
            state.Papers.Add(new PaperData { Id = "todo", Type = PaperTypes.Todo, IsVisible = true,
                X = 500, Y = 80, Width = 360, Height = 260, Items = [new PaperItem { Text = "original task", Order = 0 }] });
            if (journey) PrepareUserJourney(state, scenario, edge);
            var store = new StateStore(); store.SaveJsonSync(store.SerializeState(state), 1);
        }
        else File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "successor.pid"), Environment.ProcessId.ToString());
        UserInjection.LoadStartup(Path.Combine(AppContext.BaseDirectory, "user.json"));
        Application app = scenario.StartsWith("restart", StringComparison.Ordinal) || resumed || scenario == "user-preview" ? new FixtureApp() : new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var result = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            AppController? controller = null;
            var restarting = false;
            try
            {
                controller = new AppController();
                await controller.StartAsync(createDefaultPaper: false);
                await Settle();
                if (journey)
                {
                    using var dialogs = new FixtureDialogs();
                    restarting = await RunUserJourney(controller, scenario);
                    Check(dialogs.Count == 0, "Unexpected dialog during " + scenario);
                }
                else if (resumed)
                {
                    controller.RestoreUserInjectionPaper(args);
                    await Settle();
                    var paper = controller.State.Papers.Single(p => p.Id == args[1]);
                    var box = Field<MarkdownPaperBodySession>(Windows(controller)[paper.Id], "_markdownBodySession").NoteBox!;
                    Check(paper.IsVisible && !paper.IsCollapsed && Windows(controller)[paper.Id].IsVisible,
                        "Restart did not return to the requested paper.");
                    Check(paper.Content == "unsaved note before restart" && box.Options.IndentationSize == 7 &&
                        PaperWindow.NoteTextMaxLength == 120000 && AppTypography.ScaleFactor == 1.1,
                        "Normal exit or startup lost draft content or restart/live overrides.");
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, Result), $"PASS real restart: pid={Environment.ProcessId}, return={paper.Id}, assertions={_assertions}");
                }
                else
                {
                    using var dialogs = scenario is "good-lock" or "restart" ? new FixtureDialogs() : null;
                    await RunScenario(controller, scenario);
                    Check(dialogs == null || dialogs.Count == 0, "Unexpected dialog during " + scenario);
                    restarting = scenario == "restart";
                }
                result = 0;
                Console.WriteLine($"SYSTEM_UI_ASSERTIONS {scenario} {_assertions}");
            }
            catch (Exception ex)
            {
                restarting = false;
                Console.Error.WriteLine(ex);
                if (journey) JourneyLog("FAIL " + ex);
                if (resumed) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, Result), "FAIL " + ex);
            }
            finally
            {
                if (!restarting)
                {
                    if (journey) JourneyInput.Release();
                    controller?.Dispose(); app.Shutdown();
                }
            }
        });
        app.Run();
        return result;
    }

    // Production exit/restart, with isolated startup instead of the desktop-wide singleton.
    private sealed class FixtureApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
}
