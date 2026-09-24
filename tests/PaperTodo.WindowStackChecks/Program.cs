using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PaperTodo;

internal static class Program
{
    private const string Marker = ".papertodo-window-stack-fixture";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--external") return RunExternal();
        if (args.Length == 3 && args[0] == "--fixture")
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, Marker)))
                throw new InvalidOperationException("Refusing to open non-fixture PaperTodo data.");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var result = 0;
            app.Dispatcher.InvokeAsync(async () =>
            {
                try { await RunFixture(args[1], args[2]); }
                catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
                finally { app.Shutdown(); }
            });
            app.Run();
            return result;
        }

        var failed = 0;
        var total = 0;
        foreach (var visibility in new[] { "normal", "taskbar-hidden", "switcher-hidden" })
        foreach (var operation in new[] { "delete", "close-hide", "button-hide", "background-delete", "background-hide",
            "animated-hide", "focus-change-during-fade", "hide-all", "peer-next", "animated-reopen" })
        {
            // Every operation still runs unowned and with the hidden native owner. The
            // intermediate taskbar-only mode needs representatives, not another full product.
            if (visibility == "taskbar-hidden" && operation is not ("delete" or "animated-hide")) continue;
            total++;
            try { RunIsolated(visibility, operation); }
            catch (Exception ex) { failed++; Console.Error.WriteLine(ex); }
        }
        Console.WriteLine($"WINDOW_STACK_RESULT {total - failed}/{total} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RunIsolated(string visibility, string operation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaperTodo.WindowStackChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // The real controller owns data beside the executable. Copy only binaries, never
            // an installed data.json, image store, plugin directory or user settings.
            CopyBinaries(AppContext.BaseDirectory, directory);
            File.WriteAllText(Path.Combine(directory, Marker), "isolated test data");
            var start = ChildStart(directory);
            start.ArgumentList.Add("--fixture");
            start.ArgumentList.Add(visibility);
            start.ArgumentList.Add(operation);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(40_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException($"Fixture timed out: {visibility}/{operation}");
            }
            Console.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, $"Fixture failed: {visibility}/{operation}");
            Console.WriteLine($"PASS stack {visibility}/{operation}");
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    private static ProcessStartInfo ChildStart(string directory) => new(
        Path.Combine(directory, "PaperTodo.WindowStackChecks.exe"))
    {
        WorkingDirectory = directory, UseShellExecute = false,
        RedirectStandardInput = true, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true
    };

    private static void CopyBinaries(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var extension = Path.GetExtension(file);
            if (extension is ".exe" or ".dll" or ".pdb" ||
                file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))
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

    private static int RunExternal()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var back = new Window { Title = "Stack Z3 external", Width = 360, Height = 260, Left = 100, Top = 100, ShowActivated = false };
        var next = new Window { Title = "Stack Z1 external", Width = 360, Height = 260, Left = 100, Top = 100, ShowActivated = false };
        back.Show();
        next.Show();
        app.Dispatcher.InvokeAsync(() =>
        {
            Console.WriteLine($"READY {new WindowInteropHelper(next).Handle.ToInt64()} {new WindowInteropHelper(back).Handle.ToInt64()}");
            Console.Out.Flush();
        }, DispatcherPriority.ApplicationIdle);
        // EOF also exits the helper if its fixture parent fails.
        _ = Task.Run(async () =>
        {
            await Console.In.ReadLineAsync();
            await app.Dispatcher.InvokeAsync(app.Shutdown);
        });
        app.Run();
        return 0;
    }

    private static async Task RunFixture(string visibility, string operation)
    {
        var state = new AppState
        {
            TelemetryEnabled = false,
            EnableAnimations = operation is "animated-hide" or "focus-change-during-fade" or "animated-reopen",
            UseCapsuleMode = false, UseDeepCapsuleMode = false,
            UsePersistentPowerShellProcess = false, McpEnabled = false,
            HidePapersFromTaskbar = visibility != "normal",
            HidePapersFromWindowSwitcher = visibility == "switcher-hidden"
        };
        foreach (var id in new[] { "closing", "peer" })
            state.Papers.Add(new PaperData
            {
                Id = id, Type = PaperTypes.Note, Content = "stack fixture " + id,
                IsVisible = true, IsCollapsed = false,
                X = 100, Y = 100, Width = 360, Height = 260
            });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Settle();
        var windows = (Dictionary<string, PaperWindow>)typeof(AppController)
            .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        var closing = windows["closing"];
        var peer = windows["peer"];
        var paper = controller.State.Papers.Single(p => p.Id == "closing");
        var z0 = new WindowInteropHelper(closing).Handle;
        var z2 = new WindowInteropHelper(peer).Handle;
        var start = ChildStart(AppContext.BaseDirectory);
        start.ArgumentList.Add("--external");
        using var external = Process.Start(start)!;
        var errors = external.StandardError.ReadToEndAsync();
        try
        {
            var ready = await external.StandardOutput.ReadLineAsync().WaitAsync(Timeout);
            Require(ready?.StartsWith("READY ") == true, "External window helper did not initialize: " + ready);
            var fields = ready!.Split(' ');
            var z1 = new IntPtr(long.Parse(fields[1]));
            var z3 = new IntPtr(long.Parse(fields[2]));
            var known = new[] { z0, z1, z2, z3 };
            var initialStack = operation is "peer-next" or "hide-all"
                ? new[] { z0, z2, z1, z3 } : known;
            foreach (var handle in initialStack.Reverse()) BringToTop(handle);
            Require(SetForegroundWindow(z0), "Fixture was not granted foreground; test was not performed.");
            await Until(() => GetForegroundWindow() == z0, "activate Z0");
            await Settle();
            Require(Stack(known).SequenceEqual(initialStack), "Could not establish Z0/Z1/Z2/Z3: " + Describe(known));
            Console.WriteLine($"BEFORE {visibility}/{operation} {Describe(known)} managedOwner={new WindowInteropHelper(closing).Owner} nativeOwner={GetWindow(z0, 4)}");

            if (operation is "background-delete" or "background-hide")
            {
                Require(SetForegroundWindow(z1), "Could not foreground external Z1 for background-deletion test.");
                await Until(() => GetForegroundWindow() == z1, "activate external Z1");
                await Settle();
            }

            if (operation == "hide-all") controller.HideAllPapers();
            else if (operation == "button-hide")
            {
                var button = (System.Windows.Controls.Button)typeof(PaperWindow)
                    .GetField("_closeButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(closing)!;
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            else if (operation is "delete" or "background-delete" or "peer-next") controller.DeletePaper(paper);
            else closing.Close();

            if (operation == "animated-reopen")
            {
                controller.ShowPaper(paper);
                await Settle();
                Require(closing.IsVisible && !closing.IsClosed && GetForegroundWindow() == z0,
                    "Cancelled hide withdrew or deactivated the reopened paper: " + Describe(known));
                Require(Stack(known).SequenceEqual(initialStack), "Cancelled hide reordered the stack.");
                Console.WriteLine($"AFTER {visibility}/{operation} {Describe(known)}");
                return;
            }
            var expectedForeground = operation == "peer-next" ? z2 : z1;
            if (operation == "focus-change-during-fade")
            {
                // Simulate a newer user activation before the fade completion reaches Hide().
                Require(SetForegroundWindow(z3), "Could not switch away during the fade.");
                expectedForeground = z3;
            }
            await Until(() => !closing.IsVisible || closing.IsClosed, "remove Z0 surface");
            // Cross-process SetForegroundWindow completion requires the other input queue to
            // dispatch messages. Pump both real queues instead of asserting on its return value.
            await Until(() => GetForegroundWindow() == expectedForeground,
                "Wrong foreground after removal; " + Describe(known));
            await Settle();
            var survivors = operation switch
            {
                "hide-all" => new[] { z1, z3 },
                "peer-next" => new[] { z2, z1, z3 },
                "focus-change-during-fade" => new[] { z3, z1, z2 },
                _ => new[] { z1, z2, z3 }
            };
            Require(GetForegroundWindow() == expectedForeground, "Foreground changed again after close: " + Describe(known));
            Require(Stack(survivors).SequenceEqual(survivors), "Close reordered background windows: " + Describe(known));
            Require(!peer.IsClosed && peer.IsVisible == (operation != "hide-all"),
                "Wrong peer lifecycle after removal.");
            Console.WriteLine($"AFTER {visibility}/{operation} {Describe(known)}");
        }
        finally
        {
            external.StandardInput.Close();
            if (!external.WaitForExit(3000)) { external.Kill(entireProcessTree: true); external.WaitForExit(); }
            Console.Error.Write(await errors);
        }
    }

    private static async Task Settle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    private static IntPtr[] Stack(IntPtr[] known)
    {
        var result = new List<IntPtr>();
        var current = GetTopWindow(IntPtr.Zero);
        for (var count = 0; current != IntPtr.Zero && count < 4096; count++)
        {
            if (known.Contains(current) && IsWindowVisible(current)) result.Add(current);
            current = GetWindow(current, 2);
        }
        return result.ToArray();
    }

    private static string Describe(IntPtr[] known) =>
        "foreground=Z" + Array.IndexOf(known, GetForegroundWindow()) +
        " order=" + string.Join(",", Stack(known).Select(h => "Z" + Array.IndexOf(known, h)));

    private static void BringToTop(IntPtr handle) =>
        Require(SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0213), "Could not order test windows.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
