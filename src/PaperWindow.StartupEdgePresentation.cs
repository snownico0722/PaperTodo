namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _stagingStartupEdgeCapsuleFirstPresentation;
    private EdgeCapsulePresentationFrame? _startupStagedEdgeCapsuleFrame;

    internal bool StageStartupDeepCapsulePresentation()
    {
        if (!HasDeepCapsuleSlotPlacement || IsClosed)
        {
            return false;
        }

        _startupStagedEdgeCapsuleFrame = null;
        _stagingStartupEdgeCapsuleFirstPresentation = true;
        try
        {
            FlushStartupDeepCapsulePresentation();
        }
        finally
        {
            _stagingStartupEdgeCapsuleFirstPresentation = false;
        }

        return _startupStagedEdgeCapsuleFrame is { Visible: true };
    }

    internal bool RevealStartupDeepCapsulePresentation()
    {
        if (_startupStagedEdgeCapsuleFrame is not { } frame ||
            _edgeCapsuleHost == null)
        {
            return false;
        }

        var revealed = _edgeCapsuleHost.RevealStartupFirstPresentation(frame);
        if (revealed)
        {
            _startupStagedEdgeCapsuleFrame = null;
        }
        return revealed;
    }

    internal void RecoverStartupDeepCapsulePresentation()
    {
        _startupStagedEdgeCapsuleFrame = null;
        _edgeCapsule.ForceApplyCurrentPresentation();
        FlushStartupDeepCapsulePresentation();
    }
}
