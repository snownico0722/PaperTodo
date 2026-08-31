using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly EdgeCapsulePresenter _edgeCapsule = new();

    private EdgeCapsuleSlotState EdgeCapsuleSlot => _edgeCapsule.State.Slot;
    private EdgeCapsuleVisualState EdgeCapsuleVisual => _edgeCapsule.State.Visual;
    private EdgeCapsuleGestureState EdgeCapsuleGesture => _edgeCapsule.State.Gesture;
    private EdgeCapsuleOpenOrigin EdgeCapsuleOrigin => _edgeCapsule.State.OpenOrigin;

    private bool HasDeepCapsuleSlotPlacement => EdgeCapsuleSlot != EdgeCapsuleSlotState.None;
    private bool HoldsDeepCapsuleSlotWhileExpanded => EdgeCapsuleSlot is
        EdgeCapsuleSlotState.ExpandedReserved or
        EdgeCapsuleSlotState.RetractedExpanded or
        EdgeCapsuleSlotState.RetractingExpanded;
    private bool IsDeepCapsuleSlotRetracting => EdgeCapsuleSlot is
        EdgeCapsuleSlotState.RetractingCollapsed or
        EdgeCapsuleSlotState.RetractingExpanded;
    private bool IsDeepCapsuleHovered => EdgeCapsuleVisual == EdgeCapsuleVisualState.Hovered;
    private bool IsDeepCapsuleSlotActive => EdgeCapsuleVisual == EdgeCapsuleVisualState.Active;
    private bool IsDeepCapsuleSlotPendingClick => EdgeCapsuleGesture == EdgeCapsuleGestureState.PendingClick;
    private bool IsDeepCapsuleDockedReordering => EdgeCapsuleGesture == EdgeCapsuleGestureState.DockedReordering;
    private bool IsDeepCapsuleFloatingReordering => EdgeCapsuleGesture == EdgeCapsuleGestureState.FloatingReordering;
    private bool IsDeepCapsuleDockingHandoff => EdgeCapsuleGesture is
        EdgeCapsuleGestureState.DockingHandoff or
        EdgeCapsuleGestureState.DockingReveal;
    private bool IsDeepCapsuleDockingFlight =>
        EdgeCapsuleGesture == EdgeCapsuleGestureState.DockingHandoff;
    private bool IsDeepCapsuleDockingReveal => EdgeCapsuleGesture == EdgeCapsuleGestureState.DockingReveal;
    private bool IsDeepCapsuleReordering => EdgeCapsuleGesture is
        EdgeCapsuleGestureState.DockedReordering or
        EdgeCapsuleGestureState.FloatingTransfer or
        EdgeCapsuleGestureState.FloatingReordering;
    private bool ExpandedFromDeepCapsuleEdge => EdgeCapsuleOrigin == EdgeCapsuleOpenOrigin.EdgeSlot;
    private bool IsDeepCapsuleRetractedIntoMaster => EdgeCapsuleSlot is
        EdgeCapsuleSlotState.RetractedCollapsed or
        EdgeCapsuleSlotState.RetractedExpanded;

    private bool AttachEdgeCapsuleToQueue(
        EdgeCapsulePlacement placement,
        EdgeCapsulePaperForm paperForm,
        bool retracted)
    {
        if (retracted)
        {
            CloseDeepCapsuleSlotContextMenu();
        }

        return DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.Attach(placement, paperForm, retracted));
    }

    private bool UpdateEdgeCapsuleQueuePlacement(EdgeCapsulePlacement placement) =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.QueuePlacementChanged(placement));

    private bool ChangeEdgeCapsulePaperForm(
        EdgeCapsulePaperForm paperForm,
        bool reserveWhileExpanded)
    {
        if (paperForm == EdgeCapsulePaperForm.Expanded && !reserveWhileExpanded)
        {
            CloseDeepCapsuleSlotContextMenu();
        }

        return DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PaperFormChanged(paperForm, reserveWhileExpanded));
    }

    private bool BeginEdgeCapsuleRetraction()
    {
        CloseDeepCapsuleSlotContextMenu();
        return DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.RetractionStarted());
    }

    private bool DetachEdgeCapsuleFromQueue()
    {
        CloseDeepCapsuleSlotContextMenu();
        return DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.Detached());
    }

    private bool SetEdgeCapsuleContextMenuOpen(bool open) =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.ContextMenuChanged(open),
            EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Pointer);

    internal void NotifyEdgeCapsulePeerReorderStarted() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PeerReorderStarted(),
            EdgeCapsuleDirty.Pointer);

    internal void NotifyEdgeCapsulePeerReorderFinished() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PeerReorderFinished(),
            EdgeCapsuleDirty.Pointer);

    private bool BeginEdgeCapsulePointerInteraction(DeviceScreenPoint point) =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.PointerPressed(point));

    private bool BeginEdgeCapsuleDockedReorder(
        DeviceScreenPoint point,
        string startMonitorDeviceName,
        double pointerOffsetY,
        double topDip) =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.DockedReorderStarted(
            point,
            startMonitorDeviceName,
            pointerOffsetY,
            topDip));

    private bool MoveEdgeCapsuleDockedReorder(
        DeviceScreenPoint point,
        double topDip) =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.DockedReorderMoved(point, topDip));

    private bool UpdateEdgeCapsuleDragPointer(DeviceScreenPoint point) =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.DragPointerMoved(point),
            EdgeCapsuleDirty.None);

    private bool UpdateEdgeCapsulePreviewIndex(int index) =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PreviewIndexChanged(index),
            EdgeCapsuleDirty.None);

    private bool BeginEdgeCapsuleFloatingTransfer(DeviceScreenPoint point) =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.FloatingTransferStarted(point));

    private bool BeginEdgeCapsuleFloatingReorder() =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.FloatingReorderStarted());

    private bool BeginEdgeCapsuleDockingHandoff() =>
        DispatchEdgeCapsuleIntent(EdgeCapsuleIntent.DockingHandoffStarted());

    private bool BeginEdgeCapsuleDockingReveal() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.DockingRevealStarted(),
            EdgeCapsuleDirty.None);

    private bool FinishEdgeCapsulePointerInteraction() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PointerInteractionFinished(),
            EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Pointer);

    private bool FinishEdgeCapsuleDockingHandoff() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.PointerInteractionFinished(),
            EdgeCapsuleDirty.None);

    private bool MarkEdgeCapsuleOpenedFromEdge() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.OpenedFromEdge(),
            EdgeCapsuleDirty.None);

    private bool ClearEdgeCapsuleOpenOrigin() =>
        DispatchEdgeCapsuleIntent(
            EdgeCapsuleIntent.OpenOriginCleared(),
            EdgeCapsuleDirty.None);

    private bool DispatchEdgeCapsuleIntent(
        EdgeCapsuleIntent intent,
        EdgeCapsuleDirty dirty = EdgeCapsuleDirty.Presentation,
        [CallerMemberName] string reason = "")
    {
        var result = _edgeCapsule.Dispatch(intent, reason);
        if (!result.Accepted)
        {
            return false;
        }
        if (result.Changed && dirty != EdgeCapsuleDirty.None)
        {
            InvalidateEdgeCapsule(dirty);
        }
        AssertDeepCapsuleModelInvariants();
        return true;
    }

    private bool TryGetEdgeCapsuleDragSession(out EdgeCapsuleDragSession session)
    {
        if (_edgeCapsule.DragSession is { } current)
        {
            session = current;
            return true;
        }

        Debug.Fail($"Edge-capsule gesture {EdgeCapsuleGesture} has no drag session.");
        session = default;
        return false;
    }

    [Conditional("DEBUG")]
    private void AssertDeepCapsuleModelInvariants()
    {
        Debug.Assert(
            !IsDeepCapsuleRetractedIntoMaster || EdgeCapsuleVisual == EdgeCapsuleVisualState.Resting,
            "A capsule retracted into the master must have resting semantics.");
        Debug.Assert(
            EdgeCapsuleSlot is not (
                EdgeCapsuleSlotState.CollapsedDocked or
                EdgeCapsuleSlotState.RetractedCollapsed or
                EdgeCapsuleSlotState.RetractingCollapsed) || _paper.IsCollapsed,
            "A collapsed edge-capsule state requires a collapsed paper model.");
        Debug.Assert(
            EdgeCapsuleSlot is not (
                EdgeCapsuleSlotState.ExpandedReserved or
                EdgeCapsuleSlotState.RetractedExpanded or
                EdgeCapsuleSlotState.RetractingExpanded) || !_paper.IsCollapsed,
            "An expanded edge-capsule state requires an expanded paper model.");
    }
}
