using System.IO;

namespace PaperTodo;

/// <summary>
/// Lifecycle for the outline that marks which free queue a drag would join.
///
/// The outline spans the target queue's whole slot range including the gap opened for the incoming
/// capsule, so one glance answers both "which queue" and "where in it".
/// </summary>
public sealed partial class AppController
{
    private EdgeCapsuleQueueDropTargetOutline? _edgeCapsuleQueueDropTargetOutline;

    internal void ShowEdgeCapsuleQueueDropTargetOutline(string queueKey, string draggedPaperId)
    {
        if (!EdgeCapsuleFreeQueuesEnabled)
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (!TryGetEdgeCapsuleQueueDropTargetBounds(queueKey, draggedPaperId, out var bounds, out var monitorDeviceName))
        {
            EndEdgeCapsuleQueueDropTargetOutline();
            return;
        }

        if (_edgeCapsuleQueueDropTargetOutline is null)
        {
            _edgeCapsuleQueueDropTargetOutline = new EdgeCapsuleQueueDropTargetOutline();
        }

        var shown = _edgeCapsuleQueueDropTargetOutline.ShowAt(
            bounds,
            queueKey,
            FullscreenAvoidanceWindowForQueue(monitorDeviceName),
            animate: State.EnableAnimations);
    }

    internal void EndEdgeCapsuleQueueDropTargetOutline()
    {
        var outline = _edgeCapsuleQueueDropTargetOutline;
        _edgeCapsuleQueueDropTargetOutline = null;
        outline?.CloseForOwner();
    }

    /// <summary>
    /// Device-pixel rectangle covering the target queue's slots after the incoming capsule is
    /// inserted, so the outline encloses the gap that is being opened rather than the queue as it
    /// stands today.
    ///
    /// The span deliberately ignores which slot is opening: it marks the queue, and the member
    /// animations inside it already show where the gap is.
    /// </summary>
    private bool TryGetEdgeCapsuleQueueDropTargetBounds(
        string queueKey,
        string draggedPaperId,
        out DeviceScreenRect bounds,
        out string monitorDeviceName)
    {
        bounds = default;
        monitorDeviceName = "";
        var edgeKey = EdgeCapsuleQueueIds.StripInstance(queueKey);
        var separator = edgeKey.LastIndexOf('|');
        if (separator < 0)
        {
            return false;
        }

        monitorDeviceName = edgeKey[..separator];
        var side = edgeKey[(separator + 1)..] == DeepCapsuleSides.Left
            ? EdgeCapsuleEdge.Left
            : EdgeCapsuleEdge.Right;
        if (!WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitorDeviceName, out var geometry))
        {
            return false;
        }

        var members = FreeQueueMembersExcluding(queueKey, draggedPaperId);
        if (members.Count == 0)
        {
            return false;
        }

        var area = geometry.LocalWorkAreaDip;
        var slotCount = members.Count + 1;
        var startTop = EdgeCapsuleLayout.NormalizeStartTopMargin(
            DeepCapsuleStartTopMarginForQueue(monitorDeviceName, side, FirstQueueIdOf(members)),
            area,
            slotCount,
            DeepCapsuleGap);

        var topDip = EdgeCapsuleLayout.TopForIndex(0, startTop, area, slotCount, DeepCapsuleGap);
        var heightDip = Math.Max(
            slotCount * EdgeCapsuleLayout.SlotHeight(DeepCapsuleGap) - DeepCapsuleGap,
            PaperLayoutDefaults.CapsuleHeight);
        var widthDevice = (int)Math.Round(geometry.DpiScaleX * PaperLayoutDefaults.CapsuleWidth);

        // Wall-anchored horizontally, exactly like a docked capsule, so the outline reads as
        // "this queue" rather than a floating rectangle.
        var leftDevice = side == EdgeCapsuleEdge.Left
            ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - widthDevice;

        // MonitorGeometry only converts device -> local, so invert it explicitly.
        double DeviceY(double dip) => geometry.WorkArea.Top + dip * geometry.DpiScaleY;

        bounds = new DeviceScreenRect(
            leftDevice,
            (int)Math.Round(DeviceY(topDip)),
            leftDevice + widthDevice,
            (int)Math.Round(DeviceY(topDip + heightDip)));
        return true;
    }
}
