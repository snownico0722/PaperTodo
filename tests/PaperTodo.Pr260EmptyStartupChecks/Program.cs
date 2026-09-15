using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const string FixtureMarker = ".papertodo-pr260-empty-start-fixture";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture", StringComparer.Ordinal))
        {
            return RunFixtureProcess(args.Contains("--expect-defect", StringComparer.Ordinal));
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
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = fixture,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--fixture");
            if (expectDefect) start.ArgumentList.Add("--expect-defect");
            using var child = Process.Start(start) ??
                throw new InvalidOperationException("Empty-start fixture process did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(20_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("Empty-start fixture timed out.");
            }
            Console.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
            return child.ExitCode;
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

    private static int RunFixtureProcess(bool expectDefect)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, FixtureMarker)))
        {
            Console.Error.WriteLine("Refusing to use a non-fixture data directory.");
            return 1;
        }

        var state = new AppState
        {
            TelemetryEnabled = false,
            EnableAnimations = true,
            UseCapsuleMode = true,
            UseDeepCapsuleMode = true,
            ExperimentalEdgeCapsuleHoverPreview = true,
            UsePersistentPowerShellProcess = false,
            McpEnabled = false
        };
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
                Require(controller.State.Papers.Count == 0,
                    "Fixture did not start from an empty paper state.");
                await controller.StartAsync(createDefaultPaper: false);
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

                var restoreGeneration = (int)Field(controller, "_paperSurfaceRestoreGeneration")!;
                var startupReady = (bool)Field(controller, "_edgePrewarmStartupReady")!;
                // Exercise the public-behaviour gate directly: candidate startup completion already
                // creates the coordinator; pinned #260 still returns before it can do so.
                Invoke(controller, "RequestEdgePrewarmForVisibleQueues");
                var coordinatorCreated = Field(controller, "_edgePrewarm") != null;

                Require(restoreGeneration == 0,
                    "Empty startup unexpectedly ran the non-empty surface restore path.");
                if (expectDefect)
                {
                    Require(!startupReady && !coordinatorCreated,
                        "Pinned #260 no longer reproduces the empty-start prewarm gate defect.");
                }
                else
                {
                    Require(startupReady && coordinatorCreated,
                        "Empty startup did not complete the native-prewarm startup gate.");
                }

                Console.WriteLine(
                    $"RESULT pr260-empty-start expectDefect={expectDefect} " +
                    $"restoreGeneration={restoreGeneration} startupReady={startupReady} " +
                    $"coordinatorCreated={coordinatorCreated}");
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
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
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
                File.Copy(file, destination);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}