using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public partial class App
{
    private readonly TelemetryBootstrap _telemetryBootstrap;

    public App()
    {
        _telemetryBootstrap = new TelemetryBootstrap(this);
    }
}

internal sealed class TelemetryBootstrap
{
    private readonly App _app;
    private DispatcherTimer? _attachTimer;
    private int _crashRecorded;

    public TelemetryBootstrap(App app)
    {
        _app = app;
        _app.Startup += OnStartup;
        _app.Exit += OnExit;
        _app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Give normal startup/restoration priority. Telemetry attaches after the app has settled,
        // so restoring persisted papers is not mistaken for fresh user activity.
        _attachTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, _app.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _attachTimer.Tick += OnAttachTimerTick;
        _attachTimer.Start();
    }

    private void OnAttachTimerTick(object? sender, EventArgs e)
    {
        try
        {
            var controller = AppController.Current;
            if (controller == null || controller.State == null)
            {
                return;
            }

            TelemetryService.Attach(controller);
            StopAttachTimer();
        }
        catch
        {
            // A partially initialized controller is possible while startup is still unwinding.
            // Retry on the next idle tick instead of affecting startup.
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        StopAttachTimer();
        try
        {
            TelemetryService.Detach();
        }
        catch
        {
            // Telemetry must never block application shutdown.
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        RecordCrashOnce(e.Exception, "dispatcher");
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        RecordCrashOnce(e.ExceptionObject as Exception, "appdomain");
    }

    private void RecordCrashOnce(Exception? exception, string source)
    {
        if (Interlocked.Exchange(ref _crashRecorded, 1) != 0)
        {
            return;
        }

        TelemetryService.RecordEmergencyCrash(exception, source);
    }

    private void StopAttachTimer()
    {
        if (_attachTimer == null)
        {
            return;
        }

        _attachTimer.Stop();
        _attachTimer.Tick -= OnAttachTimerTick;
        _attachTimer = null;
    }
}
