using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr hwndParent,
        EnumChildWindowsProc callback,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hwnd, bool enabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hwndParent, IntPtr hwnd);

    private readonly HashSet<IntPtr> _advancedDisabledChildWindows = [];
    private bool _advancedInteractionLocked;
    private Border? _advancedLockShield;
    private Button? _advancedLockButton;

    internal bool OwnsNativeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var root = new WindowInteropHelper(this).Handle;
        return root != IntPtr.Zero &&
            (root == hwnd || IsChild(root, hwnd));
    }

    internal void SetAdvancedInteractionLocked(bool locked)
    {
        var changed = _advancedInteractionLocked != locked;
        if (changed)
        {
            // The presentation-only proxy cannot inherit a mid-flight input lock. Settle its
            // covered endpoint first; if native handoff retries, the live eligibility check in
            // the proxy still makes the whole transient surface click-through immediately.
            _controller.CompleteEdgeCapsuleQueueCompositionProxyFor(this);
        }
        _advancedInteractionLocked = locked;
        if (changed && locked)
        {
            CancelExperimentalAutoCollapse();
        }

        _edgeCapsuleHost?.SetInteractionLocked(locked);
        UpdateAdvancedInteractionLockVisuals();
    }

    private void EnsureAdvancedInteractionLockVisuals()
    {
        if (!_isShellBuilt || _advancedLockShield != null)
        {
            return;
        }

        _advancedLockShield = new Border
        {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Focusable = true,
            Cursor = Cursors.Arrow
        };
        _advancedLockShield.PreviewMouseDown += (_, e) => e.Handled = true;
        _advancedLockShield.PreviewMouseUp += (_, e) => e.Handled = true;
        _advancedLockShield.PreviewMouseMove += (_, e) => e.Handled = true;
        _advancedLockShield.PreviewMouseWheel += (_, e) => e.Handled = true;
        _advancedLockShield.PreviewKeyDown += (_, e) => e.Handled = true;
        Panel.SetZIndex(_advancedLockShield, 10000);
        _windowHost.Children.Add(_advancedLockShield);

        _advancedLockButton = IconButton("", Strings.Get("LabsUnlockAllPapers"));
        _advancedLockButton.Width = 23;
        _advancedLockButton.Height = 23;
        _advancedLockButton.HorizontalAlignment = HorizontalAlignment.Left;
        _advancedLockButton.VerticalAlignment = VerticalAlignment.Top;
        _advancedLockButton.Margin = new Thickness(
            WindowChromeMargin + 8,
            WindowChromeMargin + 4,
            0,
            0);
        _advancedLockButton.Content = CreateAdvancedLockIcon();
        _advancedLockButton.Click += (_, _) =>
            _controller.UnlockAllPapersFromLockIcon();
        Panel.SetZIndex(_advancedLockButton, 10001);
        _windowHost.Children.Add(_advancedLockButton);
    }

    internal void UpdateAdvancedInteractionLockVisuals()
    {
        _edgeCapsuleHost?.SetInteractionLocked(_advancedInteractionLocked);
        if (!_isShellBuilt)
        {
            return;
        }

        EnsureAdvancedInteractionLockVisuals();
        if (_advancedLockShield == null || _advancedLockButton == null)
        {
            return;
        }

        _advancedLockShield.Visibility = _advancedInteractionLocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        _advancedLockButton.Visibility =
            _advancedInteractionLocked && !_paper.IsCollapsed
                ? Visibility.Visible
                : Visibility.Collapsed;
        _advancedLockButton.IsHitTestVisible =
            _controller.State.ExperimentalAllowLockIconUnlock;
        _advancedLockButton.Focusable = false;
        _advancedLockButton.Cursor =
            _controller.State.ExperimentalAllowLockIconUnlock
                ? Cursors.Hand
                : Cursors.Arrow;
        _advancedLockButton.Opacity =
            _controller.State.ExperimentalAllowLockIconUnlock
                ? 1.0
                : 0.78;
        if (_paperIconButton != null)
        {
            // Keep the pin slot in layout while the lock glyph visually replaces it.
            // Collapsing this element would let the title slide under the floating lock button.
            _paperIconButton.Visibility = _advancedInteractionLocked
                ? Visibility.Hidden
                : Visibility.Visible;
        }

        ApplyAdvancedNativeChildLock();
        if (_advancedInteractionLocked)
        {
            Keyboard.ClearFocus();
            _advancedLockShield.Focus();
        }
    }

    private void ApplyAdvancedNativeChildLock()
    {
        var root = new WindowInteropHelper(this).Handle;
        if (root == IntPtr.Zero)
        {
            return;
        }

        if (_advancedInteractionLocked)
        {
            EnumChildWindows(
                root,
                (child, _) =>
                {
                    if (IsWindowEnabled(child))
                    {
                        EnableWindow(child, enabled: false);
                        _advancedDisabledChildWindows.Add(child);
                    }
                    return true;
                },
                IntPtr.Zero);
            return;
        }

        foreach (var child in _advancedDisabledChildWindows.ToArray())
        {
            EnableWindow(child, enabled: true);
        }
        _advancedDisabledChildWindows.Clear();
    }

    private static UIElement CreateAdvancedLockIcon()
    {
        var canvas = new Grid
        {
            Width = 13,
            Height = 14,
            IsHitTestVisible = false
        };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 3.1 6 V 4.4 C 3.1 1.9 4.8 0.8 6.5 0.8 C 8.2 0.8 9.9 1.9 9.9 4.4 V 6"),
            StrokeThickness = 1.45,
            Stroke = Theme.ActiveBrush,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 1.5 6.2 H 11.5 V 13.2 H 1.5 Z M 6.5 8.5 V 10.9"),
            StrokeThickness = 1.35,
            Stroke = Theme.ActiveBrush,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        return canvas;
    }
}
