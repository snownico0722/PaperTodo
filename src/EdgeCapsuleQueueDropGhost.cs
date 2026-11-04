using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

// One passive, ordinary-capsule-sized silhouette reused during a drag. It indicates
// the selected drop slot, not the height of all carried queue members. No animation
// or independent placement model: the controller supplies the final target slot.
internal sealed class EdgeCapsuleQueueDropGhost : IDisposable
{
    private readonly Window _window;
    private readonly Border _silhouette;
    private string _side = "";
    private bool _shown;

    internal EdgeCapsuleQueueDropGhost()
    {
        var accent = SystemColors.HighlightColor;
        _silhouette = new Border
        {
            Height = PaperLayoutDefaults.CapsuleHeight - 2 * EdgeCapsuleLayout.WindowChromeMargin,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B)),
            Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)),
            IsHitTestVisible = false
        };
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
            Content = _silhouette
        };
        _window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(_window);
            WindowNative.SetInputPassthrough(_window, true);
        };
    }

    internal void ShowAt(MonitorGeometry geometry, string side, double topDip, double widthDip)
    {
        if (!double.IsFinite(topDip) || !double.IsFinite(widthDip) || widthDip <= 0)
        {
            Hide();
            return;
        }

        // Match the real docked capsule: its HWND owns one 46-DIP slot while the
        // visible pill sits inside the vertical chrome margins, flush to the wall.
        if (_side != side)
        {
            var left = side == DeepCapsuleSides.Left;
            var margin = EdgeCapsuleLayout.WindowChromeMargin;
            var radius = EdgeCapsuleLayout.CornerRadius;
            _silhouette.Margin = left
                ? new Thickness(0, margin, margin, margin)
                : new Thickness(margin, margin, 0, margin);
            _silhouette.CornerRadius = left
                ? new CornerRadius(0, radius, radius, 0)
                : new CornerRadius(radius, 0, 0, radius);
            _silhouette.BorderThickness = left
                ? new Thickness(0, 1.5, 1.5, 1.5)
                : new Thickness(1.5, 1.5, 0, 1.5);
            _side = side;
        }

        var width = Math.Min(geometry.LocalWorkAreaDip.Width, Math.Max(34, widthDip));
        var height = PaperLayoutDefaults.CapsuleHeight;
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
