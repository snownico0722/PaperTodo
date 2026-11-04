using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

// One passive WPF silhouette reused during a drag. No paper/plugin content, mouse handlers,
// separate placement logic or animation: it draws the exact slot already chosen by the policy.
internal sealed class EdgeCapsuleQueueDropGhost : IDisposable
{
    private readonly Window _window;
    private readonly StackPanel _slots;
    private int _slotCount;
    private double _gap = double.NaN;
    private bool _shown;

    internal EdgeCapsuleQueueDropGhost()
    {
        _slots = new StackPanel { Orientation = Orientation.Vertical, IsHitTestVisible = false };
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
            Content = _slots
        };
        _window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(_window);
            WindowNative.SetInputPassthrough(_window, true);
        };
    }

    internal void ShowAt(MonitorGeometry geometry, string side, double topDip, int count,
        double widthDip, double gapDip)
    {
        if (!double.IsFinite(topDip) || count < 1 || !double.IsFinite(widthDip))
        {
            Hide();
            return;
        }
        gapDip = Math.Max(0, gapDip);
        if (count != _slotCount || gapDip != _gap)
        {
            _slots.Children.Clear();
            var accent = SystemColors.HighlightColor;
            var border = new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B));
            var fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
            for (var i = 0; i < count; i++)
            {
                _slots.Children.Add(new Border
                {
                    Height = PaperLayoutDefaults.CapsuleHeight,
                    Margin = new Thickness(0, 0, 0, i < count - 1 ? gapDip : 0),
                    CornerRadius = new CornerRadius(EdgeCapsuleLayout.CornerRadius),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = border,
                    Background = fill,
                    IsHitTestVisible = false
                });
            }
            _slotCount = count;
            _gap = gapDip;
        }

        var width = Math.Clamp(widthDip, 40, geometry.LocalWorkAreaDip.Width);
        var height = count * EdgeCapsuleLayout.SlotHeight(gapDip) - gapDip;
        _window.Width = width;
        _window.Height = height;
        var deviceWidth = Math.Max(1, (int)Math.Round(width * geometry.DpiScaleX));
        var deviceHeight = Math.Max(1, (int)Math.Round(height * geometry.DpiScaleY));
        var left = side == DeepCapsuleSides.Left ? geometry.WorkArea.Left
            : geometry.WorkArea.Right - deviceWidth;
        var top = (int)Math.Round(geometry.WorkArea.Top + topDip * geometry.DpiScaleY);
        var bounds = new DeviceScreenRect(left, top, left + deviceWidth, top + deviceHeight);
        if (!_shown)
        {
            // The first HWND becomes visible only after it is positioned; never flash at (0,0).
            _window.Opacity = 0;
            _window.Show();
            _shown = true;
        }
        if (!WindowNative.TrySetWindowDeviceBounds(_window, bounds))
        {
            Hide();
            return;
        }
        _window.Opacity = 1; // Instant, not a fade-in.
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
