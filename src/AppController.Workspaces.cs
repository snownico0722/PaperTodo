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

    internal long WorkspaceGeneration => Interlocked.Read(ref _workspaceGeneration);
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
        UiLanguages.ApplyWorkspacePreference(State.UiLanguage);
    }

    private void InitializeWorkspacePluginData()
    {
        if (WorkspaceNumber == 1) return;
        _paperBodyPlugins.ExchangeWorkspaceDataStore(
            new PaperBodyPluginDataStore(_paperBodyPlugins.PluginRoot, WorkspaceNumber)).Dispose();
    }

    private void NotifyWorkspaceStartup()
    {
        if (_workspaceCatalog.StartupError is not { } error) return;
        ReportWorkspaceError(Strings.Format("WorkspaceStartupFallback", error));
        // Save the fallback slot through the ordinary main StateStore, never over the bad secondary.
        SaveNow(sync: true);
    }

    internal Task<bool> CycleWorkspaceAsync()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!WorkspaceOptions.Enabled || _workspaceSwitching) return Task.FromResult(false);
        var next = WorkspaceNumber == WorkspaceOptions.Count ? 1 : WorkspaceNumber + 1;
        return SwitchWorkspaceAsync(next);
    }

    internal Task<bool> SwitchWorkspaceAsync(int number) =>
        SwitchWorkspaceCoreAsync(number, WorkspaceOptions.Normalized(), forceReload: false);

    internal async Task<bool> ChangeWorkspaceConfigurationAsync(WorkspaceConfiguration requested)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!CanBeginWorkspaceChange()) return false;
        var next = requested.Normalized();
        next.Active = !next.Enabled || WorkspaceNumber > next.Count ? 1 : WorkspaceNumber;
        var followsChanged = WorkspaceNumber > 1 &&
            next.FollowsMain(WorkspaceNumber) != _workspace.FollowsMain;
        if (next.Active != WorkspaceNumber || followsChanged)
            return await SwitchWorkspaceCoreAsync(next.Active, next, forceReload: true);

        var previous = WorkspaceOptions.Normalized();
        var previouslyConfigured = _workspaceCatalog.HasConfiguration;
        _workspaceSwitching = true;
        RefreshWorkspaceButtons();
        try
        {
            CommitWorkspaceEditors();
            if (!TrySaveNow(sync: true, workspaceBoundary: true)) return false;
            // Persist with the main store before publishing a changed count/enablement.
            _workspaceCatalog.MainStore.UpdateWorkspaceConfiguration(next, NextWorkspaceSaveVersion());
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

    private async Task<bool> SwitchWorkspaceCoreAsync(int number, WorkspaceConfiguration requested,
        bool forceReload)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var nextOptions = requested.Normalized();
        if (number < 1 || number > (nextOptions.Enabled ? nextOptions.Count : 1)) return false;
        if (!CanBeginWorkspaceChange()) return false;
        if (number == WorkspaceNumber && !forceReload) return true;
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
            candidate = _workspaceCatalog.Open(number, nextOptions, allowCreate: true);
            if (number == previous.Paths.Number)
            {
                // Toggling inheritance reloads software settings, not a second LMDB handle.
                candidateImages = previousImages;
                candidatePluginData = previousPluginData;
            }
            else
            {
                candidateImages = new NoteImageStore(candidate.Paths.Images);
                candidateImages.Load();
                if (candidateImages.IsWriteDisabled)
                    throw new InvalidDataException(Strings.Format("WorkspaceImagesUnavailable", candidate.Paths.Images));
                if (!File.Exists(candidate.Paths.Images) && candidate.State.Papers.Any(p =>
                    p.Type == PaperTypes.Note && MarkdownImageReferences.CollectImageIds(p.Content).Any()))
                    throw new FileNotFoundException(Strings.Get("WorkspaceImagesMissing"), candidate.Paths.Images);
                candidatePluginData = new PaperBodyPluginDataStore(_paperBodyPlugins.PluginRoot, number);
            }

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
            await RestoreWorkspacePresentationAsync(previous.State);
            if (IsExiting) return false;

            // Save normalized target geometry/new blank papers first. Only then commit the active
            // slot in main data.json. A failed metadata write still permits restoring the old UI.
            if (!TrySaveNow(sync: true, workspaceBoundary: true))
                throw new IOException(Strings.Get("WorkspaceSaveFailed"));
            if (number != 1) _workspaceCatalog.SaveOptions(NextWorkspaceSaveVersion());
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

    private async Task RestoreWorkspacePresentationAsync(AppState before)
    {
        RefreshReloadedSettings(before);
        // Every workspace handoff stopped the old processes, even when both slots use
        // identical preferences. Recreate the target's optional warm process once.
        PaperWindow.EnsurePersistentScriptProcessForSettings(State);
        EnablePluginRuntimeReconciliation();
        EnsurePapersOnScreen();
        await RestorePaperSurfacesAsync(State.Papers.Where(p => p.IsVisible).ToArray());
        RefreshTodoReminderSchedule();
        ArrangeDeepCapsules(animate: false);
        RebuildTrayMenu();
        // GC is not part of a reversible handoff. Each store's normal startup protection scan
        // is already scoped to its own primary, backup, recovery and conflict files.
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
