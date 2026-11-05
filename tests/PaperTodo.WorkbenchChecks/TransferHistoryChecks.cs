using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
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
}
