using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _stateBackupTimer;
    private DispatcherTimer? _coreHistoryDueTimer;
    private int _coreHistoryBackupRunning;
    private bool _coreHistoryDueIsFailureRetry;
    private DateTimeOffset _nextCoreHistoryCheckUtc = DateTimeOffset.MinValue;

    internal void StartStateBackupPolicy()
    {
        if (IsExiting || _stateBackupTimer != null)
        {
            return;
        }

        TryRefreshStateBackup();
        QueueCoreHistoryBackup();

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
        QueueCoreHistoryBackup();
    }

    private void QueueCoreHistoryBackup(bool isFailureRetry = false)
    {
        if (IsExiting || _workspaceSwitching || IsDataReloading) return;
        var now = DateTimeOffset.UtcNow;
        // If the wall clock moves backward, don't leave a year-long due timer.
        if (_nextCoreHistoryCheckUtc - now > CoreHistoryBackup.MinimumInterval)
            _nextCoreHistoryCheckUtc = DateTimeOffset.MinValue;
        if (now < _nextCoreHistoryCheckUtc)
        {
            ScheduleCoreHistoryDueCheck(_nextCoreHistoryCheckUtc - now, isFailureRetry);
            return;
        }
        if (Interlocked.CompareExchange(ref _coreHistoryBackupRunning, 1, 0) != 0)
        {
            // A previous snapshot is still being compressed. Check again shortly.
            ScheduleCoreHistoryDueCheck(TimeSpan.FromSeconds(1), isFailureRetry);
            return;
        }
        _coreHistoryDueTimer?.Stop();
        _coreHistoryDueIsFailureRetry = false;
        _nextCoreHistoryCheckUtc = now.Add(CoreHistoryBackup.MinimumInterval);
        var lifetime = _store.WriteLifetime;
        _ = Task.Run(() =>
        {
            var failed = true;
            try
            {
                new CoreHistoryBackup(AppContext.BaseDirectory, lifetime.ThrowIfStopped)
                    .TryCreateIfDue(now, out failed);
            }
            catch (Exception ex) { Trace.TraceWarning("Core history backup failed: {0}", ex); }
            finally
            {
                Interlocked.Exchange(ref _coreHistoryBackupRunning, 0);
                if (failed && !isFailureRetry)
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
                    {
                        _ = dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!IsExiting && !lifetime.IsStopped)
                                ScheduleCoreHistoryDueCheck(
                                    _nextCoreHistoryCheckUtc - DateTimeOffset.UtcNow, isFailureRetry: true);
                        }));
                    }
                }
            }
        });
    }

    private void ScheduleCoreHistoryDueCheck(TimeSpan delay, bool isFailureRetry = false)
    {
        _coreHistoryDueIsFailureRetry |= isFailureRetry;
        if (_coreHistoryDueTimer?.IsEnabled == true) return;
        if (_coreHistoryDueTimer == null)
        {
            _coreHistoryDueTimer = new DispatcherTimer();
            _coreHistoryDueTimer.Tick += OnCoreHistoryDueTick;
        }
        _coreHistoryDueTimer.Interval = delay > TimeSpan.Zero
            ? delay
            : TimeSpan.FromMilliseconds(100);
        _coreHistoryDueTimer.Start();
    }

    private void OnCoreHistoryDueTick(object? sender, EventArgs e)
    {
        _coreHistoryDueTimer?.Stop();
        var isFailureRetry = _coreHistoryDueIsFailureRetry;
        _coreHistoryDueIsFailureRetry = false;
        QueueCoreHistoryBackup(isFailureRetry);
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
        _coreHistoryDueTimer?.Stop();
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
