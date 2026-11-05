using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task RunMixedHistory()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        initial.Papers.Single(p => p.Id == "note-a").Content = "alpha\nbeta\ngamma";
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && controller.WorkbenchLayout.Cards.Count == 3,
            "Mixed history fixture did not mount its papers");
        var source = controller.WorkbenchPaperWindow("note-a");
        var box = (MarkdownTextBox)Member(source, "_noteBox");
        box.Select(6, 5);
        Assert(source.TryCreateWorkbenchContentTransfer(out var payload), "Mixed history selection failed");
        InvokeWorkbench(desk, "ApplyContentTransfer", payload, null, new WorkbenchPoint(1800, 300));
        var split = controller.State.Papers.Single(p => p.WorkspaceMaterials == null &&
            p.Id is not ("note-a" or "note-b" or "todo-a"));
        await Until(() => CardReady(desk, split.Id), "Split paper did not appear");
        var original = controller.WorkbenchLayout.Cards[split.Id].Copy();
        Drag("Move", 90, 50);
        var moved = controller.WorkbenchLayout.Cards[split.Id].Copy();
        Drag("Resize", 40, 30);
        var resized = controller.WorkbenchLayout.Cards[split.Id].Copy();
        Assert(moved.X == original.X + 90 && moved.Y == original.Y + 50 &&
               resized.Width == moved.Width + 40 && resized.Height == moved.Height + 30,
            "Mixed history fixture did not record its real drag and resize");

        Native(ApplicationCommands.Undo);
        Assert(!controller.State.Papers.Contains(split), "Native split undo left the paper alive");
        InvokeWorkbench(desk, "UndoWorkspaceAction"); // resize, while the split paper is absent
        InvokeWorkbench(desk, "UndoWorkspaceAction"); // move, while the split paper is absent
        controller.SaveNow(sync: true);
        Assert(!controller.WorkbenchLayout.Cards.ContainsKey(split.Id) &&
               !WorkspaceMaterials.FindOwner(StateStore.ReadWorkbenchOverview(store.FilePath)!)!
                   .WorkspaceMaterials!.Layout.Cards.ContainsKey(split.Id),
            "A dormant split placement leaked into the live or persisted layout");

        Native(ApplicationCommands.Redo);
        Assert(controller.WorkbenchLayout.Cards[split.Id].Bounds == original.Bounds,
            "Redoing a split resurrected an undone move or resize");
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Cards[split.Id].Bounds == moved.Bounds,
            "Layout redo lost the dormant paper's pre-resize placement");
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Cards[split.Id].Bounds == resized.Bounds,
            "Layout redo did not restore the moved and resized paper");
        Assert(box.Text == "alpha\ngamma" && split.Content == "beta\n",
            "Mixed layout and native history changed the transferred content");

        void Drag(string member, double x, double y)
        {
            var thumb = (Thumb)Member(ActiveCards(desk)[split.Id]!, member);
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(x, y, false) { RoutedEvent = Thumb.DragCompletedEvent });
        }
        void Native(RoutedCommand command)
        {
            Assert(command.CanExecute(null, box.TextArea), "Native mixed history command was disabled");
            command.Execute(null, box.TextArea);
        }
    }
}
