namespace PaperTodo;

public sealed partial class PaperWindow
{
    // The same vector box supplies both the host layout slot and the capsule width calculation.
    private double MeasureDeepCapsuleIconSlotWidth(double pixelsPerDip)
    {
        var slotWidth = MeasureCapsuleIconWidth(pixelsPerDip);
        _edgeCapsuleHost?.SetDefaultIconSlotWidth(slotWidth);
        return slotWidth;
    }
}
