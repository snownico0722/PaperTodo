using System.Threading;
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
            if (_store.OrdinaryWritesPaused && !IsDataReloading)
                HandleSaveFailure(new StateWriteBlockedException());
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

    // May run from an AppDomain crash callback: no I/O, locks, waits or UI dispatch here.
    internal void AbortPersistenceForTermination()
    {
        Volatile.Write(ref _persistenceTerminationRequested, true);
        _store?.StopWritesForTermination();
    }

    internal void ExitForSystemShutdown()
    {
        AbortPersistenceForTermination();
        if (_lifecycleState != AppLifecycleState.Running)
        {
            return;
        }

        // Autosave already owns durability during normal runtime. Do not start new core or plugin
        // state writes while Windows is ending the session. The plugin store waits for any write
        // already holding its gate to finish, then disables queued/final flushes before disposal.
        _lifecycleState = AppLifecycleState.Exiting;
        _pendingDataReload = null;
        _exitAfterDataReload = false;
        _saveTimer.Stop();
        _forceSaveTimer.Stop();
        StopStateBackupPolicy();
        _paperBodyPlugins.DataStore.SuppressFinalFlushOnDispose();
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
