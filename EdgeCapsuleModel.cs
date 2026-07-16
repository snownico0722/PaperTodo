namespace PaperTodo;

internal enum EdgeCapsuleSlotState
{
    None,
    CollapsedDocked,
    ExpandedReserved,
    RetractedCollapsed,
    RetractedExpanded,
    RetractingCollapsed,
    RetractingExpanded
}

internal enum EdgeCapsuleVisualState
{
    Resting,
    Hovered,
    Active
}

internal enum EdgeCapsuleGestureState
{
    Idle,
    PendingClick,
    DockedReordering,
    FloatingTransfer,
    FloatingReordering,
    // Pointer input has ended and the destination queue is committed. The floating HWND still
    // covers the suppressed docked host while it animates into the authoritative target frame.
    DockingHandoff
}

internal enum EdgeCapsuleOpenOrigin
{
    Normal,
    EdgeSlot
}

internal enum EdgeCapsulePaperForm
{
    Collapsed,
    Expanded
}

internal enum EdgeCapsuleCaptureAction
{
    None,
    IgnoreExpectedTransfer,
    Recapture,
    CancelDrag
}

[Flags]
internal enum EdgeCapsuleDirty
{
    None = 0,
    Presentation = 1 << 0,
    Measure = 1 << 1,
    Pointer = 1 << 2,
    Frame = 1 << 3,
    // Apply can fail transiently while Windows is completing a per-monitor DPI or display-topology
    // hand-off. This flag is internal scheduler work, distinct from Measure deferred by a drag.
    ApplyRetry = 1 << 4,
    // Unlike a title-only Measure, display geometry cannot be replayed from the previous drag
    // snapshot. Keep the whole display batch deferred until the gesture ends.
    DisplayMetrics = 1 << 5
}

internal readonly record struct EdgeCapsuleState(
    EdgeCapsuleSlotState Slot,
    EdgeCapsuleVisualState Visual,
    EdgeCapsuleGestureState Gesture,
    EdgeCapsuleOpenOrigin OpenOrigin)
{
    public static EdgeCapsuleState Initial => new(
        EdgeCapsuleSlotState.None,
        EdgeCapsuleVisualState.Resting,
        EdgeCapsuleGestureState.Idle,
        EdgeCapsuleOpenOrigin.Normal);
}

internal readonly record struct EdgeCapsuleDragSession(
    DeviceScreenPoint PointerDownScreenPosition,
    DeviceScreenPoint LastScreenPosition,
    string StartMonitorDeviceName,
    double DockedPointerOffsetY,
    int PreviewIndex)
{
    public static EdgeCapsuleDragSession Begin(DeviceScreenPoint point) => new(
        point,
        point,
        "",
        0,
        -1);
}

internal readonly record struct EdgeCapsuleModel(
    EdgeCapsuleState State,
    EdgeCapsulePlacement Placement,
    EdgeCapsuleDragSession? DragSession,
    bool ContextMenuOpen,
    bool PeerReorderActive,
    double? DockedDragTopDipOverride)
{
    public static EdgeCapsuleModel Initial => new(
        EdgeCapsuleState.Initial,
        EdgeCapsulePlacement.None,
        null,
        false,
        false,
        null);
}

/// <summary>
/// Product-level input to the edge-capsule state machine. Callers describe what happened; the
/// reducer derives Slot/Visual/Gesture/Placement atomically instead of accepting field setters.
/// </summary>
internal abstract record EdgeCapsuleIntent
{
    internal sealed record AttachQueue(
        EdgeCapsulePlacement Placement,
        EdgeCapsulePaperForm PaperForm,
        bool Retracted) : EdgeCapsuleIntent;

    internal sealed record ChangeQueuePlacement(
        EdgeCapsulePlacement Placement) : EdgeCapsuleIntent;

    internal sealed record ChangePaperForm(
        EdgeCapsulePaperForm PaperForm,
        bool ReserveWhileExpanded) : EdgeCapsuleIntent;

    internal sealed record BeginRetraction : EdgeCapsuleIntent;
    internal sealed record DetachQueue : EdgeCapsuleIntent;
    internal sealed record CompleteRetraction : EdgeCapsuleIntent;
    internal sealed record SamplePointer(bool OverInteractiveSurface) : EdgeCapsuleIntent;
    internal sealed record ChangeContextMenu(bool Open) : EdgeCapsuleIntent;
    internal sealed record BeginPeerReorder : EdgeCapsuleIntent;
    internal sealed record FinishPeerReorder : EdgeCapsuleIntent;
    internal sealed record BeginPointer(DeviceScreenPoint Point) : EdgeCapsuleIntent;

    internal sealed record BeginDockedReorder(
        DeviceScreenPoint Point,
        string StartMonitorDeviceName,
        double PointerOffsetY,
        double TopDip) : EdgeCapsuleIntent;

    internal sealed record MoveDockedReorder(
        DeviceScreenPoint Point,
        double TopDip) : EdgeCapsuleIntent;

    internal sealed record MoveDragPointer(DeviceScreenPoint Point) : EdgeCapsuleIntent;
    internal sealed record ChangePreviewIndex(int Index) : EdgeCapsuleIntent;
    internal sealed record BeginFloatingTransfer(DeviceScreenPoint Point) : EdgeCapsuleIntent;
    internal sealed record BeginFloatingReorder : EdgeCapsuleIntent;
    internal sealed record BeginDockingHandoff : EdgeCapsuleIntent;
    internal sealed record FinishPointer : EdgeCapsuleIntent;
    internal sealed record MarkOpenedFromEdge : EdgeCapsuleIntent;
    internal sealed record ClearOpenOrigin : EdgeCapsuleIntent;
    internal sealed record Reset : EdgeCapsuleIntent;

    public static EdgeCapsuleIntent Attach(
        EdgeCapsulePlacement placement,
        EdgeCapsulePaperForm paperForm,
        bool retracted) =>
        new AttachQueue(placement, paperForm, retracted);

    public static EdgeCapsuleIntent QueuePlacementChanged(EdgeCapsulePlacement placement) =>
        new ChangeQueuePlacement(placement);

    public static EdgeCapsuleIntent PaperFormChanged(
        EdgeCapsulePaperForm paperForm,
        bool reserveWhileExpanded) =>
        new ChangePaperForm(paperForm, reserveWhileExpanded);

    public static EdgeCapsuleIntent RetractionStarted() =>
        new BeginRetraction();

    public static EdgeCapsuleIntent Detached() =>
        new DetachQueue();

    public static EdgeCapsuleIntent RetractionCompleted() =>
        new CompleteRetraction();

    public static EdgeCapsuleIntent PointerSampled(bool overInteractiveSurface) =>
        new SamplePointer(overInteractiveSurface);

    public static EdgeCapsuleIntent ContextMenuChanged(bool open) =>
        new ChangeContextMenu(open);

    public static EdgeCapsuleIntent PeerReorderStarted() =>
        new BeginPeerReorder();

    public static EdgeCapsuleIntent PeerReorderFinished() =>
        new FinishPeerReorder();

    public static EdgeCapsuleIntent PointerPressed(DeviceScreenPoint point) =>
        new BeginPointer(point);

    public static EdgeCapsuleIntent DockedReorderStarted(
        DeviceScreenPoint point,
        string startMonitorDeviceName,
        double pointerOffsetY,
        double topDip) =>
        new BeginDockedReorder(
            point,
            startMonitorDeviceName,
            pointerOffsetY,
            topDip);

    public static EdgeCapsuleIntent DockedReorderMoved(
        DeviceScreenPoint point,
        double topDip) =>
        new MoveDockedReorder(point, topDip);

    public static EdgeCapsuleIntent DragPointerMoved(DeviceScreenPoint point) =>
        new MoveDragPointer(point);

    public static EdgeCapsuleIntent PreviewIndexChanged(int index) =>
        new ChangePreviewIndex(index);

    public static EdgeCapsuleIntent FloatingTransferStarted(DeviceScreenPoint point) =>
        new BeginFloatingTransfer(point);

    public static EdgeCapsuleIntent FloatingReorderStarted() =>
        new BeginFloatingReorder();

    public static EdgeCapsuleIntent DockingHandoffStarted() =>
        new BeginDockingHandoff();

    public static EdgeCapsuleIntent PointerInteractionFinished() =>
        new FinishPointer();

    public static EdgeCapsuleIntent OpenedFromEdge() =>
        new MarkOpenedFromEdge();

    public static EdgeCapsuleIntent OpenOriginCleared() =>
        new ClearOpenOrigin();

    public static EdgeCapsuleIntent ResetModel() =>
        new Reset();
}

internal enum EdgeCapsuleDispatchStatus
{
    Applied,
    Unchanged,
    Rejected
}

internal readonly record struct EdgeCapsuleDispatchResult(
    EdgeCapsuleDispatchStatus Status,
    EdgeCapsuleModel Model,
    string? Error = null)
{
    public bool Accepted => Status != EdgeCapsuleDispatchStatus.Rejected;
    public bool Changed => Status == EdgeCapsuleDispatchStatus.Applied;
}
