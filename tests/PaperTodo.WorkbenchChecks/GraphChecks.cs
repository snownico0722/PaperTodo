using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
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
        await Until(() => desk.HostedPaperCount == 3, "Graph fixture did not mount its source papers");

        var root = (string)typeof(WorkbenchWindow).GetMethod("AddNode", Private)!
            .Invoke(desk, [new WorkbenchPoint(1700, 120), null])!;
        await Until(() => CardReady(desk, root), "New graph node did not appear");
        var rootEditor = (TextBox)Member(ActiveCards(desk)[root]!, "MaterialEditor");
        await Until(() => rootEditor.IsKeyboardFocusWithin, "New node was not ready for continuous typing");
        rootEditor.Text = "Root idea";
        Assert(controller.FindWorkbenchMaterial(root)?.Content == "Root idea",
            "Typing in a node did not update the materials collection");

        // Exercise the editor shortcuts that make branch creation continuous.
        var known = WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Keys.ToHashSet();
        SendNodeKey(desk, rootEditor, Key.Tab);
        await Until(() => WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Count == known.Count + 1,
            "Tab in a node did not create a child");
        var child = WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Keys.Except(known).Single();
        await Until(() => CardReady(desk, child) &&
                          ((TextBox)Member(ActiveCards(desk)[child]!, "MaterialEditor")).IsKeyboardFocusWithin,
            "Child node did not receive typing focus");
        var childEditor = (TextBox)Member(ActiveCards(desk)[child]!, "MaterialEditor");
        childEditor.Text = "First branch";
        known.Add(child);
        SendNodeKey(desk, childEditor, Key.Enter);
        await Until(() => WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Count == known.Count + 1,
            "Enter in a node did not create a sibling");
        var sibling = WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Keys.Except(known).Single();
        await Until(() => CardReady(desk, sibling), "Sibling node did not appear");
        Assert(controller.WorkbenchLayout.Connections.Count(e => e.IsBranch && e.From == root &&
                   (e.To == child || e.To == sibling)) == 2,
            "Continuous child and sibling creation built the wrong branch parentage");

        known.Add(sibling);
        InvokeWorkbench(desk, "AddBranch", child, false);
        var leaf = WorkspaceMaterials.FindOwner(controller.State)!.WorkspaceMaterials!.Items.Keys.Except(known).Single();
        await Until(() => CardReady(desk, leaf), "Nested graph branch did not appear");
        Assert(controller.WorkbenchLayout.Connections.Any(e => e.IsBranch && e.From == child && e.To == leaf),
            "Adding a nested branch lost its parent");
        Assert(new[] { root, child, sibling, leaf }.All(id =>
                   controller.FindWorkbenchMaterial(id)?.Kind == WorkspaceMaterialKinds.Node) &&
               desk.HostedPaperCount == 3,
            "Graph nodes did not remain material items independent of ordinary paper editors");

        InvokeWorkbench(desk, "CreateConnection", root, "note-b", false);
        var free = controller.WorkbenchLayout.Connections.Single(e => e.From == root && e.To == "note-b");
        Assert(!free.IsBranch, "Free connection was incorrectly saved as a parent-child branch");
        InvokeWorkbench(desk, "CreateConnection", root, "note-b", false);
        Assert(controller.WorkbenchLayout.Connections.Count(e => e.From == root && e.To == "note-b") == 1,
            "Repeated connection command created duplicate links");
        InvokeWorkbench(desk, "UndoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Connections.All(e => e.Id != free.Id),
            "Undo did not remove the last free connection");
        InvokeWorkbench(desk, "RedoWorkspaceAction");
        Assert(controller.WorkbenchLayout.Connections.Any(e => e.Id == free.Id),
            "Redo did not restore the free connection with its original identity");
        Assert(controller.FindPaper("todo-a")!.Items.First().LinkedPaperId == "note-a",
            "Editing free graph links changed an existing Todo business association");
        InvokeWorkbench(desk, "FitActive");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveImage(desk, "workbench-graph.png");

        controller.SetWorkbenchMaterialCollapsed(root, true);
        controller.SetWorkbenchMaterialCollapsed(child, true);
        InvokeWorkbench(desk, "ApplySceneVisibility");
        Assert(CardFrame(desk, root).Visibility == Visibility.Visible &&
               new[] { child, sibling, leaf }.All(id => CardFrame(desk, id).Visibility == Visibility.Collapsed) &&
               CardFrame(desk, "note-b").Visibility == Visibility.Visible,
            "Branch collapse did not hide descendants while retaining a freely connected paper");
        InvokeWorkbench(desk, "RevealCard", Member(desk, "ActiveScene"), leaf);
        Assert(controller.FindWorkbenchMaterial(root)?.Collapsed == false &&
               controller.FindWorkbenchMaterial(child)?.Collapsed == false &&
               CardFrame(desk, leaf).Visibility == Visibility.Visible,
            "Navigating to a hidden descendant did not expand its branch ancestors");

        controller.SetWorkbenchMaterialCollapsed(root, true);
        InvokeWorkbench(desk, "ApplySceneVisibility");
        controller.SaveNow(sync: true);
        var saved = WorkspaceMaterials.FindOwner(StateStore.ReadWorkbenchOverview(store.FilePath)!)!.WorkspaceMaterials!;
        Assert(saved.Items[root].Collapsed && saved.Items[root].Content == "Root idea" &&
               saved.Layout.Connections.Count(e => e.IsBranch) == 3 &&
               saved.Layout.Connections.Any(e => e.Id == free.Id && !e.IsBranch),
            "Graph nodes, collapse state, or connections did not persist in the materials note");
        controller.ToggleWorkbench();
        controller.ToggleWorkbench();
        desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && ActiveCards(desk).Contains(leaf),
            "Reopening the workspace did not restore all graph nodes");
        Assert(CardFrame(desk, root).Visibility == Visibility.Visible &&
               CardFrame(desk, leaf).Visibility == Visibility.Collapsed &&
               CardFrame(desk, "note-b").Visibility == Visibility.Visible,
            "Reopening the workspace did not restore saved branch visibility");

        controller.DeleteWorkbenchMaterial(root);
        await Until(() => !ActiveCards(desk).Contains(root), "Deleting a graph node left its canvas card behind");
        Assert(controller.WorkbenchLayout.Connections.All(e => e.From != root && e.To != root) &&
               new[] { child, sibling, leaf }.All(id => controller.FindWorkbenchMaterial(id) != null) &&
               CardFrame(desk, child).Visibility == Visibility.Visible,
            "Deleting a parent left dangling connections or deleted surviving branch content");
        controller.ToggleWorkbench();
        controller.SaveNow(sync: true);
    }

    private static void SendNodeKey(WorkbenchWindow desk, TextBox editor, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(desk), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(args);
        Assert(args.Handled, "Node branch shortcut was not handled by its editor");
    }
}
