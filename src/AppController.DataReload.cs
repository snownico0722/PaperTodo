using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo.Plugin;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record PendingDataReload(AppState Before, AppState Next,
        DataReloadResult Result, PaperOperationContext Origin);

    private FileSystemWatcher? _dataWatcher;
    private DispatcherTimer? _dataReloadTimer;
    private bool _dataReloadReady;
    private bool _dataReloadEvaluating;
    private bool _applyingDataReload;
    private int _dataReloadNotificationPosted;
    private PendingDataReload? _pendingDataReload;
    private DateTimeOffset? _lastDataReloadAt;
    private DataReloadResult? _lastDataReloadResult;
    private string? _lastDataReloadNotice;

    internal bool IsDataReloading => _dataReloadEvaluating || _applyingDataReload || _pendingDataReload != null;
    internal bool SuppressDataReloadEditorCommit => _applyingDataReload || _pendingDataReload != null;

    private void InitializeDataReload()
    {
        _store.EnableExternalReload(State);
        _dataReloadTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _dataReloadTimer.Tick += OnDataReloadTimer;
    }

    private void StartDataReloadWatcher()
    {
        if (IsExiting || _dataReloadReady) return;
        _dataReloadReady = true;
        try
        {
            _dataWatcher = new FileSystemWatcher(Path.GetDirectoryName(_store.FilePath)!, "data.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            _dataWatcher.Changed += OnDataFileChanged;
            _dataWatcher.Created += OnDataFileChanged;
            _dataWatcher.Deleted += OnDataFileChanged;
            _dataWatcher.Renamed += OnDataFileRenamed;
            _dataWatcher.Error += OnDataWatcherError;
            _dataWatcher.EnableRaisingEvents = true;
            QueueDataReload(); // covers edits between Load and watcher installation
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dataWatcher?.Dispose();
            _dataWatcher = null;
            _lastDataReloadResult = new() { Outcome = "failed", Error = ex.Message };
            QueueDataReloadNotice(_lastDataReloadResult);
        }
    }

    private void OnDataFileChanged(object sender, FileSystemEventArgs e) => QueueDataReload();
    private void OnDataFileRenamed(object sender, RenamedEventArgs e) => QueueDataReload();
    private void OnDataWatcherError(object sender, ErrorEventArgs e) => QueueDataReload();

    private void QueueDataReload()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _dataReloadNotificationPosted, 1) != 0) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _dataReloadNotificationPosted, 0);
            if (IsExiting || !_dataReloadReady) return;
            _dataReloadTimer!.Stop();
            _dataReloadTimer.Start();
        }), DispatcherPriority.Background);
    }

    private void OnDataReloadTimer(object? sender, EventArgs e)
    {
        _dataReloadTimer!.Stop();
        if (IsExiting || !_store.HasExternalStateChange()) return;
        var result = ReloadDataCore(ImportDataOperation());
        // Only a real, outstanding change waits for an ongoing gesture/apply. Invalid JSON
        // waits for a new file event or explicit request, not a permanent retry loop.
        if (result.Outcome == "busy" && !IsExiting) _dataReloadTimer.Start();
    }

    private static PaperOperationContext ImportDataOperation() =>
        new(PaperTodoEventOrigin.Import, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    internal DataReloadStatus GetDataReloadStatusCore()
    {
        Application.Current.Dispatcher.VerifyAccess();
        return new()
        {
            IsReloading = IsDataReloading,
            PendingExternalChange = _store.HasExternalStateChange(),
            BaselineRevision = _store.ReloadRevision,
            LastAttemptAt = _lastDataReloadAt,
            LastResult = _lastDataReloadResult
        };
    }

    internal DataReloadResult ReloadDataCore(PaperOperationContext origin)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (IsExiting) return new() { Outcome = "failed", Error = "PaperTodo is exiting." };
        if (!_dataReloadReady || IsDataReloading || Mouse.Captured != null || HasDeepCapsuleReorderDragInProgress())
            return new() { Outcome = "busy" };

        _dataReloadEvaluating = true;
        _lastDataReloadAt = DateTimeOffset.UtcNow;
        try
        {
            // Flush edits into MEMORY, never overwrite the candidate file before reading it.
            // Reentrant saves are suppressed while collecting the current UI snapshot.
            CommitSettingsExternalMarkdownEditor(saveImmediately: false);
            foreach (var window in _windows.Values.ToArray()) window.CommitPendingEditsForSave();
            PrepareExternalPaperOperation();
            var before = StateStore.CopyForReload(State);
            var commit = _store.ReloadExternalState(State, Interlocked.Increment(ref _saveVersion));
            if (commit.Result.Outcome != "unchanged" || _lastDataReloadResult == null)
                _lastDataReloadResult = commit.Result;
            if (commit.State == null)
            {
                QueueDataReloadNotice(commit.Result);
                return commit.Result;
            }

            _saveTimer.Stop();
            _forceSaveTimer.Stop();
            _hasPendingDirty = false;
            _pendingDataReload = new(before, commit.State, commit.Result, origin);
            // A plugin can delete its own paper through this call. Return to the caller before
            // tearing down that session/runtime. Send precedes the next input/render/timer turn;
            // shared commands and saves remain gated until the committed model is reconciled.
            Application.Current.Dispatcher.BeginInvoke(new Action(CompletePendingDataReload), DispatcherPriority.Send);
            return commit.Result;
        }
        catch (Exception ex)
        {
            var result = new DataReloadResult { Outcome = "failed", Error = ex.GetBaseException().Message };
            _lastDataReloadResult = result;
            QueueDataReloadNotice(result);
            return result;
        }
        finally { _dataReloadEvaluating = false; }
    }

    private void CompletePendingDataReload()
    {
        var pending = _pendingDataReload;
        if (pending == null || _applyingDataReload || IsExiting) return;
        _applyingDataReload = true;
        var suppressDirty = _suppressDirty;
        _suppressDirty = true;
        _trayRefreshSuppressionDepth++;
        var events = SuppressPaperPluginEventScans();
        try
        {
            CancelStartupDisplayRestore();
            _paperSurfaceRestoreGeneration++;
            _startupShellPrewarmGeneration++;
            var oldPapers = pending.Before.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var nextPapers = pending.Next.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
            foreach (var pair in _windows.ToArray())
            {
                if (!nextPapers.TryGetValue(pair.Key, out var next) ||
                    (oldPapers.TryGetValue(pair.Key, out var old) &&
                     (old.Type != next.Type || old.BodyProviderId != next.BodyProviderId)))
                {
                    // Close against the OLD model/provider, before installing the new snapshot.
                    // All dirty editor state was captured before disk commit.
                    TryExitCleanup(pair.Value.CloseForReal);
                    _windows.Remove(pair.Key);
                }
                else if (oldPapers.TryGetValue(pair.Key, out var oldPaper) &&
                         (oldPaper.CapsuleSide != next.CapsuleSide ||
                          oldPaper.CapsuleMonitorDeviceName != next.CapsuleMonitorDeviceName))
                    pair.Value.PrepareForCapsulePresentationModeChange();
            }

            StateReloadModels.Apply(State, pending.Next);
            foreach (var paper in State.Papers)
            {
                if (_windows.TryGetValue(paper.Id, out var window) && oldPapers.TryGetValue(paper.Id, out var old))
                {
                    window.ApplyReloadedPaper(old);
                    if (!StateReloadModels.Equivalent(old.Items, paper.Items)) PrunePluginTodoActionsForPaper(paper.Id);
                    if (!paper.IsVisible && old.IsVisible) HidePaper(paper);
                    else if (paper.IsVisible && !old.IsVisible) ShowPaper(paper, activate: false);
                }
                else if (paper.IsVisible) ShowPaper(paper, activate: false);
            }
            if (StateReloadModels.SettingsChanged(pending.Before, State)) RefreshReloadedSettings(pending.Before);
            foreach (var id in oldPapers.Keys.Except(nextPapers.Keys, StringComparer.Ordinal))
                QueuePluginPaperStateDeletion(id);
            TryFlushPendingPluginPaperStateDeletes();
            ReconcilePluginRuntimes();
            RefreshTodoReminderSchedule();
            ArrangeDeepCapsules(animate: false);
            foreach (var paper in State.Papers)
                if (!oldPapers.TryGetValue(paper.Id, out var old) || old.Title != paper.Title ||
                    old.Content != paper.Content || old.BodyHeaderText != paper.BodyHeaderText)
                    RefreshTodoRowsForLinkedPaper(paper.Id);
            Interlocked.Increment(ref _stateRevision);
            NotifyPluginEventMutationStampChanged();
        }
        catch (Exception ex)
        {
            // Disk is already committed. Do not report a rolled-back transaction or reapply the
            // external file. Preserve the authoritative candidate and report presentation failure.
            StateReloadModels.Apply(State, pending.Next);
            _lastDataReloadResult = pending.Result with
            {
                Outcome = "failed", Applied = true,
                Error = "The data was saved, but a display refresh failed: " + ex.GetBaseException().Message
            };
        }
        finally
        {
            _trayRefreshSuppressionDepth--;
            _suppressDirty = suppressDirty;
            _pendingDataReload = null;
            _applyingDataReload = false;
            events.Dispose();
        }
        RefreshTrayMenu();
        PublishExternalPaperOperation(pending.Origin);
        QueueDataReloadNotice(_lastDataReloadResult ?? pending.Result);
        // Existing UI rules may normalize a placeholder or geometry. Save those actual runtime
        // adjustments through the normal versioned writer, not a second persistence path.
        if (!StateReloadModels.Equivalent(State, pending.Next)) MarkDirty();
    }

    private void RefreshReloadedSettings(AppState before)
    {
        _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages;
        AppTypography.Configure(State.UiFontPreset, State.Zoom, State.CustomFontEnhancedBold, State.TextRenderingProfile);
        NoteTypography.Configure(State.NoteTextSize, State.NoteTextBold);
        ApplyGeneralSettingsAfterRestore();
        foreach (var window in _windows.Values.ToArray())
        {
            window.UpdateImageReferenceTextMode();
            window.UpdateMarkdownEditAnimation();
            window.UpdateTodoVisualSize();
        }
        RefreshTypography();
        RefreshThemeSurfaces();
        InitializeGlobalHotkeys();
        RefreshPluginShortcuts();
        RefreshFullscreenAvoidanceRuntime();
        RefreshExperimentalWindowRuntime();
        RefreshEdgeCapsuleHoverIntentRuntime();
        RefreshExperimentalOpacitySurfaces(animate: false);
        RefreshExperimentalFocusPresentationSurfaces();
        TelemetryService.SetEnabled(State.TelemetryEnabled);
        if (before.UsePersistentPowerShellProcess != State.UsePersistentPowerShellProcess ||
            before.PreferPowerShell7 != State.PreferPowerShell7)
        {
            PaperWindow.StopPersistentScriptProcesses();
            PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        }
        // Delay host transport changes until the originating request has returned.
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsExiting) RefreshMcpRuntime();
        }), DispatcherPriority.ContextIdle);
    }

    private void QueueDataReloadNotice(DataReloadResult result)
    {
        if (result.Outcome is "unchanged" or "busy") return;
        if (result.Outcome == "applied" && !result.RestartRequired)
        {
            _lastDataReloadNotice = null;
            return;
        }
        var key = $"{result.Outcome}|{result.ConflictFile}|{result.Error}|{result.RestartRequired}";
        if (key == _lastDataReloadNotice) return;
        _lastDataReloadNotice = key;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsExiting) return;
            var message = result.Outcome == "conflict"
                ? Strings.Format("DataReloadConflict", result.ConflictCount, result.ConflictFile ?? "", result.ConflictDiffFile ?? "")
                : result.Outcome == "applied"
                    ? Strings.Get("DataReloadRestart")
                    : Strings.Format("DataReloadFailed", result.Error ?? result.Outcome);
            if (result.Outcome == "conflict" && result.RestartRequired)
                message += Environment.NewLine + Strings.Get("DataReloadRestart");
            MessageBox.Show(message, Strings.Get("DataReloadTitle"), MessageBoxButton.OK,
                result.Outcome is "failed" or "invalid" ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }), DispatcherPriority.ContextIdle);
    }

    private void StopDataReloadWatcher()
    {
        _dataReloadReady = false;
        _dataWatcher?.Dispose();
        _dataWatcher = null;
        if (_dataReloadTimer != null)
        {
            _dataReloadTimer.Stop();
            _dataReloadTimer.Tick -= OnDataReloadTimer;
            _dataReloadTimer = null;
        }
    }

    private bool FinishDataReloadBeforeExit()
    {
        if (IsDataReloading)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(Exit), DispatcherPriority.Background);
            return false;
        }
        if (_dataReloadReady && _store.HasExternalStateChange())
        {
            _ = ReloadDataCore(ImportDataOperation());
            if (_pendingDataReload != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(Exit), DispatcherPriority.Background);
                return false;
            }
        }
        return true;
    }

    private bool SaveForNormalShutdown()
    {
        if (TrySaveNow(sync: true)) return true;
        if (!_store.HasExternalStateChange()) return false;
        try
        {
            var path = _store.SavePendingExitRecovery(State);
            MessageBox.Show(Strings.Format("DataReloadExitRecovery", Path.GetFileName(path)),
                Strings.Get("DataReloadTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        catch { return false; } // existing exit failure dialog handles inability to preserve data
    }
}
