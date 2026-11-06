using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static partial class WorkbenchChecks
{
    private static async Task RunGraph()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        initial.Papers.Single(p => p.Id == "todo-a").Items.First().LinkPaper("note-a");
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3, "Graph fixture did not mount source papers");

        string NewNode(WorkbenchPoint point) => (string)typeof(WorkbenchWindow).GetMethod("AddNode", Private)!
            .Invoke(desk, [point])!;
        var root = NewNode(new WorkbenchPoint(1700, 120));
        await Until(() => CardReady(desk, root), "New node did not appear");
        var editor = (TextBox)Member(ActiveCards(desk)[root]!, "MaterialEditor");
        await Until(() => editor.IsKeyboardFocusWithin, "Node text editor did not get focus");
        editor.Text = "Root idea";
        Assert(controller.FindWorkbenchMaterial(root)?.Content == "Root idea",
            "Editing the node did not update the workspace materials note");

        // Tab and Enter belong to normal editor navigation/text, not automatic branch creation.
        var materials = WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!;
        var originalCount = materials.Items.Count;
        SendNodeKey(desk, editor, Key.Tab);
        SendNodeKey(desk, editor, Key.Enter);
        Assert(materials.Items.Count == originalCount, "Tab/Enter unexpectedly created graph nodes");

        var child = NewNode(new WorkbenchPoint(2200, 120));
        await Until(() => CardReady(desk, child), "Second independent node did not appear");
        Assert(new[] { root, child }.All(id => controller.FindWorkbenchMaterial(id)?.Kind == WorkspaceMaterialKinds.Node)
               && desk.HostedPaperCount == 3,
            "Canvas nodes were incorrectly turned into standard paper windows");

        InvokeWorkbench(desk, "CreateConnection", root, child, false);
        Assert(controller.WorkbenchLayout.Connections.Any(c => c.From == root && c.To == child && !c.IsBranch),
            "Manual connection was not stored as a free link");
        InvokeWorkbench(desk, "CreateConnection", root, "note-b", false);
        var free = controller.WorkbenchLayout.Connections.Single(c => c.From == root && c.To == "note-b");
        InvokeWorkbench(desk, "CreateConnection", root, "note-b", false);
        Assert(controller.WorkbenchLayout.Connections.Count(c => c.From == root && c.To == "note-b") == 1,
            "Repeated manual connection created duplicates");
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Connections.All(c => c.Id != free.Id),
            "Undo did not remove the last connection");
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Connections.Any(c => c.Id == free.Id),
            "Redo did not restore its connection identity");
        Assert(controller.FindPaper("todo-a")!.Items.First().LinkedPaperId == "note-a",
            "Free canvas connections changed a Todo's business association");

        InvokeWorkbench(desk, "FitActive");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveImage(desk, "workbench-graph.png");
        controller.SaveNow(sync: true);
        var saved = WorkspaceMaterials.FindOwner(StateStore.ReadWorkbenchOverview(store.FilePath)!)!.WorkspaceMaterials!;
        Assert(saved.Items[root].Content == "Root idea" &&
               saved.Layout.Connections.Count(c => c.From == root && !c.IsBranch) == 2,
            "Manual graph nodes or connections were not persisted");
        controller.ToggleWorkbench();
        controller.ToggleWorkbench();
        desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && ActiveCards(desk).Contains(child),
            "Reopened workspace lost its graph nodes");
        Assert(CardFrame(desk, root).Visibility == Visibility.Visible &&
               CardFrame(desk, child).Visibility == Visibility.Visible &&
               CardFrame(desk, "note-b").Visibility == Visibility.Visible,
            "Free connections unexpectedly hid their targets");

        controller.DeleteWorkbenchMaterial(root);
        await Until(() => !ActiveCards(desk).Contains(root), "Deleting a node left its visual behind");
        Assert(controller.WorkbenchLayout.Connections.All(c => c.From != root && c.To != root) &&
               controller.FindWorkbenchMaterial(child) != null,
            "Deleting a node left dangling connections or removed another node");
        controller.ToggleWorkbench();
        controller.SaveNow(sync: true);
    }

    private static void SendNodeKey(WorkbenchWindow desk, TextBox editor, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(desk), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(args);
    }
}
