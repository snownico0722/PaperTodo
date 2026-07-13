using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Input adapter for the rest of PaperWindow. Every mutation is reduced as an event; this
    // partial class exposes no independently writable edge-capsule state.
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
    private bool IsDeepCapsuleFloatingTransfer => EdgeCapsuleGesture == EdgeCapsuleGestureState.FloatingTransfer;
    private bool IsDeepCapsuleFloatingReordering => EdgeCapsuleGesture == EdgeCapsuleGestureState.FloatingReordering;
    private bool IsDeepCapsuleReordering =>
        IsDeepCapsuleDockedReordering || IsDeepCapsuleFloatingTransfer || IsDeepCapsuleFloatingReordering;
    private bool ExpandedFromDeepCapsuleEdge => EdgeCapsuleOrigin == EdgeCapsuleOpenOrigin.EdgeSlot;
    private bool IsDeepCapsuleRetractedIntoMaster => EdgeCapsuleSlot is
        EdgeCapsuleSlotState.RetractedCollapsed or
        EdgeCapsuleSlotState.RetractedExpanded;

    private void SetEdgeCapsuleSlotState(
        EdgeCapsuleSlotState state,
        [CallerMemberName] string reason = "")
    {
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.SlotChanged(state),
            affectsPresentation: true,
            reason);
        AssertDeepCapsuleModelInvariants();
    }

    private void BeginEdgeCapsuleSlotRetraction()
    {
        SetEdgeCapsuleSlotState(_paper.IsCollapsed
            ? EdgeCapsuleSlotState.RetractingCollapsed
            : EdgeCapsuleSlotState.RetractingExpanded);
    }

    private void SetEdgeCapsuleSlotForPaperForm(bool collapsed, bool reserveWhileExpanded)
    {
        var target = IsDeepCapsuleRetractedIntoMaster
            ? collapsed
                ? EdgeCapsuleSlotState.RetractedCollapsed
                : EdgeCapsuleSlotState.RetractedExpanded
            : collapsed
                ? EdgeCapsuleSlotState.CollapsedDocked
                : reserveWhileExpanded
                    ? EdgeCapsuleSlotState.ExpandedReserved
                    : EdgeCapsuleSlotState.None;
        SetEdgeCapsuleSlotState(target);
    }

    private void SetEdgeCapsuleVisualState(EdgeCapsuleVisualState state)
    {
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.VisualChanged(state),
            affectsPresentation: true);
        AssertDeepCapsuleModelInvariants();
    }

    private void SetEdgeCapsuleGestureState(
        EdgeCapsuleGestureState state,
        [CallerMemberName] string reason = "")
    {
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.GestureChanged(state),
            affectsPresentation: true,
            reason);
    }

    private void SetEdgeCapsuleOpenOrigin(EdgeCapsuleOpenOrigin origin) =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.OpenOriginChanged(origin),
            affectsPresentation: false);

    private void SetEdgeCapsulePlacement(EdgeCapsulePlacement placement) =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.PlacementChanged(placement),
            affectsPresentation: true);

    private void SetEdgeCapsuleContextMenuOpen(bool open) =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.ContextMenuChanged(open),
            affectsPresentation: false);

    private void SetEdgeCapsuleDockedDragTop(double topDip) =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.DockedDragTopChanged(topDip),
            affectsPresentation: true);

    private bool DispatchEdgeCapsuleEvent(
        EdgeCapsuleEvent input,
        bool affectsPresentation,
        [CallerMemberName] string reason = "")
    {
        var previous = _edgeCapsule.Model;
        if (!_edgeCapsule.Dispatch(input, reason))
        {
            return false;
        }
        if (affectsPresentation && _edgeCapsule.Model != previous)
        {
            InvalidateEdgeCapsule(EdgeCapsuleDirty.Presentation);
        }
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
        session = null!;
        return false;
    }

    private void BeginEdgeCapsulePointerInteraction(DeviceScreenPoint pointerDownScreenPosition) =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.PointerStarted(pointerDownScreenPosition),
            affectsPresentation: true);

    private void FinishEdgeCapsulePointerInteraction() =>
        DispatchEdgeCapsuleEvent(
            EdgeCapsuleEvent.PointerFinished(),
            affectsPresentation: true);

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
