using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _stateBackupTimer;
    private DispatcherTimer? _coreHistoryTimer;
    private int _coreHistoryBackupRunning;

    // The core JSON history is independent of the active workspace and the normal
    // autosave flow. Delay the first check so startup can settle, then scan every
    // ten minutes, including after a workspace's transient read failure.
    internal void StartCoreHistoryBackupPolicy()
    {
        if (IsExiting || _coreHistoryTimer != null) return;
        _coreHistoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _coreHistoryTimer.Tick += OnCoreHistoryTimerTick;
        _coreHistoryTimer.Start();
    }

    private void OnCoreHistoryTimerTick(object? sender, EventArgs e)
    {
        if (IsExiting)
        {
            StopCoreHistoryBackupPolicy();
            return;
        }

        // This also changes the first-start delay into the steady ten-minute cycle.
        if (_coreHistoryTimer != null)
            _coreHistoryTimer.Interval = CoreHistoryBackup.MinimumInterval;

        if (Interlocked.CompareExchange(ref _coreHistoryBackupRunning, 1, 0) != 0)
            return;

        // The shared write-lifetime is the only link to persistence: it prevents
        // publishing files after shutdown. No workspace UI or save hooks participate.
        var lifetime = _workspaceCatalog.MainStore.WriteLifetime;
        _ = Task.Run(() =>
        {
            try
            {
                new CoreHistoryBackup(AppContext.BaseDirectory, lifetime.ThrowIfStopped)
                    .TryCreateIfDue(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) { Trace.TraceWarning("Core history backup failed: {0}", ex); }
            finally { Interlocked.Exchange(ref _coreHistoryBackupRunning, 0); }
        });
    }

    private void StopCoreHistoryBackupPolicy()
    {
        var timer = _coreHistoryTimer;
        _coreHistoryTimer = null;
        if (timer == null) return;
        timer.Stop();
        timer.Tick -= OnCoreHistoryTimerTick;
    }

    internal void StartStateBackupPolicy()
    {
        if (IsExiting || _stateBackupTimer != null) return;

        TryRefreshStateBackup();
        _stateBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
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
            // Reuse the existing maintenance timer; do not install another
            // background loop just to age out conflict recovery records.
            _store.TryPruneConflictArchives(DateTime.UtcNow);
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
        StopCoreHistoryBackupPolicy();
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

