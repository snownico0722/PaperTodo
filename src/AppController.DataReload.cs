using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PaperTodo.Plugin;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private sealed record PendingDataReload(
        AppState Before, AppState Next, DataReloadResult Result, PaperOperationContext Origin,
        bool IsMerge = false, long SaveVersion = 0,
        IReadOnlyList<StateMergeConflict>? Conflicts = null,
        JsonObject? ExternalJson = null, string? ObservedDiskJson = null);

    // No watcher or background history: reload happens only after an explicit API/MCP request.
    private bool _dataReloadEvaluating;
    private bool _applyingDataReload;
    private PendingDataReload? _pendingDataReload;
    private DateTimeOffset? _lastDataReloadAt;
    private DataReloadResult? _lastDataReloadResult;

    internal bool IsDataReloading => _dataReloadEvaluating || _applyingDataReload || _pendingDataReload != null;
    internal bool SuppressDataReloadEditorCommit => _applyingDataReload || _pendingDataReload != null;

    internal DataReloadStatus GetDataReloadStatusCore()
    {
        Application.Current.Dispatcher.VerifyAccess();
        return new()
        {
            IsReloading = IsDataReloading,
            LastAttemptAt = _lastDataReloadAt,
            LastResult = _lastDataReloadResult
        };
    }

    internal DataReloadResult ReloadDataCore(PaperOperationContext origin, bool merge = false, string? baselineJson = null)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (IsExiting) return new() { Outcome = "failed", Error = "PaperTodo is exiting." };
        if (IsDataReloading || Mouse.Captured != null || HasDeepCapsuleReorderDragInProgress())
            return new() { Outcome = "busy" };

        _dataReloadEvaluating = true;
        _lastDataReloadAt = DateTimeOffset.UtcNow;
        try
        {
            // Flush edits into MEMORY, never overwrite the candidate file before reading it.
            // Reentrant saves are suppressed while collecting the current UI snapshot.
            CommitSettingsExternalMarkdownEditor(saveImmediately: false);
            foreach (var window in _windows.Values.ToArray()) window.CommitPendingEditsForDataReload();
            PrepareExternalPaperOperation();
            var before = StateStore.CopyForReload(State);
            var version = Interlocked.Increment(ref _saveVersion);
            var commit = merge
                ? _store.PreviewExternalMerge(State, version, baselineJson)
                : _store.ReloadExternalState(version);
            // Native HWND transparency is fixed at startup. The first switch into a native
            // material needs restart, like the normal skin setting path.
            var result = commit.State == null ? commit.Result : commit.Result with
            {
                RestartRequired = commit.Result.RestartRequired ||
                    (PaperSkins.UsesNativeBackdrop(commit.State.PaperSkin) &&
                     NativeMicaBackdrop.IsSupported && !UsesNativeMicaWindows)
            };
            _lastDataReloadResult = result;
            if (commit.State == null)
            {
                return result;
            }

            _saveTimer.Stop();
            _forceSaveTimer.Stop();
            _hasPendingDirty = false;
            _pendingDataReload = new(before, commit.State, result, origin,
                merge, version, commit.Conflicts, commit.ExternalJson, commit.ObservedDiskJson);
            // A plugin can delete its own paper through this call. Return to the caller before
            // tearing down that session/runtime. Send precedes the next input/render/timer turn;
            // shared commands and saves remain gated until the committed model is reconciled.
            Application.Current.Dispatcher.BeginInvoke(new Action(CompletePendingDataReload), DispatcherPriority.Send);
            return result;
        }
        catch (Exception ex)
        {
            var result = new DataReloadResult { Outcome = "failed", Error = ex.GetBaseException().Message };
            _lastDataReloadResult = result;
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
        var storageAccepted = !pending.IsMerge; // ordinary #340 reload is already committed by StateStore.
        _suppressDirty = true;
        _trayRefreshSuppressionDepth++;
        var events = SuppressPaperPluginEventScans();
        try
        {
            if (pending.IsMerge)
            {
                var effectiveNext = ResolveConflictsInteractively(pending);
                var files = _store.CommitExternalMerge(effectiveNext,
                    pending.ObservedDiskJson ?? throw new InvalidOperationException("Missing data merge source."),
                    pending.SaveVersion, pending.Conflicts);
                var result = pending.Result with
                {
                    Outcome = pending.Conflicts is { Count: > 0 } ? "conflict" : "applied",
                    Applied = true, ConflictFile = files.ConflictFile,
                    ConflictDiffFile = files.ConflictDiffFile,
                    RestartRequired = !string.Equals(pending.Before.UiLanguage, effectiveNext.UiLanguage,
                        StringComparison.Ordinal) ||
                        (PaperSkins.UsesNativeBackdrop(effectiveNext.PaperSkin) &&
                         NativeMicaBackdrop.IsSupported && !UsesNativeMicaWindows)
                };
                pending = pending with { Next = effectiveNext, Result = result };
                _lastDataReloadResult = result;
                storageAccepted = true;
            }
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
                    // The explicitly accepted disk model supersedes any pending editor values.
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
            // The external file has been accepted. Reconciliation failed; report this without
            // claiming a rollback or triggering a second import.
            if (storageAccepted)
            {
                StateReloadModels.Apply(State, pending.Next);
                _lastDataReloadResult = pending.Result with
                {
                    Outcome = "failed", Applied = true,
                    Error = "Data was committed, but a display refresh failed: " + ex.GetBaseException().Message
                };
            }
            else
            {
                // The original running objects are still untouched. In particular, never
                // install a stale merged candidate when the external file changed mid-dialog.
                _lastDataReloadResult = pending.Result with
                {
                    Outcome = ex is ExternalDocumentChangedException ? "busy" : "failed",
                    Applied = false, Error = ex.GetBaseException().Message
                };
            }
        }
        finally
        {
            _trayRefreshSuppressionDepth--;
            _suppressDirty = suppressDirty;
            _pendingDataReload = null;
            _applyingDataReload = false;
            events.Dispose();
        }
        if (!storageAccepted) return;
        RefreshTrayMenu();
        PublishExternalPaperOperation(pending.Origin);
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

    /// <summary>Test seam for user choices; null uses the real #349 dialogs.</summary>
    internal static Func<Window?, ConflictBucket, object?>? ConflictDialogDriver;

    private AppState ResolveConflictsInteractively(PendingDataReload pending)
    {
        if (pending.Conflicts is not { Count: > 0 } conflicts) return pending.Next;
        var buckets = ConflictAggregator.Group(conflicts, pending.ExternalJson);
        var paperChoices = new Dictionary<string, PaperConflictChoice>(StringComparer.Ordinal);
        var leafChoices = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
        var useEntireExternalState = false;
        var owner = _windows.Values.FirstOrDefault(w =>
            w.IsVisible && PresentationSource.FromVisual(w) is HwndSource);

        foreach (var bucket in buckets)
        {
            switch (bucket)
            {
                case PaperBucket paper:
                {
                    var choice = ConflictDialogDriver?.Invoke(owner, paper);
                    var result = choice is PaperConflictChoice chosen
                        ? chosen : DataReloadPaperConflictDialog.Show(owner, paper);
                    if (result == PaperConflictChoice.KeepExternal)
                        paperChoices[paper.PaperId] = result;
                    break;
                }
                case UnknownBaselineBucket unknown:
                {
                    var choice = ConflictDialogDriver?.Invoke(owner, unknown);
                    var result = choice is ConflictChoice chosen
                        ? chosen : DataReloadConflictDialog.Show(owner, unknown.Conflict);
                    if (result == ConflictChoice.KeepExternal) useEntireExternalState = true;
                    break;
                }
                case OrderBucket order:
                {
                    var choice = ConflictDialogDriver?.Invoke(owner, order);
                    var result = choice is ConflictChoice chosen
                        ? chosen : DataReloadConflictDialog.Show(owner, order.Conflict);
                    if (result == ConflictChoice.KeepExternal)
                        leafChoices[order.Conflict.Path] = result;
                    break;
                }
                case SingleLeafBucket single:
                {
                    var choice = ConflictDialogDriver?.Invoke(owner, single);
                    var result = choice is ConflictChoice chosen
                        ? chosen : DataReloadConflictDialog.Show(owner, single.Conflict);
                    if (result == ConflictChoice.KeepExternal)
                        leafChoices[single.Conflict.Path] = result;
                    break;
                }
            }
        }

        if (useEntireExternalState && pending.ExternalJson != null)
            return JsonSerializer.Deserialize<AppState>(
                pending.ExternalJson.ToJsonString(StateStore.JsonOptions),
                StateStore.JsonOptions)!;
        var withLeaf = leafChoices.Count == 0
            ? pending.Next
            : JsonPath.ApplyChoices(pending.Next, conflicts, leafChoices);
        return paperChoices.Count == 0 || pending.ExternalJson == null
            ? withLeaf
            : JsonPath.ApplyPaperChoices(withLeaf, paperChoices, pending.ExternalJson, conflicts);
    }

    private bool FinishDataReloadBeforeExit()
    {
        if (!IsDataReloading) return true;
        Application.Current.Dispatcher.BeginInvoke(new Action(Exit), DispatcherPriority.Background);
        return false;
    }
}
