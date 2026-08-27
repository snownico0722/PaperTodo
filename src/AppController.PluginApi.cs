using System.Threading;
using System.Windows;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private PaperBodyPluginEventHub? _paperBodyPluginEvents;
    private PaperCommandService? _paperCommands;
    private readonly HashSet<string> _pendingPluginPaperStateDeletes =
        new(StringComparer.Ordinal);

    internal PaperBodyPluginEventHub PaperBodyPluginEvents =>
        _paperBodyPluginEvents ??= new PaperBodyPluginEventHub(
            this,
            Application.Current.Dispatcher);

    internal PaperCommandService PaperCommands =>
        _paperCommands ??= new PaperCommandService(this);

    // Plugin event subscriptions reuse persistence's monotonic stamps. Most mutations increment
    // stateRevision through MarkDirty; a few intentional immediate-save paths only advance
    // saveVersion. Watching both lets the event hub wake after real mutations without a recurring
    // full-workspace poll.
    internal long PluginEventStateRevision => Interlocked.Read(ref _stateRevision);
    internal long PluginEventSaveVersion => Interlocked.Read(ref _saveVersion);

    internal void NotifyPluginEventMutationStampChanged() =>
        _paperBodyPluginEvents?.NotifyMutationStampChanged();

    internal PaperSnapshot CapturePaperSnapshot(PaperData paper) =>
        new(
            paper.Id,
            paper.Type,
            PaperTitleText(paper),
            paper.IsVisible,
            paper.IsCollapsed,
            paper.AlwaysOnTop,
            paper.BodyProviderId);

    internal TodoSnapshot CaptureTodoSnapshot(PaperData paper, PaperItem item) =>
        new(
            paper.Id,
            PaperTitleText(paper),
            item.Id,
            item.Text,
            item.Done,
            item.Order,
            item.LinkedPaperId,
            item.LinkedPath,
            item.ReminderAt);

    internal NoteSnapshot CaptureNoteSnapshot(PaperData paper)
    {
        var contentAvailable = paper.Type == PaperTypes.Note &&
            string.Equals(
                paper.BodyProviderId,
                PaperBodyProviderIds.Markdown,
                StringComparison.Ordinal);
        return new NoteSnapshot(
            paper.Id,
            PaperTitleText(paper),
            paper.BodyProviderId,
            contentAvailable,
            contentAvailable ? paper.Content ?? "" : "");
    }

    internal void PrepareExternalPaperOperation()
    {
        // Markdown edits live in the editor until CommitPendingNoteContentsForSave() copies them
        // into PaperData. A prior mutation-stamp scan may already have observed the revision before
        // that copy happened, so the external-operation boundary must diff unconditionally here.
        // Otherwise the newly committed user edit can be misattributed to the following MCP/plugin
        // operation.
        CommitPendingNoteContentsForSave();
        _paperBodyPluginEvents?.ScanNow(PaperOperationContext.User());
    }

    internal IDisposable SuppressPaperPluginEventScans() =>
        _paperBodyPluginEvents?.SuppressScans() ?? EmptyDisposable.Instance;

    internal void PublishExternalPaperOperation(PaperOperationContext context) =>
        _paperBodyPluginEvents?.ScanNow(context);

    internal void ResetPaperPluginEventBaseline() =>
        _paperBodyPluginEvents?.ResetBaseline();

    internal bool TryCommitExternalMutation()
    {
        MarkDirty();
        return TrySaveNow(sync: true);
    }

    internal void RunExternalPostCommitUi(Action update) =>
        RunMcpPostCommitUi(update);

    internal void RollbackExternalCreatedPaper(PaperData paper) =>
        RollbackMcpCreatedPaper(paper);

    internal void FinalizeExternalPaperCreated(PaperData paper, bool show) =>
        FinalizeMcpPaperCreated(paper, show);

    internal void RefreshExternalTodoPaper(PaperData paper) =>
        RefreshMcpTodoPaper(paper);

    internal void RefreshExternalNotePaper(PaperData paper) =>
        RefreshMcpNotePaper(paper);

    internal void FinalizeExternalPaperDeletion(
        PaperData deleted,
        PaperData? replacement,
        bool refreshLinkedTodos) =>
        FinalizeMcpPaperDeletion(deleted, replacement, refreshLinkedTodos);

    internal void RefreshAfterExternalRollback() =>
        RefreshMcpAfterRollback();

    internal void UpdatePaperTitleFromPlugin(
        PaperData paper,
        string title,
        string providerId)
    {
        PrepareExternalPaperOperation();
        using (SuppressPaperPluginEventScans())
        {
            UpdatePaperTitle(paper, title);
        }
        PublishExternalPaperOperation(PaperOperationContext.Plugin(providerId));
    }

    internal void QueuePluginPaperStateDeletion(string paperId)
    {
        if (!string.IsNullOrWhiteSpace(paperId))
        {
            _pendingPluginPaperStateDeletes.Add(paperId);
        }
    }

    internal void TryFlushPendingPluginPaperStateDeletes()
    {
        foreach (var paperId in _pendingPluginPaperStateDeletes.ToArray())
        {
            if (State.Papers.Any(paper =>
                    string.Equals(paper.Id, paperId, StringComparison.Ordinal)))
            {
                _pendingPluginPaperStateDeletes.Remove(paperId);
                continue;
            }

            // Runtime Backoff has no live lease to reconcile. Remove any retained rich
            // presentation for the now-deleted Paper here as part of the independent post-commit
            // plugin cleanup, so a provider with zero Papers cannot keep stale volatile snapshots.
            foreach (var providerId in _pluginRuntimePresentationCache.Keys.ToArray())
            {
                RemovePluginRuntimePresentationCache(providerId, paperId);
            }

            try
            {
                _paperBodyPlugins.DataStore.RemovePaperStateEverywhere(paperId);
                _pendingPluginPaperStateDeletes.Remove(paperId);
            }
            catch
            {
                // Main data is already authoritative. Retry this independent cleanup after a
                // later successful save without converting it into a core save failure.
            }
        }

        // Deletion is committed before this cleanup pass. Reconcile from the final entity-paper
        // set so the provider Runtime loses deleted logical Paper instances promptly.
        ReconcilePluginRuntimes();
    }

    internal void DisposePaperPluginHostRuntime()
    {
        DisposePluginRuntimes();
        _paperBodyPluginEvents?.Dispose();
        _paperBodyPluginEvents = null;
        _paperCommands = null;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
