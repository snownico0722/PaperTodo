using System.Globalization;
using System.Windows;

namespace PaperTodo;

public enum EdgeCapsuleEdge
{
    Right,
    Left
}

// Shared geometry for the edge-aligned (deep) capsule stack. Both the real paper
// capsules (PaperWindow) and the standalone "collapse-all" master capsule resolve
// their positions through here so they never disagree on where a slot sits.
public static class EdgeCapsuleLayout
{
    // Expanded windows opened from edge-aligned capsules should not hug the docked screen edge.
    public const double ExpandedEdgeInset = 36;
    // Vertical breathing room at the top/bottom of the work area.
    public const double TopMargin = 8;
    // Where slot 0 starts from the top of the work area (leaves room above for reach).
    public const double StartTopMargin = 48;
    // Shared pill corner radius for the real capsules and the master capsule.
    public const double CornerRadius = 12;
    // Transparent outer chrome around the capsule body. Edge capsules omit this margin on the
    // wall side and keep it on the interior side for the shadow.
    public const double WindowChromeMargin = 8;
    internal const double CapsuleCloseWidth = 14;
    // The focus outline extends one DIP beyond the body and is the outermost stable alpha edge.
    // Proxy clips share these values with the WPF host so animation and endpoint silhouettes use
    // the same coordinate system instead of intersecting two offset rounded rectangles.
    internal const double OutlineThickness = 2;
    internal const double OutlineOverlap = 1;
    internal const double CrossQueueDragUnlockDistance = 56;
    internal const double OutlineSilhouetteInset =
        WindowChromeMargin - OutlineThickness + OutlineOverlap;
    internal const double OutlineSilhouetteRadius =
        CornerRadius + OutlineThickness - OutlineOverlap;
    public const int HorizontalResizeMilliseconds = 160;
    public const int SlotMoveMilliseconds = 200;
    // Quick retract toward the master when a slot leaves the queue.
    public const int SlotRetractMoveMilliseconds = 120;
    // Shared detached-capsule return flight and reveal timings. Master and ordinary capsules use
    // the same floating HWND, so their wall-return motion must not drift apart.
    internal const int DockingHandoffMilliseconds = 160;
    internal const int DockingRevealMilliseconds = 80;
    // Shared display-width units for title limits and compact linked-paper labels:
    // CJK/fullwidth and supplementary Unicode glyphs count as 2; halfwidth as 1.
    // Count text elements, not UTF-16 code units, to keep emoji sequences intact.
    public static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            width += IsWide((string)enumerator.Current) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(string element)
    {
        if (element.Length == 0)
        {
            return false;
        }

        var c = element[0];
        // Some BMP emoji need an emoji presentation selector (❤️) or a keycap mark (1️⃣).
        // Others, such as clocks, dingbats and stars, render as emoji without selectors.
        // CJK/fullwidth characters retain the existing width rules below.
        return element.Contains('\uFE0F')    // Emoji presentation selector
            || element.Contains('\u20E3')    // Combining enclosing keycap
            || char.IsHighSurrogate(c)        // Supplementary symbols (including emoji)
            || (c >= 0x231A && c <= 0x231B)   // Watch / hourglass
            || (c >= 0x23E9 && c <= 0x23FA)   // Emoji controls / clock
            || (c >= 0x25FB && c <= 0x27BF)   // Geometric emoji / symbols / dingbats
            || (c >= 0x2B05 && c <= 0x2B07)   // Emoji arrows
            || (c >= 0x2B1B && c <= 0x2B1C)   // Square emoji
            || c == '\u2B50' || c == '\u2B55' // Star / circle emoji
            || (c >= 0x1100 && c <= 0x115F)   // Hangul Jamo
            || (c >= 0x2E80 && c <= 0x303E)   // CJK radicals / Kangxi / symbols
            || (c >= 0x3041 && c <= 0x33FF)   // Hiragana, Katakana, CJK symbols
            || (c >= 0x3400 && c <= 0x4DBF)   // CJK Ext A
            || (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified
            || (c >= 0xA000 && c <= 0xA4CF)   // Yi
            || (c >= 0xAC00 && c <= 0xD7A3)   // Hangul syllables
            || (c >= 0xF900 && c <= 0xFAFF)   // CJK compatibility ideographs
            || (c >= 0xFF00 && c <= 0xFF60)   // Fullwidth forms
            || (c >= 0xFFE0 && c <= 0xFFE6);  // Fullwidth signs
    }

    // Work area of a specific monitor device (empty => primary), with nearest-monitor fallback.
    // Per-queue geometry resolves through this so each (monitor, edge) queue is independent.
    public static Rect WorkAreaForQueue(string? monitorDeviceName)
    {
        var normalizedMonitor = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName);
        if (!string.IsNullOrEmpty(normalizedMonitor))
        {
            var resolved = WindowWorkAreaHelper.WorkAreaForDevice(normalizedMonitor);
            if (resolved.HasValue)
            {
                return resolved.Value;
            }
        }

        return SystemParameters.WorkArea;
    }

    // Edge HWNDs lay out in the target monitor's own 96-DPI coordinate space, then convert the
    // finished rectangle to physical pixels. This keeps slot height, gap and width consistent on
    // mixed-scale displays without mixing the primary monitor's desktop coordinates into sizing.
    internal static Rect LocalWorkAreaForQueue(string? monitorDeviceName)
    {
        return WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(monitorDeviceName, out var geometry)
            ? geometry.LocalWorkAreaDip
            : new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
    }

    // ── Pure per-queue geometry: same math as the static-anchor methods below, but every
    // input (edge + work area) is explicit, so N independent queues can each compute their own
    // docked positions without sharing one global anchor.

    public static double TopForIndex(
        int index,
        double startTopMargin,
        Rect area,
        int slotCount,
        double gap)
    {
        // Deliberately unbounded: a long edge queue keeps its normal slot spacing and may extend
        // below the monitor work area. Do not clamp overflow items onto the last visible slot.
        return area.Top +
            NormalizeStartTopMargin(startTopMargin, area, slotCount, gap) +
            Math.Max(0, index) * SlotHeight(gap);
    }

    public static double MaxStartTopMarginForCount(int slotCount, Rect area, double gap)
    {
        var count = Math.Max(1, slotCount);
        var stackHeight = PaperLayoutDefaults.CapsuleHeight + (count - 1) * SlotHeight(gap);
        var maxMargin = area.Height - stackHeight - TopMargin;
        return Math.Max(TopMargin, maxMargin);
    }

    public static double NormalizeStartTopMargin(
        double value,
        Rect area,
        int slotCount,
        double gap)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = StartTopMargin;
        }

        var max = MaxStartTopMarginForCount(slotCount, area, gap);
        return Math.Round(Math.Clamp(value, TopMargin, max), 1);
    }

    // Free queues promise an exact anchor, even when a member extends beyond the work area.
    // The legacy one-queue mode keeps its existing fit-to-monitor normalization.
    internal static double FreeQueueTopForIndex(int index, double startTopMargin, Rect area, double gap) =>
        area.Top + Math.Round(double.IsFinite(startTopMargin) ? startTopMargin : StartTopMargin, 1) +
        Math.Max(0, index) * SlotHeight(gap);

    public static double SlotHeight(double gap) =>
        PaperLayoutDefaults.CapsuleHeight + Math.Max(0, gap);
}

internal readonly record struct EdgeCapsuleLayoutFacts(
    MonitorGeometry Monitor,
    EdgeCapsuleEdge Edge,
    EdgeCapsulePlacement Placement,
    double QueueStartTopMarginDip,
    double GapDip,
    double RestingWidthDip,
    double MaximumCloseWidthDip,
    double HeightDip,
    double PreviewWidthDip,
    double PreviewHeightDip,
    bool CloseSegmentActsAsContent,
    double RestingContentOpacity,
    double? ForcedContentOpacity,
    double HostCapacityWidthDip = 0,
    double HostCapacityHeightDip = 0,
    double ExpandedWidthDip = 0,
    bool HideRestingTitle = false,
    bool PreserveFreeQueueAnchor = false);

/// <summary>
/// Converts measured/environment facts into the planner snapshot. PaperWindow supplies target
/// monitor and text width only; queue top policy lives here and is independently testable.
/// </summary>
internal static class EdgeCapsuleLayoutService
{
    public static EdgeCapsuleLayoutSnapshot Calculate(EdgeCapsuleLayoutFacts facts)
    {
        var placement = facts.Placement.Normalize();
        var localWorkArea = facts.Monitor.LocalWorkAreaDip;
        double Top(int index) => facts.PreserveFreeQueueAnchor
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(index, facts.QueueStartTopMarginDip, localWorkArea, facts.GapDip)
            : EdgeCapsuleLayout.TopForIndex(index, facts.QueueStartTopMarginDip, localWorkArea, placement.SlotCount, facts.GapDip);
        var normalTop = placement.IsPlaced ? Top(placement.VisualIndex) + placement.TopOffsetDip : 0;
        var masterTop = placement.IsPlaced ? Top(0) : normalTop;
        return new EdgeCapsuleLayoutSnapshot(
            facts.Monitor,
            facts.Edge,
            normalTop,
            masterTop,
            facts.RestingWidthDip,
            facts.MaximumCloseWidthDip,
            facts.HeightDip,
            facts.PreviewWidthDip,
            facts.PreviewHeightDip,
            facts.CloseSegmentActsAsContent,
            facts.RestingContentOpacity,
            facts.ForcedContentOpacity,
            facts.HostCapacityWidthDip,
            facts.HostCapacityHeightDip,
            facts.ExpandedWidthDip,
            facts.HideRestingTitle);
    }

    public static double TopForVisualIndex(
        MonitorGeometry monitor,
        int visualIndex,
        int slotCount,
        double queueStartTopMarginDip,
        double gapDip,
        bool preserveFreeQueueAnchor = false) =>
        preserveFreeQueueAnchor
            ? EdgeCapsuleLayout.FreeQueueTopForIndex(visualIndex, queueStartTopMarginDip, monitor.LocalWorkAreaDip, gapDip)
            : EdgeCapsuleLayout.TopForIndex(visualIndex, queueStartTopMarginDip, monitor.LocalWorkAreaDip, slotCount, gapDip);
}
