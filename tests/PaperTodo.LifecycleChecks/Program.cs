using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private const string FixtureMarker = ".papertodo-lifecycle-fixture";
    private static readonly string[] Cases = ["startup", "missing-monitor", "cancel-monitor", "real-exit", "early-expand", "cancel-prewarm", "real-exit-scripts", "early-exit"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--sleep-child"))
        {
            Console.WriteLine("ready");
            Thread.Sleep(60_000); // test process ignores EOF; must be killed after the shared grace period
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--fixture")
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, FixtureMarker)))
                throw new InvalidOperationException("Refusing to use a non-fixture data directory.");
            // Explicit shutdown can stop the Dispatcher before the awaiting caller resumes.
            // Failures set the exit code directly; success does not depend on that continuation.
            var result = 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.InvokeAsync(async () =>
            {
                try { await RunFixture(args[1]); result = 0; }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { app.Shutdown(); }
            });
            app.Exit += (_, _) => Console.WriteLine("WPF_EXIT_COMPLETED");
            app.Run();
            return result;
        }
        try
        {
            if (args.Length != 0) throw new ArgumentException("Lifecycle measurements moved to tools/PaperTodo.DesktopBenchmarks.");
            foreach (var name in Cases) RunIsolated(name);
            Console.WriteLine("PASS lifecycle fixtures (isolated data; startup, cache, cancellation, shutdown and persistence)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunIsolated(string name)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PaperTodo.LifecycleChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            // Never point the real controller at a user's data directory. Each process owns a
            // fresh copy of just these test binaries, no installed plugins or existing state.
            CopyBinaries(AppContext.BaseDirectory, fixture);
            File.WriteAllText(Path.Combine(fixture, FixtureMarker), "owned test data");
            var start = ChildStart(fixture);
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(name);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(entireProcessTree: true); child.WaitForExit();
                throw new TimeoutException("Lifecycle fixture timed out: " + name);
            }
            var text = output.GetAwaiter().GetResult();
            Console.Write(text); Console.Error.Write(error.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, name + " failed");
            Console.WriteLine("PASS lifecycle " + name);
            if (name.StartsWith("real-exit") || name == "early-exit")
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "data.json")));
                var paper = saved.RootElement.GetProperty("papers")[0];
                Require(paper.GetProperty("content").GetString() == (name == "early-exit" ? "short note 0" : "pending editor text at exit"), "last editor change was lost");
                Require(paper.GetProperty("isVisible").GetBoolean(), "hiding for exit persisted as a user hide");
                Require(text.Contains("WPF_EXIT_COMPLETED"), "normal WPF exit event was skipped");
                foreach (var childLine in text.Split('\n').Where(value => value.StartsWith("SCRIPT_CHILD ")))
                {
                    var id = int.Parse(childLine["SCRIPT_CHILD ".Length..]);
                    Process? script;
                    try { script = Process.GetProcessById(id); }
                    catch (ArgumentException) { continue; }
                    using (script) Require(script.HasExited, "script survived real application exit");
                }
            }
        }
        finally { try { Directory.Delete(fixture, recursive: true); } catch { } }
    }

    private static ProcessStartInfo ChildStart(string directory)
    {
        var executable = Path.Combine(directory, "PaperTodo.LifecycleChecks.exe");
        return new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
    }

    private static void CopyBinaries(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var extension = Path.GetExtension(file);
            if (extension is ".exe" or ".dll" or ".pdb" || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var locale in new[] { "en", "ja", "ko", "runtimes" })
        {
            var directory = Path.Combine(source, locale);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
    }

    private static async Task RunFixture(string name)
    {
        const int count = 5;
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = true,
            UseCapsuleMode = true, UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = true,
            UsePersistentPowerShellProcess = false, McpEnabled = false
        };
        var area = SystemParameters.WorkArea;
        for (var i = 0; i < count; i++)
            state.Papers.Add(new PaperData
            {
                Id = "fixture-" + i, Type = PaperTypes.Note, Content = "short note " + i,
                IsVisible = true, IsCollapsed = true, X = area.Left + 60, Y = area.Top + 60,
                Width = 300, Height = 240, CapsuleSide = DeepCapsuleSides.Right
            });
        if (name is "missing-monitor" or "cancel-monitor")
            state.Papers.Add(new PaperData
            {
                Id = "missing-screen", Type = PaperTypes.Note, Content = "keep my coordinates",
                IsVisible = true, IsCollapsed = false, X = 1_000_000, Y = 100, Width = 300, Height = 240
            });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        var controller = new AppController();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        var children = new List<Process>();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var visible = windows.Values.Count(window => window.HasVisibleSurface);
            if (name is "missing-monitor" or "cancel-monitor")
            {
                Require(!windows.ContainsKey("missing-screen"), "ambiguous paper was restored before topology settled");
                Require(visible == count, "known-monitor capsules waited for the missing display");
                Require(controller.State.Papers.Single(paper => paper.Id == "missing-screen").X == 1_000_000,
                    "startup overwrote the unresolved monitor coordinates");
            }
            if (name == "early-exit")
            {
                controller.Exit();
                return;
            }
            if (name == "cancel-prewarm")
            {
                controller.HideAllPapers();
                await (Task)Field(controller, "_startupShellPrewarmTask");
                await Until(() => cache.PendingCount == 0, "cancelled preview drain");
                Require(windows.Values.All(window => !window.HasVisibleSurface) && cache.ArtifactCount == 0,
                    "deferred startup resurrected a hidden surface or its cache");
                return;
            }
            if (name == "early-expand")
            {
                windows["fixture-0"].ActivateFromEdgeShortcut();
                Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                    "early demand did not construct and show the selected paper");
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");
            await Until(() => cache.PendingCount == 0, "artifact drain");
            if (name == "startup")
                Require(windows.Count == count && windows.Values.All(window => window.HasVisibleSurface),
                    "startup did not restore the requested visible papers");
            if (name == "cancel-monitor")
            {
                controller.HideAllPapers();
                var savedGeometry = controller.State.Papers.Single(paper => paper.Id == "missing-screen").X;
                // Cross the original settle deadline, not just its first polling interval.
                await Task.Delay(5500);
                Require(!windows.ContainsKey("missing-screen") && controller.State.Papers.Single(paper => paper.Id == "missing-screen").X == savedGeometry,
                    "cancelled display restore resurrected/relocated a hidden paper");
            }
            if (name == "missing-monitor")
            {
                await Until(() => windows.TryGetValue("missing-screen", out var missing) && missing.HasVisibleSurface,
                    "deferred display timeout recovery");
                var recovered = controller.State.Papers.Single(paper => paper.Id == "missing-screen");
                Require(recovered.X != 1_000_000, "unplugged-monitor paper never reached normal rescue");
                Require(windows.Values.Count(window => window.HasVisibleSurface) == count + 1,
                    "deferred rescue hid or duplicated an already-restored paper");
            }
            if (name == "real-exit-scripts")
            {
                var registry = (IDictionary)typeof(PaperWindow).GetField("PersistentScriptProcesses", Private)!.GetValue(null)!;
                for (var i = 0; i < 3; i++)
                {
                    var start = ChildStart(AppContext.BaseDirectory);
                    start.ArgumentList.Add("--sleep-child");
                    var process = Process.Start(start)!;
                    children.Add(process);
                    Console.WriteLine("SCRIPT_CHILD " + process.Id);
                    Require(await process.StandardOutput.ReadLineAsync() == "ready", "script fixture did not start");
                    registry.Add("fixture-script-" + i, process);
                }
            }
            if (name.StartsWith("real-exit"))
            {
                var editor = Field(windows["fixture-0"], "_noteBox");
                editor.GetType().GetProperty("Text")!.SetValue(editor, "pending editor text at exit");
                controller.Exit();
                return;
            }
            var childIds = children.Select(process => process.Id).ToArray();
            var surfaces = Application.Current.Windows.Cast<Window>().ToArray();
            controller.Dispose();
            Require(surfaces.All(window => !window.IsVisible), "visible surfaces remained after dispose");
            foreach (var id in childIds)
            {
                Process? remaining;
                try { remaining = Process.GetProcessById(id); }
                catch (ArgumentException) { continue; }
                using (remaining) Require(remaining.HasExited, "script fixture survived shutdown");
            }
        }
        finally
        {
            controller.Dispose();
            foreach (var process in children)
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); } catch { }
        }
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().FullName, name);
    private static async Task Until(Func<bool> ready, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
