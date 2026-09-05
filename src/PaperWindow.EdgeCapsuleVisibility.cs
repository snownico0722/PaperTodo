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

    public static bool HasExternalVisibleDisabledForegroundOwnerOverlapping(
        IntPtr paperWindow,
        DeviceScreenRect paperBounds)
    {
        if (paperWindow == IntPtr.Zero || paperBounds.IsEmpty)
        {
            return false;
        }

        var foreground = ForegroundWindow;
        if (foreground == IntPtr.Zero || foreground == paperWindow)
        {
            return false;
        }

        var owner = GetWindow(foreground, GwOwner);
        if (owner == IntPtr.Zero ||
            owner == paperWindow ||
            !IsWindowVisible(owner) ||
            IsWindowEnabledForEdgeToggle(owner))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(paperWindow, out var paperProcessId);
        _ = GetWindowThreadProcessId(owner, out var ownerProcessId);
        if (paperProcessId != 0 && ownerProcessId == paperProcessId)
        {
            return false;
        }

        if (!GetWindowRect(owner, out var ownerBounds))
        {
            return false;
        }

        return ownerBounds.Left < paperBounds.Right &&
            ownerBounds.Right > paperBounds.Left &&
            ownerBounds.Top < paperBounds.Bottom &&
            ownerBounds.Bottom > paperBounds.Top;
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointForEdgeToggle(
        EdgeToggleNativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestorForEdgeToggle(
        IntPtr hwnd,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabledForEdgeToggle(IntPtr hwnd);
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

        // Native restore for maximized/snapped windows can activate the paper while collapsing.
        // Keep this edge case on the safe retrieval path instead of trying to make restore silent.
        if (WindowState == WindowState.Maximized || _isSnappedPresentation)
        {
            _controller.BringPaperToFront(_paper);
            return true;
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

        // WindowFromPoint skips disabled windows. A common modal-dialog pattern therefore makes
        // the still-visible disabled owner look transparent to the grid below it. Handle only
        // that narrow, externally-owned case and conservatively retrieve instead of retracting.
        if (WindowNative.HasExternalVisibleDisabledForegroundOwnerOverlapping(handle, bounds))
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
