namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    /// <summary>
    /// Keeps the vector layout slot in sync with PaperWindow's capsule width calculation.
    /// </summary>
    internal void SetDefaultIconSlotWidth(double widthDip)
    {
        if (_disposed)
        {
            return;
        }

        var normalized = double.IsFinite(widthDip)
            ? Math.Max(0, widthDip)
            : 0;
        if (Math.Abs(Icon.MinWidth - normalized) < 0.01)
        {
            return;
        }

        Icon.MinWidth = normalized;
        InvalidateNativeMetrics();
    }

    internal double DefaultIconSlotWidthForChecks =>
        _disposed ? 0 : Icon.MinWidth;
}
