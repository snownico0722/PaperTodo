namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    /// <summary>
    /// Reserves the actual WPF layout slot used by the default capsule icon. The outer capsule
    /// width is measured separately by PaperWindow; keeping the TextBlock's MinWidth in sync makes
    /// the following title start at the same X for the narrower ✓ and wider ✎ glyphs.
    /// Script capsules pass zero and keep their natural icon width.
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
