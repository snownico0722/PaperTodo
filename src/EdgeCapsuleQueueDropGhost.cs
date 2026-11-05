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
    private string _side = "";
    private int _count = 1;
    private double _gap;
    private bool _shown;

    internal EdgeCapsuleQueueDropGhost()
    {
        // Resolve the PaperTodo skin at drag creation, not the Windows system accent.
        // A new ghost is created per gesture, so every gesture adopts the current theme.
        var outline = Theme.CapsuleFocusBorderBrush.Clone();
        outline.Opacity *= 0.68;
        _silhouette = new Border
        {
            Height = PaperLayoutDefaults.CapsuleHeight - 2 * EdgeCapsuleLayout.WindowChromeMargin,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1.5),
            BorderBrush = outline,
            Background = Theme.Tint(42),
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

    internal void ShowAt(MonitorGeometry geometry, string side, double topDip, double widthDip,
        int count = 1, double gapDip = 0, double? masterWidthDip = null)
    {
        if (!double.IsFinite(topDip) || !double.IsFinite(widthDip) || widthDip <= 0)
        {
            Hide();
            return;
        }

        count = Math.Max(1, count);
        gapDip = Math.Max(0, gapDip);
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

        // An independent stack starts with its own short master. Its slot height
        // stays unchanged, but its outline must not stretch to the member width.
        var width = Math.Min(geometry.LocalWorkAreaDip.Width,
            count == 1 && masterWidthDip is { } onlyMaster
                ? Math.Max(1, onlyMaster) : Math.Max(Math.Max(34, widthDip), masterWidthDip ?? 0));
        _silhouette.Width = masterWidthDip is { } masterWidth
            ? Math.Max(1, Math.Min(width, masterWidth) - EdgeCapsuleLayout.WindowChromeMargin)
            : double.NaN;
        _silhouette.HorizontalAlignment = masterWidthDip.HasValue
            ? side == DeepCapsuleSides.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
        var height = count * PaperLayoutDefaults.CapsuleHeight + (count - 1) * gapDip;
        _window.Width = width;
        _window.Height = height;
        var deviceWidth = Math.Max(1, (int)Math.Round(width * geometry.DpiScaleX));
        var deviceHeight = Math.Max(1, (int)Math.Round(height * geometry.DpiScaleY));
        var leftDevice = side == DeepCapsuleSides.Left ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - deviceWidth;
        var topDevice = (int)Math.Round(geometry.WorkArea.Top + topDip * geometry.DpiScaleY);
        var bounds = new DeviceScreenRect(leftDevice, topDevice,
            leftDevice + deviceWidth, topDevice + deviceHeight);
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
        _window.Opacity = 1;
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

