using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private WorkspaceCatalog _workspaceCatalog = null!;
    private WorkspaceSession _workspace = null!;
    private bool _workspaceSwitching;
    private bool _workspaceClosingSurfaces;
    private long _workspaceGeneration;
    private Guid _workspaceBindingId = Guid.NewGuid();

    internal long WorkspaceGeneration => Interlocked.Read(ref _workspaceGeneration);
    internal string WorkspaceBindingToken => _workspaceBindingId.ToString("N");
    internal int WorkspaceNumber => _workspace.Paths.Number;
    internal WorkspaceConfiguration WorkspaceOptions => _workspaceCatalog.Options;
    internal bool FollowsMainWorkspaceSettings => _workspace.FollowsMain;
    internal bool IsWorkspaceSwitching => _workspaceSwitching;
    internal string? WorkspaceSettingsUnavailable => _workspaceSwitching
        ? Strings.Get("WorkspaceSwitching")
        : FollowsMainWorkspaceSettings ? Strings.Get("WorkspaceSettingsReadOnly") : null;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(State), nameof(_store), nameof(_imageStore))]
    private void InitializeWorkspaceState()
    {
        _workspaceCatalog = new WorkspaceCatalog(AppContext.BaseDirectory, DurableAtomicFileWriter.Shared);
        _workspace = _workspaceCatalog.OpenInitial();
        _workspace.BindPersistence();
        _store = _workspace.Store;
        State = _workspace.State;
        _imageStore = new NoteImageStore(_workspace.Paths.Images);
    }

    private void InitializeWorkspacePluginData()
    {
        if (WorkspaceNumber == 1) return;
        _paperBodyPlugins.ExchangeWorkspaceDataStore(
            new PaperBodyPluginDataStore(_paperBodyPlugins.PluginRoot, WorkspaceNumber)
            { WriteLifetime = _store.WriteLifetime }).Dispose();
    }

    private void NotifyWorkspaceStartup()
    {
        if (_workspaceCatalog.StartupError is not { } error) return;
        // The selected data file may only be busy. Show the existing notice but do
        // not save a new active slot or mutate either workspace on this fallback.
        ReportWorkspaceError(Strings.Format("WorkspaceStartupFallback", error));
    }

    internal Task<bool> CycleWorkspaceAsync()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!WorkspaceOptions.Enabled || _workspaceSwitching) return Task.FromResult(false);
        var next = WorkspaceOptions.NextSlot(WorkspaceNumber);
        return SwitchWorkspaceAsync(next);
    }

    internal Task<bool> SwitchWorkspaceAsync(int number) =>
        SwitchWorkspaceCoreAsync(number, WorkspaceOptions.Normalized());

    internal async Task<bool> ChangeWorkspaceConfigurationAsync(WorkspaceConfiguration requested)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!CanBeginWorkspaceChange()) return false;
        var next = requested.Normalized();
        next.Active = !next.Enabled || !next.ContainsSlot(WorkspaceNumber) ? 1 : WorkspaceNumber;
        var followsChanged = WorkspaceNumber > 1 &&
            next.FollowsMain(WorkspaceNumber) != _workspace.FollowsMain;
        if (next.Active != WorkspaceNumber)
            return await SwitchWorkspaceCoreAsync(next.Active, next);
        if (followsChanged)
            return ApplyCurrentWorkspaceSettings(next);

        var previous = WorkspaceOptions.Normalized();
        var previouslyConfigured = _workspaceCatalog.HasConfiguration;
        _workspaceSwitching = true;
        RefreshWorkspaceButtons();
        try
        {
            if (WorkspaceNumber == 1)
            {
                CommitWorkspaceEditors();
                // This is the live main AppState: persist its updated catalog and papers in ONE
                // guarded ordinary save. Reading and accepting the main file a second time
                // could allow an intervening external edit to be overwritten by live memory.
                State.Workspaces = next;
                if (!TrySaveNow(sync: true, workspaceBoundary: true))
                {
                    State.Workspaces = previouslyConfigured ? previous : null;
                    return false;
                }
            }
            else
            {
                // Updating the main catalog must not flush an unrelated secondary document.
                _workspaceCatalog.MainStore.UpdateWorkspaceConfiguration(next, NextWorkspaceSaveVersion());
            }
            _workspaceCatalog.AdoptOptions(next);
            if (WorkspaceNumber == 1) State.Workspaces = WorkspaceOptions;
            Interlocked.Increment(ref _stateRevision);
            return true;
        }
        catch (Exception ex)
        {
            _workspaceCatalog.AdoptOptions(previous, previouslyConfigured);
            if (WorkspaceNumber == 1) State.Workspaces = previouslyConfigured ? WorkspaceOptions : null;
            ReportWorkspaceError(ex.GetBaseException().Message);
            return false;
        }
        finally
        {
            _workspaceSwitching = false;
            RefreshWorkspaceControls();
            ResumeExitAfterDataReload();
        }
    }

    private bool ApplyCurrentWorkspaceSettings(WorkspaceConfiguration options)
    {
        // A follow toggle changes settings, not the workspace's windows or data owner.
        _workspaceSwitching = true;
        RefreshWorkspaceButtons();
        var catalogCommitted = false;
        try
        {
            var (session, effectiveSettings) = _workspaceCatalog.PreviewSettingsChange(_workspace, options);
            var previousPersistentPowerShell = State.UsePersistentPowerShellProcess;
            var previousPreferPowerShell7 = State.PreferPowerShell7;

            // Only the main catalog is persisted here. A failed write must not
            // change the current settings or the ordinary-save projection.
            _workspaceCatalog.MainStore.UpdateWorkspaceConfiguration(options, NextWorkspaceSaveVersion());
            catalogCommitted = true;
            _workspaceCatalog.AdoptOptions(options);
            effectiveSettings.ApplyTo(State);
            _workspace = session;
            session.BindPersistence();
            Interlocked.Increment(ref _stateRevision);
            NotifyPluginEventMutationStampChanged();

            try
            {
                UiLanguages.ApplyWorkspacePreference(State.UiLanguage);
                State.PaperSkin = PaperSkins.Resolve(State);
                NormalizePaperSystemVisibilitySettings();
                _paperBodyPlugins.RefreshWorkspaceLocalization();
                RefreshReloadedSettings(previousPersistentPowerShell, previousPreferPowerShell7);
                ReconcilePluginRuntimes();
            }
            catch (Exception ex)
            {
                // An already committed catalog cannot be rolled back by a UI refresh.
                Trace.TraceWarning("Workspace settings refresh failed: {0}", ex);
                ReportWorkspaceError(ex.GetBaseException().Message);
            }
        }
        catch (Exception ex)
        {
            ReportWorkspaceError(ex.GetBaseException().Message);
        }
        finally
        {
            _workspaceSwitching = false;
            RefreshWorkspaceControls();
            ResumeExitAfterDataReload();
        }

        if (catalogCommitted) OfferWorkspaceSettingsRestart();
        return catalogCommitted;
    }

    private void OfferWorkspaceSettingsRestart()
    {
        // This operation originates from the Workspaces settings page. Direct
        // internal calls (including tests) have no visible dialog owner.
        var owner = _settingsWindow;
        if (owner == null || !owner.IsVisible) return;
        if (!PaperNoticeDialog.ShowChoice(
                owner,
                SettingsSidebarLocalized(
                    "重启 PaperTodo", "Restart PaperTodo", "PaperTodo を再起動", "PaperTodo 다시 시작"),
                SettingsSidebarLocalized(
                    "工作区设置已切换。如果不重启，部分视觉效果可能并非最佳。是否立即重启？",
                    "Workspace settings have changed. Without restarting, some visual effects may not appear at their best. Restart now?",
                    "ワークスペース設定を切り替えました。再起動しない場合、一部の視覚効果が最適に表示されない可能性があります。今すぐ再起動しますか？",
                    "작업 공간 설정이 변경되었습니다. 다시 시작하지 않으면 일부 시각 효과가 최적으로 표시되지 않을 수 있습니다. 지금 다시 시작할까요?"),
                SettingsSidebarLocalized("稍后", "Later", "後で", "나중에"),
                SettingsSidebarLocalized("立即重启", "Restart now", "今すぐ再起動", "지금 다시 시작")))
            return;

        if (Application.Current is App app) app.RequestRestartAfterExit();
        Exit();
    }

    private bool CanBeginWorkspaceChange()
    {
        if (IsExiting || IsDataReloading || _publicSettings?.IsChanging == true || Mouse.Captured != null ||
            HasDeepCapsuleReorderDragInProgress()) return false;
        return true;
    }

    private long NextWorkspaceSaveVersion() => Interlocked.Increment(ref _saveVersion);

    private void CommitWorkspaceEditors()
    {
        CommitSettingsExternalMarkdownEditor(saveImmediately: false);
        // A workspace boundary ends these sessions, unlike an ordinary background core save.
        foreach (var window in _windows.Values.ToArray()) window.CommitPendingEditsForSave();
    }

    private async Task<bool> SwitchWorkspaceCoreAsync(int number, WorkspaceConfiguration requested)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var nextOptions = requested.Normalized();
        if (!nextOptions.Enabled || !nextOptions.ContainsSlot(number)) return false;
        if (!CanBeginWorkspaceChange()) return false;
        if (number == WorkspaceNumber) return true;
        nextOptions.Active = number;

        var previous = _workspace;
        var previousOptions = WorkspaceOptions.Normalized();
        var previouslyConfigured = _workspaceCatalog.HasConfiguration;
        var previousImages = _imageStore;
        var previousPluginData = _paperBodyPlugins.DataStore;
        var previousSuppressDirty = _suppressDirty;
        var reopenSettings = _settingsWindow?.IsVisible == true;
        var settingsPage = _settingsPage;
        WorkspaceSession? candidate = null;
        NoteImageStore? candidateImages = null;
        PaperBodyPluginDataStore? candidatePluginData = null;
        var presentationStopped = false;
        var installed = false;
        var committed = false;
        _workspaceSwitching = true;
        RefreshWorkspaceButtons();
        try
        {
            // Complete/fence the source writer first. Candidate validation never overwrites it.
            CommitWorkspaceEditors();
            if (!TrySaveNow(sync: true, workspaceBoundary: true)) return false;
            candidate = _workspaceCatalog.Open(number, nextOptions,
                main: previous.Paths.Number == 1 ? previous.State : null);
            var needsInitialSave = candidate.Store.NeedsStartupRecoverySave;
            var originalHideFromTaskbar = candidate.State.HidePapersFromTaskbar;
            candidateImages = new NoteImageStore(candidate.Paths.Images);
            candidateImages.Load();
            // Missing images must not prevent entry or recreate an unsafe image library.
            IReadOnlySet<string>? protectedImageIds = null;
            if (!File.Exists(candidateImages.FilePath) &&
                candidate.Store.TryCollectProtectedImageIds(candidate.State, out var ids))
                protectedImageIds = ids;
            candidateImages.ProtectPersistedImageReferences(candidate.State, protectedImageIds,
                File.Exists(candidate.Paths.Data) || File.Exists(candidate.Paths.Backup));
            candidatePluginData = new PaperBodyPluginDataStore(_paperBodyPlugins.PluginRoot, number)
                { WriteLifetime = _store.WriteLifetime };

            _suppressDirty = true;
            _workspaceClosingSurfaces = true;
            StopStateBackupPolicy();
            var scriptShutdown = PaperWindow.StopAllScriptProcessesAsync();
            presentationStopped = true;
            StopWorkspacePresentation();
            previousPluginData.DetachForWorkspaceSwitch();
            await scriptShutdown;
            if (IsExiting) return false;

            _workspaceCatalog.AdoptOptions(nextOptions);
            if (number == 1) candidate.State.Workspaces = WorkspaceOptions;
            InstallWorkspace(candidate, candidateImages, candidatePluginData);
            installed = true;
            _workspaceClosingSurfaces = false;
            var relocated = await RestoreWorkspacePresentationAsync(previous.State);
            if (IsExiting) return false;

            // A healthy existing target needs no content write. Save only when
            // initializing/recovering the document or rescuing off-screen geometry.
            var targetChanged = needsInitialSave || candidate.Store.NormalizedCollapsedStateOnLoad || relocated ||
                candidate.State.HidePapersFromTaskbar != originalHideFromTaskbar;
            if (targetChanged && !TrySaveNow(sync: true, workspaceBoundary: true))
                throw new IOException(Strings.Get("WorkspaceSaveFailed"));
            // The active slot always belongs to the main catalog. Returning to a
            // healthy main workspace updates that node alone, without resaving papers.
            if (number != 1 || !targetChanged)
                _workspaceCatalog.SaveOptions(NextWorkspaceSaveVersion());
            committed = true;
            _lastDataReloadAt = null;
            _lastDataReloadResult = null;
            if (!ReferenceEquals(previousImages, candidateImages)) previousImages.Dispose();
            if (!ReferenceEquals(previousPluginData, candidatePluginData)) previousPluginData.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            if (!committed && !IsExiting)
            {
                try
                {
                    var rollbackFrom = State;
                    if (installed)
                    {
                        _workspaceClosingSurfaces = true;
                        var targetScriptShutdown = PaperWindow.StopAllScriptProcessesAsync();
                        StopWorkspacePresentation();
                        await targetScriptShutdown;
                    }
                    previous.BindPersistence();
                    _workspaceCatalog.AdoptOptions(previousOptions, previouslyConfigured);
                    if (previous.Paths.Number == 1)
                        previous.State.Workspaces = previouslyConfigured ? WorkspaceOptions : null;
                    if (presentationStopped)
                    {
                        InstallWorkspace(previous, previousImages, previousPluginData);
                        _workspaceClosingSurfaces = false;
                        await RestoreWorkspacePresentationAsync(rollbackFrom);
                    }
                }
                catch (Exception restoreError)
                {
                    Trace.TraceError("Workspace UI restoration failed: {0}", restoreError);
                }
            }
            ReportWorkspaceError(ex.GetBaseException().Message);
            return committed;
        }
        finally
        {
            // Candidate validation/failure must not leak LMDB handles or background store timers.
            if (!committed)
            {
                if (candidateImages != null && !ReferenceEquals(candidateImages, _imageStore))
                    candidateImages.Dispose();
                if (candidatePluginData != null && !ReferenceEquals(candidatePluginData, _paperBodyPlugins.DataStore))
                    candidatePluginData.Dispose();
            }
            _workspaceClosingSurfaces = false;
            _suppressDirty = previousSuppressDirty;
            _workspaceSwitching = false;
            if (!IsExiting)
            {
                // No old-workspace request can re-enable a transport or mutate the new store
                // during the handoff. The active workspace now owns these services.
                RefreshMcpRuntime();
                StartStateBackupPolicy();
                RefreshWorkspaceControls();
                if (reopenSettings && _settingsWindow == null) ShowSettingsWindow(settingsPage);
            }
            ResumeExitAfterDataReload();
        }
    }

    private void StopWorkspacePresentation()
    {
        _paperSurfaceRestoreGeneration++;
        _startupShellPrewarmGeneration++;
        _pluginStartupPaperGeneration++;
        Interlocked.Increment(ref _workspaceGeneration);
        _workspaceBindingId = Guid.NewGuid();
        // Complete an already committed response, but reject delayed old-workspace requests.
        // Each new workspace creates a command adapter bound to its own generation.
        _mcpApiHost?.StopAfterResponse();
        _mcpApiHost = null;
        _mcpCommands = null;
        CancelMasterCapsuleQueueTransfers();
        StopTodoReminderTimer();
        StopFullscreenAvoidanceRuntime(restoreTopmost: false);
        _displayMetricsRefreshTimer.Stop();
        DisposeGlobalHotkeys();
        DisposePluginShortcuts();
        DisposeExperimentalWindowRuntime();
        foreach (var timer in _pluginSettingPropagationTimers.Values) timer.Stop();
        _pluginSettingPropagationTimers.Clear();
        _pluginRuntimeReconciliationEnabled = false;
        MarkdownEdgePreviewPreload.For(Application.Current.Dispatcher).Clear();
        ClearPaperLinkDropTarget();
        _deepCapsuleContextMenuOwners.Clear();
        DisposeEdgeCapsuleQueueCompositionProxies();
        _settingsWindow?.Close();
        _settingsWindow = null;
        foreach (var pair in _windows.ToArray())
        {
            pair.Value.CloseForReal();
            _windows.Remove(pair.Key);
        }
        foreach (var master in _masterCapsules.Values.ToArray()) master.CloseForReal();
        _masterCapsules.Clear();
        _visibilityAnimationVersions.Clear();
        // Runtime disposal happens against the OLD model. Unloading is not PaperRemoved,
        // and must never call QueuePluginPaperStateDeletion/RemovePaperStateEverywhere.
        DisposePaperPluginHostRuntime();
        foreach (var paper in State.Papers)
        {
            _pluginPaperActions?.RemovePaper(paper.Id);
            RemovePluginTodoActionsForPaper(paper.Id);
            RemovePluginTopBarLabelsForPaper(paper.Id);
        }
        _pluginRuntimePresentationCache.Clear();
        _pendingPluginPaperStateDeletes.Clear();
        _publicSettings = null;
    }

    private void InstallWorkspace(WorkspaceSession session, NoteImageStore images,
        PaperBodyPluginDataStore pluginData)
    {
        _workspace = session;
        _store = session.Store;
        State = session.State;
        session.BindPersistence();
        _imageStore = images;
        _paperBodyPlugins.ExchangeWorkspaceDataStore(pluginData);
        pluginData.ResumeAfterWorkspaceSwitch();
        _pluginRuntimeDisposing = false;
        _pluginRuntimeReconciliationEnabled = false;
        _hasPendingDirty = false;
        _hasShownSaveFailure = false;
        UiLanguages.ApplyWorkspacePreference(State.UiLanguage);
        State.PaperSkin = PaperSkins.Resolve(State);
        // All ordinary HWNDs have been closed, so changing window kind here is legitimate.
        UsesNativeMicaWindows = PaperSkins.UsesNativeBackdrop(State.PaperSkin) && NativeMicaBackdrop.IsSupported;
        _paperBodyPlugins.RefreshWorkspaceLocalization();
        Theme.Invalidate();
        RefreshApplicationThemeResources();
        NormalizePaperSystemVisibilitySettings();
        _imageStore.AutoCompressLargeImages = State.AutoCompressLargeImages;
        Interlocked.Increment(ref _stateRevision);
    }

    private async Task<bool> RestoreWorkspacePresentationAsync(AppState before)
    {
        // ShowPaper may expand a persisted capsule that the effective settings no
        // longer allow. MarkDirty is suppressed while rebuilding the presentation,
        // so record this narrow, real state change explicitly.
        var originalCollapse = State.Papers.Select(paper => (Paper: paper, paper.IsCollapsed)).ToArray();
        RefreshReloadedSettings(before);
        // Every workspace handoff stopped the old processes, even when both slots use
        // identical preferences. Recreate the target's optional warm process once.
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        EnablePluginRuntimeReconciliation();
        var relocated = EnsurePapersOnScreen();
        await RestorePaperSurfacesAsync(State.Papers.Where(p => p.IsVisible).ToArray());
        RefreshTodoReminderSchedule();
        ArrangeDeepCapsules(animate: false);
        RebuildTrayMenu();
        // GC is not part of a reversible handoff. Each store's normal startup protection scan
        // is already scoped to its own primary, backup, recovery and conflict files.
        return relocated || originalCollapse.Any(entry => entry.Paper.IsCollapsed != entry.IsCollapsed);
    }

    private AppState PrepareAcceptedWorkspaceReload(AppState candidate) =>
        _workspaceCatalog.AcceptReload(_workspace, candidate);

    private void RefreshWorkspaceButtons()
    {
        if (_workspaceSettingsPanel != null) _workspaceSettingsPanel.IsEnabled = !_workspaceSwitching;
        foreach (var window in _windows.Values.ToArray()) window.RefreshWorkspaceButton();
    }

    private void RefreshWorkspaceControls()
    {
        RefreshWorkspaceButtons();
        RebuildTrayMenu();
        RefreshSettingsWindowContent();
    }

    private void ReportWorkspaceError(string message)
    {
        Trace.TraceWarning("Workspace: {0}", message);
        _trayIcon?.ShowBalloonTip(Strings.Get("WorkspaceSwitchFailed"), message, BalloonIcon.Warning);
    }
}
