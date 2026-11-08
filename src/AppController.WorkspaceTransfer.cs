using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    internal async Task<bool> MovePaperToWorkspaceAsync(PaperData paper, int number)
    {
        try { return await MovePaperToWorkspaceAttemptAsync(paper, number); }
        catch (WorkspaceRecoveryExitException)
        {
            // The move has finished rollback and released its gate and plugin-event
            // suppression before the existing normal exit path runs.
            Exit();
            return false;
        }
    }

    private async Task<bool> MovePaperToWorkspaceAttemptAsync(PaperData paper, int number)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (!WorkspaceOptions.Enabled || number == WorkspaceNumber ||
            !WorkspaceOptions.ContainsSlot(number) || !State.Papers.Contains(paper) ||
            !CanBeginWorkspaceChange()) return false;

        WorkspaceSession? target = null;
        string? targetBefore = null;
        var targetWritten = false;
        var sourceChanged = false;
        var committed = false;
        var sourceIndex = -1;
        PaperItem[] affectedLinks = [];
        PaperData? replacement = null;
        PaperWindow? detachedBodyWindow = null;
        _workspaceSwitching = true;
        RefreshWorkspaceButtons();
        using var events = SuppressPaperPluginEventScans();
        try
        {
            // Reuse the workspace boundary: no backup round or delayed source save
            // may race this explicit transfer. Only this paper's plugin body commits.
            _coreHistoryTimer?.Stop();
            await _coreHistoryBackupTask;
            if (IsExiting) return false;
            if (_windows.TryGetValue(paper.Id, out var window)) window.CommitPendingEditsForSave();
            if (IsExiting || !State.Papers.Contains(paper)) return false;
            if (!TrySaveNow(sync: true, workspaceBoundary: true, throwOnFailure: true)) return false;

            target = _workspaceCatalog.Open(number, WorkspaceOptions,
                main: WorkspaceNumber == 1 ? State : null);
            // Recovery may have opened a modal dialog, and a plugin Commit may
            // remove its own paper. Recheck the live identity after those callbacks.
            if (IsExiting || !State.Papers.Contains(paper)) return false;
            if (target.State.Papers.Count >= 200)
                throw new InvalidOperationException(Strings.Get("PaperLimitMessage"));
            if (paper.Type == PaperTypes.Note &&
                _paperBodyPlugins.TryGet(paper.BodyProviderId, out var provider) &&
                provider.Manifest is { MaxPaperInstances: > 0 } manifest &&
                target.State.Papers.Count(candidate => candidate.Type == PaperTypes.Note &&
                    candidate.BodyProviderId == paper.BodyProviderId) >= manifest.MaxPaperInstances)
                throw new InvalidOperationException(Strings.Format("PluginInstanceLimitMessage",
                    provider.DisplayName, manifest.MaxPaperInstances));

            if (_windows.TryGetValue(paper.Id, out window) && window.HasLivePluginBodyForWorkspaceMove)
            {
                detachedBodyWindow = window;
                window.DetachPluginBodyForWorkspaceMove();
            }
            // Dispose can also call into the host. Never resurrect a paper it
            // deleted, and only snapshot links after the final plugin callback.
            if (IsExiting || !State.Papers.Contains(paper)) return false;
            sourceIndex = State.Papers.IndexOf(paper);
            affectedLinks = State.Papers.Where(candidate => candidate.Type == PaperTypes.Todo)
                .SelectMany(candidate => candidate.Items)
                .Where(item => item.LinkedPaperId == paper.Id).ToArray();
            target.BindPersistence();
            targetBefore = target.Store.SerializeState(target.State);
            var moved = JsonSerializer.Deserialize<PaperData>(
                JsonSerializer.Serialize(paper, StateStore.JsonOptions), StateStore.JsonOptions)!;
            if (target.State.Papers.Any(candidate => candidate.Id == moved.Id))
                moved.Id = Guid.NewGuid().ToString("N");

            // Paper links and free-queue identities are local to one workspace.
            // File/program links and the paper's own window geometry remain valid.
            foreach (var item in moved.Items.Where(item => item.LinkedPaperId != null))
                item.ClearQuickLaunch();
            moved.CapsuleQueueId = "";
            moved.StartupOwnerPluginId = "";
            moved.StartupInstanceKey = "";

            if (moved.Type == PaperTypes.Note && MarkdownImageReferences.Enumerate(moved.Content).Any())
            {
                using var images = new NoteImageStore(target.Paths.Images);
                images.Load();
                IReadOnlySet<string>? protectedIds = null;
                if (!File.Exists(images.FilePath) &&
                    target.Store.TryCollectProtectedImageIds(target.State, out var ids)) protectedIds = ids;
                images.ProtectPersistedImageReferences(target.State, protectedIds,
                    File.Exists(target.Paths.Data) || File.Exists(target.Paths.Backup));
                moved.Content = _imageStore.CopyImageReferencesTo(images, moved.Id, moved.Content);
            }
            using (var pluginData = new PaperBodyPluginDataStore(_paperBodyPlugins.PluginRoot, number)
                   { WriteLifetime = _store.WriteLifetime })
                _paperBodyPlugins.DataStore.CopyPaperStateTo(pluginData, paper.Id, moved.Id);

            // Resource owners finish first. A failed target write leaves the live
            // source intact; unreferenced resources remain subject to ordinary GC.
            target.State.Papers.Add(moved);
            target.Store.SaveJsonSync(target.Store.SerializeState(target.State), NextWorkspaceSaveVersion());
            targetWritten = true;
            State.Papers.Remove(paper);
            sourceChanged = true;
            foreach (var item in affectedLinks) item.ClearQuickLaunch();
            if (State.Papers.Count == 0)
            {
                replacement = CreatePaper(PaperTypes.Todo, show: false);
                if (replacement == null) throw new InvalidOperationException(Strings.Get("WorkspaceSaveFailed"));
                replacement.IsVisible = true;
            }
            MarkDirty();
            if (!TrySaveNow(sync: true, workspaceBoundary: true, throwOnFailure: true))
                throw new IOException(Strings.Get("WorkspaceSaveFailed"));
            committed = true;

            // Removing the source presentation is post-commit. It cannot turn a
            // saved transfer into a failure or delete data in the target workspace.
            NotifyPaperRemoved(paper.Id);
            RunExternalPostCommitUi(() => FinalizeExternalPaperDeletion(
                paper, replacement, refreshLinkedTodos: true));
            RunExternalPostCommitUi(TryFlushPendingPluginPaperStateDeletes);
            return true;
        }
        catch (Exception ex)
        {
            if (committed)
            {
                Trace.TraceWarning("Committed workspace paper move cleanup failed: {0}", ex);
                return true;
            }
            if (sourceChanged)
            {
                if (replacement != null) RollbackExternalCreatedPaper(replacement);
                State.Papers.Insert(Math.Clamp(sourceIndex, 0, State.Papers.Count), paper);
                foreach (var item in affectedLinks) item.LinkPaper(paper.Id);
                ResetPaperPluginEventBaseline();
            }

            var message = ex is InvalidOperationException or InvalidDataException
                ? ex.Message : WorkspaceFailureMessage.For(ex);
            if (targetWritten && target != null && targetBefore != null)
            {
                try { target.Store.SaveJsonSync(targetBefore, NextWorkspaceSaveVersion()); }
                catch (Exception rollbackError)
                {
                    Trace.TraceWarning("Workspace paper move rollback failed: {0}", rollbackError);
                    message = Strings.Get("WorkspaceMoveRollbackFailed");
                }
            }
            if (!IsExiting && ex is not OperationCanceledException)
            {
                Trace.TraceWarning("Workspace paper move failed: {0}", ex);
                _trayIcon?.ShowBalloonTip(Strings.Get("WorkspaceMoveFailed"), message, BalloonIcon.Warning);
            }
            if (ex is WorkspaceRecoveryExitException) throw;
            return false;
        }
        finally
        {
            // Persistence/rollback is finished. A restored plugin may read the
            // current workspace during Create, which must no longer be gated.
            _workspaceSwitching = false;
            if (!committed && !IsExiting && State.Papers.Contains(paper) &&
                detachedBodyWindow != null && _windows.TryGetValue(paper.Id, out var liveWindow) &&
                ReferenceEquals(liveWindow, detachedBodyWindow))
            {
                try { detachedBodyWindow.RestorePluginBodyAfterFailedWorkspaceMove(); }
                catch (Exception restoreError)
                {
                    Trace.TraceWarning("Workspace paper move body restore failed: {0}", restoreError);
                }
            }
            if (!IsExiting)
            {
                ResumeCoreHistoryBackupTimer();
                RefreshWorkspaceControls();
            }
            ResumeExitAfterDataReload();
        }
    }
}
