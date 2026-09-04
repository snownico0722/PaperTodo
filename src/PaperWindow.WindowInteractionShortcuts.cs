using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const int VkW = 0x57;
    private bool _middleClickTitleArmed;

    private bool TryActivateAfterCapsuleInteraction()
    {
        if (!IsVisible ||
            IsExperimentalPassive ||
            _controller.FullscreenAvoidanceWindowFor(this) != System.IntPtr.Zero)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == System.IntPtr.Zero)
        {
            return false;
        }

        // The NOACTIVATE capsule click can leave WPF's IsActive/focus state stale even after the
        // real foreground moved elsewhere. Use the OS foreground HWND as the authority here, but
        // only for this explicit user-driven capsule activation path.
        if (WindowNative.ForegroundWindow != handle)
        {
            base.Activate();

            if (WindowNative.ForegroundWindow != handle)
            {
                WindowNative.TrySetForegroundWindow(handle);
            }
        }

        if (WindowNative.ForegroundWindow != handle)
        {
            return false;
        }

        Focus();
        return true;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);

        // PreviewKeyDown requires a WPF keyboard-focus target. Register at the HWND boundary too
        // so Ctrl+W still works in preview states where the paper is foreground but WPF has no
        // focused element to route a key event through.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnWindowShortcutMessage);
        }
    }

    private System.IntPtr OnWindowShortcutMessage(
        System.IntPtr hwnd,
        int msg,
        System.IntPtr wParam,
        System.IntPtr lParam,
        ref bool handled)
    {
        if (!handled &&
            msg == WmKeyDown &&
            wParam.ToInt32() == VkW &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            !_advancedInteractionLocked &&
            !_paper.IsCollapsed)
        {
            handled = true;
            CloseExpandedPaperFromWindowGesture();
        }

        return System.IntPtr.Zero;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!e.Handled && !_advancedInteractionLocked && !_paper.IsCollapsed &&
            e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
            TryCopySelectedTodosAsMarkdown())
        {
            e.Handled = true;
            return;
        }

        // Normally the HWND hook above consumes Ctrl+W before WPF creates a routed key event.
        // Keep this as a bounded fallback for input surfaces that can still route a WPF key event
        // without delivering the parent PaperWindow's WM_KEYDOWN first.
        if (e.Handled ||
            _advancedInteractionLocked ||
            _paper.IsCollapsed ||
            e.Key != Key.W ||
            Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        CloseExpandedPaperFromWindowGesture();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        // Treat the complete title bar as one middle-click close target, including title-bar
        // buttons and drag space. As with a normal button, the release must still land on it.
        _middleClickTitleArmed =
            !e.Handled &&
            !_advancedInteractionLocked &&
            !_paper.IsCollapsed &&
            _topBarHost?.IsMouseOver == true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);

        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        var shouldClose =
            _middleClickTitleArmed &&
            !e.Handled &&
            !_advancedInteractionLocked &&
            !_paper.IsCollapsed &&
            _topBarHost?.IsMouseOver == true;
        _middleClickTitleArmed = false;
        if (!shouldClose)
        {
            return;
        }

        e.Handled = true;
        CloseExpandedPaperFromWindowGesture();
    }

    private void CloseExpandedPaperFromWindowGesture()
    {
        // The title-bar close button remains the single owner of close semantics. Keyboard and
        // middle-click gestures invoke that same routed Click path instead of duplicating the
        // collapse-vs-hide policy here.
        if (_closeButton == null)
        {
            return;
        }

        _closeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
