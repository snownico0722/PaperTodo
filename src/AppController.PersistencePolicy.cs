using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _stateBackupTimer;

    internal void StartStateBackupPolicy()
    {
        if (IsExiting || _stateBackupTimer != null)
        {
            return;
        }

        TryRefreshStateBackup();

        _stateBackupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6)
        };
        _stateBackupTimer.Tick += OnStateBackupTimerTick;
        _stateBackupTimer.Start();
    }

    private void OnStateBackupTimerTick(object? sender, EventArgs e)
    {
        if (IsExiting)
        {
            StopStateBackupPolicy();
            return;
        }

        TryRefreshStateBackup();
    }

    private void TryRefreshStateBackup()
    {
        try
        {
            _store.TryRefreshBackupFromPrimary();
        }
        catch
        {
            // Backup refresh is an independent safety layer. A failed snapshot must never stop
            // normal primary autosave or make startup/long-running sessions unusable.
        }
    }

    private void StopStateBackupPolicy()
    {
        var timer = _stateBackupTimer;
        _stateBackupTimer = null;
        if (timer == null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnStateBackupTimerTick;
    }

    internal void ExitForSystemShutdown()
    {
        if (IsExiting)
        {
            return;
        }

        // Autosave already owns normal runtime durability. Do not start a new final state save
        // while Windows is ending the session. A save already in flight may finish naturally.
        _lifecycleState = AppLifecycleState.Exiting;
        StopStateBackupPolicy();
        DisposeRuntimeResources();
        _lifecycleState = AppLifecycleState.Disposed;

        try
        {
            Application.Current.Shutdown();
        }
        catch
        {
            // Windows is already ending the session; cleanup must not delay shutdown.
        }
    }
}
