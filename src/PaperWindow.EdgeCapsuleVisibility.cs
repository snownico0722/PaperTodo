using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal static partial class WindowNative
{
    private const uint EdgeToggleGaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct EdgeToggleNativePoint
    {
        public int X;
        public int Y;
    }

    public static bool IsRootWindowAtDeviceScreenPoint(
        IntPtr rootWindow,
        DeviceScreenPoint point)
    {
        if (rootWindow == IntPtr.Zero)
        {
            return false;
        }

        var nativePoint = new EdgeToggleNativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        };
        var hit = WindowFromPointForEdgeToggle(nativePoint);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        if (hit == rootWindow)
        {
            return true;
        }

        return GetAncestorForEdgeToggle(hit, EdgeToggleGaRoot) == rootWindow;
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointForEdgeToggle(
        EdgeToggleNativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestorForEdgeToggle(
        IntPtr hwnd,
        uint flags);
}

public sealed partial class PaperWindow
{
    private const int EdgeToggleVisibilityGridSize = 8;
    // 23 / 64 = 35.94%. A paper with this much directly visible surface is sufficiently obvious
    // that an edge-capsule repeat click can be interpreted as a deliberate retract. Below this
    // threshold, the capsule behaves as a retrieval target and brings the expanded paper forward.
    private const int EdgeToggleMinimumVisibleSamples = 23;

    private bool TryHandleExpandedDeepCapsuleRepeatClick()
    {
        if (!_controller.State.CollapseExpandedDeepCapsuleOnClick ||
            (!HoldsDeepCapsuleSlotWhileExpanded && !HasExpandedDeepCapsuleSlotReservation))
        {
            return false;
        }

        if (IsExpandedPaperMeaningfullyVisible())
        {
            CollapseExpandedDeepCapsuleFromEdgeSilently();
        }
        else
        {
            EnsureExpandedSurfaceGeometry(alignToDockedEdge: true);
            _controller.BringPaperToFront(_paper);
        }

        return true;
    }

    private bool IsExpandedPaperMeaningfullyVisible()
    {
        if (!IsVisible ||
            WindowState == WindowState.Minimized ||
            _paper.IsCollapsed)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !WindowNative.TryGetWindowDeviceBounds(this, out var bounds) ||
            bounds.IsEmpty)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var insetX = Math.Max(0, (int)Math.Ceiling(WindowChromeMargin * dpi.DpiScaleX));
        var insetY = Math.Max(0, (int)Math.Ceiling(WindowChromeMargin * dpi.DpiScaleY));
        var sampleBounds = new DeviceScreenRect(
            bounds.Left + insetX,
            bounds.Top + insetY,
            bounds.Right - insetX,
            bounds.Bottom - insetY);
        if (sampleBounds.IsEmpty)
        {
            sampleBounds = bounds;
        }

        var visibleSamples = 0;
        for (var row = 0; row < EdgeToggleVisibilityGridSize; row++)
        {
            var y = sampleBounds.Top +
                (row + 0.5) * sampleBounds.Height / EdgeToggleVisibilityGridSize;
            for (var column = 0; column < EdgeToggleVisibilityGridSize; column++)
            {
                var x = sampleBounds.Left +
                    (column + 0.5) * sampleBounds.Width / EdgeToggleVisibilityGridSize;
                if (WindowNative.IsRootWindowAtDeviceScreenPoint(
                        handle,
                        new DeviceScreenPoint(x, y)))
                {
                    visibleSamples++;
                }
            }
        }

        return visibleSamples >= EdgeToggleMinimumVisibleSamples;
    }

    private void CollapseExpandedDeepCapsuleFromEdgeSilently()
    {
        SetCollapsedState(true, alignExpandedToDockedEdge: true);
    }
}
