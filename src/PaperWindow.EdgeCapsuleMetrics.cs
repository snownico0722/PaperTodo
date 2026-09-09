using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    /// <summary>
    /// Ordinary Todo/Note edge capsules share one icon slot. Their symbols are different glyphs
    /// (`✓` / `✎`) with different advances, but that must not make otherwise identical one-character
    /// titles produce different pill widths or different title start positions. Script capsules keep
    /// their own natural icon metrics.
    /// </summary>
    private double MeasureDeepCapsuleIconSlotWidth(double pixelsPerDip)
    {
        if (IsScriptCapsule())
        {
            _edgeCapsuleHost?.SetDefaultIconSlotWidth(0);
            return MeasureCapsuleIconWidth(pixelsPerDip);
        }

        var todoWidth = MeasureCapsuleTextWidth(
            "✓",
            CapsuleIconFontSize,
            FontWeights.SemiBold,
            AppTypography.SymbolFontFamily,
            pixelsPerDip);
        var noteWidth = MeasureCapsuleTextWidth(
            "✎",
            CapsuleIconFontSize,
            FontWeights.SemiBold,
            AppTypography.SymbolFontFamily,
            pixelsPerDip);
        var slotWidth = Math.Max(todoWidth, noteWidth);
        _edgeCapsuleHost?.SetDefaultIconSlotWidth(slotWidth);
        return slotWidth;
    }
}
