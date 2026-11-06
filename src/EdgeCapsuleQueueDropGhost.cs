using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

// One passive, theme-aware landing stack reused during a drag. Every carried paper has
// its own silhouette at the confirmed insertion slot; there is no animation or input.
internal sealed class EdgeCapsuleQueueDropGhost : IDisposable
{
    private readonly Window _window;
    private readonly Border _silhouette;
    private readonly StackPanel _stack;
    private readonly Brush _normalBackground;
    private const double MergeHaloOutset = 4;
    private string _side = "";
    private int _count = 1;
    private double _gap;
    private bool _shown;
    private DeviceScreenRect? _lastBounds;
    private double[]? _lastWidths;
    private bool _mergeHalo;

    internal EdgeCapsuleQueueDropGhost()
    {
        // Resolve the PaperTodo skin at drag creation, not the Windows system accent.
        // A new ghost is created per gesture, so every gesture adopts the current theme.
        var outline = Theme.CapsuleFocusBorderBrush.Clone();
        outline.Opacity *= 0.68;
        _normalBackground = Theme.Tint(42);
        _silhouette = new Border
        {
            Height = PaperLayoutDefaults.CapsuleHeight - 2 * EdgeCapsuleLayout.WindowChromeMargin,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1.5),
            BorderBrush = outline,
            Background = _normalBackground,
            IsHitTestVisible = false
        };
        _stack = new StackPanel { Orientation = Orientation.Vertical, IsHitTestVisible = false };
        _stack.Children.Add(_silhouette);
        _window = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Focusable = false,
            Topmost = true,
            Opacity = 0,
            Content = _stack
        };
        _window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(_window);
            WindowNative.SetInputPassthrough(_window, true);
        };
    }

    // A merge into an already folded queue changes only the target master.
    // Expand on its interior and vertical sides; the wall edge stays flush.
    internal void ShowMergeHaloAt(MonitorGeometry geometry, string side,
        double masterTopDip, double masterWidthDip, Action? raiseDraggedWindow = null,
        bool topmost = true) =>
        ShowAt(geometry, side, masterTopDip - MergeHaloOutset,
            [masterWidthDip + MergeHaloOutset], 0, raiseDraggedWindow,
            topmost, mergeHalo: true);

    internal void ShowAt(MonitorGeometry geometry, string side, double topDip,
        IReadOnlyList<double> slotWidthsDip, double gapDip = 0, Action? raiseDraggedWindow = null,
        bool topmost = true, bool mergeHalo = false)
    {
        if (!double.IsFinite(topDip) || slotWidthsDip.Count == 0)
        {
            Hide();
            return;
        }

        var requiredWidth = 0d;
        foreach (var slotWidth in slotWidthsDip)
        {
            if (!double.IsFinite(slotWidth) || slotWidth <= 0)
            {
                Hide();
                return;
            }
            requiredWidth = Math.Max(requiredWidth, slotWidth);
        }

        var count = slotWidthsDip.Count;
        gapDip = Math.Max(0, gapDip);
        var width = Math.Min(geometry.LocalWorkAreaDip.Width, requiredWidth);
        var height = count * PaperLayoutDefaults.CapsuleHeight + (count - 1) * gapDip +
            (mergeHalo ? 2 * MergeHaloOutset : 0);
        var deviceWidth = Math.Max(1, (int)Math.Round(width * geometry.DpiScaleX));
        var deviceHeight = Math.Max(1, (int)Math.Round(height * geometry.DpiScaleY));
        var leftDevice = side == DeepCapsuleSides.Left ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - deviceWidth;
        var topDevice = (int)Math.Round(geometry.WorkArea.Top + topDip * geometry.DpiScaleY);
        var bounds = new DeviceScreenRect(leftDevice, topDevice,
            leftDevice + deviceWidth, topDevice + deviceHeight);
        var wasHidden = !_shown || _window.Opacity <= 0;
        var bandChanged = _window.Topmost != topmost;
        if (!wasHidden && !bandChanged && _mergeHalo == mergeHalo &&
            _lastBounds == bounds && _count == count &&
            _side == side && Math.Abs(_gap - gapDip) <= 0.01 &&
            _lastWidths != null && _lastWidths.SequenceEqual(slotWidthsDip))
            return;
        if (bandChanged) _window.Topmost = topmost;

        if (count != _count || Math.Abs(gapDip - _gap) > 0.01)
        {
            _stack.Children.Clear();
            for (var i = 0; i < count; i++)
                _stack.Children.Add(i == 0 ? _silhouette : new Border
                {
                    Height = _silhouette.Height,
                    BorderBrush = _silhouette.BorderBrush,
                    Background = _silhouette.Background,
                    BorderThickness = _silhouette.BorderThickness,
                    CornerRadius = _silhouette.CornerRadius,
                    IsHitTestVisible = false
                });
            _count = count;
            _gap = gapDip;
            _side = ""; // Reapply wall-facing chrome to every silhouette.
        }

        // Every ghost occupies one normal capsule pitch, including the real inter-slot gap.
        if (_side != side)
        {
            var left = side == DeepCapsuleSides.Left;
            var margin = EdgeCapsuleLayout.WindowChromeMargin;
            var radius = EdgeCapsuleLayout.CornerRadius;
            for (var i = 0; i < _stack.Children.Count; i++)
            {
                var pill = (Border)_stack.Children[i];
                pill.Margin = left
                    ? new Thickness(0, margin, margin, margin + (i < count - 1 ? gapDip : 0))
                    : new Thickness(margin, margin, 0, margin + (i < count - 1 ? gapDip : 0));
                pill.CornerRadius = left
                    ? new CornerRadius(0, radius, radius, 0)
                    : new CornerRadius(radius, 0, 0, radius);
                pill.BorderThickness = left
                    ? new Thickness(0, 1.5, 1.5, 1.5)
                    : new Thickness(1.5, 1.5, 0, 1.5);
            }
            _side = side;
        }

        // Widths are those of the real docked capsules. Each outline hugs its
        // respective screen edge; the shared window is only as wide as the longest.
        var chrome = EdgeCapsuleLayout.WindowChromeMargin;
        var leftAligned = side == DeepCapsuleSides.Left;
        for (var i = 0; i < count; i++)
        {
            var pill = (Border)_stack.Children[i];
            pill.Height = PaperLayoutDefaults.CapsuleHeight - 2 * chrome +
                (mergeHalo ? 2 * MergeHaloOutset : 0);
            pill.Background = mergeHalo ? Brushes.Transparent : _normalBackground;
            pill.Width = Math.Max(1, Math.Min(width, slotWidthsDip[i]) - chrome);
            pill.HorizontalAlignment = leftAligned ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        _window.Width = width;
        _window.Height = height;
        if (!_shown)
        {
            // Position before the first visible frame; no entrance animation.
            _window.Opacity = 0;
            _window.Show();
            _shown = true;
        }
        if (!WindowNative.TrySetWindowDeviceBounds(_window, bounds))
        {
            Hide();
            return;
        }
        _lastBounds = bounds;
        _lastWidths = slotWidthsDip.ToArray();
        _mergeHalo = mergeHalo;
        _window.Opacity = 1;
        // The ghost is a destination marker, never a foreground drag surface.
        // Reassert only when it becomes visible, not on every pointer sample.
        if (wasHidden || bandChanged) raiseDraggedWindow?.Invoke();
    }

    internal void Hide()
    {
        if (_shown) _window.Opacity = 0; // Instant, not a fade-out.
    }

    public void Dispose()
    {
        _window.Close();
    }
}
