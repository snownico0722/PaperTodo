using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static async Task RunGroups()
    {
        var initial = FixtureState();
        initial.UseCapsuleMode = false;
        initial.UseDeepCapsuleMode = false;
        var store = new StateStore();
        store.SaveJsonSync(store.SerializeState(initial), 1);
        using var controller = new AppController();
        await controller.StartAsync(createDefaultPaper: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        controller.ToggleWorkbench();
        var desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3, "Group fixture did not mount its source papers");
        var nodeId = controller.CreateWorkbenchMaterial(WorkspaceMaterialKinds.Node,
            "Grouped idea", "Grouped idea", "", new WorkbenchPoint(1600, 100));
        await Until(() => CardReady(desk, nodeId) && controller.WorkbenchLayout.Cards.ContainsKey(nodeId),
            "Node was not ready for grouping");

        var scene = Member(desk, "ActiveScene");
        var select = typeof(WorkbenchWindow).GetMethod("SelectCardsInRectangle", Private)!;
        select.Invoke(desk, [scene, CardScreenCenter(desk, scene, "note-a"), null]);
        select.Invoke(desk, [scene, CardScreenCenter(desk, scene, nodeId), new[] { "note-a" }]);
        var selected = (HashSet<string>)Member(desk, "_selectedCards");
        Assert(selected.SetEquals(["note-a", nodeId]),
            "Canvas rectangle selection did not select the intended paper and material");

        var selectedBefore = selected.ToDictionary(id => id, id => controller.WorkbenchLayout.Cards[id].Bounds);
        var moveSelection = (Thumb)Member(ActiveCards(desk)["note-a"]!, "Move");
        moveSelection.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Assert(Panel.GetZIndex(CardFrame(desk, "note-a")) == 13 &&
               Panel.GetZIndex(CardFrame(desk, nodeId)) == 12 &&
               Panel.GetZIndex(CardFrame(desk, "todo-a")) == 2,
            "Dragged selection was not raised above neighboring papers");
        moveSelection.RaiseEvent(new DragDeltaEventArgs(60, 35) { RoutedEvent = Thumb.DragDeltaEvent });
        moveSelection.RaiseEvent(new DragCompletedEventArgs(60, 35, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(Panel.GetZIndex(CardFrame(desk, "note-a")) == 2 &&
               Panel.GetZIndex(CardFrame(desk, nodeId)) == 2,
            "Dragging selection left floating elevated layers behind");
        await Until(() => controller.WorkbenchLayout.Cards["note-a"].X != selectedBefore["note-a"].X,
            "Dragging a multiselection did not store its new position");
        Assert(selected.All(id => Math.Abs(controller.WorkbenchLayout.Cards[id].X - selectedBefore[id].X - 60) < .001 &&
                                  Math.Abs(controller.WorkbenchLayout.Cards[id].Y - selectedBefore[id].Y - 35) < .001),
            "Dragging ungrouped selected cards changed their relative positions");
        InvokeWorkbench(desk, "CreateGroupFromSelection");
        var group = controller.WorkbenchLayout.Groups.Values.Single();
        var groupId = group.Id;
        Assert(group.Members.ToHashSet().SetEquals(["note-a", nodeId]),
            "Manual grouping lost a selected paper or material");

        InvokeWorkbench(desk, "ToggleGroupCollapsed", groupId);
        Assert(controller.WorkbenchLayout.Groups[groupId].Collapsed &&
               CardFrame(desk, "note-a").Visibility == Visibility.Collapsed &&
               CardFrame(desk, nodeId).Visibility == Visibility.Collapsed &&
               CardFrame(desk, "todo-a").Visibility == Visibility.Visible,
            "Collapsing a group did not hide exactly its member cards");

        // Drag the real collapsed group header. Both saved member positions move by
        // one displacement while the group's internal arrangement remains unchanged.
        var before = group.Members.ToDictionary(id => id, id => controller.WorkbenchLayout.Cards[id].Bounds);
        var groupThumb = GroupThumb(desk, group.Title);
        groupThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Assert(group.Members.All(id => Panel.GetZIndex(CardFrame(desk, id)) == 12),
            "Moving group members did not raise them above other papers");
        groupThumb.RaiseEvent(new DragDeltaEventArgs(130, 80) { RoutedEvent = Thumb.DragDeltaEvent });
        groupThumb.RaiseEvent(new DragCompletedEventArgs(130, 80, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(group.Members.All(id => Panel.GetZIndex(CardFrame(desk, id)) == 2),
            "Moving a group did not restore member Z order");
        await Until(() => controller.WorkbenchLayout.Cards["note-a"].X != before["note-a"].X,
            "Dragging a collapsed group did not store its new position");
        var after = group.Members.ToDictionary(id => id, id => controller.WorkbenchLayout.Cards[id].Bounds);
        Assert(Math.Abs(after["note-a"].X - before["note-a"].X - 130) < .001 &&
               Math.Abs(after["note-a"].Y - before["note-a"].Y - 80) < .001 &&
               Math.Abs(after[nodeId].X - before[nodeId].X - 130) < .001 &&
               Math.Abs(after[nodeId].Y - before[nodeId].Y - 80) < .001,
            "Group drag changed members by different amounts");

        // A canceled second drag restores both members and creates no extra undo step.
        var undo = (System.Collections.ICollection)Member(desk, "_undo");
        var undoCount = undo.Count;
        groupThumb = GroupThumb(desk, group.Title);
        groupThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        groupThumb.RaiseEvent(new DragDeltaEventArgs(45, -20) { RoutedEvent = Thumb.DragDeltaEvent });
        groupThumb.RaiseEvent(new DragCompletedEventArgs(45, -20, true) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(group.Members.All(id => controller.WorkbenchLayout.Cards[id].Bounds == after[id]) &&
               undo.Count == undoCount, "Canceled group drag changed saved geometry or undo history");

        var scale = (ScaleTransform)Member(desk, "_scale");
        var pan = (TranslateTransform)Member(desk, "_pan");
        var camera = (Zoom: scale.ScaleX, X: pan.X, Y: pan.Y);
        InvokeWorkbench(desk, "FocusGroup", groupId);
        Assert(CardFrame(desk, "note-a").Visibility == Visibility.Visible &&
               CardFrame(desk, nodeId).Visibility == Visibility.Visible &&
               CardFrame(desk, "todo-a").Visibility == Visibility.Collapsed &&
               controller.WorkbenchLayout.Groups[groupId].Collapsed,
            "Group focus did not temporarily reveal the selected group without changing its saved collapse state");
        InvokeWorkbench(desk, "LeaveGroupFocus");
        Assert(CardFrame(desk, "note-a").Visibility == Visibility.Collapsed &&
               CardFrame(desk, "todo-a").Visibility == Visibility.Visible &&
               Math.Abs(scale.ScaleX - camera.Zoom) < .001 &&
               Math.Abs(pan.X - camera.X) < .001 && Math.Abs(pan.Y - camera.Y) < .001,
            "Leaving group focus did not restore the prior visibility and camera");

        controller.SaveNow(sync: true);
        var saved = StateStore.ReadWorkbenchOverview(store.FilePath)!;
        var savedGroup = WorkspaceMaterials.FindOwner(saved)!.WorkspaceMaterials!.Layout.Groups[groupId];
        Assert(savedGroup.Collapsed && savedGroup.Members.ToHashSet().SetEquals(["note-a", nodeId]),
            "Group membership and collapsed state did not persist with the materials note");
        controller.ToggleWorkbench();
        controller.ToggleWorkbench();
        desk = (WorkbenchWindow)Member(controller, "_workbenchWindow");
        await Until(() => desk.HostedPaperCount == 3 && CardReady(desk, nodeId),
            "Reopening the workspace did not restore group members");
        Assert(CardFrame(desk, "note-a").Visibility == Visibility.Collapsed &&
               CardFrame(desk, nodeId).Visibility == Visibility.Collapsed,
            "Reopening the workspace expanded a saved collapsed group");

        // Deleting the last members from another presentation invalidates the focused
        // group. The canvas must leave that temporary focus before the next navigation.
        InvokeWorkbench(desk, "FocusGroup", groupId);
        controller.DeleteWorkbenchMaterial(nodeId);
        controller.DeletePaper(controller.FindPaper("note-a")!);
        await Until(() => !controller.WorkbenchLayout.Groups.ContainsKey(groupId) && desk.HostedPaperCount == 2,
            "Deleting group members left a stale group or borrowed editor");
        Assert(typeof(WorkbenchWindow).GetField("_focusedGroupId", Private)!.GetValue(desk) == null,
            "Deleting a focused group's final member left the canvas in an invalid focus");
        Assert(CardFrame(desk, "todo-a").Visibility == Visibility.Visible,
            "Other cards stayed hidden after the focused group disappeared");
        controller.ToggleWorkbench();
        controller.SaveNow(sync: true);
    }

    private static void InvokeWorkbench(WorkbenchWindow desk, string method, params object?[] args) =>
        typeof(WorkbenchWindow).GetMethod(method, Private)!.Invoke(desk, args);

    private static Border CardFrame(WorkbenchWindow desk, string id) =>
        (Border)Member(ActiveCards(desk)[id]!, "Frame");

    private static Rect CardScreenCenter(WorkbenchWindow desk, object scene, string id)
    {
        var card = ActiveCards(desk)[id]!;
        var bounds = ((WorkbenchRect)Member(card, "Bounds")).Offset((WorkbenchPoint)Member(scene, "Origin"));
        var scale = (ScaleTransform)Member(desk, "_scale");
        var pan = (TranslateTransform)Member(desk, "_pan");
        return new Rect(bounds.Center.X * scale.ScaleX + pan.X - 2,
            bounds.Center.Y * scale.ScaleY + pan.Y - 2, 4, 4);
    }

    private static Thumb GroupThumb(WorkbenchWindow desk, string title)
    {
        var canvas = (Canvas)Member(Member(desk, "ActiveScene"), "Canvas");
        return VisualDescendants<Thumb>(canvas).Single(t => Equals(t.Tag, title));
    }
}
