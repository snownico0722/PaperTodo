using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;

// Runs in LifecycleChecks' existing isolated-data child, never against user data.
internal static class NativeFreeQueueReviewChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string A = "|right#qa", B = "|right#qb", C = "|right#qc";

    internal static void Prepare(AppState state)
    {
        state.Papers.Clear();
        state.EnableAnimations = false;
        state.UseCapsuleMode = state.UseDeepCapsuleMode = state.UseCapsuleFreeQueues = true;
        state.UseCapsuleCollapseAll = true;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        for (var i = 0; i < 11; i++)
            state.Papers.Add(new PaperData
            {
                Id = "review-" + i, Type = PaperTypes.Note, Content = "preserved " + i,
                IsVisible = true, IsCollapsed = true, Width = 300, Height = 240,
                X = 60, Y = 60, CapsuleMonitorDeviceName = "",
                CapsuleSide = i == 10 ? DeepCapsuleSides.Left : DeepCapsuleSides.Right,
                CapsuleQueueId = i < 6 ? "qa" : i < 8 ? "qb" : i < 10 ? "qc" : "source"
            });
        state.DeepCapsuleQueueStartTopMargins.Clear();
        state.DeepCapsuleQueueStartTopMargins[A] = 100;
        state.DeepCapsuleQueueStartTopMargins[B] = 200;
        state.DeepCapsuleQueueStartTopMargins[C] = 350;
        state.DeepCapsuleQueueStartTopMargins["|left#source"] = 120;
        state.CapsuleCollapseAllActiveQueues.Clear();
        state.CapsuleCollapseAllActiveQueues[A] = true;
    }

    internal static async Task Run(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store)
    {
        CheckAnchorReconstruction();
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "review monitor");
        CheckDropGhostReset(monitor);
        var originalContents = c.State.Papers.ToDictionary(p => p.Id, p => p.Content);
        await CheckPostDragPreview(c, windows, monitor);
        await CheckSingleQueueGhost(c, windows);
        await CheckDisplacedInsertion(c, windows, store, monitor);
        await CheckDragPreviewUpdates(c, windows, monitor);
        await CheckYieldAnimations(c, windows, monitor);
        await CheckMasterAnimationStacking(c, windows);
        await CheckVacatedSourcePreview(c, windows, store, monitor);
        await CheckIndependentGroupPreview(c, windows, store, monitor);
        await CheckFoldedDropAndShortcuts(c, monitor);
        await CheckDisconnectedGesturesAndMerge(c, windows, store, monitor);
        Require(c.State.Papers.Count == originalContents.Count &&
                c.State.Papers.All(p => originalContents[p.Id] == p.Content),
            "queue operations changed paper identities or note contents");
        Console.WriteLine("PASS free-queue review: displaced insertion, anchor roundtrip, folded drops, shortcut geometry, post-disconnect gestures and merge identity");
    }

    private static async Task CheckPostDragPreview(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, MonitorGeometry monitor)
    {
        var free = c.State.UseCapsuleFreeQueues;
        var master = c.State.UseCapsuleCollapseAll;
        var preview = c.State.ExperimentalEdgeCapsuleHoverPreview;
        var prediction = c.State.ExperimentalEdgeCapsuleHoverIntent;
        var margins = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        var folded = new Dictionary<string, bool>(c.State.CapsuleCollapseAllActiveQueues);
        var order = c.State.Papers.ToList();
        var identities = order.Select(paper => (Paper: paper, Monitor: paper.CapsuleMonitorDeviceName,
            Side: paper.CapsuleSide, QueueId: paper.CapsuleQueueId)).ToArray();
        var cursorSaved = WindowNative.TryGetCursorScreenPosition(out var savedCursor);
        var source = windows["review-6"];
        var other = windows["review-10"];
        var outside = new DeviceScreenPoint(
            monitor.WorkArea.Left + monitor.WorkArea.Width / 2,
            monitor.WorkArea.Top + monitor.WorkArea.Height / 2);

        static DeviceScreenPoint Hit(PaperWindow window)
        {
            Require(window.TryGetEdgeCapsuleInteractiveGeometry(out var geometry),
                "preview fixture needs a real interactive capsule");
            return new(geometry.Bounds.Left + geometry.Bounds.Width / 2,
                geometry.Bounds.Top + geometry.Bounds.Height / 2);
        }
        static void Move(DeviceScreenPoint point) =>
            Require(SetPreviewCursorPos((int)point.X, (int)point.Y), "move native preview pointer");
        void RestoreQueues()
        {
            c.State.DeepCapsuleQueueStartTopMargins = new(margins, StringComparer.Ordinal);
            c.State.CapsuleCollapseAllActiveQueues = new(folded, StringComparer.Ordinal);
            c.State.Papers = order.ToList();
            foreach (var row in identities)
            {
                row.Paper.CapsuleMonitorDeviceName = row.Monitor;
                row.Paper.CapsuleSide = row.Side;
                row.Paper.CapsuleQueueId = row.QueueId;
            }
        }

        try
        {
            // The release debounce is required even when optional motion prediction is disabled.
            c.State.ExperimentalEdgeCapsuleHoverIntent = false;
            foreach (var outcome in new[] { "stay", "other", "leave", "master" })
            {
                c.State.ExperimentalEdgeCapsuleHoverPreview = false;
                c.CloseEdgeCapsulePreviewForDrag(source);
                Move(outside);
                // A real queue drop may merge/reorder, and switching legacy mode prunes fold keys.
                // Start every scenario from the same layout and restore that complete state below.
                RestoreQueues();
                c.State.UseCapsuleCollapseAll = outcome == "master";
                c.State.UseCapsuleFreeQueues = outcome != "stay";
                c.ArrangeDeepCapsules(false);
                await Idle();
                if (outcome == "master")
                {
                    var sourceQueue = c.QueueKeyForPaper(source.EdgeCapsulePreviewPaper);
                    var queueMaster = c.MasterCapsuleForQueue(sourceQueue)!;
                    Require(WindowNative.TryGetWindowDeviceBounds(queueMaster, out var bounds),
                        "master preview fixture needs a real window");
                    Move(new(bounds.Right - 10, bounds.Top + bounds.Height / 2));
                    var (_, session, down) = PressMaster(queueMaster);
                    var release = new DeviceScreenPoint(down.X, down.Y + 24 * monitor.DpiScaleY);
                    Require((bool)Call(queueMaster, "ContinueMasterDrag", release, session)!,
                        "master preview fixture entered a real queue drag");
                    Move(release);
                    c.State.ExperimentalEdgeCapsuleHoverPreview = true;
                    Call(queueMaster, "FinishMasterGesture", true, true, release);
                    Require(c.QueueKeyForPaper(source.EdgeCapsulePreviewPaper) == sourceQueue,
                        "master preview fixture moves the independent queue without merging");
                    // Placement reconciliation runs at Render; Send still sees the drag preview.
                    // Drain the real layout work before selecting the landed interactive rectangle.
                    await Idle();
                    var hit = Hit(source);
                    Move(hit);
                    c.NotifyEdgeCapsulePreviewPhysicalPointer(source, hit);
                }
                else
                {
                    var center = Hit(source);
                    // The 46-DIP host has only 30 DIP of interactive height. Starting at its
                    // center and releasing 16 DIP lower lands in the shadow after a legacy drop.
                    // Keep both ends inside the real capsule while still crossing the drag threshold.
                    var down = new DeviceScreenPoint(center.X, center.Y - 8 * monitor.DpiScaleY);
                    var release = new DeviceScreenPoint(down.X, down.Y + 16 * monitor.DpiScaleY);
                    Move(down);
                    source.GetType().GetField("_nativeQueuePointerDownKeys", Private)!
                        .SetValue(source, outcome == "other" ? 8 : 0);
                    Call(source, "OnEdgeCapsulePointerPressed", down);
                    c.State.ExperimentalEdgeCapsuleHoverPreview = true;
                    Move(release);
                    Call(source, "OnEdgeCapsulePointerMoved", release, true);
                    Require(source.IsDeepCapsuleReorderDragInProgress,
                        outcome + " fixture entered a real capsule drag");
                    Call(source, "OnEdgeCapsulePointerReleased", release);
                }
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
                Require(WindowNative.TryGetCursorScreenPosition(out var releasedPointer) &&
                        source.IsEdgeCapsuleInteractiveAt(releasedPointer),
                    outcome + " fixture must leave the physical cursor inside the landed capsule");
                Require(!source.IsEdgeCapsulePreviewOpen,
                    outcome + " release must not immediately reopen its preview");

                var target = outcome == "other" ? other : source;
                if (outcome is "other" or "leave")
                {
                    var point = outcome == "other" ? Hit(other) : outside;
                    Move(point);
                    c.NotifyEdgeCapsulePreviewPhysicalPointer(target, point);
                }
                if (outcome == "leave")
                {
                    await Task.Delay(250);
                    Require(windows.Values.All(window => !window.IsEdgeCapsulePreviewOpen),
                        "leaving the queue cancels the delayed preview");
                    continue;
                }

                var watch = Stopwatch.StartNew();
                while (!target.IsEdgeCapsulePreviewOpen)
                {
                    Require(outcome != "other" || !source.IsEdgeCapsulePreviewOpen,
                        "moving to another capsule must never reopen the dragged source");
                    if (watch.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        var hasCursor = WindowNative.TryGetCursorScreenPosition(out var currentPointer);
                        var hasGeometry = target.TryGetEdgeCapsuleInteractiveGeometry(out var currentGeometry);
                        throw new TimeoutException(outcome + " preview did not open after pointer residence; " +
                            $"cursor={hasCursor}:{currentPointer}; interactive={hasGeometry}:{currentGeometry.Bounds}; " +
                            $"intent={Field<object?>(c, "_edgeCapsulePreviewActivationIntent")}; " +
                            $"eligibility={target.EdgeCapsulePreviewEligibilityTrace()}");
                    }
                    await Task.Delay(10);
                }
                Require(outcome != "other" || !source.IsEdgeCapsulePreviewOpen,
                    "the new physical target owns the preview after a Ctrl queue drag");
            }
            Console.WriteLine("PASS drag-release preview: legacy stay, Ctrl transfer, free-queue leave cancellation and master queue release");
        }
        finally
        {
            c.State.ExperimentalEdgeCapsuleHoverPreview = false;
            c.CloseEdgeCapsulePreviewForDrag(source);
            c.State.UseCapsuleFreeQueues = free;
            c.State.UseCapsuleCollapseAll = master;
            c.State.ExperimentalEdgeCapsuleHoverIntent = prediction;
            RestoreQueues();
            c.ArrangeDeepCapsules(false);
            c.State.ExperimentalEdgeCapsuleHoverPreview = preview;
            if (cursorSaved) Move(savedCursor);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool SetPreviewCursorPos(int x, int y);

    private static async Task CheckSingleQueueGhost(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var free = c.State.UseCapsuleFreeQueues;
        var master = c.State.UseCapsuleCollapseAll;
        var folded = new Dictionary<string, bool>(c.State.CapsuleCollapseAllActiveQueues, StringComparer.Ordinal);
        PaperData? ghostOwner = null;
        try
        {
            c.State.UseCapsuleFreeQueues = false;
            c.State.UseCapsuleCollapseAll = false;
            c.ArrangeDeepCapsules(false);
            await Idle();

            var queue = c.State.Papers.Where(p => p.IsVisible &&
                p.CapsuleSide == DeepCapsuleSides.Right && windows.ContainsKey(p.Id)).ToList();
            Require(queue.Count >= 2, "single-queue fixture needs two right-edge members");
            ghostOwner = queue[0];
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                ghostOwner.CapsuleMonitorDeviceName, out var geometry), "single-queue monitor");
            // Floating into the other physical edge opens its projected target
            // slot and closes the source gap without changing persisted membership.
            var targetPaper = c.State.Papers.First(p => p.IsVisible &&
                p.CapsuleSide == DeepCapsuleSides.Left && windows.ContainsKey(p.Id));
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                targetPaper.CapsuleMonitorDeviceName, out var targetGeometry),
                "ordinary cross-edge target monitor");
            var targetCount = c.State.Papers.Count(p => p.IsVisible &&
                p.CapsuleSide == DeepCapsuleSides.Left &&
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(p.CapsuleMonitorDeviceName) ==
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(targetPaper.CapsuleMonitorDeviceName));
            var targetFirst = EdgeCapsuleLayout.TopForIndex(0,
                c.DeepCapsuleStartTopMarginFor(targetPaper),
                targetGeometry.LocalWorkAreaDip, targetCount + 1, c.DeepCapsuleGap);
            var targetPointer = Point(targetGeometry,
                targetFirst - PaperLayoutDefaults.CapsuleHeight / 2, left: true);
            c.PreviewSingleCapsuleDropGhost(ghostOwner, 0, targetPointer, floating: true);
            Require(Field<EdgeCapsulePresenter>(windows[targetPaper.Id],
                "_edgeCapsule").Placement.Index == 1,
                "ordinary cross-edge hover opens one real target slot");
            var targetGhost = Field<Window>(Field<EdgeCapsuleQueueDropGhost>(
                c, "_nativeQueueDropGhost"), "_window");
            Require(WindowNative.TryGetWindowDeviceBounds(targetGhost, out var targetBounds),
                "cross-edge ghost has physical bounds");
            Near(targetGeometry.WorkArea.Top + Math.Round(targetFirst * targetGeometry.DpiScaleY),
                targetBounds.Top, "cross-edge ghost matches eventual insertion index");
            c.PreviewSingleCapsuleDropGhost(ghostOwner, 1,
                Point(geometry, 80), floating: true);
            Require(Field<EdgeCapsulePresenter>(windows[targetPaper.Id],
                "_edgeCapsule").Placement.Index == 0,
                "floating back to source restores the previous cross-edge target");
            var sourcePeer = queue[1];
            Require(Field<EdgeCapsulePresenter>(windows[sourcePeer.Id],
                "_edgeCapsule").Placement.Index == 0,
                "floating return also reopens the requested in-queue insertion slot");
            c.PreviewDeepCapsuleReorder(ghostOwner, 0);
            c.EndSingleCapsuleDropGhost(ghostOwner.Id);
            Require(Field<object?>(c, "_nativeQueueDropGhost") == null,
                "ending the cross-edge gesture disposes its landing ghost");

            c.State.UseCapsuleCollapseAll = true;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();
            // Folding a populated target keeps only the visible master slot.
            var targetKey = c.QueueKeyForPaper(targetPaper);
            c.State.CapsuleCollapseAllActiveQueues[targetKey] = true;
            c.ArrangeDeepCapsules(false);
            await Idle();
            c.PreviewSingleCapsuleDropGhost(ghostOwner, 0, targetPointer, floating: true);
            var foldedGhost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            var foldedStack = Field<StackPanel>(foldedGhost, "_stack");
            var foldedWindow = Field<Window>(foldedGhost, "_window");
            Require(foldedWindow.IsVisible && foldedWindow.Opacity > 0 &&
                    foldedStack.Children.Count == 1 &&
                    ((Border)foldedStack.Children[0]).Background is SolidColorBrush { Color.A: 0 },
                "folded legacy destination displays one visible hollow outline");
            c.EndSingleCapsuleDropGhost(ghostOwner.Id);
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();

            // An empty edge creates a new short master plus the incoming paper.
            // Its real membership must remain unchanged until release.
            targetPaper.CapsuleSide = DeepCapsuleSides.Right;
            c.ArrangeDeepCapsules(false);
            await Idle();
            c.PreviewSingleCapsuleDropGhost(ghostOwner, 0, targetPointer, floating: true);
            var emptyGhost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            Field<Window>(emptyGhost, "_window").UpdateLayout();
            Require(Field<StackPanel>(emptyGhost, "_stack").Children.Count == 2,
                "empty target edge previews its new master and incoming member");
            var sourceMaster = c.MasterCapsuleForQueue(c.QueueKeyForPaper(ghostOwner))!;
            Near(sourceMaster.DesiredDockedWidthFor(targetGeometry) -
                    EdgeCapsuleLayout.WindowChromeMargin,
                ((Border)Field<StackPanel>(emptyGhost, "_stack").Children[0]).ActualWidth,
                "empty-edge new master uses target DPI and short silhouette");
            c.EndSingleCapsuleDropGhost(ghostOwner.Id);
            targetPaper.CapsuleSide = DeepCapsuleSides.Left;
            c.ArrangeDeepCapsules(false);
            await Idle();

            // Lifting the last source member withdraws its real master even
            // over its original edge; only ending the gesture restores it.
            var leftMaster = c.MasterCapsuleForQueue(c.QueueKeyForPaper(targetPaper))!;
            Require(leftMaster.IsVisible, "one-member source master initially visible");
            c.PreviewSingleCapsuleDropGhost(targetPaper, 0,
                Point(targetGeometry, 80, left: true), floating: true);
            var lastMemberGhost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            Require(!leftMaster.IsVisible && Field<Window>(lastMemberGhost, "_window").IsVisible &&
                    Field<StackPanel>(lastMemberGhost, "_stack").Children.Count == 2,
                "lifting the last member must replace its source master with a projected landing stack");
            c.PreviewSingleCapsuleDropGhost(targetPaper, 0,
                Point(geometry, 80), floating: true);
            Require(!leftMaster.IsVisible, "leaving a one-member legacy queue hides its master");
            c.PreviewSingleCapsuleDropGhost(targetPaper, 0,
                Point(targetGeometry, 80, left: true), floating: true);
            Require(!leftMaster.IsVisible, "floating home must keep the source master withdrawn");
            c.PreviewSingleCapsuleDropGhost(targetPaper, 0,
                Point(geometry, 80), floating: true);
            Require(!leftMaster.IsVisible, "leaving again hides the source master before cancel");
            var animationsEnabled = c.State.EnableAnimations;
            try
            {
                c.State.EnableAnimations = true;
                c.EndSingleCapsuleDropGhost(targetPaper.Id);
                Require(leftMaster.IsVisible &&
                        !DependencyPropertyHelper.GetValueSource(leftMaster, UIElement.OpacityProperty).IsAnimated,
                    "restoring the sole source master does not replay its entrance fade");
            }
            finally
            {
                c.State.EnableAnimations = animationsEnabled;
            }

            // Legacy vertical master movement already renders the real entire
            // queue. Ghosts are reserved for merge or horizontal unlock.
            var (pill, session, masterDown) = PressMaster(sourceMaster);
            try
            {
                var move = new DeviceScreenPoint(masterDown.X,
                    masterDown.Y + 24 * geometry.DpiScaleY);
                Require((bool)Call(sourceMaster, "ContinueMasterDrag", move, session)!,
                    "ordinary master drag enters the existing vertical movement path");
                Require(Field<object?>(c, "_nativeQueueDropGhost") == null,
                    "ordinary vertical master movement needs no overlapping landing ghost");
                Call(sourceMaster, "ContinueMasterDrag", move, session);
            }
            finally
            {
                Call(sourceMaster, "FinishMasterGesture", false, true, null);
            }
            Require(Field<object?>(c, "_nativeQueueDropGhost") == null,
                "ending the docked master gesture leaves no ghost");

            // Once that master actually unlocks from its wall, the shared
            // floating transfer uses its target's real landing geometry.
            var masterKey = c.QueueKeyForPaper(ghostOwner);
            Require(c.TryBeginMasterCapsuleQueueTransfer(
                    ghostOwner.CapsuleMonitorDeviceName, EdgeCapsuleEdge.Right,
                    c.MasterCapsuleStartTopMargin(masterKey), out var floatingTransfer),
                "legacy master transfer begins after horizontal unlock");
            try
            {
                c.PreviewFreeMasterQueueDropGhost(floatingTransfer, targetPointer);
                var floatingGhost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
                Require(Field<Window>(floatingGhost, "_window").Opacity > 0,
                    "detached legacy master shows its landing target");
            }
            finally
            {
                c.EndFreeMasterQueueDropGhost();
                c.CancelMasterCapsuleQueueTransfer(floatingTransfer);
            }
            c.ArrangeDeepCapsules(false);
            await Idle();

            c.State.UseCapsuleCollapseAll = false;
            c.ArrangeDeepCapsules(false);
            await Idle();

            // With free queues off, only the master may move the whole edge queue.
            var ctrlOwner = c.State.Papers.First(p => p.IsVisible &&
                p.CapsuleSide == DeepCapsuleSides.Left && windows.ContainsKey(p.Id));
            Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(
                ctrlOwner.CapsuleMonitorDeviceName, out var ctrlGeometry), "Ctrl legacy monitor");
            var ctrlCount = c.State.Papers.Count(p => p.IsVisible &&
                p.CapsuleSide == DeepCapsuleSides.Left &&
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(p.CapsuleMonitorDeviceName) ==
                WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(ctrlOwner.CapsuleMonitorDeviceName));
            var ctrlTop = EdgeCapsuleLayout.TopForIndex(0,
                c.DeepCapsuleStartTopMarginFor(ctrlOwner),
                ctrlGeometry.LocalWorkAreaDip, ctrlCount, c.DeepCapsuleGap);
            var pointer = Point(ctrlGeometry, ctrlTop, left: true);
            Require(!c.BeginNativeEdgeQueueDrag(ctrlOwner, true, pointer, false),
                "Ctrl must not start whole-queue movement while free queues are off");
            await Idle();
            Console.WriteLine("PASS ordinary landing ghost: cross-edge return, folded/empty target, source master recovery and Ctrl/master transfer");
        }
        finally
        {
            if (ghostOwner != null) c.EndSingleCapsuleDropGhost(ghostOwner.Id);
            foreach (var paper in c.State.Papers.Where(p => p.Id == "review-10"))
                paper.CapsuleSide = DeepCapsuleSides.Left;
            c.State.UseCapsuleFreeQueues = free;
            c.State.UseCapsuleCollapseAll = master;
            c.State.CapsuleCollapseAllActiveQueues = folded;
            c.ArrangeDeepCapsules(false);
        }
    }

    private static void CheckDropGhostReset(MonitorGeometry monitor)
    {
        using var ghost = new EdgeCapsuleQueueDropGhost();
        var window = Field<Window>(ghost, "_window");
        var stack = Field<StackPanel>(ghost, "_stack");
        ghost.ShowMergeHaloAt(monitor, DeepCapsuleSides.Right, 30, 50, topmost: false);
        window.UpdateLayout();
        Require(window.IsVisible && window.Opacity > 0 && !window.Topmost &&
                stack.Children.Count == 1 &&
                ((Border)stack.Children[0]).Background is SolidColorBrush { Color.A: 0 },
            "a non-topmost merge marker displays one hollow outline");

        ghost.ShowAt(monitor, DeepCapsuleSides.Right, 30, new[] { 50d, 180d }, 4);
        window.UpdateLayout();
        Require(window.IsVisible && window.Opacity > 0 && window.Topmost &&
                stack.Children.Count == 2 && stack.Children.Cast<Border>().All(pill =>
                    pill.Background is SolidColorBrush { Color.A: > 0 }),
            "a regular landing resets the hollow fill and topmost state after a merge");
        Console.WriteLine("PASS landing marker reuse: hollow non-topmost merge to filled topmost landing");
    }

    private static async Task CheckDisplacedInsertion(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        await Idle();
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        var source = c.State.Papers.Single(p => p.Id == "review-10");
        var firstTarget = c.State.Papers.Single(p => p.Id == "review-6");
        c.ToggleCapsuleCollapseAllActive(A);
        await Idle();
        var masters = Masters(c);
        var beforeB = masters[B].AnchorTop;
        var beforeC = masters[C].AnchorTop;
        Near(100 + 7 * pitch, beforeB, "expanding the upper queue displaces B");
        Near(beforeB + 3 * pitch, beforeC, "C is also passively displaced");
        var savedBefore = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        var targetMemberBefore = MemberTop(c, windows[firstTarget.Id], firstTarget);
        var dropPoint = Point(monitor, beforeB);

        for (var pass = 0; pass < 2; pass++)
        {
            Require(c.BeginNativeEdgeQueueDrag(source, false, Point(monitor, 120 + pitch, left: true), false),
                "begin prepend from the opposite edge");
            c.PreviewNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true);
            var drop = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!;
            Require(drop.Decision.Kind == EdgeFreeQueueDropKind.Insert && drop.DestinationKey == B &&
                    drop.Decision.Index == 0, "a displaced queue must accept its visible head insertion");
            if (pass == 0)
            {
                var peer = c.State.Papers.Single(p => p.Id == "review-7");
                peer.IsVisible = false;
                c.PreviewNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true);
                var updated = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!;
                Require(updated.DestinationKey == B && updated.TargetMembers.SequenceEqual(new[] { firstTarget }),
                    "a peer hidden during the same-slot drag must leave the projected queue");
                peer.IsVisible = true;
                c.PreviewNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true);
            }
            Near(beforeB, masters[B].AnchorTop, "first-slot preview retains the target master");
            Near(beforeC + pitch, masters[C].AnchorTop, "intersected lower neighbour gives way down");
            Near(targetMemberBefore + pitch, MemberTop(c, windows[firstTarget.Id], firstTarget),
                "first-slot insertion moves old members down");
            Require(savedBefore.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "preview wrote saved preferences");
            if (pass == 1)
                Require(c.CommitNativeEdgeQueueDrag(source, dropPoint, beforeB, floating: true), "prepend commit handled");
            c.EndNativeEdgeQueueDrag(source.Id);
            await Idle();
            if (pass == 0)
            {
                Near(beforeB, masters[B].AnchorTop, "cancel restores B");
                Near(beforeC, masters[C].AnchorTop, "cancel restores C");
                Require(source.CapsuleQueueId == "source" &&
                        savedBefore.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                    "cancel changed membership or saved anchors");
            }
        }

        Require(c.QueueKeyForPaper(source) == B, "accepted drop actually joined the target");
        Near(100, masters[A].AnchorTop, "upper neighbour remains at its original position");
        Near(beforeB, masters[B].AnchorTop, "commit reproduces B's preview");
        Near(beforeC + pitch, masters[C].AnchorTop, "commit reproduces the lower neighbour preview");
        Near(350, c.State.DeepCapsuleQueueStartTopMargins[C], "C's passive offset was not saved");
        Near(200, c.State.DeepCapsuleQueueStartTopMargins[B], "target preference excludes its passive displacement");
        Near(targetMemberBefore + pitch, MemberTop(c, windows[firstTarget.Id], firstTarget), "commit reproduces downward member positions");
        c.SaveNow(sync: true);
        var loaded = store.Load();
        Require(loaded.Papers.Single(p => p.Id == source.Id).CapsuleQueueId == "qb", "merged membership reloads");
        Near(350, loaded.DeepCapsuleQueueStartTopMargins[C], "saved C preference survives reload");
        // Repeated ordinary arrange must neither ratchet positions nor undo the insertion.
        for (var i = 0; i < 3; i++) c.ArrangeDeepCapsules(animate: false);
        await Idle();
        Near(beforeB, masters[B].AnchorTop, "repeated layout does not add displacement");
        Console.WriteLine("PASS displaced head insertion: cancel, commit, preferences, reload and stable rearrange");
    }

    private static async Task CheckDragPreviewUpdates(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, MonitorGeometry monitor)
    {
        const string sourceKey = "|right#source", unrelatedKey = "|left#unrelated";
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        foreach (var paper in c.State.Papers)
        {
            var i = int.Parse(paper.Id["review-".Length..]);
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = i < 6 ? DeepCapsuleSides.Left : DeepCapsuleSides.Right;
            paper.CapsuleQueueId = i < 6 ? "unrelated" : i < 8 ? "qb" : i < 10 ? "qc" : "source";
        }
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 20;
        c.State.DeepCapsuleQueueStartTopMargins[unrelatedKey] = 50;
        c.State.DeepCapsuleQueueStartTopMargins[B] = 160;
        c.State.DeepCapsuleQueueStartTopMargins[C] = 160 + 3 * pitch;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.ArrangeDeepCapsules(false);
        await Idle();

        var source = c.State.Papers.Single(p => p.Id == "review-10");
        var peers = c.State.Papers.Where(p => p != source).ToArray();
        var beforeTops = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
        var beforeMargins = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        var masters = Masters(c);
        var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
        c.PreviewFreeMasterQueueTop(layout, 28);
        await Idle();
        Near(28, masters[sourceKey].AnchorTop, "source master follows its temporary anchor");
        Near(28 + pitch, MemberTop(c, windows[source.Id], source), "source member follows its temporary anchor");
        var sourceFrame = Field<EdgeCapsulePresenter>(windows[source.Id], "_edgeCapsule").AppliedPresentation;
        Require(Math.Abs(sourceFrame.Bounds.Top - (monitor.WorkArea.Top + (28 + pitch) * monitor.DpiScaleY)) <= 1,
            "unchanged source slot indices must still refresh actual presented geometry");

        var insertionTop = 160 + 2 * pitch;
        c.PreviewFreeMasterQueueTop(layout, insertionTop, Point(monitor, insertionTop));
        var slot = Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview");
        Require(slot.TargetKey == B && slot.Index == 1, "master selects the interior target slot");
        await Idle();
        Near(beforeTops["review-6"], MemberTop(c, windows["review-6"], peers.Single(p => p.Id == "review-6")),
            "target prefix stays in place");
        Near(beforeTops["review-7"] + pitch, MemberTop(c, windows["review-7"], peers.Single(p => p.Id == "review-7")),
            "target suffix reserves one carried slot");
        Require(masters[C].AnchorTop > beforeMargins[C], "intersected neighbouring queue gives way");
        var previewTops = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
        c.PreviewFreeMasterQueueTop(layout, insertionTop + 2, Point(monitor, insertionTop + 2));
        Require(peers.All(p => Math.Abs(MemberTop(c, windows[p.Id], p) - previewTops[p.Id]) < .1),
            "same-slot dragging retains the gap without moving its peers again");
        await Idle();

        c.ArrangeDeepCapsules(false);
        await Idle();
        c.PreviewFreeMasterQueueTop(layout, insertionTop + 3, Point(monitor, insertionTop + 3));
        await Idle();
        Require(peers.All(p => Math.Abs(MemberTop(c, windows[p.Id], p) - previewTops[p.Id]) < .1),
            "a global arrange invalidates the old gap and the next sample restores its current preview");

        c.PreviewFreeMasterQueueTop(layout, 24, Point(monitor, 24));
        await Idle();
        Require(peers.All(p => Math.Abs(MemberTop(c, windows[p.Id], p) - beforeTops[p.Id]) < .1),
            "leaving the slot restores its suffix and displaced neighbours");
        Near(beforeMargins[C], masters[C].AnchorTop, "leaving restores the neighbour master");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Near(20, masters[sourceKey].AnchorTop, "cancel restores the source anchor");
        Require(beforeMargins.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
            "preview and cancel must not persist temporary anchors");

        c.ToggleCapsuleCollapseAllActive(sourceKey);
        await Idle();
        layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
        c.PreviewFreeMasterQueueTop(layout, 32);
        Require(windows[source.Id].IsCollapseAllRetracted,
            "moving a folded queue keeps its member retracted");
        Near(32, masters[sourceKey].AnchorTop, "folded master still follows the pointer");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Console.WriteLine("PASS drag preview: live anchor, stable slot, arrange recovery, neighbour restore, cancel and folded source");
    }

    private static async Task CheckVacatedSourcePreview(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        // Leave enough space below B for its inserted member on the CI desktop;
        // downward-first overflow is covered separately by the policy checks.
        var sourceTop = pitch;
        var targetTop = sourceTop + 7 * pitch;
        var emptyTop = targetTop + 5 * pitch;
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        // Each outcome reaches both direct and animated handoff. Distribute
        // master presence and cross-edge detachment without multiplying them.
        foreach (var (animated, hasMaster, floating, ending) in new[]
        {
            (false, false, false, "cancel"),
            (true, true, false, "cancel"),
            (false, true, true, "return"),
            (true, false, false, "return"),
            (false, false, true, "split"),
            (true, true, true, "split"),
            (false, true, false, "merge"),
            (true, false, true, "merge")
        })
        {
            c.State.EnableAnimations = false;
            c.State.UseCapsuleCollapseAll = hasMaster;
            c.State.Papers = c.State.Papers.OrderBy(p => int.Parse(p.Id["review-".Length..])).ToList();
            foreach (var paper in c.State.Papers)
            {
                var i = int.Parse(paper.Id["review-".Length..]);
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = i < 8 ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
                paper.CapsuleQueueId = i < 5 ? "qa" : i < 8 ? "qb" : "qc";
            }
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[A] = sourceTop;
            c.State.DeepCapsuleQueueStartTopMargins[B] = targetTop;
            c.State.DeepCapsuleQueueStartTopMargins["|left#qc"] = sourceTop;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();

            c.State.EnableAnimations = animated;

            var source = c.State.Papers.Take(5).ToArray();
            var owner = source[1];
            var target = c.State.Papers.Skip(5).Take(3).ToArray();
            var peers = c.State.Papers.Where(p => p != owner).ToArray();
            var before = c.State.Papers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            var saved = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
            var order = c.State.Papers.Select(p => p.Id).ToArray();
            var identities = c.State.Papers.ToDictionary(p => p.Id, c.QueueKeyForPaper);
            var start = Point(monitor, before[owner.Id]);
            var outside = Point(monitor, emptyTop, left: floating);
            Require(c.BeginNativeEdgeQueueDrag(owner, false, start, false), "begin single-source drag");
            Require(!c.PreviewNativeEdgeQueueDrag(owner, start, before[owner.Id], false),
                "ordinary sorting remains on its existing path before detachment");
            // Start from an existing in-group sorting preview, not only from rest.
            c.PreviewDeepCapsuleReorder(owner, 3);
            Require(c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating), "detach single member");
            var drag = Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag");
            Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Move, "independent destination selected");
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            EdgeCapsuleQueueCompositionProxy? sourceProxy = null;
            if (animated)
                Require(proxies.TryGetValue(A, out sourceProxy) &&
                        sourceProxy.Members.Any(m => ReferenceEquals(m.Window, windows[source[2].Id])) &&
                        !sourceProxy.Members.Any(m => ReferenceEquals(m.Window, windows[owner.Id])),
                    "remaining source members animate while the carried capsule stays direct");
            else await Idle();
            for (var i = 0; i < source.Length; i++)
                if (i != 1)
                {
                    var expected = before[source[i].Id] - (i > 1 ? pitch : 0);
                    Near(expected, MemberTop(c, windows[source[i].Id], source[i]), "source immediately closes its gap");
                    if (!animated)
                    {
                        var frame = Field<EdgeCapsulePresenter>(windows[source[i].Id], "_edgeCapsule").AppliedPresentation;
                        Require(Math.Abs(frame.Bounds.Top - (monitor.WorkArea.Top + expected * monitor.DpiScaleY)) <= 1,
                            "source compaction reaches the visible frame");
                    }
                }
            if (animated && hasMaster && !floating && ending == "cancel")
            {
                // A detached single capsule placed above its own group pushes
                // that group's master too. It is a passive source, not a carried group.
                var above = 20d;
                c.PreviewNativeEdgeQueueDrag(owner, Point(monitor, above), above, false);
                Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Move &&
                        Masters(c)[A].AnchorTop > sourceTop, "independent single placement displaces its source");
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
                Require(proxies.TryGetValue(A, out var shiftedSource) &&
                        shiftedSource.Members.Any(m => ReferenceEquals(m.Window, Masters(c)[A])),
                    "a passively displaced source master joins the existing member animation");
                c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating);
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
                Near(sourceTop, Masters(c)[A].AnchorTop, "source master returns while the source gap stays closed");
            }

            // Return to the compacted source's second slot, then leave again.
            // The selected index must use the remaining members, not an empty old slot.
            c.PreviewNativeEdgeQueueDrag(owner, start, before[owner.Id], floating);
            Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                    drag.Drop.DestinationKey == A && drag.Drop.Decision.Index == 1,
                "return uses the compacted source insertion index");
            foreach (var paper in source.Where(p => p != owner))
                Near(before[paper.Id], MemberTop(c, windows[paper.Id], paper), "return reopens the source slot");
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            var releasePoint = start;
            var releaseTop = before[owner.Id];
            if (ending != "return")
            {
                c.PreviewNativeEdgeQueueDrag(owner, outside, emptyTop, floating);
                Near(before[source[2].Id] - pitch, MemberTop(c, windows[source[2].Id], source[2]),
                    "leaving the reopened source closes it again");
                releaseTop = before[target[1].Id];
                releasePoint = Point(monitor, releaseTop);
                c.PreviewNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating);
                Require(drag.Drop!.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                        drag.Drop.DestinationKey == B && drag.Drop.Decision.Index == 1, "merge selects target middle");
                Near(before[source[2].Id] - pitch, MemberTop(c, windows[source[2].Id], source[2]),
                    "source remains compact while target opens a slot");
                Near(before[target[1].Id] + pitch, MemberTop(c, windows[target[1].Id], target[1]),
                    "target suffix makes room simultaneously");
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
                if (animated)
                    Require(proxies.ContainsKey(A) && proxies.ContainsKey(B),
                        "source compaction and target insertion animate as separate queues");
                if (ending == "split")
                {
                    releasePoint = outside;
                    releaseTop = emptyTop;
                    c.PreviewNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating);
                    foreach (var paper in target)
                        Near(before[paper.Id], MemberTop(c, windows[paper.Id], paper), "leaving target restores it");
                }
            }
            Require(order.SequenceEqual(c.State.Papers.Select(p => p.Id)) &&
                    c.State.Papers.All(p => c.QueueKeyForPaper(p) == identities[p.Id]) &&
                    saved.Count == c.State.DeepCapsuleQueueStartTopMargins.Count &&
                    saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "source and target previews do not mutate data");
            var preview = peers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            if (ending != "cancel")
                Require(c.CommitNativeEdgeQueueDrag(owner, releasePoint, releaseTop, floating), "commit single-source drag");
            c.EndNativeEdgeQueueDrag(owner.Id);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            await Idle();
            foreach (var paper in peers)
                Near(ending == "cancel" ? before[paper.Id] : preview[paper.Id],
                    MemberTop(c, windows[paper.Id], paper), "cancel or commit matches the source/target preview");
            Require(proxies.Count == 0 && peers.All(p => windows[p.Id].VerifyEdgeCapsuleQueueProxyEndpoint(
                    Field<EdgeCapsulePresenter>(windows[p.Id], "_edgeCapsule").AppliedPresentation)),
                "source/target animation hands every real window back at its final position");
            if (ending is "cancel" or "return")
                Require(c.QueueKeyForPaper(owner) == A, "owner returns to source");
            else
            {
                Require(ending == "merge" ? c.QueueKeyForPaper(owner) == B :
                    c.QueueKeyForPaper(owner) != A && c.QueueKeyForPaper(owner) != B, "committed destination identity");
                c.SaveNow(sync: true);
                var loaded = store.Load();
                Require(loaded.Papers.Single(p => p.Id == owner.Id).CapsuleQueueId == owner.CapsuleQueueId &&
                        loaded.Papers.Where(p => p.CapsuleQueueId == "qa").Select(p => p.Id)
                            .SequenceEqual(source.Where(p => p != owner).Select(p => p.Id)),
                    "saved source membership and destination reload together");
            }
        }
        c.State.EnableAnimations = false;
        c.State.UseCapsuleCollapseAll = true;
        Console.WriteLine("PASS vacated source: representative direct/animated handoff, sorting-to-detach, return, cancel, split, merge and reload");
    }

    private static async Task CheckIndependentGroupPreview(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        // A = master + four members, B = master + three members. Each input
        // route cancels and commits; cramped animated cases exercise native
        // stacking, while direct cases retain the sufficient-space boundary.
        foreach (var (animated, route, cramped, commit) in new[]
        {
            (false, "ctrl-docked", false, false),
            (true, "ctrl-docked", true, true),
            (true, "ctrl-floating", true, false),
            (false, "ctrl-floating", false, true),
            (true, "master-docked", true, false),
            (false, "master-docked", false, true),
            (false, "master-floating", false, false),
            (true, "master-floating", true, true)
        })
        {
            c.State.EnableAnimations = false;
            var aTop = (cramped ? 2 : 4) * pitch;
            var bTop = aTop + 6 * pitch;
            foreach (var paper in c.State.Papers)
            {
                var i = int.Parse(paper.Id["review-".Length..]);
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = i < 7 ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
                paper.CapsuleQueueId = i < 4 ? "qa" : i < 7 ? "qb" : "qc";
            }
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[A] = aTop;
            c.State.DeepCapsuleQueueStartTopMargins[B] = bTop;
            c.State.DeepCapsuleQueueStartTopMargins["|left#qc"] = 40;
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.ArrangeDeepCapsules(false);
            await Idle();
            c.State.EnableAnimations = animated;
            var saved = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
            var targetPapers = c.State.Papers.Where(p => p.CapsuleQueueId == "qa").ToArray();
            var memberTops = targetPapers.ToDictionary(p => p.Id, p => MemberTop(c, windows[p.Id], p));
            var owner = c.State.Papers.Single(p => p.Id == "review-4");
            var ctrl = route.StartsWith("ctrl", StringComparison.Ordinal);
            var floating = route.EndsWith("floating", StringComparison.Ordinal);
            // The carried tail overlaps A, but the held capsule remains above
            // the merge entrance. Only the landing ghost consumes that overlap.
            var top = aTop - 2 * pitch;
            var landingTop = Math.Max(0, aTop - 4 * pitch);
            var ownerTop = top + pitch;
            var point = Point(monitor, ctrl && !floating ? ownerTop : top);
            FreeMasterDragLayout? layout = null;
            MasterCapsuleQueueTransferSnapshot transfer = default;
            EdgeCapsuleDragWindow? floatingHost = null;
            void ShowFloatingHost()
            {
                Require(c.TryCreateMasterQueueFloatingDragHostOptions("", EdgeCapsuleEdge.Right,
                    "▾", "3", out var options, sourceQueueKey: B), "floating stacking fixture options");
                floatingHost = EdgeCapsuleDragWindow.Rent(options);
                floatingHost.ShowWithEntrance(point, animate: false, scaleFrom: 1, durationMilliseconds: 0);
            }
            if (ctrl)
            {
                Require(c.BeginNativeEdgeQueueDrag(owner, true, Point(monitor, bTop + pitch), false), route);
                if (floating) ShowFloatingHost();
                c.PreviewNativeEdgeQueueDrag(owner, point, ownerTop, floating);
                Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!.Decision.Kind == EdgeFreeQueueDropKind.Move,
                    "B above A remains an independent group");
            }
            else
            {
                layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, B)!;
                if (floating)
                {
                    Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, bTop,
                        out transfer, freeLayout: layout, sourceQueueKey: B), route);
                    ShowFloatingHost();
                    c.PreviewFreeMasterQueueDropGhost(transfer, point);
                }
                else c.PreviewFreeMasterQueueTop(layout, top, point);
                Require(Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview").Decision?.Kind == EdgeFreeQueueDropKind.Move,
                    "master preview keeps the two groups separate");
            }
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            var expectedA = Math.Max(aTop, 4 * pitch);
            var masters = Masters(c);
            EdgeCapsuleQueueCompositionProxy? yielding = null;
            if (animated && cramped)
            {
                var context = $"{route}, cramped={cramped}, commit={commit}, proxies=" + string.Join(";", proxies.Select(pair =>
                    pair.Key + ":" + string.Join(",", pair.Value.Members.Select(m => m.Plan.PaperId))));
                Require(proxies.TryGetValue(A, out yielding), "independent target owns an animation: " + context);
                Require(yielding!.Members.Any(m => ReferenceEquals(m.Window, masters[A])),
                    "independent target master joins its members: " + context);
                Require(targetPapers.All(p => yielding.Members.Any(m => ReferenceEquals(m.Window, windows[p.Id]))),
                    "independent target includes every displaced member: " + context);
                Require(!proxies.ContainsKey(B), "carried group remains direct: " + context);
                var interactionHandle = floating ? EdgeCapsuleDragWindow.VisibleLeaseHandle :
                    ctrl ? windows[owner.Id].EdgeCapsuleQueueProxySourceHandle : masters[B].QueueProxySourceHandle;
                Require(WindowNative.IsWindowBehind(yielding.OutputHandle, interactionHandle),
                    "independent yield remains behind the actual drag HWND: " + context);
            }
            else Require(!proxies.ContainsKey(A), "sufficient upper space or disabled animations needs no target animation");
            Near(expectedA, masters[A].AnchorTop, route + " moves A only by missing space above");
            foreach (var paper in targetPapers)
                Near(memberTops[paper.Id] + expectedA - aTop, MemberTop(c, windows[paper.Id], paper),
                    route + " previews A as one rigid group");
            var ghost = Field<EdgeCapsuleQueueDropGhost?>(c, "_nativeQueueDropGhost");
            if (!ctrl && !floating)
                Require(ghost == null || Field<Window>(ghost, "_window").Opacity == 0,
                    "unmerged docked master follows the real queue without a second ghost");
            else
            {
                Require(ghost != null && Field<Window>(ghost, "_window").Opacity > 0 &&
                        Field<StackPanel>(ghost, "_stack").Children.Count == 4,
                    "detached/whole-queue independent drop previews its landing stack");
                Field<Window>(ghost!, "_window").UpdateLayout();
                Near(masters[B].DesiredDockedWidth - EdgeCapsuleLayout.WindowChromeMargin,
                    ((Border)Field<StackPanel>(ghost!, "_stack").Children[0]).ActualWidth,
                    route + " passes the source master width to its first silhouette");
            }
            Require(saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                "independent preview must not write saved positions");

            EdgeFreeQueueDrop Decision() => ctrl
                ? Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop!.Decision
                : Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview").Decision!;
            void Sample(double requested)
            {
                var samplePoint = Point(monitor, ctrl && !floating ? requested + pitch : requested);
                if (ctrl) c.PreviewNativeEdgeQueueDrag(owner, samplePoint, requested + pitch, floating);
                else if (floating) c.PreviewFreeMasterQueueDropGhost(transfer, samplePoint);
                else c.PreviewFreeMasterQueueTop(layout!, requested, samplePoint);
            }
            // Actual pointer movement, rather than just identical samples, must
            // keep one landing position and one neighbour displacement.
            foreach (var delta in new[] { 4d, 12d, 0d })
            {
                Sample(top + delta);
                Require(Decision().Kind == EdgeFreeQueueDropKind.Move, "approaching above remains independent");
                Near(landingTop, Decision().TopDip, "upper landing ghost is stable while approaching");
                Near(expectedA, masters[A].AnchorTop, "approaching cannot progressively push A");
            }

            Sample(aTop + 5 * pitch + 40);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(Decision().Kind == EdgeFreeQueueDropKind.Move, "moving below selects an independent landing");
            Near(aTop, masters[A].AnchorTop, "moving below immediately withdraws the earlier top-space displacement");
            foreach (var paper in targetPapers)
                Near(memberTops[paper.Id], MemberTop(c, windows[paper.Id], paper), "moving below restores every A member");

            Sample(aTop + (ctrl && !floating ? pitch : 2 * pitch));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(Decision().Kind == EdgeFreeQueueDropKind.Insert && Decision().TargetKey == A,
                "the held capsule entering A selects a merge");
            Near(aTop, masters[A].AnchorTop, "merge uses A's original head, not its previous independent displacement");
            var mergeGhostStack = Field<StackPanel>(
                Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost"), "_stack");
            Require(mergeGhostStack.Children.Count == 3 &&
                    Math.Abs(((Border)mergeGhostStack.Children[0]).Width -
                        (windows[owner.Id].DeepCapsuleRestingVisibleWidthFor(monitor) -
                         EdgeCapsuleLayout.WindowChromeMargin)) < .1,
                "merge replaces the short master with a correctly sized ordinary member ghost");
            Sample(top);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            if (!ctrl && !floating)
                Require(Field<Window>(Field<EdgeCapsuleQueueDropGhost>(
                        c, "_nativeQueueDropGhost"), "_window").Opacity == 0,
                    "leaving merge hides the unused docked master ghost immediately");
            Near(landingTop, Decision().TopDip, "returning above recovers the same independent landing");
            Near(expectedA, masters[A].AnchorTop, "returning above applies the deficit once again");

            if (ctrl)
            {
                if (commit) Require(c.CommitNativeEdgeQueueDrag(owner, point, ownerTop, floating), route + " commit");
                c.EndNativeEdgeQueueDrag(owner.Id);
            }
            else if (floating)
            {
                if (commit) Require(c.CommitMasterCapsuleQueueTransfer(transfer, point), route + " commit");
                else c.CancelMasterCapsuleQueueTransfer(transfer);
                c.EndFreeMasterQueueDropGhost();
            }
            else c.FinishFreeMasterQueueDrag(layout!, commit);
            floatingHost?.ReturnToPool();
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            await Idle();
            Require(proxies.Count == 0 && masters[A].VerifyQueueProxyEndpoint(masters[A].QueueProxyTarget),
                "independent landing or cancel hands the master back at its final position");
            Near(commit ? expectedA : aTop, masters[A].AnchorTop, route + " A release result matches preview or cancel");
            Near(commit ? landingTop : bTop, masters[B].AnchorTop, route + " B release result matches preview or cancel");
            Require(c.State.Papers.Count(p => p.CapsuleQueueId == "qa") == 4 &&
                    c.State.Papers.Count(p => p.CapsuleQueueId == "qb") == 3,
                "independent placement never merges membership");
            if (!commit)
                Require(saved.All(pair => c.State.DeepCapsuleQueueStartTopMargins[pair.Key] == pair.Value),
                    "cancel retains every original anchor");
            else
            {
                c.SaveNow(sync: true);
                var loaded = store.Load();
                Near(landingTop, loaded.DeepCapsuleQueueStartTopMargins[B], "independent landing anchor survives reload");
                foreach (var pair in loaded.DeepCapsuleQueueStartTopMargins)
                    c.State.DeepCapsuleQueueStartTopMargins[pair.Key] = pair.Value;
                c.ArrangeDeepCapsules(false);
                await Idle();
                Near(expectedA, masters[A].AnchorTop, "saved independent order reproduces A's preview");
                Near(landingTop, masters[B].AnchorTop, "saved independent order reproduces B's preview");
            }
        }
        c.State.EnableAnimations = false;
        Console.WriteLine("PASS independent group preview: animations on/off, Ctrl/master, docked/floating, stable approach, top deficit, below/merge/return, short master, native stacking, cancel, commit and reload");
    }

    private static async Task CheckYieldAnimations(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, MonitorGeometry monitor)
    {
        const string sourceKey = "|right#source";
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.ArrangeDeepCapsules(false);
        await Idle();
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        var masters = Masters(c);
        var source = c.State.Papers.Single(p => p.Id == "review-10");
        c.State.EnableAnimations = true;
        try
        {
            // Exercise ordinary reorder through the public controller path.
            var first = c.State.Papers.Single(p => p.Id == "review-6");
            c.PreviewDeepCapsuleReorder(first, 1);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(B, out var ordinary), "ordinary reorder owns a queue translation");
            Require(!ordinary!.Members.Any(m => ReferenceEquals(m.Window, windows[first.Id])),
                "ordinary reorder excludes the directly dragged capsule");
            Require(WindowNative.IsWindowBehind(ordinary.OutputHandle,
                    windows[first.Id].EdgeCapsuleQueueProxySourceHandle),
                "ordinary yield stays below the operated capsule");
            c.PreviewDeepCapsuleReorder(first, 0);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(B, out var reversed),
                "rapid reverse retains a live queue translation");
            Require(WindowNative.IsWindowBehind(reversed!.OutputHandle,
                    windows[first.Id].EdgeCapsuleQueueProxySourceHandle),
                "retarget preserves the operated capsule's foreground position");
            reversed!.CompleteNow(true);
            Require(!proxies.ContainsKey(B) &&
                    windows["review-7"].VerifyEdgeCapsuleQueueProxyEndpoint(
                        Field<EdgeCapsulePresenter>(windows["review-7"], "_edgeCapsule").AppliedPresentation),
                "retarget handoff leaves the real capsule at its current endpoint");
            c.ArrangeDeepCapsules(false);
            await Idle();

            await CheckAnimatedPrepend(c, windows, source, monitor, pitch);

            var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            var insertion = 160 + 2 * pitch;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.ContainsKey(B), "merge preview translates the target queue");
            Require(proxies.TryGetValue(C, out var neighbour),
                "merge preview translates the neighbouring queue independently");
            Require(neighbour!.Members.Any(m => ReferenceEquals(m.Window, masters[C])) &&
                    neighbour.Members.Any(m => m.Window is PaperWindow),
                "passive neighbour master and members share the same native translation");
            Require(!proxies.ContainsKey(sourceKey), "carried queue remains direct while merging");
            var sourceFrame = Field<EdgeCapsulePresenter>(windows[source.Id], "_edgeCapsule").AppliedPresentation;
            Require(Math.Abs(sourceFrame.Bounds.Top -
                    (monitor.WorkArea.Top + (insertion + pitch) * monitor.DpiScaleY)) <= 1,
                "carried queue follows the pointer without waiting for passive animation");

            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.PreviewFreeMasterQueueTop(layout, 24, Point(monitor, 24));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.ContainsKey(B) && proxies.ContainsKey(C),
                "leaving the merge slot animates both suffix and neighbour back");
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.FinishFreeMasterQueueDrag(layout, false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            Require(proxies.Count == 0, "completion releases all queue covers");

            c.State.EnableAnimations = false;
            c.State.CapsuleCollapseAllActiveQueues[C] = true;
            c.ArrangeDeepCapsules(false);
            await Idle();
            layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            c.State.EnableAnimations = true;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.TryGetValue(C, out var folded) && folded.Members.Count == 1 &&
                    ReferenceEquals(folded.Members[0].Window, masters[C]),
                "a folded neighbour animates its master without fabricating a paper member");
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            c.FinishFreeMasterQueueDrag(layout, false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);

            c.State.EnableAnimations = false;
            layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, sourceKey)!;
            c.PreviewFreeMasterQueueTop(layout, insertion, Point(monitor, insertion));
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
            Require(proxies.Count == 0, "disabled animations leave all sources directly owned");
            c.FinishFreeMasterQueueDrag(layout, false);
        }
        finally
        {
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(false);
            c.State.EnableAnimations = false;
            c.ArrangeDeepCapsules(false);
            await Idle();
        }
        Console.WriteLine("PASS ordinary and merge yield: independent queues, master/member handoff, direct drag and animated return");
    }

    private static async Task CheckMasterAnimationStacking(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        foreach (var side in new[] { DeepCapsuleSides.Left, DeepCapsuleSides.Right })
        {
            c.State.EnableAnimations = false;
            foreach (var paper in c.State.Papers)
            {
                paper.CapsuleMonitorDeviceName = "";
                paper.CapsuleSide = side;
                paper.CapsuleQueueId = "stack";
            }
            var key = "|" + side + "#stack";
            c.State.CapsuleCollapseAllActiveQueues.Clear();
            c.State.DeepCapsuleQueueStartTopMargins.Clear();
            c.State.DeepCapsuleQueueStartTopMargins[key] = 40;
            c.ArrangeDeepCapsules(false);
            await Idle();
            c.State.EnableAnimations = true;
            var master = Masters(c)[key];
            var foreground = WindowNative.ForegroundWindow;
            foreach (var collapsed in new[] { true, false, true })
            {
                c.SuppressEdgeCapsulePreviewForMasterQueueLayout(key);
                c.ToggleCapsuleCollapseAllActive(key);
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
                Require(proxies.TryGetValue(key, out var proxy) && proxy.Members.Count == windows.Count,
                    "master toggle publishes a complete member animation");
                Require(WindowNative.IsWindowBehind(proxy!.OutputHandle, master.QueueProxySourceHandle),
                    "collapse and rapid reverse stay behind the master on " + side);
                Require(WindowNative.ForegroundWindow == foreground,
                    "animation stacking must not activate a window");
                Require(c.State.CapsuleCollapseAllActiveQueues.GetValueOrDefault(key) == collapsed,
                    "stacking must retain the requested collapse state");
            }
            foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
            await Idle();
            Require(proxies.Count == 0 && windows.Values.All(window =>
                    window.VerifyEdgeCapsuleQueueProxyEndpoint(
                        Field<EdgeCapsulePresenter>(window, "_edgeCapsule").AppliedPresentation)),
                "collapse hands all real endpoints back beneath the master");
        }
        c.State.EnableAnimations = false;
        Console.WriteLine("PASS animation stacking: both edges, master collapse, rapid expand/retract and native handoff without activation");
    }

    private static async Task CheckAnimatedPrepend(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, PaperData source,
        MonitorGeometry monitor, double pitch)
    {
        var proxies = Field<Dictionary<string, EdgeCapsuleQueueCompositionProxy>>(
            c, "_edgeCapsuleQueueCompositionProxies");
        var master = Masters(c)[B];
        var firstPaper = c.State.Papers.Single(p => p.Id == "review-6");
        var first = windows[firstPaper.Id];
        var firstRestingTop = MemberTop(c, first, firstPaper);
        var insertion = 160 + 2 * pitch;
        Require(c.BeginNativeEdgeQueueDrag(source, false, Point(monitor, 20 + pitch), false),
            "begin a single-capsule transfer for animated prepend");
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, insertion), insertion, floating: true);
        Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop?.Decision.Index == 1,
            "select the interior slot before testing the head transition");
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.ContainsKey(B), "interior insertion starts a real translation");
        Require(WindowNative.TryGetWindowDeviceBounds(master, out var masterBeforeHead),
            "capture the unshifted destination master");
        var firstBeforeHead = Field<EdgeCapsulePresenter>(first, "_edgeCapsule").AppliedPresentation;

        // With space below, prepend keeps the master fixed and adds the old
        // prefix to the moving suffix before the first animation finishes.
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, 160), 160, floating: true);
        Require(Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop?.Decision.Index == 0,
            "the second sample selects prepend, not append to the master");
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.TryGetValue(B, out var head) &&
                head.Members.Any(m => ReferenceEquals(m.Window, first)) &&
                !head.Members.Any(m => ReferenceEquals(m.Window, master)),
            "interior-to-head moves the prefix with the suffix and keeps the master fixed");
        Require(WindowNative.TryGetWindowDeviceBounds(master, out var masterAfterHead) &&
                masterAfterHead == masterBeforeHead, "downward-first prepend leaves the master in place");
        Require(head!.TryGetPresentationAt(first,
                Field<long>(head, "_animationStartedAtTimestamp"), out var firstStart) &&
                firstStart.Bounds.Top == firstBeforeHead.Bounds.Top,
            "the newly admitted prefix starts at its visible position without a jump");
        c.PreviewNativeEdgeQueueDrag(source, Point(monitor, insertion), insertion, floating: true);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(proxies.ContainsKey(B) &&
                Field<NativeEdgeQueueDrag>(c, "_nativeEdgeQueueDrag").Drop?.Decision.Index == 1,
            "head-to-interior retains a live translation and selects the interior slot");
        Near(firstRestingTop, MemberTop(c, first, firstPaper),
            "returning to the interior restores the prefix position");
        c.EndNativeEdgeQueueDrag(source.Id);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        foreach (var proxy in proxies.Values.ToArray()) proxy.CompleteNow(true);
        await Idle();
        Require(proxies.Count == 0 && first.VerifyEdgeCapsuleQueueProxyEndpoint(
                Field<EdgeCapsulePresenter>(first, "_edgeCapsule").AppliedPresentation),
            "canceling the prepend returns the real prefix window at its endpoint");
        Console.WriteLine("PASS animated prepend: interior -> head -> interior and real-window handoff");
    }

    private static async Task CheckFoldedDropAndShortcuts(AppController c, MonitorGeometry monitor)
    {
        const string shortKey = "|right#short", longKey = "|right#long";
        var shortPaper = c.State.Papers.Single(p => p.Id == "review-10");
        foreach (var paper in c.State.Papers)
        {
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = DeepCapsuleSides.Right;
            paper.CapsuleQueueId = paper.Id == shortPaper.Id ? "short" : "long";
        }
        var bottom = Math.Round(monitor.LocalWorkAreaDip.Height - 80, 1);
        var upper = Math.Max(80, bottom - 320);
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[shortKey] = upper;
        c.State.DeepCapsuleQueueStartTopMargins[longKey] = bottom;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.CapsuleCollapseAllActiveQueues[longKey] = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Near(bottom, Masters(c)[longKey].AnchorTop, "folded queue starts near the bottom");

        // Exercise the actual controller-to-shortcut geometry adapter, not a
        // resolver fed with hand-written correct screen coordinates.
        var plan = EdgeCapsuleQueueCoordinator.Build(c.State.Papers.Select(p =>
            new EdgeCapsuleQueueMember(p, c.QueueKeyForPaper(p))), true);
        var deltas = (IReadOnlyDictionary<string, double>)Call(c, "BuildEffectiveQueueRestingDeltas")!;
        var shortcutQueues = plan.Queues.Select(queue =>
            (EdgeCapsuleShortcutQueue)Call(c, "EdgeShortcutQueue", queue, deltas)!).ToArray();
        foreach (var queue in shortcutQueues)
            Near(Masters(c)[queue.QueueKey].AnchorTop, queue.StartTopDip, "shortcut uses displayed queue top");
        Require(EdgeCapsuleShortcutResolver.Resolve(shortcutQueues, 1)?.Id == shortPaper.Id,
            "first shortcut must select the visibly upper queue, not the folded queue below");
        c.State.UseCapsuleFreeQueues = false;
        try
        {
            var queue = plan.Queues.Single(q => q.Key == longKey);
            var legacy = (EdgeCapsuleShortcutQueue)Call(c, "EdgeShortcutQueue", queue, deltas)!;
            Near(EdgeCapsuleLayout.TopForIndex(0, bottom, monitor.LocalWorkAreaDip,
                    queue.Papers.Count + 1, c.DeepCapsuleGap) + deltas.GetValueOrDefault(longKey),
                legacy.StartTopDip, "legacy shortcut geometry is unchanged");
        }
        finally { c.State.UseCapsuleFreeQueues = true; }

        // The folded destination is represented by its master alone. A
        // hovering master-to-master merge gets an OUTSIDE outline instead of
        // drawing invisible members; ordinary same-edge motion gets no ghost.
        var sourceLayout = c.CaptureFreeMasterDragLayout(
            "", EdgeCapsuleEdge.Right, shortKey)!;
        c.PreviewFreeMasterQueueTop(sourceLayout, upper,
            Point(monitor, upper));
        Require(Field<object?>(c, "_nativeQueueDropGhost") == null ||
                Field<Window>(Field<EdgeCapsuleQueueDropGhost>(c,
                    "_nativeQueueDropGhost"), "_window").Opacity == 0,
            "independent docked master movement has no ghost");
        c.PreviewFreeMasterQueueTop(sourceLayout, upper,
            Point(monitor, bottom));
        var foldedDecision = Field<FreeQueueSlotPreviewState>(c,
            "_freeMasterSlotPreview").Decision!;
        Require(foldedDecision.Kind == EdgeFreeQueueDropKind.Insert &&
                foldedDecision.TargetKey == longKey,
            "hovering the collapsed destination master selects a real merge");
        var foldedHalo = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
        var haloWindow = Field<Window>(foldedHalo, "_window");
        var haloStack = Field<StackPanel>(foldedHalo, "_stack");
        Require(haloWindow.IsVisible && haloWindow.Opacity > 0 &&
                haloStack.Children.Count == 1 &&
                ((Border)haloStack.Children[0]).Background is SolidColorBrush { Color.A: 0 },
            "folded target uses one visible hollow master outline");
        c.FinishFreeMasterQueueDrag(sourceLayout, commit: false);
        await Idle();

        var currentKey = longKey;
        foreach (var left in new[] { false, true })
        {
            var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, currentKey)!;
            Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, bottom,
                    out var transfer, freeLayout: layout, sourceQueueKey: currentKey), "begin folded floating transfer");
            c.PreviewFreeMasterQueueDropGhost(transfer, Point(monitor, bottom, left));
            var ghost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
            Require(Field<Window>(ghost, "_window").Opacity > 0 &&
                    Field<StackPanel>(ghost, "_stack").Children.Count == 1,
                "an actually folded source previews only its retained master slot");
            Near(Masters(c)[currentKey].DesiredDockedWidth, Field<Window>(ghost, "_window").Width,
                "folded transfer passes the actual master width to its ghost");
            Near(bottom, Field<FreeQueueSlotPreviewState>(c, "_freeMasterSlotPreview").Top,
                "folded preview uses the same visible height as commit");
            Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(monitor, bottom, left)),
                "folded independent drop commits");
            c.EndFreeMasterQueueDropGhost();
            await Idle();
            currentKey = left ? "|left#long" : longKey;
            Near(bottom, Masters(c)[currentKey].AnchorTop, "folded drop uses only the visible master height");
            Near(bottom, c.State.DeepCapsuleQueueStartTopMargins[currentKey], "folded drop saves the requested bottom anchor");
            Require(c.State.CapsuleCollapseAllActiveQueues.GetValueOrDefault(currentKey), "drop retains folded state");
        }
        Console.WriteLine("PASS folded same-edge/cross-edge drops and actual shortcut geometry");
    }

    private static async Task CheckDisconnectedGesturesAndMerge(AppController c,
        IReadOnlyDictionary<string, PaperWindow> windows, StateStore store, MonitorGeometry monitor)
    {
        // A missing persisted display exercises the normal fallback without
        // pretending that this fixture physically unplugged a monitor.
        const string missingMonitor = "PaperTodo.Review.DisconnectedDisplay";
        const string targetKey = missingMonitor + "|right#fallback";
        const string sourceKey = "|left#incoming";
        foreach (var paper in c.State.Papers)
        {
            var target = int.Parse(paper.Id["review-".Length..]) < 6;
            paper.CapsuleMonitorDeviceName = target ? missingMonitor : "";
            paper.CapsuleSide = target ? DeepCapsuleSides.Right : DeepCapsuleSides.Left;
            paper.CapsuleQueueId = target ? "fallback" : "incoming";
        }
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins[targetKey] = 180;
        c.State.DeepCapsuleQueueStartTopMargins[sourceKey] = 100;
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.CapsuleCollapseAllActiveQueues[targetKey] = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var master = Masters(c)[targetKey];
        var before = master.AnchorTop;
        var (pill, session, start) = PressMaster(master);
        var pointer = new DeviceScreenPoint(start.X, start.Y + 24 * monitor.DpiScaleY);
        Require((bool)Call(master, "ContinueMasterDrag", pointer, session)!, "post-disconnect move handled");
        Near(before + 24, master.AnchorTop, "a NEW gesture on an already-fallback master can move");
        Near(180, c.State.DeepCapsuleQueueStartTopMargins[targetKey], "new drag has not saved its preview");
        master.CancelQueueTransfer();
        await Idle();
        Require(!pill.IsMouseCaptured, "cancelling the new gesture releases input");
        Near(before, master.AnchorTop, "cancel restores the fallback master");

        var source = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Left, sourceKey)!;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left, source.MasterTopDip,
                out var transfer, freeLayout: source, sourceQueueKey: sourceKey), "begin merge into fallback target");
        c.PreviewFreeMasterQueueDropGhost(transfer, Point(monitor, before));
        var ghost = Field<EdgeCapsuleQueueDropGhost>(c, "_nativeQueueDropGhost");
        Require(Field<Window>(ghost, "_window").IsVisible, "fallback target has a merge preview");
        Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(monitor, before)), "merge into fallback target commits");
        c.EndFreeMasterQueueDropGhost();
        await Idle();
        Require(c.State.Papers.All(p => c.QueueKeyForPaper(p) == targetKey &&
                    p.CapsuleMonitorDeviceName == missingMonitor && p.CapsuleQueueId == "fallback"),
            "all carried papers must inherit the complete persistent destination identity");
        Require(Masters(c).Count == 1 && Masters(c).ContainsKey(targetKey), "merge leaves one destination master, not a phantom second queue");
        Require(c.State.Papers.All(p => windows[p.Id].IsCollapseAllRetracted), "merge retains the target's folded state");
        Near(before, Masters(c)[targetKey].AnchorTop, "merge keeps target visible at its original anchor");
        c.SaveNow(sync: true);
        var loaded = store.Load();
        Require(loaded.Papers.All(p => p.CapsuleMonitorDeviceName == missingMonitor && p.CapsuleQueueId == "fallback") &&
                loaded.CapsuleCollapseAllActiveQueues.GetValueOrDefault(targetKey),
            "fallback membership and folded state survive reload");
        Console.WriteLine("PASS new post-disconnect master gesture and complete destination identity on merge");
    }

    private static void CheckAnchorReconstruction()
    {
        EdgeQueueAnchor[][] cases =
        [
            [new("a", 50, 346, 50), new("b", 400, 196, 150), new("c", 600, 146, 350)],
            [new("a", 100, 346, 100), new("b", 450, 196, 200), new("c", 650, 146, 350)],
            // A shorter upstream source creates a real gap before the target.
            [new("z", 100, 46, 100), new("b", 200, 146, 120), new("a", 350, 96, 160)]
        ];
        foreach (var rows in cases)
        {
            var anchors = EdgeCapsuleQueueAnchorPlan.Build(rows, 4);
            var queues = rows.Select(row => new EdgeCapsuleRestingQueue("edge", row.Key,
                [new EdgeCapsuleRestingQueueMember(row.Key, anchors[row.Key])],
                anchors[row.Key], anchors[row.Key] + row.HeightDip)).ToArray();
            var offsets = EdgeCapsuleRestingLayoutCoordinator.BuildDownwardOffsets(queues, 4);
            foreach (var row in rows)
                Near(row.TopDip, anchors[row.Key] + offsets.GetValueOrDefault(row.Key),
                    "ordinary production spacing reproduces the complete chosen layout");
            var repeated = EdgeCapsuleQueueAnchorPlan.Build(rows.Select(row =>
                row with { PreferredTopDip = anchors[row.Key] }).ToArray(), 4);
            Require(anchors.All(pair => Math.Abs(pair.Value - repeated[pair.Key]) < .01),
                "repeated anchor conversion must not ratchet preferences");
        }
        Near(350, EdgeCapsuleQueueAnchorPlan.Build(cases[0], 4)["c"], "unchanged displaced neighbour retains preference");
        Near(350, EdgeCapsuleQueueAnchorPlan.Build(cases[1], 4)["c"], "downstream insertion displacement stays derived");
    }

    private static double MemberTop(AppController c, PaperWindow window, PaperData paper)
    {
        var presenter = Field<EdgeCapsulePresenter>(window, "_edgeCapsule");
        var placement = presenter.Placement;
        return EdgeCapsuleLayout.FreeQueueTopForIndex(placement.VisualIndex,
            c.DeepCapsuleStartTopMarginFor(paper), EdgeCapsuleLayout.LocalWorkAreaForQueue(paper.CapsuleMonitorDeviceName),
            c.DeepCapsuleGap) + placement.TopOffsetDip;
    }

    private static DeviceScreenPoint Point(MonitorGeometry monitor, double top, bool left = false) => new(
        left ? monitor.WorkArea.Left + 10 : monitor.WorkArea.Right - 10,
        monitor.WorkArea.Top + (top + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
    private static Dictionary<string, MasterCapsuleWindow> Masters(AppController c) =>
        Field<Dictionary<string, MasterCapsuleWindow>>(c, "_masterCapsules");
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) =>
        value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static async Task Idle() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    private static void Near(double expected, double actual, string name) =>
        Require(Math.Abs(expected - actual) < .11, $"{name}: expected {expected:F2}, actual {actual:F2}");
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static (FrameworkElement Pill, object Session, DeviceScreenPoint Start) PressMaster(MasterCapsuleWindow master)
    {
        var pill = Field<FrameworkElement>(master, "_pill");
        void IgnoreCaptureMove(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent == Mouse.PreviewMouseMoveEvent ||
                e.StagingItem.Input.RoutedEvent == Mouse.MouseMoveEvent) e.Cancel();
        }
        InputManager.Current.PreProcessInput += IgnoreCaptureMove;
        try
        {
            pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        }
        finally { InputManager.Current.PreProcessInput -= IgnoreCaptureMove; }
        var session = Field<object>(master, "_dragSession");
        Require(pill.IsMouseCaptured && session != null, "master press captures a new session");
        return (pill, session!, (DeviceScreenPoint)session!.GetType().GetProperty("StartScreenPosition")!.GetValue(session)!);
    }
}
