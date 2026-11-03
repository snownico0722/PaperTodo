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

    // The primary write has already succeeded. Independent maintenance must neither
    // turn it into a reported save failure nor borrow another component's cleanup hook.
    private void CompleteStateSaveMaintenance(bool releaseImageCache)
    {
        if (IsExiting) return;
        if (releaseImageCache) TryReleaseUnreferencedImageCache();
        RunExternalPostCommitUi(TryFlushPendingPluginPaperStateDeletes);
        RunExternalPostCommitUi(ReconcilePluginRuntimes);
    }

    internal void ExitForSystemShutdown()
    {
        if (IsExiting)
        {
            return;
        }

        // Autosave already owns durability during normal runtime. Do not start new core or plugin
        // state writes while Windows is ending the session. The plugin store waits for any write
        // already holding its gate to finish, then disables queued/final flushes before disposal.
        _lifecycleState = AppLifecycleState.Exiting;
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
