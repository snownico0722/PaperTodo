using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const string FixtureMarker = ".papertodo-pr260-empty-start-fixture";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] Cases = ["empty-no-default", "empty-default", "nonempty"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture", StringComparer.Ordinal))
        {
            var scenario = ValueAfter(args, "--case") ??
                throw new ArgumentException("Fixture case is required.");
            return RunFixtureProcess(
                scenario,
                args.Contains("--expect-defect", StringComparer.Ordinal));
        }

        var expectDefect = args.Contains("--expect-defect", StringComparer.Ordinal);
        var fixture = Path.Combine(
            Path.GetTempPath(),
            "PaperTodo.Pr260EmptyStartupChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            CopyBinaries(AppContext.BaseDirectory, fixture);
            File.WriteAllText(Path.Combine(fixture, FixtureMarker), "owned test data");
            var executable = Path.Combine(fixture, "PaperTodo.Pr260EmptyStartupChecks.exe");
            foreach (var scenario in Cases)
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = fixture,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("--fixture");
                start.ArgumentList.Add("--case");
                start.ArgumentList.Add(scenario);
                if (expectDefect) start.ArgumentList.Add("--expect-defect");
                using var child = Process.Start(start) ??
                    throw new InvalidOperationException($"Empty-start fixture did not start ({scenario}).");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(20_000))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                    throw new TimeoutException($"Empty-start fixture timed out ({scenario}).");
                }
                Console.Write(output.GetAwaiter().GetResult());
                Console.Error.Write(error.GetAwaiter().GetResult());
                if (child.ExitCode != 0) return child.ExitCode;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(fixture, recursive: true); } catch { }
        }
    }

    private static int RunFixtureProcess(string scenario, bool expectDefect)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, FixtureMarker)))
        {
            Console.Error.WriteLine("Refusing to use a non-fixture data directory.");
            return 1;
        }

        var state = NewState();
        var createDefaultPaper = scenario == "empty-default";
        if (scenario == "nonempty")
        {
            // Hidden paper keeps the fixture non-empty without showing UI. The normal restore path
            // must still increment its generation and perform the existing asynchronous startup
            // completion; the candidate empty-start fix must not short-circuit that ordering.
            state.Papers.Add(new PaperData
            {
                Type = PaperTypes.Note,
                Title = "startup-control",
                IsVisible = false,
                IsCollapsed = true
            });
        }
        else
        {
            Require(scenario is "empty-no-default" or "empty-default",
                $"Unknown fixture case: {scenario}");
        }

        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);

        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            AppController? controller = null;
            try
            {
                controller = new AppController();
                var expectedInitialPapers = scenario == "nonempty" ? 1 : 0;
                Require(controller.State.Papers.Count == expectedInitialPapers,
                    $"Fixture initial paper count mismatch ({scenario}).");
                await controller.StartAsync(createDefaultPaper);

                // Non-empty startup completion is deliberately scheduled behind the shell queue.
                // Empty startup candidate completion occurs synchronously at the common tail. Pump
                // ordinary Dispatcher work for both so the control arm proves that ordering remains.
                var deadline = Stopwatch.StartNew();
                while (scenario == "nonempty" &&
                       !(bool)Field(controller, "_edgePrewarmStartupReady")! &&
                       deadline.Elapsed < TimeSpan.FromSeconds(3))
                {
                    await Dispatcher.CurrentDispatcher.InvokeAsync(
                        static () => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(10);
                }
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

                var restoreGeneration = (int)Field(controller, "_paperSurfaceRestoreGeneration")!;
                var startupReady = (bool)Field(controller, "_edgePrewarmStartupReady")!;
                Invoke(controller, "RequestEdgePrewarmForVisibleQueues");
                var coordinatorCreated = Field(controller, "_edgePrewarm") != null;

                if (scenario == "nonempty")
                {
                    Require(restoreGeneration == 1,
                        "Non-empty control did not execute the normal restore generation.");
                    Require(startupReady && coordinatorCreated,
                        "Non-empty control did not complete the existing startup-prewarm sequence.");
                }
                else
                {
                    Require(restoreGeneration == 0,
                        $"Empty startup unexpectedly ran the non-empty restore path ({scenario}).");
                    if (expectDefect)
                    {
                        Require(!startupReady && !coordinatorCreated,
                            $"Pinned #260 no longer reproduces the empty-start gate defect ({scenario}).");
                    }
                    else
                    {
                        Require(startupReady && coordinatorCreated,
                            $"Empty startup did not complete the native-prewarm startup gate ({scenario}).");
                    }
                }

                Console.WriteLine(
                    $"RESULT pr260-empty-start case={scenario} expectDefect={expectDefect} " +
                    $"papers={controller.State.Papers.Count} restoreGeneration={restoreGeneration} " +
                    $"startupReady={startupReady} coordinatorCreated={coordinatorCreated}");
            }
            catch (Exception error)
            {
                result = 1;
                Console.Error.WriteLine(error);
            }
            finally
            {
                controller?.Dispose();
                app.Shutdown();
            }
        });
        app.Run();
        return result;
    }

    private static AppState NewState() => new()
    {
        TelemetryEnabled = false,
        EnableAnimations = true,
        UseCapsuleMode = true,
        UseDeepCapsuleMode = true,
        ExperimentalEdgeCapsuleHoverPreview = true,
        UsePersistentPowerShellProcess = false,
        McpEnabled = false
    };

    private static string? ValueAfter(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], option, StringComparison.Ordinal)) return args[index + 1];
        return null;
    }

    private static object? Field(object target, string name)
    {
        var field = target.GetType().GetField(name, Private) ??
            throw new MissingFieldException(target.GetType().FullName, name);
        return field.GetValue(target);
    }

    private static void Invoke(object target, string name)
    {
        var method = target.GetType().GetMethod(name, Private) ??
            throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, null);
    }

    private static void CopyBinaries(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var extension = Path.GetExtension(file);
            if (extension is ".exe" or ".dll" or ".pdb" ||
                file.EndsWith(".deps.json", StringComparison.Ordinal) ||
                file.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        foreach (var locale in new[] { "en", "ja", "ko", "runtimes" })
        {
            var directory = Path.Combine(source, locale);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}