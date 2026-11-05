using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private readonly List<WorkbenchTransferTransaction> _workbenchContentTransfers = [];
    internal Guid LastWorkbenchContentTransferId { get; private set; }
    internal event Action<Guid, bool>? WorkbenchContentTransferReplayed;
    internal event Action? WorkbenchContentTransferBlocked;

    internal bool CanTransferWorkbenchContent(WorkbenchContentTransferPayload payload, string? targetPaperId)
    {
        if (IsExiting || IsDataReloading || payload.WorkspaceGeneration != WorkspaceGeneration ||
            payload.WorkspaceBindingToken != WorkspaceBindingToken) return false;
        var source = FindPaper(payload.SourcePaperId);
        if (source == null || targetPaperId == source.Id || (targetPaperId == null && State.Papers.Count >= 200)) return false;
        var target = targetPaperId == null ? new PaperData { Type = source.Type } : FindPaper(targetPaperId);
        return target != null && WorkbenchContentTransfer.TryPlan(source, target, payload,
            CurrentMarkdownContentForExternalRead(source), CurrentMarkdownContentForExternalRead(target), out _, out _);
    }

    internal bool TransferWorkbenchContent(WorkbenchContentTransferPayload payload, string? targetPaperId, WorkbenchPoint? at = null)
    {
        if (!CanTransferWorkbenchContent(payload, targetPaperId)) return false;
        var source = FindPaper(payload.SourcePaperId)!;
        var sourceWindow = GetOrCreatePaperWindow(source);
        sourceWindow.PrepareWorkbenchContentTransfer();
        PrepareExternalPaperOperation(source);
        var layoutBefore = WorkbenchLayout.Copy();
        using var suppress = SuppressPaperPluginEventScans();
        var created = targetPaperId == null;
        var target = created ? CreatePaper(source.Type, show: false, sourcePaper: source) : FindPaper(targetPaperId!);
        if (target == null) return false;
        var targetWindow = GetOrCreatePaperWindow(target);
        targetWindow.PrepareWorkbenchContentTransfer();
        PrepareExternalPaperOperation(target);
        if (!WorkbenchContentTransfer.TryPlan(source, target, payload,
                CurrentMarkdownContentForExternalRead(source), CurrentMarkdownContentForExternalRead(target),
                out var sourceAfter, out var targetAfter))
        {
            if (created) RollbackExternalCreatedPaper(target);
            return false;
        }
        var sourceBefore = new WorkbenchContentSnapshot(source);
        var targetBefore = new WorkbenchContentSnapshot(target);
        var sourceItemModels = source.Items;
        var targetItemModels = target.Items;
        if (source.Type == PaperTypes.Note)
        {
            try
            {
                // Image assets have a note owner. Reuse the normal paste/import ownership
                // conversion, retaining the original ids in the source's undo snapshot.
                var targetContent = ImageStore.CloneForeignImageReferencesForNote(target.Id, targetAfter.Content);
                if (targetContent.Length > PaperWindow.NoteTextMaxLength)
                {
                    if (created) RollbackExternalCreatedPaper(target);
                    return false;
                }
                targetAfter = new WorkbenchContentSnapshot(targetContent, targetAfter.Items);
            }
            catch
            {
                // No paper has been changed yet; an unavailable image store cannot turn a
                // failed move into a deletion. Unreferenced imported blobs remain GC-safe.
                if (created) RollbackExternalCreatedPaper(target);
                return false;
            }
        }
        WorkbenchCardState? createdCard = null;
        if (created)
        {
            var layout = WorkbenchLayout.Copy();
            var point = at ?? (layout.Cards.TryGetValue(source.Id, out var sourceCard)
                ? new WorkbenchPoint(sourceCard.X + sourceCard.Width + 32, sourceCard.Y) : new WorkbenchPoint(80, 80));
            createdCard = new WorkbenchCardState
            {
                X = point.X, Y = point.Y, Width = source.Width, Height = source.Height, Preferred = true
            };
            layout.Cards[target.Id] = createdCard.Copy();
            StoreWorkbenchLayout(layout);
        }
        sourceAfter.Apply(source);
        targetAfter.Apply(target);
        if (!TryCommitExternalMutation())
        {
            sourceBefore.Apply(source);
            targetBefore.Apply(target);
            source.Items = sourceItemModels;
            target.Items = targetItemModels;
            if (created) RollbackExternalCreatedPaper(target);
            StoreWorkbenchLayout(layoutBefore);
            ResetPaperPluginEventBaseline();
            return false;
        }

        var transaction = new WorkbenchTransferTransaction
        {
            Generation = WorkspaceGeneration, Binding = WorkspaceBindingToken,
            Source = source, Target = target, SourceWindow = sourceWindow, TargetWindow = targetWindow,
            SourceBefore = sourceBefore, TargetBefore = targetBefore, SourceAfter = sourceAfter, TargetAfter = targetAfter,
            CreatedTarget = created, CreatedCard = createdCard
        };
        // Row handlers close over live PaperItem objects. Reconcile from the original models
        // after persistence, so unchanged rows retain their identity and changed rows rebuild.
        source.Items = sourceItemModels;
        target.Items = targetItemModels;
        transaction.SourceBoundary = sourceWindow.ApplyWorkbenchContentTransfer(transaction.Id, sourceBefore, sourceAfter);
        transaction.TargetBoundary = targetWindow.ApplyWorkbenchContentTransfer(transaction.Id, targetBefore, targetAfter);
        _workbenchContentTransfers.RemoveAll(entry => entry.Generation != WorkspaceGeneration || entry.Binding != WorkspaceBindingToken);
        _workbenchContentTransfers.Add(transaction);
        LastWorkbenchContentTransferId = transaction.Id;
        if (created) FinalizeExternalPaperCreated(target, show: false);
        suppress.Dispose();
        CompleteWorkbenchTransferUi(source, target);
        return true;
    }

    internal bool TryUndoWorkbenchContentTransfer(Guid? id = null) => ReplayWorkbenchContentTransfer(id, redo: false);
    internal bool TryRedoWorkbenchContentTransfer(Guid? id = null) => ReplayWorkbenchContentTransfer(id, redo: true);

    // Returning true at a shared boundary also consumes a blocked native command. Letting
    // AvalonEdit or the Todo snapshot stack undo just one side would duplicate moved content.
    internal bool TryHandleWorkbenchContentHistory(string paperId, bool redo)
    {
        var transaction = _workbenchContentTransfers.LastOrDefault(entry =>
            IsCurrentWorkbenchTransfer(entry) && entry.Applied != redo &&
            ((entry.Source.Id == paperId && entry.SourceWindow.IsAtWorkbenchTransferBoundary(entry.SourceBoundary!, redo)) ||
             (entry.Target.Id == paperId && entry.TargetWindow.IsAtWorkbenchTransferBoundary(entry.TargetBoundary!, redo))));
        if (transaction == null) return false;
        if (!ReplayWorkbenchContentTransfer(transaction.Id, redo))
        {
            if (WorkbenchContentTransferBlocked != null) WorkbenchContentTransferBlocked.Invoke();
            else System.Windows.MessageBox.Show(Strings.Get("WorkbenchTransferHistoryBlocked"), "PaperTodo",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        return true;
    }

    private bool ReplayWorkbenchContentTransfer(Guid? id, bool redo)
    {
        if (IsExiting || IsDataReloading) return false;
        var transaction = id.HasValue
            ? _workbenchContentTransfers.LastOrDefault(entry => entry.Id == id && IsCurrentWorkbenchTransfer(entry))
            : _workbenchContentTransfers.LastOrDefault(entry => IsCurrentWorkbenchTransfer(entry) && entry.Applied != redo);
        if (transaction == null) return false;
        if (transaction.Applied == redo) return true;
        if (!ReferenceEquals(FindPaper(transaction.Source.Id), transaction.Source) ||
            (!transaction.CreatedTarget || !redo) && !ReferenceEquals(FindPaper(transaction.Target.Id), transaction.Target)) return false;
        var sourceExpected = redo ? transaction.SourceBefore : transaction.SourceAfter;
        var targetExpected = redo ? transaction.TargetBefore : transaction.TargetAfter;
        if (!sourceExpected.Matches(transaction.Source, CurrentMarkdownContentForExternalRead(transaction.Source)) ||
            !targetExpected.Matches(transaction.Target, CurrentMarkdownContentForExternalRead(transaction.Target)) ||
            !transaction.SourceWindow.IsAtWorkbenchTransferBoundary(transaction.SourceBoundary!, redo) ||
            !transaction.TargetWindow.IsAtWorkbenchTransferBoundary(transaction.TargetBoundary!, redo)) return false;
        // A new split paper can only disappear when no subsequent operation has linked to it.
        if (transaction.CreatedTarget && !redo &&
            (State.Papers.Any(p => p.Items.Any(i => i.LinkedPaperId == transaction.Target.Id)) ||
             WorkbenchLayout.Groups.Values.Any(group => group.Members.Contains(transaction.Target.Id)) ||
             WorkbenchLayout.Connections.Any(connection => connection.From == transaction.Target.Id || connection.To == transaction.Target.Id))) return false;
        if (transaction.CreatedTarget && redo && (State.Papers.Count >= 200 || FindPaper(transaction.Target.Id) != null)) return false;

        transaction.SourceWindow.PrepareWorkbenchContentTransfer();
        transaction.TargetWindow.PrepareWorkbenchContentTransfer();
        PrepareExternalPaperOperation(transaction.Source);
        PrepareExternalPaperOperation(transaction.Target);
        PreserveWorkbenchTriggeredReminders(transaction);
        var layoutBefore = WorkbenchLayout.Copy();
        var sourceItemModels = transaction.Source.Items;
        var targetItemModels = transaction.Target.Items;
        var targetWasVisible = transaction.Target.IsVisible;
        using var suppress = SuppressPaperPluginEventScans();
        (redo ? transaction.SourceAfter : transaction.SourceBefore).Apply(transaction.Source);
        (redo ? transaction.TargetAfter : transaction.TargetBefore).Apply(transaction.Target);
        if (transaction.CreatedTarget)
        {
            var layout = WorkbenchLayout.Copy();
            if (redo)
            {
                transaction.Target.IsVisible = transaction.TargetVisibleBeforeUndo;
                State.Papers.Add(transaction.Target);
                layout.Cards[transaction.Target.Id] = transaction.CreatedCard!.Copy();
            }
            else
            {
                transaction.TargetVisibleBeforeUndo = transaction.Target.IsVisible;
                transaction.Target.IsVisible = false;
                if (layout.Cards.TryGetValue(transaction.Target.Id, out var card)) transaction.CreatedCard = card.Copy();
                State.Papers.Remove(transaction.Target);
                layout.Cards.Remove(transaction.Target.Id);
            }
            StoreWorkbenchLayout(layout);
        }
        if (!TryCommitExternalMutation())
        {
            sourceExpected.Apply(transaction.Source);
            targetExpected.Apply(transaction.Target);
            transaction.Source.Items = sourceItemModels;
            transaction.Target.Items = targetItemModels;
            if (transaction.CreatedTarget)
            {
                transaction.Target.IsVisible = targetWasVisible;
                if (redo) State.Papers.Remove(transaction.Target);
                else State.Papers.Add(transaction.Target);
                StoreWorkbenchLayout(layoutBefore);
            }
            ResetPaperPluginEventBaseline();
            return false;
        }
        transaction.Source.Items = sourceItemModels;
        transaction.Target.Items = targetItemModels;
        transaction.SourceWindow.ReplayWorkbenchContentTransfer(transaction.SourceBoundary!, redo, transaction.SourceBefore, transaction.SourceAfter);
        transaction.TargetWindow.ReplayWorkbenchContentTransfer(transaction.TargetBoundary!, redo, transaction.TargetBefore, transaction.TargetAfter);
        transaction.Applied = redo;
        if (transaction.CreatedTarget)
        {
            if (!redo)
            {
                NextVisibilityAnimationVersion(transaction.Target.Id);
                transaction.TargetWindow.PrepareForHide();
                transaction.TargetWindow.DetachFromDeepCapsuleStack(animate: false);
                transaction.TargetWindow.HideWithoutGeometrySave();
                transaction.TargetWindow.ReleaseHiddenNoteImages();
                ArrangeDeepCapsules(animate: false);
            }
            else if (transaction.Target.IsVisible && !HasWorkbench) ShowPaper(transaction.Target, activate: false);
        }
        suppress.Dispose();
        CompleteWorkbenchTransferUi(transaction.Source, transaction.Target);
        WorkbenchContentTransferReplayed?.Invoke(transaction.Id, redo);
        return true;
    }

    private bool IsCurrentWorkbenchTransfer(WorkbenchTransferTransaction transaction) =>
        transaction.Generation == WorkspaceGeneration && transaction.Binding == WorkspaceBindingToken;

    private void CompleteWorkbenchTransferUi(PaperData source, PaperData target)
    {
        NotifyTodoReminderCollectionChanged();
        RefreshCapsuleEligibilityForLinkedPapers();
        RefreshTodoRowsForLinkedPaper(source.Id);
        RefreshTodoRowsForLinkedPaper(target.Id);
        RefreshTrayMenu();
        WorkbenchModelChanged?.Invoke();
        PublishExternalPaperOperation(PaperOperationContext.User());
    }

    private static void PreserveWorkbenchTriggeredReminders(WorkbenchTransferTransaction transaction)
    {
        var triggered = transaction.Source.Items.Concat(transaction.Target.Items)
            .Where(i => i.ReminderTriggered).ToDictionary(i => i.Id, i => i.ReminderAt, StringComparer.Ordinal);
        foreach (var item in new[] { transaction.SourceBefore, transaction.TargetBefore, transaction.SourceAfter, transaction.TargetAfter }.SelectMany(s => s.Items))
            if (triggered.TryGetValue(item.Id, out var at) && item.ReminderAt == at) item.ReminderTriggered = true;
    }

    private sealed class WorkbenchTransferTransaction
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal long Generation { get; init; }
        internal string Binding { get; init; } = "";
        internal required PaperData Source { get; init; }
        internal required PaperData Target { get; init; }
        internal required PaperWindow SourceWindow { get; init; }
        internal required PaperWindow TargetWindow { get; init; }
        internal required WorkbenchContentSnapshot SourceBefore { get; init; }
        internal required WorkbenchContentSnapshot TargetBefore { get; init; }
        internal required WorkbenchContentSnapshot SourceAfter { get; init; }
        internal required WorkbenchContentSnapshot TargetAfter { get; init; }
        internal WorkbenchTransferBoundary? SourceBoundary { get; set; }
        internal WorkbenchTransferBoundary? TargetBoundary { get; set; }
        internal bool CreatedTarget { get; init; }
        internal bool TargetVisibleBeforeUndo { get; set; }
        internal WorkbenchCardState? CreatedCard { get; set; }
        internal bool Applied { get; set; } = true;
    }
}
