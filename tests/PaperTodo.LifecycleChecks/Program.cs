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
    private static readonly string[] Cases = ["capsules-1", "capsules-5", "capsules-10", "capsules-11", "preview-off", "missing-monitor", "cancel-monitor", "scripts", "real-exit", "preview-before-shell", "early-expand", "cancel-prewarm", "real-exit-scripts", "early-exit"];

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
                try { await RunFixture(args[1], args.Contains("--baseline")); result = 0; }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { app.Shutdown(); }
            });
            app.Exit += (_, _) => Console.WriteLine("WPF_EXIT_COMPLETED");
            app.Run();
            return result;
        }
        try
        {
            var baseline = args.Contains("--baseline");
            var repetitions = args.Contains("--profile") ? 3 : 1;
            var cases = args.Contains("--profile")
                ? new[] { "capsules-1", "capsules-5", "capsules-10", "missing-monitor", "scripts" }
                : Cases;
            for (var round = 0; round < repetitions; round++)
            foreach (var name in round % 2 == 0 ? cases : cases.Reverse())
                RunIsolated(name, baseline);
            Console.WriteLine("PASS lifecycle fixtures (isolated data; startup, cache, cancellation, shutdown and persistence)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RunIsolated(string name, bool baseline)
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
            if (baseline) start.ArgumentList.Add("--baseline");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(entireProcessTree: true); child.WaitForExit();
                throw new TimeoutException("Lifecycle fixture timed out: " + name);
            }
            var endedAt = Stopwatch.GetTimestamp();
            var text = output.GetAwaiter().GetResult();
            Console.Write(text); Console.Error.Write(error.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, name + " failed");
            if (name.StartsWith("real-exit") || name == "early-exit")
            {
                var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "data.json")));
                var paper = saved.RootElement.GetProperty("papers")[0];
                Require(paper.GetProperty("content").GetString() == (name == "early-exit" ? "short note 0" : "pending editor text at exit"), "last editor change was lost");
                Require(paper.GetProperty("isVisible").GetBoolean(), "hiding for exit persisted as a user hide");
                Require(baseline || text.Contains("WPF_EXIT_COMPLETED"), "normal WPF exit event was skipped");
                foreach (var childLine in text.Split('\n').Where(value => value.StartsWith("SCRIPT_CHILD ")))
                {
                    var id = int.Parse(childLine["SCRIPT_CHILD ".Length..]);
                    Process? script;
                    try { script = Process.GetProcessById(id); }
                    catch (ArgumentException) { continue; }
                    using (script) Require(script.HasExited, "script survived real application exit");
                }
                var line = text.Split('\n').Single(value => value.StartsWith("EXIT_REQUEST "));
                var requestAt = long.Parse(line["EXIT_REQUEST ".Length..].Trim());
                Console.WriteLine("EXIT_PROCESS_MS " + Stopwatch.GetElapsedTime(requestAt, endedAt).TotalMilliseconds);
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

    private static async Task RunFixture(string name, bool baseline)
    {
        var count = name.StartsWith("capsules-") ? int.Parse(name[9..]) : 5;
        var state = new AppState
        {
            TelemetryEnabled = false, EnableAnimations = true,
            UseCapsuleMode = true, UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = name != "preview-off",
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
        var started = Stopwatch.GetTimestamp();
        var controller = new AppController();
        var constructed = Stopwatch.GetTimestamp();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var cache = MarkdownEdgePreviewPreload.For(Dispatcher.CurrentDispatcher);
        var children = new List<Process>();
        try
        {
            await controller.StartAsync(createDefaultPaper: false);
            var returned = Stopwatch.GetTimestamp();
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var visible = windows.Values.Count(window => window.HasVisibleSurface);
            if (!baseline && (name is "missing-monitor" or "cancel-monitor"))
            {
                Require(!windows.ContainsKey("missing-screen"), "ambiguous paper was restored before topology settled");
                Require(visible == count, "known-monitor capsules waited for the missing display");
                Require(controller.State.Papers.Single(paper => paper.Id == "missing-screen").X == 1_000_000,
                    "startup overwrote the unresolved monitor coordinates");
            }
            if (name == "early-exit")
            {
                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());
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
            long[]? previewVersions = null;
            if (name is "preview-before-shell" or "early-expand")
            {
                await cache.StartStartupWork();
                Require(cache.ArtifactCount == count, "previews were not available ahead of shells");
                Require(windows.Values.All(window => !window.IsShellBuilt), "optional shells blocked the first preview pass");
                previewVersions = windows.Values.Select(window =>
                    ((EdgeCapsulePreviewInvalidationSource)Field(window, "_edgeCapsulePreviewInvalidationSource")).Version).ToArray();
                if (name == "early-expand")
                {
                    windows["fixture-0"].ActivateFromEdgeShortcut();
                    Require(windows["fixture-0"].IsShellBuilt && windows["fixture-0"].HasExpandedPaperSurface,
                        "early demand did not construct and show the selected paper");
                }
            }
            await Until(() => windows.Values.All(window => window.IsShellBuilt), "shell drain");
            var shells = Stopwatch.GetTimestamp();
            await Until(() => cache.PendingCount == 0, "artifact drain");
            var ready = Stopwatch.GetTimestamp();
            if (name == "preview-before-shell")
            {
                Require(cache.ArtifactCount == count && cache.WarmCompletions == count,
                    "shell initialization discarded or rebuilt valid preview artifacts");
                Require(previewVersions!.SequenceEqual(windows.Values.Select(window =>
                    ((EdgeCapsulePreviewInvalidationSource)Field(window, "_edgeCapsulePreviewInvalidationSource")).Version)),
                    "initial title/capsule materialization changed the content generation");
            }
            if (!baseline)
            {
                if ((name.StartsWith("capsules-") && count <= 10) || name == "preview-before-shell")
                {
                    Require(cache.ArtifactCount == count, "small workset did not cache all short notes");
                    var first = windows["fixture-0"];
                    var source = (EdgeCapsulePreviewInvalidationSource)Field(first, "_edgeCapsulePreviewInvalidationSource");
                    var keyBefore = source.Version;
                    var completionsBefore = cache.WarmCompletions;
                    var editor = Field(first, "_noteBox");
                    editor.GetType().GetProperty("Text")!.SetValue(editor, "edited short note");
                    first.CommitPendingNoteContentForSave();
                    first.RequestMarkdownPreviewLayoutPreload();
                    await Until(() => cache.PendingCount == 0 && cache.ArtifactCount == count, "light note edit rewarm");
                    Require(source.Version > keyBefore && cache.WarmCompletions > completionsBefore,
                        "real editor changes did not invalidate and rebuild preview content");
                }
                if (name == "capsules-11" || name == "preview-off")
                    Require(cache.ArtifactCount == 0, "heavy filter or feature-off gate was bypassed");
            }
            if (name == "cancel-monitor" && !baseline)
            {
                controller.HideAllPapers();
                var savedGeometry = controller.State.Papers.Single(paper => paper.Id == "missing-screen").X;
                // Cross the original settle deadline, not just its first polling interval.
                await Task.Delay(5500);
                Require(!windows.ContainsKey("missing-screen") && controller.State.Papers.Single(paper => paper.Id == "missing-screen").X == savedGeometry,
                    "cancelled display restore resurrected/relocated a hidden paper");
            }
            if (name == "missing-monitor" && !baseline)
            {
                await Until(() => windows.TryGetValue("missing-screen", out var missing) && missing.HasVisibleSurface,
                    "deferred display timeout recovery");
                var recovered = controller.State.Papers.Single(paper => paper.Id == "missing-screen");
                Require(recovered.X != 1_000_000, "unplugged-monitor paper never reached normal rescue");
                Require(windows.Values.Count(window => window.HasVisibleSurface) == count + 1,
                    "deferred rescue hid or duplicated an already-restored paper");
            }
            if (name is "scripts" or "real-exit-scripts")
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
                Console.WriteLine("EXIT_REQUEST " + Stopwatch.GetTimestamp());
                controller.Exit();
                return;
            }
            var childIds = children.Select(process => process.Id).ToArray();
            var exitAt = Stopwatch.GetTimestamp();
            double? allHiddenMs = null;
            var surfaces = Application.Current.Windows.Cast<Window>().ToArray();
            foreach (var surface in surfaces)
                surface.IsVisibleChanged += (_, _) =>
                {
                    if (allHiddenMs == null && surfaces.All(window => !window.IsVisible))
                        allHiddenMs = Stopwatch.GetElapsedTime(exitAt).TotalMilliseconds;
                };
            controller.Dispose();
            var disposedAt = Stopwatch.GetTimestamp();
            Require(surfaces.All(window => !window.IsVisible), "visible surfaces remained after dispose");
            foreach (var id in childIds)
            {
                Process? remaining;
                try { remaining = Process.GetProcessById(id); }
                catch (ArgumentException) { continue; }
                using (remaining) Require(remaining.HasExited, "script fixture survived shutdown");
            }
            Console.WriteLine("LIFECYCLE_SAMPLE " + JsonSerializer.Serialize(new
            {
                fixture = name, baseline, count, visible,
                ctorMs = Stopwatch.GetElapsedTime(started, constructed).TotalMilliseconds,
                restoreMs = Stopwatch.GetElapsedTime(constructed, returned).TotalMilliseconds,
                shellsReadyMs = Stopwatch.GetElapsedTime(constructed, shells).TotalMilliseconds,
                preloadReadyMs = Stopwatch.GetElapsedTime(constructed, ready).TotalMilliseconds,
                uiGoneMs = allHiddenMs,
                disposeMs = Stopwatch.GetElapsedTime(exitAt, disposedAt).TotalMilliseconds
            }));
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
