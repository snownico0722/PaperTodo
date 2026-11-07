using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static class NativeFreeQueueChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Part(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o)!;
    private static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Private)!.Invoke(o, args);
    private static EdgeCapsulePlacement Placement(PaperWindow w) => ((EdgeCapsulePresenter)Part(w, "_edgeCapsule")).Placement;
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle).Task;

    internal static void Prepare(AppState state)
    {
        state.UseCapsuleFreeQueues = true;
        state.UseCapsuleCollapseAll = false;
        state.EnableAnimations = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        state.Papers[0].Title = "A";
        state.Papers[1].Title = "Longer";
        for (var i = 0; i < state.Papers.Count; i++) state.Papers[i].CapsuleQueueId = i < 2 ? "qa" : i < 4 ? "qb" : "qc";
        state.DeepCapsuleQueueStartTopMargins["|right#qa"] = 100;
        state.DeepCapsuleQueueStartTopMargins["|right#qb"] = 350;
        state.DeepCapsuleQueueStartTopMargins["|right#qc"] = Math.Min(600, SystemParameters.WorkArea.Height - 80);
    }

    internal static async Task Run(AppController c, IReadOnlyDictionary<string, PaperWindow> windows, StateStore store)
    {
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var monitor), "fixture monitor");
        await Idle();
        var a = c.State.Papers.Single(p => p.Id == "fixture-0");
        var aPeer = c.State.Papers.Single(p => p.Id == "fixture-1");
        var b = c.State.Papers.Single(p => p.Id == "fixture-2");
        var bPeer = c.State.Papers.Single(p => p.Id == "fixture-3");
        var pitch = EdgeCapsuleLayout.SlotHeight(c.DeepCapsuleGap);
        DeviceScreenPoint Point(double top, bool distant = false, bool left = false) => new(
            left ? monitor.WorkArea.Left + 10 : distant ? monitor.WorkArea.Left + monitor.WorkArea.Width * .65 : monitor.WorkArea.Right - 10,
            monitor.WorkArea.Top + (top + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
        double Top(PaperData paper)
        {
            var placement = Placement(windows[paper.Id]);
            var area = EdgeCapsuleLayout.LocalWorkAreaForQueue(paper.CapsuleMonitorDeviceName);
            var margin = c.DeepCapsuleStartTopMarginFor(paper);
            return (c.State.UseCapsuleFreeQueues
                ? EdgeCapsuleLayout.FreeQueueTopForIndex(placement.VisualIndex, margin, area, c.DeepCapsuleGap)
                : EdgeCapsuleLayout.TopForIndex(placement.VisualIndex, margin, area, placement.SlotCount, c.DeepCapsuleGap))
                + placement.TopOffsetDip;
        }
        var margins = new Dictionary<string, double>(c.State.DeepCapsuleQueueStartTopMargins);
        // Exercise the real PaperWindow input entry points, not just the controller primitives.
        var ownerWindow = windows[a.Id];
        ownerWindow.GetType().GetField("_nativeQueuePointerDownKeys", Private)!.SetValue(ownerWindow, 8);
        Call(ownerWindow, "OnEdgeCapsulePointerPressed", Point(100));
        Call(ownerWindow, "OnEdgeCapsulePointerMoved", Point(140), true);
        await Idle();
        var presenter = (EdgeCapsulePresenter)Part(ownerWindow, "_edgeCapsule");
        Require(presenter.State.Gesture == EdgeCapsuleGestureState.DockedReordering && PartOrNull(ownerWindow, "_deepCapsuleFloatingDragHost") == null,
            "Ctrl plus vertical motion must stay docked and must not rent a floating host");
        Require(Math.Abs(Top(aPeer) - 190) < .1, "other members follow the docked Ctrl owner as a rigid block");
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value), "pointer-move preview must not persist anchors");
        Call(ownerWindow, "CancelDeepCapsuleReorderDrag", true);
        await Idle();
        Require(Math.Abs(Top(aPeer) - 150) < .1, "cancel restores the source layout through the existing arrange path");

        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin controller whole queue");
        var ownerTop = c.NativeEdgeQueueOwnerTop(a.Id, Point(140), 140);
        Require(Math.Abs(ownerTop - 140) < .1, "whole-queue pointer offset");
        c.PreviewNativeEdgeQueueDrag(a, Point(140), ownerTop, false);
        Require(Math.Abs(Top(aPeer) - 190) < .1, "whole queue placement preview");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        var originalTargetTop = Top(b);
        var originalNextTargetTop = Top(bPeer);

        // A confirmed slot previews only its affected suffix; it never pushes the
        // same queue again just because the pointer moved within that slot.
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false),
            "begin single-slot ghost and stable local insertion preview");
        var interiorTop = originalTargetTop + pitch;
        Require(c.PreviewNativeEdgeQueueDrag(a, Point(interiorTop), interiorTop, false),
            "whole-queue interior ghost preview handled");
        var interiorDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(interiorDrop?.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                interiorDrop.DestinationKey == "|right#qb" &&
                interiorDrop.Decision.Index == 1,
            "the ghost selects the actual interior target without committing it");
        Require(Math.Abs(Top(b) - originalTargetTop) < .1 &&
                Math.Abs(Top(bPeer) - originalNextTargetTop - 2 * pitch) < .1,
            "the target prefix stays fixed while the suffix makes one exact carried-block gap");
        c.PreviewNativeEdgeQueueDrag(a, Point(interiorTop + 2), interiorTop + 2, false);
        Require(Math.Abs(Top(bPeer) - originalNextTargetTop - 2 * pitch) < .1,
            "pointer motion within the same confirmed slot cannot progressively move the target");
        var ghost = (EdgeCapsuleQueueDropGhost)Part(c, "_nativeQueueDropGhost");
        var ghostWindow = (Window)Part(ghost, "_window");
        var ghostStack = (StackPanel)Part(ghost, "_stack");
        var ghostWidths = new[]
        {
            windows[a.Id].DeepCapsuleRestingVisibleWidthFor(monitor),
            windows[aPeer.Id].DeepCapsuleRestingVisibleWidthFor(monitor)
        };
        ghostWindow.UpdateLayout();
        Require(ghostWindow.IsVisible && ghostWindow.Opacity > 0 &&
                ghostStack.Children.Count == 2 &&
                Math.Abs(ghostWidths[0] - ghostWidths[1]) > 1 &&
                Math.Abs(ghostWindow.Width - ghostWidths.Max()) < .1 &&
                Math.Abs(((Border)ghostStack.Children[0]).ActualWidth -
                    (ghostWidths[0] - EdgeCapsuleLayout.WindowChromeMargin)) < .1 &&
                Math.Abs(((Border)ghostStack.Children[1]).ActualWidth -
                    (ghostWidths[1] - EdgeCapsuleLayout.WindowChromeMargin)) < .1,
            "Ctrl carrying two papers previews each docked capsule at its own width");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(Math.Abs(Top(bPeer) - originalNextTargetTop) < .1,
            "cancelling a local insertion preview restores the original layout");

        // A two-capsule insertion must also make room in the NEXT independent
        // queue when it would overlap, but only for the selected slot. No
        // continuous global pushing as the pointer moves within that slot.
        var neighbour = c.State.Papers.Single(p => p.CapsuleQueueId == "qc");
        var oldNeighbourMargin = c.State.DeepCapsuleQueueStartTopMargins["|right#qc"];
        c.State.DeepCapsuleQueueStartTopMargins["|right#qc"] = originalTargetTop + 2 * pitch + 20;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var neighbourBefore = Top(neighbour);
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false),
            "begin a multi-capsule merge with a neighbouring queue close below");
        c.PreviewNativeEdgeQueueDrag(a, Point(interiorTop), interiorTop, false);
        var displacedNeighbourTop = Top(neighbour);
        Require(displacedNeighbourTop > neighbourBefore + 2 &&
                Math.Abs(Top(bPeer) - originalNextTargetTop - 2 * pitch) < .1,
            $"two-capsule preview must open the target suffix and neighbour: " +
            $"neighbour={neighbourBefore:F1}->{displacedNeighbourTop:F1}, " +
            $"suffix={originalNextTargetTop:F1}->{Top(bPeer):F1}, " +
            $"expectedSuffix={originalNextTargetTop + 2 * pitch:F1}");
        c.PreviewNativeEdgeQueueDrag(a, Point(interiorTop + 2), interiorTop + 2, false);
        Require(Math.Abs(Top(neighbour) - displacedNeighbourTop) < .1,
            "the same insertion slot cannot push the neighbouring queue a second time");
        c.EndNativeEdgeQueueDrag(a.Id);
        c.State.DeepCapsuleQueueStartTopMargins["|right#qc"] = oldNeighbourMargin;
        c.ArrangeDeepCapsules(false);
        await Idle();

        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin prepend");
        var approachTop = originalTargetTop - (2 * pitch - c.DeepCapsuleGap);
        Require(c.PreviewNativeEdgeQueueDrag(a, Point(approachTop), approachTop, false), "approach preview handled");
        Require(((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop?.Decision.Kind == EdgeFreeQueueDropKind.Move &&
                Math.Abs(Top(b) - originalTargetTop) < .1 && Math.Abs(Top(bPeer) - originalNextTargetTop) < .1,
            "the carried tail touching the target keeps an independent ghost without pushing its members");
        var headTop = originalTargetTop - PaperLayoutDefaults.CapsuleHeight;
        Require(c.PreviewNativeEdgeQueueDrag(a, Point(headTop), headTop, false), "prepend preview handled");
        Require(Math.Abs(Top(b) - originalTargetTop - 2 * pitch) < .1 && Math.Abs(Top(bPeer) - originalNextTargetTop - 2 * pitch) < .1,
            "prepending moves existing members down by the whole carried block");
        Require(a.CapsuleQueueId == "qa" && b.CapsuleQueueId == "qb", "preview does not change queue membership");
        Require(c.CommitNativeEdgeQueueDrag(a, Point(headTop), headTop, false), "whole queue prepend handled");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(a.CapsuleQueueId == "qb" && aPeer.CapsuleQueueId == "qb", "whole-queue merge commits all carried members");
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb").Select(p => p.Id)
            .SequenceEqual(["fixture-0", "fixture-1", "fixture-2", "fixture-3"]), "block insertion preserves member order");
        Require(Math.Abs(Top(b) - originalTargetTop - 2 * pitch) < .1 && Math.Abs(Top(bPeer) - originalNextTargetTop - 2 * pitch) < .1,
            "committed prepend exactly reproduces the downward preview");
        Require(!c.State.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qa"), "vacated independent anchor is removed");

        // Four member slots: a, aPeer, b, bPeer. Snap is to the fixed outer slot,
        // not a continuous damping function. Both directions can snap repeatedly.
        var fourthTop = Top(bPeer);
        Require(c.BeginNativeEdgeQueueDrag(b, false, Point(Top(b)), false),
            "begin moving the third capsule in a four-member queue");
        Require(c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 7) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 14) == fourthTop,
            "third to fourth is free, then a fixed bottom-slot snap starts outside the queue");
        Require(c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 20) == fourthTop + 20 &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 9) == fourthTop + 9 &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 8) == fourthTop,
            "after 18-DIP release, inward travel is free and the next outward pull snaps again");
        var beyondDetach = fourthTop + EdgeCapsuleLayout.CrossQueueDragUnlockDistance + 1;
        c.PreviewNativeEdgeQueueDrag(b, Point(beyondDetach), beyondDetach, false);
        Require(((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Detached,
            "the member becomes detached after crossing, not merely touching, its 56-DIP boundary");
        Require(c.NativeEdgeQueueMagnetizedOwnerTop(b.Id,
                fourthTop + EdgeCapsuleLayout.CrossQueueDragUnlockDistance) ==
                fourthTop + EdgeCapsuleLayout.CrossQueueDragUnlockDistance &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(b.Id, fourthTop + 6) == fourthTop,
            "even a detached drag can be rearmed by returning to the source and pulling out again");
        c.EndNativeEdgeQueueDrag(b.Id);
        await Idle();

        Require(c.BeginNativeEdgeQueueDrag(bPeer, false, Point(Top(bPeer)), false),
            "begin dragging the final capsule beyond the last slot");
        fourthTop = Top(bPeer);
        Require(c.NativeEdgeQueueMagnetizedOwnerTop(bPeer.Id, fourthTop) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(bPeer.Id, fourthTop + 11) == fourthTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(bPeer.Id, fourthTop + 19) == fourthTop + 19,
            "fourth capsule snaps to the final slot and releases sharply after 18 DIP");
        c.EndNativeEdgeQueueDrag(bPeer.Id);
        await Idle();

        var firstTop = Top(a);
        Require(c.BeginNativeEdgeQueueDrag(a, false, Point(firstTop), false),
            "begin upward exit from the first slot");
        Require(c.NativeEdgeQueueMagnetizedOwnerTop(a.Id, firstTop) == firstTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(a.Id, firstTop - 9) == firstTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(a.Id, firstTop - 19) == firstTop - 19 &&
                c.NativeEdgeQueueMagnetizedOwnerTop(a.Id, firstTop) == firstTop &&
                c.NativeEdgeQueueMagnetizedOwnerTop(a.Id, firstTop - 7) == firstTop,
            "upward exit and repeated upward exit snap to the first slot, without viscous motion");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();

        Require(c.BeginNativeEdgeQueueDrag(a, false, Point(Top(a)), false), "begin single detach");
        c.PreviewNativeEdgeQueueDrag(a, Point(100), 100, false);
        c.CommitNativeEdgeQueueDrag(a, Point(100), 100, false);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(a.CapsuleQueueId != "qb" && Math.Abs(Top(a) - 100) < .1, "a single capsule forms a queue at the requested height");
        var beforeMove = c.State.Papers.Select(p => (p.Id, p.CapsuleQueueId)).ToArray();
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(100), false), "begin independent drop near another queue");
        c.PreviewNativeEdgeQueueDrag(a, Point(130, distant: true), 130, true);
        var independentGhost = (EdgeCapsuleQueueDropGhost)Part(c, "_nativeQueueDropGhost");
        Require(((Window)Part(independentGhost, "_window")).Opacity > 0 &&
                ((StackPanel)Part(independentGhost, "_stack")).Children.Count == 1,
            "independent placement shows its own single-capsule landing position");
        c.CommitNativeEdgeQueueDrag(a, Point(130, distant: true), 130, true);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(c.State.Papers.Select(p => (p.Id, p.CapsuleQueueId)).SequenceEqual(beforeMove) &&
                Math.Abs(Top(a) - 130) < .1, "an independent floating landing outside all vertical merge slots keeps its exact anchor");

        // A floating drag previews a confirmed vertical target even from the middle of
        // the screen; the mouse does not first have to touch the edge or ghost window.
        var wallDip = monitor.LocalWorkAreaDip.Width * .35;
        var distantFloatingPoint = new DeviceScreenPoint(
            monitor.WorkArea.Right - wallDip * monitor.DpiScaleX,
            monitor.WorkArea.Top + (Top(b) + PaperLayoutDefaults.CapsuleHeight / 2) * monitor.DpiScaleY);
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin approach from far from the wall");
        c.PreviewNativeEdgeQueueDrag(a, distantFloatingPoint, Top(a), true);
        var distantDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(distantDrop?.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                distantDrop.DestinationKey == "|right#qb" &&
                ((Window)Part(Part(c, "_nativeQueueDropGhost"), "_window")).Opacity > 0,
            "far-from-edge floating preview displays an actual merge ghost at the chosen target slot");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();

        c.State.UseCapsuleFreeQueues = false;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var rememberedIds = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        var legacyTop = Top(a);
        Require(!c.BeginNativeEdgeQueueDrag(a, true, Point(legacyTop), false), "Ctrl cannot start an ordinary queue drag when free queues are disabled");
        c.PreviewNativeEdgeQueueDrag(a, Point(legacyTop + 60), legacyTop + 60, false);
        c.CommitNativeEdgeQueueDrag(a, Point(legacyTop + 60), legacyTop + 60, false);
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(Math.Abs(Top(a) - legacyTop) < .1, "a rejected Ctrl queue drag cannot move the ordinary queue");
        Require(c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(rememberedIds), "legacy mode preserves free grouping memory");
        c.State.UseCapsuleFreeQueues = true;
        c.State.DeepCapsuleQueueStartTopMargins["|right"] = 48;
        c.ArrangeDeepCapsules(false);
        await Idle();
        c.PublicSettings.Set("capsule.master_enabled", JsonSerializer.SerializeToElement(true));
        await Idle();
        Require(c.State.UseCapsuleFreeQueues && c.State.UseCapsuleCollapseAll,
            "free queues and master coexist via the settings API");
        var masters = (Dictionary<string, MasterCapsuleWindow>)Part(c, "_masterCapsules");
        var aKey = c.QueueKeyForPaper(a);
        Require(masters.Count == 3 && masters.ContainsKey(aKey) &&
                masters.ContainsKey("|right#qb") && masters.ContainsKey("|right#qc"),
            "every independent group has its own master on the same edge");

        var upwardMasterLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qc")!;
        var upperMasterTop = masters["|right#qb"].AnchorTop;
        c.PreviewFreeMasterQueueTop(upwardMasterLayout, upperMasterTop, Point(upperMasterTop));
        Require(Math.Abs(masters["|right#qc"].AnchorTop - upperMasterTop) < .1 &&
                ((Window)Part(Part(c, "_nativeQueueDropGhost"), "_window")).Opacity > 0,
            "dragging a master straight UP may overlap and target the upper master without being pushed below it");
        c.FinishFreeMasterQueueDrag(upwardMasterLayout, false);
        await Idle();

        var boundedMasterLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qc")!;
        c.PreviewFreeMasterQueueTop(boundedMasterLayout, -200);
        Require(masters["|right#qc"].AnchorTop >= -.1,
            "actively dragged master may not be pulled above the top of its screen");
        c.PreviewFreeMasterQueueTop(boundedMasterLayout, monitor.LocalWorkAreaDip.Height + 200);
        Require(masters["|right#qc"].AnchorTop +
                (boundedMasterLayout.Groups[0].PaperIds.Length + 1) * pitch - c.DeepCapsuleGap <=
                    monitor.LocalWorkAreaDip.Height + .1,
            "a movable queue that fits must stop before its final capsule leaves the bottom of the screen");
        c.FinishFreeMasterQueueDrag(boundedMasterLayout, false);
        await Idle();

        var masterForMove = masters["|right#qb"];
        var originalMasterPosition = masterForMove.AnchorTop;
        var originalAnimations = c.State.EnableAnimations;
        c.State.EnableAnimations = true;
        masterForMove.SetQueueTopPreview(originalMasterPosition + 10);
        masterForMove.UpdateState(2, false, animate: true);
        Require(Math.Abs(masterForMove.AnchorTop - (originalMasterPosition + 10)) < .1,
            "passive master movement publishes its logical endpoint immediately");
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        Require(c.TryGetEdgeCapsuleQueueProxyPresentation(masterForMove, out var movingMaster) &&
                movingMaster.Visible, "passive master movement uses the shared queue compositor");
        c.CompleteEdgeCapsuleQueueCompositionProxyFor(masterForMove, success: true);
        Require(masterForMove.VerifyQueueProxyEndpoint(masterForMove.QueueProxyTarget),
            "master handoff settles the real HWND at its exact logical endpoint");
        masterForMove.SetQueueTopPreview(null);
        masterForMove.UpdateState(2, false, animate: true);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send);
        c.CompleteEdgeCapsuleQueueCompositionProxyFor(masterForMove, success: true);
        c.State.EnableAnimations = originalAnimations;
        await Idle();
        var masterLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, aKey)!;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
                masterLayout.MasterTopDip, out var masterPreviewSnapshot,
                freeLayout: masterLayout, sourceQueueKey: aKey),
            "begin a direct master-capsule transfer preview");
        c.PreviewFreeMasterQueueDropGhost(masterPreviewSnapshot, Point(Top(b), distant: true));
        var masterGhost = (EdgeCapsuleQueueDropGhost)Part(c, "_nativeQueueDropGhost");
        Require(((Window)Part(masterGhost, "_window")).Opacity > 0 &&
                ((StackPanel)Part(masterGhost, "_stack")).Children.Count == masterLayout.Groups[0].PaperIds.Length,
            "dragging the master shows one merged landing ghost per carried member even far from the edge");
        c.EndFreeMasterQueueDropGhost();
        c.CancelMasterCapsuleQueueTransfer(masterPreviewSnapshot);
        await Idle();
        Require(masters.ContainsKey(aKey) && masters[aKey].IsVisible,
            "cancelling master preview restores its source without leaving a ghost");
        // With independent masters, only the actually affected suffix moves in
        // the preview; the target master and unrelated queues stay anchored.
        var untouchedPeerTop = Top(bPeer);
        var untouchedMasterTop = masters["|right#qb"].AnchorTop;
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin free-queue ghost with independently visible masters");
        var masterTargetTop = Top(b) + pitch;
        Require(c.PreviewNativeEdgeQueueDrag(a, Point(masterTargetTop), masterTargetTop, false),
            "master-enabled interior ghost preview handled");
        var hoverDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(hoverDrop?.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                hoverDrop.DestinationKey == "|right#qb",
            "master-enabled ghost identifies the existing target queue");
        Require(Math.Abs(Top(bPeer) - untouchedPeerTop - pitch) < .1 &&
                Math.Abs(masters["|right#qb"].AnchorTop - untouchedMasterTop) < .1,
            "a slot insertion creates one local space without moving the master");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();
        Require(Math.Abs(Top(bPeer) - untouchedPeerTop) < .1,
            "cancelled master-enabled preview returns the target suffix");
        foreach (var wholeQueue in new[] { false, true })
        {
            Require(c.BeginNativeEdgeQueueDrag(a, wholeQueue, Point(Top(a)), false),
                "begin floating drag of the last queue member");
            if (wholeQueue)
            {
                Require(c.NativeEdgeQueueCarriesMaster(a.Id), "Ctrl includes the queue master");
                c.PrepareNativeEdgeQueueFloating(a.Id);
            }
            c.PreviewNativeEdgeQueueDrag(a, Point(100, distant: true), 100, floating: true);
            Require(!masters[aKey].IsVisible &&
                    ((Window)Part(Part(c, "_nativeQueueDropGhost"), "_window")).IsVisible,
                "lifting the last member must withdraw its master and show a landing projection");
            c.EndNativeEdgeQueueDrag(a.Id);
            await Idle();
            Require(masters[aKey].IsVisible,
                "cancelling the last-member drag restores the source master");
        }

        var idsBeforeCollection = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        margins = new(c.State.DeepCapsuleQueueStartTopMargins);
        c.ToggleCapsuleCollapseAllActive("|right#qb");
        await Idle();
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
                    .All(p => windows[p.Id].IsCollapseAllRetracted) &&
                c.State.Papers.Where(p => p.CapsuleQueueId != "qb")
                    .All(p => !windows[p.Id].IsCollapseAllRetracted),
            "toggling one master retracts only its own members");
        // Turning the optional free mode off must not discard an individual
        // queue's collapsed choice. Legacy mode can temporarily use a side-wide master.
        c.State.UseCapsuleFreeQueues = false;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(c.State.CapsuleCollapseAllActiveQueues.TryGetValue("|right#qb", out var rememberedFold) &&
                rememberedFold, "legacy mode retains the free queue's remembered collapsed state");
        c.State.UseCapsuleFreeQueues = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(masters.Count == 3 && windows[b.Id].IsCollapseAllRetracted &&
                !windows[a.Id].IsCollapseAllRetracted,
            "returning to free mode restores only the previously folded queue");

        // A collapsed target accepts Ctrl-whole-queue drops only near its
        // visible master, never over its hidden expanded member positions.
        var foldedMasterTop = masters["|right#qb"].AnchorTop;
        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin Ctrl whole-queue probe against folded target");
        c.PreviewNativeEdgeQueueDrag(a, Point(foldedMasterTop + 3 * pitch), Top(a), true);
        var phantomDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(phantomDrop?.DestinationKey != "|right#qb" ||
                phantomDrop.Decision.Kind != EdgeFreeQueueDropKind.Insert,
            "hidden member region is not a folded queue merge target");
        c.PreviewNativeEdgeQueueDrag(a, Point(foldedMasterTop), Top(a), true);
        var masterDrop = ((NativeEdgeQueueDrag)Part(c, "_nativeEdgeQueueDrag")).Drop;
        Require(masterDrop?.Decision.Kind == EdgeFreeQueueDropKind.Insert &&
                masterDrop.DestinationKey == "|right#qb",
            "Ctrl whole-queue merges when the pointer reaches the visible master");
        c.EndNativeEdgeQueueDrag(a.Id);
        await Idle();

        c.ToggleCapsuleCollapseAllActive("|right#qb");
        await Idle();
        Require(windows.Values.All(w => !w.IsCollapseAllRetracted) &&
                c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(idsBeforeCollection) &&
                margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "expanding a single master restores the original groups and anchors");

        var layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qb")!;
        c.PreviewFreeMasterQueueTop(layout, layout.MasterTopDip - 20);
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "master preview never leaks to autosave");
        c.FinishFreeMasterQueueDrag(layout, false);
        await Idle();
        Require(margins.All(p => c.State.DeepCapsuleQueueStartTopMargins[p.Key] == p.Value),
            "canceled individual master movement preserves preferences");
        layout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qb")!;
        var otherAnchor = c.State.DeepCapsuleQueueStartTopMargins["|right#qc"];
        c.PreviewFreeMasterQueueTop(layout, layout.MasterTopDip - 10);
        c.FinishFreeMasterQueueDrag(layout, true);
        await Idle();
        Require(Math.Abs(masters["|right#qb"].AnchorTop - layout.MasterTopDip + 10) < .1,
            $"one master commits its queue translation: actual={masters["|right#qb"].AnchorTop:F1}, " +
            $"expected={layout.MasterTopDip - 10:F1}");
        Require(c.State.DeepCapsuleQueueStartTopMargins["|right#qc"] == otherAnchor,
            "moving a master never rewrites another queue anchor");

        var transferIds = c.State.Papers.Select(p => p.CapsuleQueueId).ToArray();
        var transferTops = c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
            .ToDictionary(p => p.Id, Top);
        var masterTop = masters["|right#qb"].AnchorTop;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right, masterTop, out var transfer,
            sourceQueueKey: "|right#qb"), "individual master transfer begins");
        Require(c.CommitMasterCapsuleQueueTransfer(transfer, Point(masterTop, left: true)),
            "individual master transfer commits");
        await Idle();
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb").All(p => p.CapsuleSide == DeepCapsuleSides.Left) &&
                c.State.Papers.Where(p => p.CapsuleQueueId != "qb").All(p => p.CapsuleSide == DeepCapsuleSides.Right) &&
                c.State.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(transferIds),
            "cross-edge master movement moves only its own independent queue");
        Require(c.State.Papers.Where(p => p.CapsuleQueueId == "qb")
                .All(p => Math.Abs(Top(p) - transferTops[p.Id]) < .1),
            "cross-edge transfer retains member positions");
        Require(masters.Count == 3 && masters.ContainsKey("|left#qb") &&
                masters.ContainsKey("|right#qc") && masters.ContainsKey(aKey),
            "only the transferred master changes its physical edge");

        // Vertical preview must be withdrawn before floating, including same-edge release.
        var sameEdgeLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Left, "|left#qb")!;
        c.PreviewFreeMasterQueueTop(sameEdgeLayout, sameEdgeLayout.MasterTopDip + 12);
        Require(PartOrNull(c, "_freeMasterDragPreview") != null, "master has a docked preview before floating");
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Left, sameEdgeLayout.MasterTopDip,
            out var sameEdgeTransfer, freeLayout: sameEdgeLayout, sourceQueueKey: "|left#qb"),
            "same-edge master transfer begins after preview");
        Require(PartOrNull(c, "_freeMasterDragPreview") == null,
            "floating transfer clears the old docked preview on entry");
        Require(c.CommitMasterCapsuleQueueTransfer(sameEdgeTransfer,
                Point(sameEdgeLayout.MasterTopDip + 12, left: true)),
            "same-edge master transfer commits after preview");
        await Idle();
        Require(Math.Abs(masters["|left#qb"].AnchorTop - sameEdgeLayout.MasterTopDip - 12) < .1,
            "master uses the committed same-edge anchor");

        c.SaveNow(sync: true);
        var saved = store.Load();
        Require(saved.UseCapsuleFreeQueues && saved.UseCapsuleCollapseAll &&
                saved.Papers.Select(p => p.CapsuleQueueId).SequenceEqual(transferIds),
            "separate masters and their groups survive a storage round trip");

        Require(c.BeginNativeEdgeQueueDrag(a, true, Point(Top(a)), false),
            "begin external-mutation cancellation");
        a.CapsuleQueueId = "external";
        c.EndNativeEdgeQueueDrag(a.Id);
        Require(a.CapsuleQueueId == "external",
            "cancellation does not overwrite a legitimate external identity change");

        // A neighboring queue can be pushed down for display without changing its saved margin.
        a.CapsuleSide = DeepCapsuleSides.Left;
        c.State.UseCapsuleCollapseAll = false;
        c.State.DeepCapsuleQueueStartTopMargins["|left#external"] = 90;
        const double preferredSourceTop = 100;
        c.State.DeepCapsuleQueueStartTopMargins["|left#qb"] = preferredSourceTop;
        c.ArrangeDeepCapsules(false);
        await Idle();
        var displacedSourceTop = Top(aPeer);
        Require(displacedSourceTop > preferredSourceTop + 1,
            "source has a passive display offset");
        Require(c.BeginNativeEdgeQueueDrag(aPeer, false, Point(displacedSourceTop, left: true), false),
            "begin detaching from a passively displaced queue");
        var detachedTop = Math.Min(420, monitor.LocalWorkAreaDip.Height - 140);
        var detachedPoint = Point(detachedTop, left: true);
        Require(c.PreviewNativeEdgeQueueDrag(aPeer, detachedPoint, detachedTop, false),
            "single detach preview is handled");
        Require(c.CommitNativeEdgeQueueDrag(aPeer, detachedPoint, detachedTop, false),
            "single detach drop is handled");
        c.EndNativeEdgeQueueDrag(aPeer.Id);
        await Idle();
        Require(aPeer.CapsuleQueueId != "qb", "single capsule was detached");
        Require(Math.Abs(c.State.DeepCapsuleQueueStartTopMargins["|left#qb"] - preferredSourceTop) < .1,
            "single detach never saves the source's passive displacement");
        // Merging two independently controlled groups keeps the target master, including
        // when the target was already collapsed, and retires only the source master.
        foreach (var paper in c.State.Papers)
        {
            paper.CapsuleMonitorDeviceName = "";
            paper.CapsuleSide = DeepCapsuleSides.Right;
            paper.CapsuleQueueId = paper.Id is "fixture-0" or "fixture-1" ? "qm" : "qn";
        }
        c.State.CapsuleCollapseAllActiveQueues.Clear();
        c.State.DeepCapsuleQueueStartTopMargins.Clear();
        c.State.DeepCapsuleQueueStartTopMargins["|right#qm"] = 80;
        c.State.DeepCapsuleQueueStartTopMargins["|right#qn"] = 330;
        c.State.UseCapsuleCollapseAll = true;
        c.ArrangeDeepCapsules(false);
        await Idle();
        Require(masters.Count == 2 && masters.ContainsKey("|right#qm") && masters.ContainsKey("|right#qn"),
            "two groups create two independent masters");

        // Expanded direct-master transfer of THREE members into the upper queue:
        // entering its second member slot must move that suffix by exactly 3 pitches.
        var lowerMasterLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qn")!;
        var upperMembers = c.State.Papers.Where(p => p.CapsuleQueueId == "qm").ToList();
        var upperSecondBefore = Top(upperMembers[1]);
        c.PreviewFreeMasterQueueTop(lowerMasterLayout, upperSecondBefore, Point(upperSecondBefore));
        var upperSecondShifted = Top(upperMembers[1]);
        Require(Math.Abs(upperSecondShifted - upperSecondBefore -
                lowerMasterLayout.Groups[0].PaperIds.Length * pitch) < .1 &&
                ((StackPanel)Part(Part(c, "_nativeQueueDropGhost"), "_stack")).Children.Count ==
                    lowerMasterLayout.Groups[0].PaperIds.Length,
            "a vertically dragged master carrying multiple capsules reserves their entire insertion space");
        c.PreviewFreeMasterQueueTop(lowerMasterLayout, upperSecondBefore + 2, Point(upperSecondBefore + 2));
        Require(Math.Abs(Top(upperMembers[1]) - upperSecondShifted) < .1,
            "within one chosen slot the multi-capsule preview stays stable without progressive pushing");
        c.FinishFreeMasterQueueDrag(lowerMasterLayout, false);
        await Idle();
        Require(Math.Abs(Top(upperMembers[1]) - upperSecondBefore) < .1,
            "cancelling a multi-capsule master insertion restores the upper queue");

        var targetOldTop = Top(b);
        c.ToggleCapsuleCollapseAllActive("|right#qn");
        await Idle();
        var mergeMasterTop = masters["|right#qn"].AnchorTop;
        Require(c.TryBeginMasterCapsuleQueueTransfer("", EdgeCapsuleEdge.Right,
            masters["|right#qm"].AnchorTop, out var phantomTransfer, sourceQueueKey: "|right#qm"),
            "begin master drag toward a folded queue's old member area");
        Require(c.CommitMasterCapsuleQueueTransfer(phantomTransfer, Point(mergeMasterTop + 3 * pitch)),
            "independent drop in hidden member area succeeds without merging");
        await Idle();
        Require(masters.Count == 2 && masters.ContainsKey("|right#qm") &&
                masters.ContainsKey("|right#qn"),
            "master drag cannot merge into a collapsed queue through invisible slots");
        var directMergeLayout = c.CaptureFreeMasterDragLayout("", EdgeCapsuleEdge.Right, "|right#qm")!;
        c.PreviewFreeMasterQueueTop(directMergeLayout, mergeMasterTop, Point(mergeMasterTop));
        Require(Math.Abs(masters["|right#qm"].AnchorTop - mergeMasterTop) < .1 &&
                ((Window)Part(Part(c, "_nativeQueueDropGhost"), "_window")).Opacity > 0,
            "docked master can approach the upper collapsed master and show a confirmed merge");
        var oldSourceMargin = c.State.DeepCapsuleQueueStartTopMargins["|right#qm"];
        Require(c.TryCommitDockedFreeMasterQueueMerge(
                directMergeLayout, Point(mergeMasterTop), oldSourceMargin, true),
            "dropping a docked master onto an upper master merges directly without a horizontal pull");
        await Idle();
        Require(c.State.Papers.All(p => p.CapsuleQueueId == "qn") &&
                masters.Count == 1 && masters.ContainsKey("|right#qn"),
            "merged source master retires while target master remains");
        Require(c.State.Papers.All(p => windows[p.Id].IsCollapseAllRetracted),
            "merging into a collapsed master preserves the target's collapse choice");
        Require(Math.Abs(Top(b) - targetOldTop) < .1 &&
                !c.State.DeepCapsuleQueueStartTopMargins.ContainsKey("|right#qm"),
            "merging into a master preserves old target members and removes the vacant anchor");

        Console.WriteLine("PASS native-free-queue-input-controller-master-and-roundtrip");
    }

    private static object? PartOrNull(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o);
}
