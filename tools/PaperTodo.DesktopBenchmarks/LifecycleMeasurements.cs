using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
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

    private const string LifecycleMarker = ".papertodo-desktop-measurement";

    private static void MeasureLifecycle(bool smoke)
    {
        var counts = smoke ? new[] { 1 } : new[] { 1, 5, 10, 25 };
        for (var round = 0; round < (smoke ? 1 : 3); round++)
        foreach (var count in round % 2 == 0 ? counts : counts.Reverse())
        {
            var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.DesktopBenchmarks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                CopyBinaries(AppContext.BaseDirectory, directory);
                File.WriteAllText(Path.Combine(directory, LifecycleMarker), "owned measurement data");
                var start = new ProcessStartInfo(Path.Combine(directory, "PaperTodo.DesktopBenchmarks.exe"))
                {
                    WorkingDirectory = directory, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                start.ArgumentList.Add("--lifecycle-fixture"); start.ArgumentList.Add(count.ToString());
                var began = Stopwatch.GetTimestamp();
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lifecycle fixture.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000))
                {
                    process.Kill(entireProcessTree: true); process.WaitForExit();
                    throw new TimeoutException("Lifecycle fixture did not finish.");
                }
                Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
                if (process.ExitCode != 0) throw new InvalidOperationException("Lifecycle fixture failed: " + process.ExitCode);
                Console.WriteLine("LIFECYCLE_PROCESS " + JsonSerializer.Serialize(new { count, round, totalMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds }));
            }
            finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
        }
    }

    private static int LifecycleFixture(int count)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, LifecycleMarker)))
            throw new InvalidOperationException("Refusing to measure against non-fixture user data.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var state = new AppState
                {
                    TelemetryEnabled = false, EnableAnimations = true, UseCapsuleMode = true,
                    UseDeepCapsuleMode = true, ExperimentalEdgeCapsuleHoverPreview = true,
                    UsePersistentPowerShellProcess = false, McpEnabled = false
                };
                var area = SystemParameters.WorkArea;
                for (var i = 0; i < count; i++) state.Papers.Add(new PaperData
                {
                    Id = "measurement-" + i, Type = PaperTypes.Note, Content = "short note " + i,
                    IsVisible = true, IsCollapsed = true, X = area.Left + 60, Y = area.Top + 60,
                    Width = 300, Height = 240, CapsuleSide = DeepCapsuleSides.Right
                });
                var store = new StateStore(); store.SaveJsonSync(store.SerializeState(state), 1);
                var began = Stopwatch.GetTimestamp();
                using var controller = new AppController();
                var constructed = Stopwatch.GetTimestamp();
                await controller.StartAsync(createDefaultPaper: false);
                var started = Stopwatch.GetTimestamp();
                var windows = (Dictionary<string, PaperWindow>)typeof(AppController)
                    .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
                var cache = MarkdownEdgePreviewPreload.For(app.Dispatcher);
                while (!windows.Values.All(window => window.IsShellBuilt) || cache.PendingCount > 0)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException("Startup never settled.");
                    await Task.Delay(10);
                }
                var ready = Stopwatch.GetTimestamp();
                var visible = windows.Values.Count(window => window.HasVisibleSurface);
                var disposal = Stopwatch.GetTimestamp();
                controller.Dispose();
                Console.WriteLine("LIFECYCLE_SAMPLE " + JsonSerializer.Serialize(new
                {
                    count, visible,
                    ctorMs = Stopwatch.GetElapsedTime(began, constructed).TotalMilliseconds,
                    startupReturnMs = Stopwatch.GetElapsedTime(constructed, started).TotalMilliseconds,
                    readyMs = Stopwatch.GetElapsedTime(constructed, ready).TotalMilliseconds,
                    disposeMs = Stopwatch.GetElapsedTime(disposal).TotalMilliseconds
                }));
            }
            catch (Exception error) { result = 1; Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return result;
    }
}
