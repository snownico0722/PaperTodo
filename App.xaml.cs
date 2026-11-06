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
    private StandaloneStudioWindow? _standaloneStudio;
    private bool _singleInstanceCommandsReady;
    private SingleInstanceHelper? _singleInstance;
    private int _handlingGlobalException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var imageImportRequested = McpImageImportCommand.IsRequested(e.Args);
        if (imageImportRequested || McpBridge.IsRequested(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = 0;
            try
            {
                if (imageImportRequested)
                    exitCode = await McpImageImportCommand.RunAsync(e.Args);
                else
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
                exitCode = 1;
            }
            Shutdown(exitCode);
            Environment.ExitCode = exitCode;
            return;
        }

        var startupCommand = StartupCommand.Parse(e.Args);
        ApplyStartupCulturePreference(UiLanguages.LoadPersistedPreference());

        // Register global unhandled exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        if (startupCommand.Standalone)
        {
            StartStandaloneStudio(e, startupCommand);
            return;
        }

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
        catch (OperationCanceledException)
        {
            // Declining historical recovery stops startup without creating blank
            // data or presenting the user's choice as an application crash.
            _singleInstance?.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                WorkspaceFailureMessage.ForStartup(ex),
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
            createDefaultPaper: !startupCommand.CreatesPaper && startupCommand.Kind != StartupCommandKind.Studio,
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
        _controller.StartCoreHistoryBackupPolicy();
        CompleteSingleInstanceStartup();
    }

    private void StartStandaloneStudio(StartupEventArgs e, StartupCommand command)
    {
        // Separate identity: a normal PaperTodo instance and this file editor can coexist.
        // Resolve relative paths in the launching process, before forwarding to another CWD.
        string? file;
        try { file = command.FilePath == null ? null : Path.GetFullPath(command.FilePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(ex.Message, Strings.Get("StudioFileErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var forwarded = file == null ? new[] { "--studio", "--standalone" }
            : new[] { "--studio", "--standalone", "--file", file };
        _singleInstance = new SingleInstanceHelper("PaperTodo-Studio-Mutex", "PaperTodo-Studio-Activate");
        if (!_singleInstance.TryAcquire())
        {
            var result = _singleInstance.SignalPrimaryInstance(forwarded);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(result);
            return;
        }
        _singleInstance.StartListener(HandleSingleInstanceCommand);
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var settings = StandaloneStudioSettings.Load();
            UiLanguages.ApplyWorkspacePreference(settings.UiLanguage);
            _standaloneStudio = new StandaloneStudioWindow(settings);
            MainWindow = _standaloneStudio;
            _standaloneStudio.Closed += (_, _) => Shutdown();
            _standaloneStudio.Loaded += (_, _) =>
            {
                _standaloneStudio.Initialize(file);
                CompleteSingleInstanceStartup();
            };
            SessionEnding += (_, _) => _standaloneStudio.PreserveForSessionEnding();
            _standaloneStudio.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(ex.Message, Strings.Get("StudioFileErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        if (_standaloneStudio != null)
        {
            // Release the pipe listener before a file request can open a modal
            // replacement dialog. The window queues requests while a dialog is active.
            _ = Dispatcher.BeginInvoke((Action)(() => _standaloneStudio.ActivateDocument(command.FilePath)));
            return true;
        }
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

        // A crash dialog pumps messages too. Stop queued persistence BEFORE displaying it,
        // not merely by omitting an explicit final SaveNow call at the bottom of this handler.
        _controller?.AbortPersistenceForTermination();
        _standaloneStudio?.StopRecovery();

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
        _standaloneStudio?.StopRecovery();
        _controller?.Dispose();
        base.OnExit(e);
#if DEBUG
        EdgeDiagnosticObservation.RemoveInput();
        EdgeDiagnosticJournal.Complete("normal-exit");
#endif
    }
}

