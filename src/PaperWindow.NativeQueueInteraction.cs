using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private int _nativeQueuePointerDownKeys;
    private bool _nativeQueueWholeRequested;
    private bool _nativeQueueClickRevoked;
    private bool _nativeQueueFeedbackSampling;

    internal bool EdgeCapsuleDragIsTopmost =>
        _deepCapsuleFloatingDragHost?.Topmost ?? _edgeCapsuleHost?.IsTopmost ?? false;

    internal void BringEdgeCapsuleDragAboveGhost()
    {
        if (_deepCapsuleFloatingDragHost?.IsVisible == true)
            WindowNative.BringToFrontNoActivate(_deepCapsuleFloatingDragHost);
        else
            _edgeCapsuleHost?.BringToFrontNoActivate();
    }

    internal static bool NativeQueueControlPressed(int nativeKeys, ModifierKeys modifiers, bool osControl) =>
        (nativeKeys & 0x0008) != 0 || (modifiers & ModifierKeys.Control) != 0 || osControl;

    private void LatchNativeQueueControl()
    {
        _nativeQueueWholeRequested = _controller.EdgeCapsuleFreeQueuesEnabled && NativeQueueControlPressed(
            _nativeQueuePointerDownKeys, Keyboard.Modifiers, WindowNative.IsControlKeyDown);
        _nativeQueuePointerDownKeys = 0;
        _nativeQueueClickRevoked = false;
    }

    private double NativeQueueLogicalOwnerTop(DeviceScreenPoint pointer, EdgeCapsuleDragSession session, bool floating)
    {
        if (floating)
        {
            var monitor = WindowWorkAreaHelper.TryGetMonitorGeometryAtDeviceScreenPoint(pointer, out var atPoint)
                ? atPoint : DeepCapsuleMonitorGeometry();
            return monitor.DeviceYToLocalDip(pointer.Y) - PaperLayoutDefaults.CapsuleHeight / 2;
        }
        var top = DeepCapsuleMonitorGeometry().DeviceYToLocalDip(pointer.Y - session.DockedPointerOffsetY);
        return _controller.NativeEdgeQueueOwnerTop(_paper.Id, pointer, top);
    }

    private void SampleNativeQueueFloatingDrag()
    {
        if (_nativeQueueFeedbackSampling || !IsDeepCapsuleFloatingReordering ||
            !TryGetEdgeCapsuleDragSession(out var session) || !WindowNative.TryGetCursorScreenPosition(out var pointer)) return;
        _nativeQueueFeedbackSampling = true;
        try
        {
            UpdateEdgeCapsuleDragPointer(pointer);
            if (!_controller.PreviewNativeEdgeQueueDrag(_paper, pointer,
                    NativeQueueLogicalOwnerTop(pointer, session, floating: true), floating: true))
                _controller.PreviewSingleCapsuleDropGhost(_paper,
                    DeepCapsuleDropIndexForCurrentPosition(), pointer, floating: true);
        }
        finally { _nativeQueueFeedbackSampling = false; }
    }

    private void RefreshNativeQueueReleasePosition(DeviceScreenPoint pointer)
    {
        if (!TryGetEdgeCapsuleDragSession(out var session)) return;
        UpdateEdgeCapsuleDragPointer(pointer);
        var floating = IsDeepCapsuleFloatingReordering;
        var top = NativeQueueLogicalOwnerTop(pointer, session, floating);
        if (!floating)
        {
            MoveEdgeCapsuleDockedReorder(pointer, top);
            FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Drag);
        }
        if (!_controller.PreviewNativeEdgeQueueDrag(_paper, pointer, top, floating))
            _controller.PreviewSingleCapsuleDropGhost(_paper,
                DeepCapsuleDropIndexForCurrentPosition(), pointer, floating);
    }

    // Establish invisible terminal endpoints under the still-visible pooled drag cover. The next
    // normal arrange reveals all members through their existing placement transition, without
    // creating another surface owner or storing a second per-paper presentation model.
    internal void PrepareNativeQueueMemberReturn(EdgeCapsulePlacement placement, double top)
    {
        if (IsClosed || !_paper.IsVisible || !AttachEdgeCapsuleToQueue(placement,
                _paper.IsCollapsed ? EdgeCapsulePaperForm.Collapsed : EdgeCapsulePaperForm.Expanded, retracted: true)) return;
        FlushEdgeCapsulePresentation(EdgeCapsuleTransitionReason.Retraction,
            EdgeCapsuleDirty.Presentation | EdgeCapsuleDirty.Measure, retractionTopDip: top);
    }
}
