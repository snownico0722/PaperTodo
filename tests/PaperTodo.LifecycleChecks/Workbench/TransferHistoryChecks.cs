using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static partial class WorkbenchChecks
{
    private static async Task RunTransferHistory()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        initial.Papers.Single(p => p.Id == "note-a").Content = "alpha\nbeta\ngamma";
        initial.Papers.Single(p => p.Id == "note-b").Content = "target";
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && CardReady(desk, "note-a"),
            "History fixture did not mount the source editors");
        var source = controller.WorkbenchPaperWindow("note-a");
        var sourceBox = (MarkdownTextBox)Member(source, "_noteBox");
        var targetBox = (MarkdownTextBox)Member(controller.WorkbenchPaperWindow("note-b"), "_noteBox");

        // Keep an older layout operation under the transfer token: a stale canvas
        // cursor would undo this connection instead of the natively replayed move.
        InvokeWorkbench(desk, "CreateConnection", "note-a", "note-b", false);
        sourceBox.Select(6, 5);
        Assert(source.TryCreateWorkbenchContentTransfer(out var payload), "Cannot select history fixture text");
        InvokeWorkbench(desk, "ApplyContentTransfer", payload, "note-b", null);
        AssertMoved();
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        AssertRestored();
        ExecuteNative(ApplicationCommands.Redo);
        AssertMoved();
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        AssertRestored();
        Assert(controller.WorkbenchLayout.Connections.Any(e => e.From == "note-a" && e.To == "note-b"),
            "Canvas undo skipped a natively replayed transfer and removed an older layout operation");

        ExecuteNative(ApplicationCommands.Redo);
        ExecuteNative(ApplicationCommands.Undo);
        AssertRestored();
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        AssertMoved();

        // An edit on the other side blocks the shared boundary. The native command
        // must be consumed, preserving both texts and reporting through the canvas.
        var blocked = 0;
        controller.WorkbenchContentTransferBlocked += () => blocked++;
        targetBox.Document.Insert(targetBox.Document.TextLength, "later");
        ExecuteNative(ApplicationCommands.Undo);
        Assert(blocked == 1 && sourceBox.Text == "alpha\ngamma" && targetBox.Text == "target\nbeta\nlater",
            "Blocked native undo changed one side of a shared transfer or failed to report the block");
        targetBox.Document.UndoStack.Undo();
        ExecuteNative(ApplicationCommands.Undo);
        AssertRestored();
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        AssertMoved();
        await CheckRetiredTransferHistory(controller, desk, source, sourceBox, targetBox);
        controller.ToggleWorkbench();
        controller.SaveNow(sync: true);

        void ExecuteNative(RoutedCommand command)
        {
            Assert(command.CanExecute(null, sourceBox.TextArea), "Native history command was unexpectedly disabled");
            command.Execute(null, sourceBox.TextArea);
        }
        void AssertMoved() => Assert(sourceBox.Text == "alpha\ngamma" && targetBox.Text == "target\nbeta\n",
            "Native/canvas history replay did not apply both sides of the move");
        void AssertRestored() => Assert(sourceBox.Text == "alpha\nbeta\ngamma" && targetBox.Text == "target",
            "Native/canvas history replay did not restore both sides of the move");
    }

    private static async Task CheckRetiredTransferHistory(AppController controller, WorkbenchWindow desk,
        PaperWindow source, MarkdownTextBox sourceBox, MarkdownTextBox targetBox)
    {
        var third = NewNote("third");
        var unrelated = NewNote("unrelated");
        unrelated.Box.Document.Insert(unrelated.Box.Document.TextLength, " edit");
        InvokeWorkbench(desk, "SyncActiveScene");
        InvokeWorkbench(desk, "CreateConnection", "note-a", "todo-a", false);
        var target = controller.WorkbenchPaperWindow("note-b");
        targetBox.Select(0, 6);
        Assert(target.TryCreateWorkbenchContentTransfer(out var secondPayload), "Cannot select a connected transfer");
        InvokeWorkbench(desk, "ApplyContentTransfer", secondPayload, third.Paper.Id, null);
        var secondMove = controller.LastWorkbenchContentTransferId;

        // Failed persistence must preserve both shared boundaries. Use the real command and
        // durable writer failure path; a rejected external write must not reset editor history.
        var store = (StateStore)Member(controller, "_store");
        var writerField = typeof(StateStore).GetField("_atomicWriter", Private)!;
        var writer = writerField.GetValue(store);
        var ignoreFailures = (bool)Member(controller, "_ignoreSaveFailures");
        typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, true);
        writerField.SetValue(store, new DurableAtomicFileWriter((stage, _) =>
        {
            if (stage == DurableAtomicWriteStage.AfterFlush) throw new IOException("injected transfer history failure");
        }));
        try
        {
            try
            {
                controller.PaperCommands.WriteNote(new WriteNoteRequest
                { PaperId = third.Paper.Id, Content = "replacement", Mode = NoteWriteMode.Replace }, PaperOperationContext.Mcp());
                throw new InvalidOperationException("The failing note write unexpectedly succeeded");
            }
            catch (PaperCommandException ex) when (ex.Code == "save_failed") { }
        }
        finally
        {
            writerField.SetValue(store, writer);
            typeof(AppController).GetField("_ignoreSaveFailures", Private)!.SetValue(controller, ignoreFailures);
        }
        Assert(controller.TryUndoWorkbenchContentTransfer(secondMove) && controller.TryRedoWorkbenchContentTransfer(secondMove),
            "Failed notes.write retired a still-replayable shared transfer");

        var sourceAfter = sourceBox.Text;
        var targetAfter = targetBox.Text;
        controller.PaperCommands.WriteNote(new WriteNoteRequest
        { PaperId = third.Paper.Id, Content = "replacement", Mode = NoteWriteMode.Replace }, PaperOperationContext.Mcp());
        Assert(sourceBox.Text == sourceAfter && targetBox.Text == targetAfter && third.Box.Text == "replacement" &&
               !sourceBox.CanUndo && !sourceBox.CanRedo && !targetBox.CanUndo && !targetBox.CanRedo,
            "Resetting the end of a transfer chain left a one-sided native undo or changed content");
        Assert(!controller.TryRedoWorkbenchContentTransfer(secondMove) &&
               !controller.TryHandleWorkbenchContentHistory("note-a", redo: false),
            "A permanently replaced native stack still owns a shared transfer boundary");
        Assert(unrelated.Box.CanUndo, "Retiring a transfer chain cleared an unrelated editor history");
        Execute(unrelated.Box, ApplicationCommands.Undo);
        Assert(unrelated.Box.Text == "unrelated", "Unrelated native history was no longer usable");
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        Assert(!controller.WorkbenchLayout.Connections.Any(c => c.From == "note-a" && c.To == "todo-a"),
            "Retired transfer tokens still block older canvas history");
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "new edit");
        Execute(sourceBox, ApplicationCommands.Undo);
        Assert(sourceBox.Text == sourceAfter && targetBox.Text == targetAfter,
            "Fresh native undo after retirement replayed one half of an old move");

        // An explicitly accepted reload resets shared history even for an unchanged peer.
        sourceBox.Select(0, 5);
        Assert(source.TryCreateWorkbenchContentTransfer(out var reloadPayload), "Cannot select reload transfer");
        InvokeWorkbench(desk, "ApplyContentTransfer", reloadPayload, "note-b", null);
        var reloadMove = controller.LastWorkbenchContentTransferId;
        var beforeReloadSource = sourceBox.Text;
        controller.SaveNow(sync: true);
        var disk = StateStore.CopyForReload(controller.State);
        disk.Papers.Single(p => p.Id == "note-b").Content = "reloaded body";
        File.WriteAllText(store.FilePath, store.SerializeState(disk));
        Assert(controller.PaperCommands.ReloadData(PaperOperationContext.Mcp()).Applied, "Reload fixture was not accepted");
        await Until(() => !controller.IsDataReloading, "Reload did not finish retiring shared histories");
        Assert(sourceBox.Text == beforeReloadSource && targetBox.Text == "reloaded body" &&
               !sourceBox.CanUndo && !sourceBox.CanRedo && !controller.TryUndoWorkbenchContentTransfer(reloadMove),
            "Reload left a peer's stale transfer boundary replayable or permanently blocking");
        sourceBox.Document.Insert(sourceBox.Document.TextLength, "after reload");
        Execute(sourceBox, ApplicationCommands.Undo);
        Assert(sourceBox.Text == beforeReloadSource && targetBox.Text == "reloaded body",
            "Native undo after reload restored previously moved content");

        // Todo history has a separate snapshot stack. Deletion must retire that stack too,
        // and remove the transfer token without discarding the preceding canvas operation.
        var todoSource = controller.CreatePaper(PaperTypes.Todo, show: false)!;
        todoSource.Items[0].Text = "moved task";
        var todoTarget = controller.CreatePaper(PaperTypes.Todo, show: false)!;
        var todoWindow = controller.WorkbenchPaperWindow(todoSource.Id);
        todoWindow.EnsureShellBuilt();
        InvokeWorkbench(desk, "SyncActiveScene");
        InvokeWorkbench(desk, "CreateConnection", "note-b", "todo-a", false);
        Assert(todoWindow.TryCreateWorkbenchContentTransfer(out var todoPayload, wholePaper: true), "Cannot select deletion transfer");
        InvokeWorkbench(desk, "ApplyContentTransfer", todoPayload, todoTarget.Id, null);
        var deletedMove = controller.LastWorkbenchContentTransferId;
        controller.PaperCommands.DeletePaper(todoTarget.Id, PaperOperationContext.Mcp());
        typeof(PaperWindow).GetMethod("Undo", Private)!.Invoke(todoWindow, null);
        typeof(PaperWindow).GetMethod("Redo", Private)!.Invoke(todoWindow, null);
        Assert(todoSource.Items.All(TodoRules.IsPlaceholder) &&
               !controller.TryUndoWorkbenchContentTransfer(deletedMove) &&
               !controller.TryHandleWorkbenchContentHistory(todoSource.Id, redo: false),
            "Deleting a transfer peer left a blocked or one-sided Todo history step");
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        Assert(!controller.WorkbenchLayout.Connections.Any(c => c.From == "note-b" && c.To == "todo-a"),
            "Deleting a transfer peer trapped older canvas history behind the retired token");

        var knownPapers = controller.State.Papers.Select(p => p.Id).ToHashSet();
        sourceBox.Select(0, 1);
        Assert(source.TryCreateWorkbenchContentTransfer(out var splitPayload), "Cannot select retirement split");
        InvokeWorkbench(desk, "ApplyContentTransfer", splitPayload, null, new WorkbenchPoint(1400, 200));
        var splitMove = controller.LastWorkbenchContentTransferId;
        var split = controller.State.Papers.Single(p => !knownPapers.Contains(p.Id));
        var splitWindow = controller.WorkbenchPaperWindow(split.Id);
        Assert(controller.TryUndoWorkbenchContentTransfer(splitMove), "Cannot undo retirement split");
        controller.PaperCommands.WriteNote(new WriteNoteRequest
        { PaperId = "note-a", Content = "source replaced", Mode = NoteWriteMode.Replace }, PaperOperationContext.Mcp());
        Assert(splitWindow.IsClosed &&
               !((Dictionary<string, PaperWindow>)Member(controller, "_windows")).ContainsKey(split.Id) &&
               controller.State.Papers.All(p => p.Id != split.Id),
            "Retiring an undone split retained its unreachable redo window or recreated the paper");

        // Flushing the user's undo can synchronously deliver a public plugin event.
        // Its committed write must supersede the shared boundary checked by the outer undo.
        var callbackSource = NewNote("callback source");
        var callbackTarget = NewNote("callback target");
        var callbackWindow = controller.WorkbenchPaperWindow(callbackSource.Paper.Id);
        callbackSource.Box.Select(0, 8);
        Assert(callbackWindow.TryCreateWorkbenchContentTransfer(out var callbackPayload) &&
               controller.TransferWorkbenchContent(callbackPayload, callbackTarget.Paper.Id),
            "Cannot prepare a transfer for the plugin callback check");
        var callbackMove = controller.LastWorkbenchContentTransferId;
        var callbackSourceAfter = callbackSource.Box.Text;
        callbackSource.Box.Document.Insert(callbackSource.Box.Document.TextLength, " later");
        controller.SaveNow(sync: true);
        callbackSource.Box.Document.UndoStack.Undo();
        Assert(callbackSource.Box.Text == callbackSourceAfter && callbackWindow.HasPendingMarkdownContentForSave,
            "Native undo did not leave a pending edit at the shared transfer boundary");

        using var api = new PaperPluginRuntimeWorkspaceApi(controller, "transfer-observer",
            [PaperTodoPermissionNames.NotesRead, PaperTodoPermissionNames.NotesReplace,
             PaperTodoPermissionNames.PapersDelete], () => true);
        var callbackWrites = 0;
        const string callbackContent = "Written by the synchronous plugin callback";
        using (api.Subscribe(new()
        {
            Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.NoteChanged },
            PaperIds = new HashSet<string> { callbackSource.Paper.Id }
        }, _ =>
        {
            api.WriteNote(new()
            { PaperId = callbackTarget.Paper.Id, Content = callbackContent, Mode = NoteWriteMode.Replace });
            callbackWrites++;
        }))
        {
            Assert(!controller.TryUndoWorkbenchContentTransfer(callbackMove),
                "Shared undo continued after its preparation callback replaced a peer");
        }
        var callbackDisk = StateStore.ReadWorkbenchOverview(store.FilePath)!;
        Assert(callbackWrites == 1 && callbackSource.Box.Text == callbackSourceAfter &&
               callbackTarget.Box.Text == callbackContent && callbackTarget.Paper.Content == callbackContent &&
               callbackDisk.Papers.Single(p => p.Id == callbackSource.Paper.Id).Content == callbackSourceAfter &&
               callbackDisk.Papers.Single(p => p.Id == callbackTarget.Paper.Id).Content == callbackContent &&
               !controller.TryHandleWorkbenchContentHistory(callbackSource.Paper.Id, redo: false),
            "Shared undo overwrote the callback's committed text or retained a retired boundary");

        // The same preparation boundary precedes a first split. A callback deleting
        // its source must not create a target from the now-detached paper object.
        var deletedSource = NewNote("delete before split");
        var deletedWindow = controller.WorkbenchPaperWindow(deletedSource.Paper.Id);
        var papersBeforeCallback = controller.State.Papers.Select(p => p.Id).ToHashSet();
        var callbackDeletes = 0;
        using (api.Subscribe(new()
        {
            Kinds = new HashSet<PaperTodoEventKind> { PaperTodoEventKind.NoteChanged },
            PaperIds = new HashSet<string> { deletedSource.Paper.Id }
        }, _ =>
        {
            api.DeletePaper(deletedSource.Paper.Id);
            callbackDeletes++;
        }))
        {
            deletedSource.Box.Document.Insert(deletedSource.Box.Document.TextLength, " pending");
            deletedSource.Box.Select(0, 6);
            Assert(deletedWindow.TryCreateWorkbenchContentTransfer(out var deletedPayload) &&
                   !controller.TransferWorkbenchContent(deletedPayload, null),
                "A split continued after its preparation callback deleted the source");
        }
        papersBeforeCallback.Remove(deletedSource.Paper.Id);
        Assert(callbackDeletes == 1 && deletedWindow.IsClosed &&
               papersBeforeCallback.SetEquals(controller.State.Papers.Select(p => p.Id)) &&
               papersBeforeCallback.SetEquals(StateStore.ReadWorkbenchOverview(store.FilePath)!.Papers.Select(p => p.Id)),
            "A rejected split recreated callback-deleted content or left an extra target");

        (PaperData Paper, MarkdownTextBox Box) NewNote(string content)
        {
            var paper = controller.CreatePaper(PaperTypes.Note, show: false)!;
            paper.Content = content;
            var window = controller.WorkbenchPaperWindow(paper.Id);
            window.EnsureShellBuilt();
            return (paper, (MarkdownTextBox)Member(window, "_noteBox"));
        }
        static void Execute(MarkdownTextBox editor, RoutedCommand command)
        {
            Assert(command.CanExecute(null, editor.TextArea), "Fresh native history command was unexpectedly disabled");
            command.Execute(null, editor.TextArea);
        }
    }
}
