using System.Globalization;
using System.Windows;

namespace PaperTodo;

public enum DeepCapsuleEdge
{
    Right,
    Left
}

// Shared geometry for the edge-aligned (deep) capsule stack. Both the real paper
// capsules (PaperWindow) and the standalone "collapse-all" master capsule resolve
// their positions through here so they never disagree on where a slot sits.
public static class DeepCapsuleLayout
{
    // Focus reveal is a right-anchored viewport expansion. It intentionally reveals only
    // part of the hidden tail so the capsule does not travel too far into the desktop.
    public const double HoverOutsideOffset = 26;
    // Expanded windows opened from edge-aligned capsules should not hug the docked screen edge.
    public const double ExpandedEdgeInset = 36;
    // Vertical breathing room at the top/bottom of the work area.
    public const double TopMargin = 8;
    // Where slot 0 starts from the top of the work area (leaves room above for reach).
    public const double StartTopMargin = 48;
    // Vertical gap between stacked capsules.
    public const double Gap = 4;
    // Shared pill corner radius for the real capsules and the master capsule.
    public const double CornerRadius = 12;
    // Transparent outer chrome around the capsule body. The docked viewport must hide at
    // least this margin plus the corner radius so the screen edge cuts through the straight
    // body, not through the rounded cap.
    public const double WindowChromeMargin = 8;
    // Top-level transparent windows are expensive to move; slightly longer durations give
    // the compositor more frames and make each frame's position delta smaller.
    public const int SlideOutMilliseconds = 220;
    public const int SlideInMilliseconds = 180;
    public const int SlotMoveMilliseconds = 200;
    // Slot-host fade when opacity-toggling the resting docked pill (retract behind master, etc.).
    public const int SlotOpacityFadeMilliseconds = 160;
    // Quick "retract toward master then fade" used when clearing an active/expanded slot.
    public const int SlotRetractMoveMilliseconds = 120;
    public const int SlotRetractFadeMilliseconds = 110;
    // The two-phase tear-down (move up, then a brief final fade) when fully releasing a slot host.
    public const int SlotReleaseSettleMilliseconds = 125;
    public const int SlotReleaseFadeMilliseconds = 45;
    public static double FocusVisibleWidth(double capsuleWindowWidth, double restingVisibleWidth)
    {
        var resting = Math.Clamp(restingVisibleWidth, 1, capsuleWindowWidth);
        var visibleWidth = resting + ((capsuleWindowWidth - resting) * 0.5);
        return Math.Clamp(visibleWidth, Math.Min(54, capsuleWindowWidth), capsuleWindowWidth);
    }

    // Display-weighted character count: CJK / fullwidth glyphs count as 2, everything
    // else as 1. A 6-digit number title then weighs the same as a 3-CJK-character title,
    // so the capsule no longer looks long-but-empty for numeric titles.
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
        // CJK Unified, Hiragana/Katakana, Hangul, fullwidth forms, CJK symbols/punct.
        return (c >= 0x1100 && c <= 0x115F)   // Hangul Jamo
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

    public static Rect WorkArea => ResolveWorkArea();

    // The screen edge the stack docks to and the monitor that hosts it. These are kept in
    // sync with AppState by AppController (the single owner of state) so every capsule and the
    // master pill resolve their geometry against the same anchor — exactly as they previously
    // all shared SystemParameters.WorkArea + the right edge.
    public static DeepCapsuleEdge Edge { get; private set; } = DeepCapsuleEdge.Right;
    public static string MonitorDeviceName { get; private set; } = "";

    public static void SetAnchor(DeepCapsuleEdge edge, string? monitorDeviceName)
    {
        Edge = edge;
        MonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName);
    }

    private static Rect ResolveWorkArea()
    {
        return WorkAreaForQueue(MonitorDeviceName);
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

    public static bool IsLeftEdgeOf(DeepCapsuleEdge edge) => edge == DeepCapsuleEdge.Left;

    // ── Pure per-queue geometry: same math as the static-anchor methods below, but every
    // input (edge + work area) is explicit, so N independent queues can each compute their own
    // docked positions without sharing one global anchor.

    public static double DockedLeft(Rect area, double visibleWidth, DeepCapsuleEdge edge)
    {
        return edge == DeepCapsuleEdge.Left
            ? area.Left
            : area.Right - visibleWidth;
    }

    public static double TopForIndex(int index, double startTopMargin, Rect area, int slotCount)
    {
        var desiredTop = area.Top + NormalizeStartTopMargin(startTopMargin, area, slotCount) + Math.Max(0, index) * SlotHeight;
        var maxTop = Math.Max(area.Top + TopMargin, area.Bottom - PaperLayoutDefaults.CapsuleHeight - TopMargin);
        return Math.Min(desiredTop, maxTop);
    }

    public static double MaxStartTopMarginForCount(int slotCount, Rect area)
    {
        var count = Math.Max(1, slotCount);
        var stackHeight = PaperLayoutDefaults.CapsuleHeight + (count - 1) * SlotHeight;
        var maxMargin = area.Height - stackHeight - TopMargin;
        return Math.Max(TopMargin, maxMargin);
    }

    public static double NormalizeStartTopMargin(double value, Rect area, int slotCount)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = StartTopMargin;
        }

        var max = MaxStartTopMarginForCount(slotCount, area);
        return Math.Round(Math.Clamp(value, TopMargin, max), 1);
    }

    // The window Left that docks a pill of the given visible width to the anchored edge.
    // Right edge: pin the window's right side to area.Right (tail tucked past the right edge).
    // Left edge:  pin the window's left side to area.Left  (tail tucked past the left edge).
    public static double DockedLeft(Rect area, double visibleWidth)
    {
        return Edge == DeepCapsuleEdge.Left
            ? area.Left
            : area.Right - visibleWidth;
    }

    // Anchored screen edge, used by the horizontal reveal animation: the coordinate that
    // stays fixed while the viewport width grows/shrinks.
    public static double DockedAnchorEdge(Rect area, double visibleWidth)
    {
        return Edge == DeepCapsuleEdge.Left
            ? area.Left + visibleWidth
            : area.Right - visibleWidth;
    }

    public static bool IsLeftEdge => Edge == DeepCapsuleEdge.Left;

    public static double SlotHeight => PaperLayoutDefaults.CapsuleHeight + Gap;

    public static double TopForIndex(int index, double startTopMargin = StartTopMargin)
    {
        return TopForIndex(index, startTopMargin, WorkArea, 1);
    }

    public static double MaxStartTopMarginForCount(int slotCount)
    {
        return MaxStartTopMarginForCount(slotCount, WorkArea);
    }

    public static double NormalizeStartTopMargin(double value, int slotCount = 1)
    {
        return NormalizeStartTopMargin(value, WorkArea, slotCount);
    }
}
