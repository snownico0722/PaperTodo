using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace PaperTodo;

public partial class App : Application
{
    private const long MaxCrashLogBytes = 100 * 1024;
    private static readonly HashSet<string> SharedDesktopRuntimeAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Windows.Forms",
        "System.Drawing",
        "System.Drawing.Common",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "WindowsFormsIntegration",
        "System.Xaml",
        "UIAutomationTypes",
        "UIAutomationProvider",
        "UIAutomationClient",
        "ReachFramework",
        "DirectWriteForwarder",
        "System.Windows.Controls.Ribbon",
        "Microsoft.VisualBasic.Forms"
    };
    private readonly object _singleInstanceCommandGate = new();
    private readonly Queue<IReadOnlyList<string>> _pendingSingleInstanceCommands = new();
    private AppController? _controller;
    private bool _singleInstanceCommandsReady;
    private SingleInstanceHelper? _singleInstance;
    private int _handlingGlobalException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (McpBridge.IsRequested(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await McpBridge.RunAsync(e.Args);
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine(
                        $"PaperTodo MCP failed: {ex.GetBaseException().Message}");
                }
                catch
                {
                    // stderr is optional; never replace an MCP failure with a logging failure.
                }
                Environment.ExitCode = 1;
            }
            Shutdown();
            return;
        }

        var startupCommand = StartupCommand.Parse(e.Args);
        ApplyStartupCulturePreference(UiLanguages.LoadPersistedPreference());

        // Register global unhandled exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _singleInstance = new SingleInstanceHelper("PaperTodo-SingleInstance-Mutex", "PaperTodo-SingleInstance-Activate");
        if (!_singleInstance.TryAcquire())
        {
            var exitCode = _singleInstance.SignalPrimaryInstance(e.Args,
                waitForResult: startupCommand.Kind == StartupCommandKind.EnableMcpForCodex);
            if (exitCode != 0 && startupCommand.Kind == StartupCommandKind.EnableMcpForCodex)
                Console.Error.WriteLine(exitCode == 1
                    ? "PaperTodo did not enable MCP. Check the Codex plugin and its Allow MCP setting."
                    : "PaperTodo did not return an MCP enable result.");
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(exitCode);
            Environment.Exit(exitCode);
            return;
        }

        // This command belongs to a live Codex plugin session. It must never start
        // a new GUI, restore papers or enable MCP after the original host has exited.
        if (startupCommand.Kind == StartupCommandKind.EnableMcpForCodex)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Console.Error.WriteLine("PaperTodo is not running; MCP was not enabled.");
            Shutdown(2);
            Environment.Exit(2);
            return;
        }

        // Listen as soon as this process owns the mutex. Commands received while
        // the controller is loading stay queued until startup is fully complete.
        _singleInstance.StartListener(HandleSingleInstanceCommand);
#if DEBUG
        EdgeDiagnosticObservation.InstallInput(); // edgeJournal: no Rendering observer
#endif

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _controller = new AppController();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                Strings.Format("AppStartupFailureMessage", ex.Message),
                Strings.Get("AppStartupFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            _singleInstance?.Dispose();
            _singleInstance = null;

            Shutdown();
            return;
        }

        if (startupCommand.Kind == StartupCommandKind.Exit)
        {
            _controller.ExecuteStartupCommand(startupCommand);
            return;
        }

        SessionEnding += (s, args) => _controller?.ExitForSystemShutdown();
        var handlesInitialVisibility = startupCommand.Kind is
            StartupCommandKind.Hide or StartupCommandKind.Toggle;
        await _controller.StartAsync(
            createDefaultPaper: !startupCommand.CreatesPaper,
            initialVisibilityCommand: handlesInitialVisibility
                ? startupCommand.Kind
                : StartupCommandKind.None);
        if (!_controller.IsRunning)
        {
            return;
        }
        if (!handlesInitialVisibility)
        {
            _controller.ExecuteStartupCommand(startupCommand);
        }
        _controller.StartStateBackupPolicy();
        CompleteSingleInstanceStartup();
    }

    private bool HandleSingleInstanceCommand(IReadOnlyList<string> args)
    {
        lock (_singleInstanceCommandGate)
        {
            if (!_singleInstanceCommandsReady)
            {
                // This command belongs to an already-running plugin, not the startup queue.
                if (StartupCommand.Parse(args).Kind == StartupCommandKind.EnableMcpForCodex) return false;
                _pendingSingleInstanceCommands.Enqueue(new List<string>(args));
                return true;
            }
        }

        return DispatchSingleInstanceCommand(args);
    }

    private void CompleteSingleInstanceStartup()
    {
        while (true)
        {
            IReadOnlyList<string> args;
            lock (_singleInstanceCommandGate)
            {
                if (_pendingSingleInstanceCommands.Count == 0)
                {
                    _singleInstanceCommandsReady = true;
                    return;
                }

                args = _pendingSingleInstanceCommands.Dequeue();
            }

            ExecuteSingleInstanceCommand(args);
        }
    }

    private bool DispatchSingleInstanceCommand(IReadOnlyList<string> args)
    {
        try
        {
            return Dispatcher.Invoke(() => ExecuteSingleInstanceCommand(args));
        }
        catch (InvalidOperationException)
        {
            // The application is already shutting down.
            return false;
        }
    }

    private bool ExecuteSingleInstanceCommand(IReadOnlyList<string> args)
    {
        var command = StartupCommand.Parse(args, StartupCommandKind.Show);
        return _controller?.ExecuteStartupCommand(command) == true;
    }

    private static void ApplyStartupCulturePreference(string preference)
    {
        if (UiLanguages.TryGetCulture(preference, out var startupCulture))
        {
            ApplyCulture(startupCulture);
        }
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs ev)
    {
        ev.Handled = true;
        HandleGlobalException(ev.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs ev)
    {
        if (ev.ExceptionObject is Exception ex)
        {
            HandleGlobalException(ex);
        }
    }

    private void HandleGlobalException(Exception ex)
    {
        if (Interlocked.Exchange(ref _handlingGlobalException, 1) != 0)
        {
            return;
        }

        // Classification must never prevent the crash log from being written.
        var isDesktopRuntimeLoadFailure = false;
        try
        {
            isDesktopRuntimeLoadFailure = IsSharedDotNetRuntimeLoadFailure(ex);
        }
        catch
        {
            // Fall back to the generic crash path.
        }

        // Do not serialize in-memory state here: auto-save + data.backup.json already cover
        // normal durability, and crash-time memory may already be inconsistent.
        WriteCrashLog(ex);

        try
        {
            var messageKey = isDesktopRuntimeLoadFailure
                ? "AppDesktopRuntimeLoadFailureMessage"
                : "AppUnhandledExceptionMessage";
            var titleKey = isDesktopRuntimeLoadFailure
                ? "AppDesktopRuntimeLoadFailureTitle"
                : "AppUnhandledExceptionTitle";

            MessageBox.Show(
                Strings.Format(messageKey, ex.Message),
                Strings.Get(titleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Ignore if GUI popup fails during crash
        }

        Environment.Exit(-1);
    }

    private static bool IsSharedDotNetRuntimeLoadFailure(Exception? ex)
    {
        try
        {
            var pending = new Stack<Exception?>();
            pending.Push(ex);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                while (current != null)
                {
                    if (current is ReflectionTypeLoadException reflectionLoad)
                    {
                        foreach (var loader in reflectionLoad.LoaderExceptions)
                        {
                            pending.Push(loader);
                        }
                    }

                    if (current is FileNotFoundException fileNotFound &&
                        IsSharedDesktopRuntimeAssembly(fileNotFound.FileName))
                    {
                        return true;
                    }

                    current = current.InnerException;
                }
            }

            return false;
        }
        catch
        {
            // Crash classification is best-effort; never rethrow into the handler.
            return false;
        }
    }

    private static bool IsSharedDesktopRuntimeAssembly(string? assemblyDisplayName)
    {
        if (string.IsNullOrWhiteSpace(assemblyDisplayName))
        {
            return false;
        }

        // AssemblyName accepts display names, but path-like FileNotFoundException.FileName
        // values throw FileLoadException (not only ArgumentException).
        string? simpleName;
        try
        {
            simpleName = new AssemblyName(assemblyDisplayName).Name;
        }
        catch (Exception)
        {
            return false;
        }

        return simpleName != null && SharedDesktopRuntimeAssemblies.Contains(simpleName);
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "PaperTodo.crash.log");
            TrimCrashLog(logPath);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures during crash handling.
        }
    }

    private static void TrimCrashLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length <= MaxCrashLogBytes)
        {
            return;
        }

        const int keepBytes = 80 * 1024;
        using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytesToRead = (int)Math.Min(keepBytes, stream.Length);
        stream.Seek(-bytesToRead, SeekOrigin.End);

        var buffer = new byte[bytesToRead];
        _ = stream.Read(buffer, 0, bytesToRead);

        var marker = $"[Crash log trimmed to last {keepBytes / 1024} KB at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}";
        File.WriteAllText(logPath, marker);
        File.AppendAllText(logPath, System.Text.Encoding.UTF8.GetString(buffer));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
#if DEBUG
        EdgeDiagnosticObservation.RemoveInput();
        EdgeDiagnosticJournal.Complete("normal-exit");
#endif
    }
}
