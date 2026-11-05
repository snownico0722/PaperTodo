using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task RunTransfers()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        initial.Papers.First(p => p.Id == "note-a").Content = "alpha\nbeta\ngamma";
        initial.Papers.First(p => p.Id == "note-b").Content = "target";
        initial.Papers.Add(new PaperData { Id = "todo-b", Type = PaperTypes.Todo, IsVisible = false, Items = [new()] });
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var source = controller.WorkbenchPaperWindow("note-a");
        var target = controller.WorkbenchPaperWindow("note-b");
        var sourceBox = (MarkdownTextBox)Member(source, "_noteBox");
        var targetBox = (MarkdownTextBox)Member(target, "_noteBox");

        var ordinaryHistoryPosition = Member(source, "_workbenchNoteHistoryPosition");
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "!");
        Assert(ReferenceEquals(ordinaryHistoryPosition, Member(source, "_workbenchNoteHistoryPosition")),
            "Ordinary Markdown typing created workbench-only history markers");
        targetBox.Document.Insert(targetBox.Document.TextLength, "?");
        sourceBox.Select(6, 5);
        Assert(source.TryCreateWorkbenchContentTransfer(out var selected), "Note selection did not produce drag payload");
        Assert(controller.CanTransferWorkbenchContent(selected, "note-b"), "Lightweight hover rejected a valid note selection");
        Assert(controller.TransferWorkbenchContent(selected, "note-b"), "Note selection was not moved");
        var moveId = controller.LastWorkbenchContentTransferId;
        Assert(sourceBox.Text == "alpha\ngamma!" && targetBox.Text == "target?\nbeta\n", "Note move duplicated or lost text");
        Assert(ReferenceEquals(Member(source, "_noteBox"), sourceBox), "Move replaced source Markdown editor");

        targetBox.Document.Insert(targetBox.Document.TextLength, "later");
        Assert(!controller.TryUndoWorkbenchContentTransfer(moveId) && targetBox.Text.EndsWith("later"),
            "Shared undo overwrote a subsequent target edit");
        targetBox.Document.UndoStack.Undo();
        Assert(controller.TryUndoWorkbenchContentTransfer(moveId), "Shared note undo did not become available after undoing later typing");
        Assert(sourceBox.Text == "alpha\nbeta\ngamma!" && targetBox.Text == "target?", "Shared undo restored only one note");
        sourceBox.Document.UndoStack.Undo();
        Assert(sourceBox.Text == "alpha\nbeta\ngamma", "Move erased prior native note undo history");
        Assert(!controller.TryRedoWorkbenchContentTransfer(moveId), "Transfer replay skipped older undone text");
        sourceBox.Document.UndoStack.Redo();
        Assert(controller.TryRedoWorkbenchContentTransfer(moveId), "Shared redo failed after restoring the native boundary");
        Assert(sourceBox.Text == "alpha\ngamma!" && targetBox.Text == "target?\nbeta\n", "Shared redo restored only one note");

        Assert(controller.TryUndoWorkbenchContentTransfer(moveId), "Second shared undo failed");
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "branch");
        sourceBox.Document.UndoStack.Undo();
        Assert(!controller.TryRedoWorkbenchContentTransfer(moveId), "A discarded redo branch replayed unrelated native text as a move");

        sourceBox.Select(0, 5);
        Assert(source.TryCreateWorkbenchContentTransfer(out selected) &&
            controller.TransferWorkbenchContent(selected, null, new WorkbenchPoint(90, 150)), "Selection did not split into a new paper");
        var splitId = controller.LastWorkbenchContentTransferId;
        var splitPaper = controller.State.Papers.Single(p => p.Id is not ("note-a" or "note-b" or "todo-a" or "todo-b") && p.WorkspaceMaterials == null);
        Assert(!splitPaper.IsVisible && splitPaper.Content == "alpha", "Split paper was not a hidden, editable note");
        Assert(controller.WorkbenchLayout.Cards[splitPaper.Id].X == 90, "Split ignored canvas coordinates");
        Assert(controller.TryUndoWorkbenchContentTransfer(splitId) && !controller.State.Papers.Contains(splitPaper), "Split undo left its newly created paper");
        Assert(controller.TryRedoWorkbenchContentTransfer(splitId) && controller.State.Papers.Contains(splitPaper), "Split redo did not restore the same paper");

        var todoSource = controller.State.Papers.First(p => p.Id == "todo-a");
        var todoTarget = controller.State.Papers.First(p => p.Id == "todo-b");
        todoSource.Items[0].Done = true;
        todoSource.Items[0].ReminderAt = DateTimeOffset.UtcNow.AddDays(1);
        todoSource.Items[0].ReminderTriggered = true;
        todoSource.Items[0].LinkPaper("note-a");
        var oldTodo = TodoRules.CloneAll(todoSource.Items);
        var todoWindow = controller.WorkbenchPaperWindow(todoSource.Id);
        todoWindow.RefreshTodoRowsForExternalMutation();
        Assert(todoWindow.TryCreateWorkbenchContentTransfer(out var todoPayload, wholePaper: true), "Whole todo paper did not produce payload");
        Assert(controller.CanTransferWorkbenchContent(todoPayload, todoTarget.Id), "Lightweight hover rejected a valid Todo group");
        Assert(controller.TransferWorkbenchContent(todoPayload, todoTarget.Id), "Todo group did not move");
        var todoMove = controller.LastWorkbenchContentTransferId;
        Assert(todoSource.Items.Count == 1 && TodoRules.IsPlaceholder(todoSource.Items[0]), "Empty todo source did not keep its input row");
        Assert(todoTarget.Items.Select(i => i.Id).SequenceEqual(oldTodo.Select(i => i.Id)) &&
            todoTarget.Items[0].Done && todoTarget.Items[0].ReminderTriggered &&
            todoTarget.Items[0].ReminderAt == oldTodo[0].ReminderAt && todoTarget.Items[0].LinkedPaperId == "note-a",
            "Todo movement lost identity, completion, reminder or paper link");
        Assert(controller.TryUndoWorkbenchContentTransfer(todoMove) && todoSource.Items[0].Id == oldTodo[0].Id &&
            TodoRules.IsPlaceholder(todoTarget.Items[0]), "Todo shared undo did not restore both papers");
        Assert(controller.TryRedoWorkbenchContentTransfer(todoMove), "Todo shared redo failed");

        var mismatched = todoPayload with { WorkspaceGeneration = todoPayload.WorkspaceGeneration + 1 };
        Assert(!controller.CanTransferWorkbenchContent(mismatched, null), "Old workspace drag was accepted");
        var noteData = controller.State.Papers.First(p => p.Id == "note-a");
        var fullTarget = new PaperData { Type = PaperTypes.Note, Content = new string('x', PaperWindow.NoteTextMaxLength) };
        var invalid = new WorkbenchContentTransferPayload(noteData.Id, controller.WorkspaceGeneration,
            controller.WorkspaceBindingToken, [], 0, 1, sourceBox.Text[..1]);
        Assert(!WorkbenchContentTransfer.TryPlan(noteData, fullTarget, invalid, sourceBox.Text, fullTarget.Content, out _, out _),
            "Oversized note transfer was accepted");

        var image = controller.ImageStore.ImportBitmapSource("note-a",
            BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4));
        var reference = MarkdownImageReferences.CreateReference(image.Id);
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "\n" + reference + "\n");
        var imageSourceBefore = sourceBox.Text;
        var imageTargetBefore = targetBox.Text;
        sourceBox.Select(sourceBox.Text.IndexOf(reference, StringComparison.Ordinal), reference.Length);
        Assert(source.TryCreateWorkbenchContentTransfer(out var imagePayload), "Image selection did not produce payload");
        Assert(controller.TransferWorkbenchContent(imagePayload, "note-b"), "Image paragraph did not move");
        var imageMove = controller.LastWorkbenchContentTransferId;
        var imageSourceAfter = sourceBox.Text;
        var imageTargetAfter = targetBox.Text;
        var movedImageId = MarkdownImageReferences.CollectImageIds(targetBox.Text).Single();
        Assert(movedImageId != image.Id && controller.ImageStore.TryGetAsset(movedImageId, out var movedImage) && movedImage.NoteId == "note-b",
            "Moved note image retained the source's asset ownership");
        Assert(imageTargetAfter.EndsWith(Environment.NewLine), "Moved final image is missing its structural caret line");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "image move");
        Assert(controller.TryUndoWorkbenchContentTransfer(imageMove), "Image move undo was rejected at its native boundary");
        Assert(sourceBox.Text == imageSourceBefore, "Image move undo did not restore the original source text/reference");
        Assert(targetBox.Text == imageTargetBefore, "Image move undo did not restore the original target text");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "image undo");
        Assert(controller.TryRedoWorkbenchContentTransfer(imageMove), "Image move redo was rejected at its native boundary");
        Assert(sourceBox.Text == imageSourceAfter && targetBox.Text == imageTargetAfter, "Image move redo changed the normalized text/reference");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "image redo");

        // Removing trailing prose can expose an image as the source's final line as well.
        Assert(controller.TryUndoWorkbenchContentTransfer(imageMove), "Image move second undo failed");
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "tail");
        var exposedSourceBefore = sourceBox.Text;
        var exposedTargetBefore = targetBox.Text;
        var tailStart = sourceBox.Text.IndexOf(reference, StringComparison.Ordinal) + reference.Length;
        sourceBox.Select(tailStart, sourceBox.Text.Length - tailStart);
        Assert(source.TryCreateWorkbenchContentTransfer(out var tailPayload), "Trailing prose did not produce payload");
        Assert(controller.TransferWorkbenchContent(tailPayload, "note-b"), "Trailing prose did not move");
        var tailMove = controller.LastWorkbenchContentTransferId;
        var exposedSourceAfter = exposedSourceBefore[..tailStart] + Environment.NewLine;
        var exposedTargetAfter = targetBox.Text;
        Assert(sourceBox.Text == exposedSourceAfter, "Exposed final source image was not normalized before commit");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "source image exposure");
        Assert(controller.TryUndoWorkbenchContentTransfer(tailMove), "Exposed source image undo was rejected");
        Assert(sourceBox.Text == exposedSourceBefore && targetBox.Text == exposedTargetBefore, "Source image exposure undo did not restore both documents");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "source image exposure undo");
        Assert(controller.TryRedoWorkbenchContentTransfer(tailMove), "Exposed source image redo was rejected");
        Assert(sourceBox.Text == exposedSourceAfter && targetBox.Text == exposedTargetAfter, "Source image exposure redo changed document text");
        AssertTransferNotesPersisted(controller, store, sourceBox, targetBox, "source image exposure redo");
    }

    private static void AssertTransferNotesPersisted(AppController controller, StateStore store,
        MarkdownTextBox sourceBox, MarkdownTextBox targetBox, string operation)
    {
        // Read the synchronous transaction commit directly, without an extra SaveNow that could
        // conceal a discrepancy between the committed plan and editor normalization.
        var persisted = StateStore.ReadWorkbenchOverview(store.FilePath)!;
        Assert(controller.State.Papers.Single(p => p.Id == "note-a").Content == sourceBox.Text,
            operation + ": source model and editor differ");
        Assert(controller.State.Papers.Single(p => p.Id == "note-b").Content == targetBox.Text,
            operation + ": target model and editor differ");
        Assert(persisted.Papers.Single(p => p.Id == "note-a").Content == sourceBox.Text,
            operation + ": source disk and editor differ");
        Assert(persisted.Papers.Single(p => p.Id == "note-b").Content == targetBox.Text,
            operation + ": target disk and editor differ");
    }
}
