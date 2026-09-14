using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Point? _findDragStart;
    private Point _findDragPopupStart;

    private void OnBuiltInFindDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!IsBuiltInFindOpen || _findHost == null || _findPopup == null)
        {
            return;
        }

        // The grip, count and surrounding space can drag; input selection and buttons keep
        // their normal gestures, including clicks on their otherwise empty padding.
        for (var source = e.OriginalSource as DependencyObject;
             source != null && !ReferenceEquals(source, _findHost);
             source = GetSafeParent(source))
        {
            if (source is TextBoxBase or ButtonBase)
            {
                return;
            }
        }

        var start = e.GetPosition(_paperChrome);
        var popupStart = _paperChrome.PointFromScreen(_findHost.PointToScreen(new Point()));
        if (!_findHost.CaptureMouse())
        {
            return;
        }

        _findDragStart = start;
        _findDragPopupStart = popupStart;
        _findInput?.Focus();
        e.Handled = true;
    }

    private void OnBuiltInFindDragMove(object sender, MouseEventArgs e)
    {
        if (_findDragStart is not Point start || _findPopup == null)
        {
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndBuiltInFindDrag();
            return;
        }

        // Use the stationary paper's coordinates, starting at the popup's actual on-screen
        // position after WPF clamping. Moving the placement rectangle also lets WPF choose
        // the monitor at the dragged position, instead of pinning it to the owner's monitor.
        var delta = e.GetPosition(_paperChrome) - start;
        _findPopup.PlacementRectangle = new Rect(_findDragPopupStart + delta, new Size());
        e.Handled = true;
    }

    private void EndBuiltInFindDrag()
    {
        _findDragStart = null;
        if (_findHost?.IsMouseCaptured == true)
        {
            _findHost.ReleaseMouseCapture();
        }
    }
}
