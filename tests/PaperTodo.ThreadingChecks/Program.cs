using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo.ThreadingChecks;

internal static partial class Program
{
    private static readonly (string Name, Action Run)[] Checks =
    [
        ("paper-menu-worker-first", CheckPaperResources),
        ("tray-menu-worker-first", CheckTrayResources),
        ("menu-caches-two-sta-threads", CheckSeparateUiThreads),
        ("shared-easings-worker-first", CheckFrozenEasings),
        ("menu-scale-refresh", CheckMenuScaleRefresh),
        ("runtime-expired-queued-calls", CheckExpiredRuntimeCalls),
        ("runtime-capsule-publish-order", CheckCapsulePublication),
        ("runtime-invalid-owner-no-publication", CheckInvalidOwner),
        ("single-instance-timeout-keeps-listening", CheckSingleInstanceTimeout),
        ("single-instance-cancel-pending-read", CheckSingleInstanceCancellation),
        ("inactive-titlebar-chrome-layout", CheckInactiveTitleBarChrome),
        ("remembered-paper-dpi-restore", CheckRememberedPaperRestore)
    ];

    private static void CheckPaperResources() =>
        CheckResources(typeof(PaperWindow), PaperResources);

    private static void CheckTrayResources() =>
        CheckResources(typeof(AppController), TrayResources);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--case")
        {
            var check = Checks.Single(value => value.Name == args[1]);
            try
            {
                // Plain WPF Application only: never load the real App, stores, plugins or user data.
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                check.Run();
                application.Shutdown();
                Console.WriteLine($"PASS {check.Name}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {check.Name}: {ex}");
                return 1;
            }
        }

        var failures = 0;
        foreach (var check in Checks)
        {
            // Type initialization runs once per process. Isolate cases so a previous test cannot
            // silently warm a UI cache on the right thread and conceal the worker-first regression.
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--case");
            start.ArgumentList.Add(check.Name);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Console.Error.WriteLine($"FAIL {check.Name}: child timed out");
                failures++;
            }
            else if (process.ExitCode != 0) failures++;
            Console.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
        }
        Console.WriteLine($"Threading checks: {Checks.Length - failures}/{Checks.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static T ReadStatic<T>(Type type, string name) =>
        (T)(type.GetProperty(name, PrivateStatic)?.GetValue(null)
            ?? type.GetField(name, PrivateStatic)?.GetValue(null)
            ?? throw new InvalidOperationException($"Missing resource {type.Name}.{name}"));

    private static T ReadField<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, PrivateInstance)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing field {name}"));

    private static void SetField(object owner, string name, object value) =>
        (owner.GetType().GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"Missing field {name}")).SetValue(owner, value);

    private static Task QueueWorker(Action action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPosted(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority == DispatcherPriority.Send) posted.TrySetResult();
        }
        dispatcher.Hooks.OperationPosted += OnPosted;
        try
        {
            var task = Task.Run(action);
            Assert(posted.Task.Wait(TimeSpan.FromSeconds(5)), "worker never queued its UI call");
            return task;
        }
        finally { dispatcher.Hooks.OperationPosted -= OnPosted; }
    }

    private static void Drain(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timeout = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(
                DispatcherPriority.Send, (Action)(() => frame.Continue = false)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Assert(task.IsCompleted, "UI call did not complete while pumping the dispatcher");
        }
        task.GetAwaiter().GetResult();
    }
}
