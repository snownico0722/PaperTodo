namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Keep normal along-edge adjustment from accidentally tearing off the binding.
    private const double ExperimentalTetherDetachThresholdDip = 72;

    private enum ExperimentalTetherDragUpdate
    {
        NotTethered,
        Pending,
        Sliding,
        Detach
    }

    private readonly record struct ExperimentalTetherDragAnchor(
        ExternalWindowIdentity Target,
        ExperimentalAttachmentEdge Edge,
        double OffsetDevice);

    private ExperimentalTetherDragAnchor?
        CaptureExperimentalTetherDragAnchor()
    {
        return _experimentalWindowAttachment is
        {
            Owner: ExperimentalAttachmentOwner.WindowTether,
            TargetKind:
                ExperimentalAttachmentTargetKind.ExternalWindow
        } session && !session.ExternalWindow.IsEmpty
            ? new ExperimentalTetherDragAnchor(
                session.ExternalWindow,
                session.Edge,
                session.PerpendicularOffsetDevice)
            : null;
    }

    private ExperimentalTetherDragUpdate
        UpdateExperimentalTetherDrag(
            ExperimentalTetherDragAnchor anchor,
            DeviceScreenPoint start,
            DeviceScreenPoint current)
    {
        var session = _experimentalWindowAttachment;
        if (session?.Owner !=
                ExperimentalAttachmentOwner.WindowTether ||
            session.TargetKind !=
                ExperimentalAttachmentTargetKind.ExternalWindow ||
            session.ExternalWindow != anchor.Target ||
            session.Edge != anchor.Edge)
        {
            return ExperimentalTetherDragUpdate.NotTethered;
        }

        if (!ExternalWindowNative.TryGetSnapshot(
                anchor.Target,
                out var snapshot) ||
            !snapshot.IsUsableTarget)
        {
            return ExperimentalTetherDragUpdate.Detach;
        }

        var deltaX = current.X - start.X;
        var deltaY = current.Y - start.Y;
        var isVerticalEdge =
            anchor.Edge is
                ExperimentalAttachmentEdge.Left or
                ExperimentalAttachmentEdge.Right;
        var parallelDelta = isVerticalEdge ? deltaY : deltaX;
        var perpendicularDelta = isVerticalEdge ? deltaX : deltaY;
        var dpiScale = Math.Max(1, snapshot.DpiScale);
        if (Math.Abs(perpendicularDelta) >=
            ExperimentalTetherDetachThresholdDip * dpiScale)
        {
            return ExperimentalTetherDragUpdate.Detach;
        }

        if (Math.Abs(parallelDelta) <
            TitleBarDragThreshold * dpiScale)
        {
            return ExperimentalTetherDragUpdate.Pending;
        }

        _experimentalWindowAttachment = session with
        {
            PerpendicularOffsetDevice =
                anchor.OffsetDevice + parallelDelta
        };
        ReconcileExperimentalWindowAttachment(snapshot);
        return ExperimentalTetherDragUpdate.Sliding;
    }
}
