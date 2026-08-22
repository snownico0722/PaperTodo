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
        var startupCommand = StartupCommand.Parse(e.Args);
        ApplyStartupCultureOverride(startupCommand.DefaultLanguage);

        // Register global unhandled exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _singleInstance = new SingleInstanceHelper("PaperTodo-SingleInstance-Mutex", "PaperTodo-SingleInstance-Activate");
        if (!_singleInstance.TryAcquire())
        {
            _singleInstance.SignalPrimaryInstance(e.Args);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            Environment.Exit(0);
            return;
        }

        // Listen as soon as this process owns the mutex. Commands received while
        // the controller is loading stay queued until startup is fully complete.
        _singleInstance.StartListener(HandleSingleInstanceCommand);

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

        SessionEnding += (s, args) => _controller?.Exit();
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
        CompleteSingleInstanceStartup();
    }

    private void HandleSingleInstanceCommand(IReadOnlyList<string> args)
    {
        lock (_singleInstanceCommandGate)
        {
            if (!_singleInstanceCommandsReady)
            {
                _pendingSingleInstanceCommands.Enqueue(new List<string>(args));
                return;
            }
        }

        DispatchSingleInstanceCommand(args);
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

    private void DispatchSingleInstanceCommand(IReadOnlyList<string> args)
    {
        try
        {
            Dispatcher.Invoke(() => ExecuteSingleInstanceCommand(args));
        }
        catch (InvalidOperationException)
        {
            // The application is already shutting down.
        }
    }

    private void ExecuteSingleInstanceCommand(IReadOnlyList<string> args)
    {
        var command = StartupCommand.Parse(args, StartupCommandKind.Show);
        _controller?.ExecuteStartupCommand(command);
    }

    private static void ApplyStartupCultureOverride(string? defaultLanguage)
    {
        // Explicit --language/--lang remains highest priority. Without it, use the
        // persisted Settings choice; "system" preserves the real process culture.
        UiLanguages.ConfigureStartupLanguage(defaultLanguage);
        if (UiLanguages.ShouldApplyThreadCulture)
        {
            ApplyCulture(UiLanguages.EffectiveCulture);
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
    }
}
