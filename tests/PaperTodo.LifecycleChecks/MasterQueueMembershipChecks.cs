using System.Diagnostics;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class MasterQueueMembershipChecks
{
    private const string SourceKey = "|" + DeepCapsuleSides.Right;

    internal static void Prepare(AppState state)
    {
        state.ShowDeepCapsuleWhileExpanded = false;
        state.RememberDeepCapsuleExpandedPosition = true;
        state.CapsuleCollapseAllActiveQueues[SourceKey] = true;
        state.Papers[3].IsVisible = false;
        state.Papers[4].IsCollapsed = false;
        foreach (var paper in state.Papers.Skip(3))
        {
            paper.DeepCapsuleExpandedX = paper.X + 30;
            paper.DeepCapsuleExpandedY = paper.Y + 20;
            paper.DeepCapsuleExpandedWidth = paper.Width;
            paper.DeepCapsuleExpandedHeight = paper.Height;
            paper.DeepCapsuleExpandedDpiScale = WindowWorkAreaHelper.SystemDpiScale().ScaleX;
            paper.DeepCapsuleExpandedSide = DeepCapsuleSides.Right;
            paper.DeepCapsuleExpandedMonitorDeviceName = "";
        }
    }

    internal static async Task RunMembership(
        AppController controller,
        Dictionary<string, PaperWindow> windows,
        StateStore store)
    {
        var hidden = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
        var expanded = controller.State.Papers.Single(paper => paper.Id == "fixture-4");
        Require(!hidden.IsVisible && !expanded.IsCollapsed &&
                windows[expanded.Id].HasExpandedPaperSurface &&
                controller.VisibleDeepCapsuleCount() == 3,
            "membership fixture did not exclude hidden and expanded non-slot papers");
        var retained = new[] { hidden, expanded }.ToDictionary(paper => paper.Id, Placement.Capture);
        const double sourceMargin = 83;
        controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, sourceMargin);
        Require(controller.TryBeginMasterCapsuleQueueTransfer(
                "", EdgeCapsuleEdge.Right, sourceMargin, out var transfer),
            "mixed-membership master transfer did not start");
        Require(transfer.PaperIds.SequenceEqual(new[] { "fixture-0", "fixture-1", "fixture-2" }),
            "master snapshot included papers that do not occupy source slots");
        Require(controller.CommitMasterCapsuleQueueTransfer(transfer, LeftDrop()),
            "mixed-membership master transfer did not commit");
        Require(controller.State.Papers.Take(3).All(paper => paper.CapsuleSide == DeepCapsuleSides.Left),
            "master transfer did not move the live source members");
        Require(controller.State.Papers.Select(paper => paper.Id).SequenceEqual(
                Enumerable.Range(0, 5).Select(index => "fixture-" + index)),
            "master transfer reordered retained non-slot papers");
        foreach (var paper in new[] { hidden, expanded })
        {
            Require(Placement.Capture(paper) == retained[paper.Id],
                "master transfer changed a non-slot paper's queue or remembered geometry");
            Require(controller.TryGetRememberedDeepCapsuleExpandedGeometry(
                    paper, paper.Width, paper.Height, out _),
                "retained paper can no longer restore its source expanded geometry");
        }
        Require(!hidden.IsVisible && !expanded.IsCollapsed,
            "master transfer changed retained paper presentation state");
        RequireSourceMargin(controller.State, sourceMargin);
        RequireSourceCollapse(controller.State, expected: true);
        await Until(() => transfer.PaperIds.All(id => windows[id].IsCollapseAllRetracted),
            "mixed-membership target retained source collapse state");
        controller.SaveNow(sync: true);
        var saved = store.Load();
        RequireSourceMargin(saved, sourceMargin);
        RequireSourceCollapse(saved, expected: true);
        Require(saved.CapsuleCollapseAllActiveQueues.TryGetValue(
                "|" + DeepCapsuleSides.Left, out var targetCollapsed) && targetCollapsed,
            "moved live members did not retain their source collapse state");
        foreach (var paper in saved.Papers.Where(paper => retained.ContainsKey(paper.Id)))
            Require(Placement.Capture(paper) == retained[paper.Id],
                "retained source geometry did not survive persistence");
    }

    internal static void RunAbsentMarginCancellation(AppController controller, StateStore store)
    {
        // A vertical preview creates the dictionary entry before the horizontal transfer begins.
        // Cancellation must restore its absence, not persist the effective default as a new key.
        controller.State.DeepCapsuleQueueStartTopMargins.Remove(SourceKey);
        var originalMargin = controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right);
        Require(Math.Abs(originalMargin - 48) < 0.01, "default source anchor fixture changed");
        controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, 68);
        Require(controller.State.DeepCapsuleQueueStartTopMargins.TryGetValue(SourceKey, out var preview) &&
                Math.Abs(preview - 68) < 0.01,
            "vertical preview did not create the source anchor");
        Require(controller.TryBeginMasterCapsuleQueueTransfer(
                "", EdgeCapsuleEdge.Right, originalMargin, out var transfer,
                rollbackHadStartTopMargin: false),
            "absent-anchor transfer did not start");
        Require(!transfer.SourceHadStartTopMargin &&
                !controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(SourceKey),
            "begin did not restore the pre-preview absence of a source anchor");
        controller.SaveNow(sync: true);
        Require(!store.Load().DeepCapsuleQueueStartTopMargins.ContainsKey(SourceKey),
            "save during transfer persisted a preview-only anchor");
        controller.CancelMasterCapsuleQueueTransfer(transfer);
        Require(!controller.State.DeepCapsuleQueueStartTopMargins.ContainsKey(SourceKey) &&
                Math.Abs(controller.DeepCapsuleStartTopMarginForQueue("", EdgeCapsuleEdge.Right) - originalMargin) < 0.01,
            "cancellation created a source anchor that did not exist before the gesture");
        controller.SaveNow(sync: true);
        Require(!store.Load().DeepCapsuleQueueStartTopMargins.ContainsKey(SourceKey),
            "cancelled absent source anchor did not survive save and reload");
    }

    internal static async Task RunMutations(
        AppController controller,
        Dictionary<string, PaperWindow> windows,
        StateStore store)
    {
        var commands = new PaperCommandService(controller);
        const double sourceMargin = 79;
        controller.SetDeepCapsuleStartTopMargin("", EdgeCapsuleEdge.Right, sourceMargin);

        await MutationCancels("creation", () =>
        {
            var created = controller.CreatePaper(PaperTypes.Note, show: false,
                sourcePaper: controller.State.Papers[0])
                ?? throw new InvalidOperationException("membership fixture could not create a paper");
            Require(controller.ApplyPaperPresentation(created, PaperPresentationAction.Collapse, activate: false),
                "membership fixture could not collapse the new hidden paper");
            controller.ShowPaper(created, activate: false);
            Require(created.IsVisible && created.IsCollapsed,
                "new paper did not enter the source capsule queue");
        });
        var hidden = controller.State.Papers.Single(paper => paper.Id == "fixture-1");
        controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
        RequireSourceCollapse(controller.State, expected: true);
        await MutationCancels("hide", () => controller.HidePaper(hidden));
        Require(!hidden.IsVisible, "cancelled transfer undid the requested hide");
        controller.ToggleCapsuleCollapseAllActive("", EdgeCapsuleEdge.Right);
        await Until(() => windows.Values.All(window => !window.IsCollapseAllRetracted),
            "source queue reopened after collapsed-source cancellation");
        await MutationCancels("deletion", () =>
            commands.DeletePaper("fixture-2", PaperOperationContext.Mcp()));
        Require(controller.State.Papers.All(paper => paper.Id != "fixture-2"),
            "cancelled transfer resurrected the deleted source member");
        var reassigned = controller.State.Papers.Single(paper => paper.Id == "fixture-3");
        await MutationCancels("reassignment", () =>
            controller.MoveCapsuleToQueue(reassigned, "", DeepCapsuleSides.Left, LeftDrop()));
        Require(reassigned.CapsuleSide == DeepCapsuleSides.Left,
            "cancelled transfer undid the requested queue reassignment");
        await MutationCancels("reorder", () =>
            controller.ReorderDeepCapsule(controller.State.Papers.Single(paper => paper.Id == "fixture-0"), 1));

        Require(controller.TryBeginMasterCapsuleQueueTransfer(
                "", EdgeCapsuleEdge.Right, sourceMargin, out var contentTransfer),
            "content mutation transfer did not start");
        const string editedContent = "unrelated content changed during the native drag loop";
        await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            commands.WriteNote(new WriteNoteRequest
            {
                PaperId = hidden.Id, Content = editedContent, Mode = NoteWriteMode.Replace
            }, PaperOperationContext.Mcp()), DispatcherPriority.Background);
        Require(hidden.Content == editedContent,
            "queued unrelated note mutation did not run during the transfer");
        Require(controller.CommitMasterCapsuleQueueTransfer(contentTransfer, LeftDrop()),
            "unrelated content mutation cancelled an unchanged source queue");
        Require(contentTransfer.PaperIds.All(id =>
                controller.State.Papers.Single(paper => paper.Id == id).CapsuleSide == DeepCapsuleSides.Left),
            "unchanged source membership did not transfer after a content mutation");
        Require(!hidden.IsVisible && hidden.CapsuleSide == DeepCapsuleSides.Right,
            "content mutation pulled a hidden non-member into the transfer");
        controller.SaveNow(sync: true);
        var saved = store.Load();
        Require(saved.Papers.Single(paper => paper.Id == hidden.Id).Content == editedContent,
            "content mutation was lost when the master transfer committed");
        RequireSourceMargin(saved, sourceMargin);

        async Task MutationCancels(string name, Action mutate)
        {
            var sourceWasCollapsed = controller.State.CapsuleCollapseAllActiveQueues.TryGetValue(
                SourceKey, out var collapsed) && collapsed;
            Require(controller.TryBeginMasterCapsuleQueueTransfer(
                    "", EdgeCapsuleEdge.Right, sourceMargin, out var transfer),
                name + " transfer did not start");
            mutate();
            var order = controller.State.Papers.Select(paper => paper.Id).ToArray();
            var placements = controller.State.Papers.ToDictionary(paper => paper.Id, Placement.Capture);
            Require(!controller.CommitMasterCapsuleQueueTransfer(transfer, LeftDrop()),
                name + " allowed a stale source membership snapshot to commit");
            Require(controller.State.Papers.Select(paper => paper.Id).SequenceEqual(order) &&
                    controller.State.Papers.All(paper => Placement.Capture(paper) == placements[paper.Id]),
                name + " cancellation changed source order, queue tags or geometry");
            RequireSourceMargin(controller.State, sourceMargin);
            RequireSourceCollapse(controller.State, sourceWasCollapsed);
            await Until(() => controller.State.Papers.All(paper =>
                    !windows.TryGetValue(paper.Id, out var window) ||
                    window.IsCollapseAllRetracted == (sourceWasCollapsed && paper.IsVisible &&
                        paper.CapsuleSide == DeepCapsuleSides.Right)),
                name + " cancellation presentation");
            controller.SaveNow(sync: true);
            var restored = store.Load();
            RequireSourceMargin(restored, sourceMargin);
            RequireSourceCollapse(restored, sourceWasCollapsed);
        }
    }

    private static DeviceScreenPoint LeftDrop()
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor),
            "membership fixture needs a real primary monitor");
        return new DeviceScreenPoint(monitor.WorkArea.Left + monitor.WorkArea.Width / 4.0,
            monitor.WorkArea.Top + monitor.WorkArea.Height / 3.0);
    }

    private static void RequireSourceMargin(AppState state, double expected) =>
        Require(state.DeepCapsuleQueueStartTopMargins.TryGetValue(SourceKey, out var actual) &&
                Math.Abs(actual - expected) < 0.01,
            "transfer lost the retained source queue's anchor");

    private static void RequireSourceCollapse(AppState state, bool expected) =>
        Require(expected
                ? state.CapsuleCollapseAllActiveQueues.TryGetValue(SourceKey, out var active) && active
                : !state.CapsuleCollapseAllActiveQueues.ContainsKey(SourceKey),
            "transfer changed the persisted source collapse state");

    private readonly record struct Placement(
        string Side, string Monitor, double X, double Y, double Width, double Height,
        double? ExpandedX, double? ExpandedY, double? ExpandedWidth, double? ExpandedHeight,
        double? ExpandedScale, string ExpandedSide, string ExpandedMonitor)
    {
        internal static Placement Capture(PaperData paper) => new(
            paper.CapsuleSide, paper.CapsuleMonitorDeviceName, paper.X, paper.Y, paper.Width, paper.Height,
            paper.DeepCapsuleExpandedX, paper.DeepCapsuleExpandedY,
            paper.DeepCapsuleExpandedWidth, paper.DeepCapsuleExpandedHeight,
            paper.DeepCapsuleExpandedDpiScale, paper.DeepCapsuleExpandedSide,
            paper.DeepCapsuleExpandedMonitorDeviceName);
    }

    private static async Task Until(Func<bool> ready, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 12) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
