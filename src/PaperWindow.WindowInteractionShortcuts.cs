using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private FrameworkElement? _windowCloseTitleBar;
    private bool _middleClickTitleArmed;

    private bool TryHandleWindowCloseKey(int message, IntPtr key, IntPtr keyData)
    {
        if (message != WmKeyDown || key.ToInt32() != 0x57 ||
            Keyboard.Modifiers != ModifierKeys.Control ||
            _windowLifecycle != PaperWindowLifecycleState.Alive || _paper.IsCollapsed) return false;
        // A held shortcut must not continue closing other papers after the first surface leaves.
        if ((keyData.ToInt64() & (1L << 30)) == 0) CloseExpandedPaperFromWindowGesture();
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key != Key.W || Keyboard.Modifiers != ModifierKeys.Control ||
            _windowLifecycle != PaperWindowLifecycleState.Alive || _paper.IsCollapsed) return;
        e.Handled = true;
        if (!e.IsRepeat) CloseExpandedPaperFromWindowGesture();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        _middleClickTitleArmed = !e.Handled && !_paper.IsCollapsed &&
            _windowLifecycle == PaperWindowLifecycleState.Alive && _windowCloseTitleBar?.IsMouseOver == true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        var close = _middleClickTitleArmed && !e.Handled && !_paper.IsCollapsed &&
            _windowLifecycle == PaperWindowLifecycleState.Alive && _windowCloseTitleBar?.IsMouseOver == true;
        _middleClickTitleArmed = false;
        if (!close) return;
        e.Handled = true;
        CloseExpandedPaperFromWindowGesture();
    }

    private void CloseExpandedPaperFromWindowGesture()
    {
        // Keep the title-bar button as the only collapse-versus-hide authority. These gestures
        // never call DeletePaper, remove a model, or invent a second close/save path.
        _closeButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
