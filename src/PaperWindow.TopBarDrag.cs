using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private enum TopBarDragKind
    {
        // 保留：note-link 拖拽按钮与反馈工厂当前未接线，NoteLink 与它们成组存在。
        NoteLink,
        WindowBinding
    }

    private enum TopBarDragGhostPlacement
    {
        Centered,
        PointerOffset
    }

    private sealed class TopBarDragFeedback
    {
        public TopBarDragFeedback(Window window, object? context = null)
        {
            Window = window;
            Context = context;
        }

        public Window Window { get; }
        public object? Context { get; }
    }

    private sealed class TopBarDragBehavior
    {
        public required TopBarDragKind Kind { get; init; }
        public required Func<bool> CanBegin { get; init; }
        public required Action Started { get; init; }
        public required Func<TopBarDragFeedback> CreateFeedback { get; init; }
        public required Action<TopBarDragFeedback, DeviceScreenPoint> Moved { get; init; }
        public required Action<bool> Completed { get; init; }
        public required TopBarDragGhostPlacement GhostPlacement { get; init; }
        public double DraggingOpacity { get; init; } = 0.82;
    }

    private sealed class TopBarDragState
    {
        public TopBarDragState(
            FrameworkElement handle,
            TopBarDragBehavior behavior,
            DeviceScreenPoint startScreenPoint)
        {
            Handle = handle;
            Behavior = behavior;
            StartScreenPoint = startScreenPoint;
        }

        public FrameworkElement Handle { get; }
        public TopBarDragBehavior Behavior { get; }
        public DeviceScreenPoint StartScreenPoint { get; }
        public bool IsDragging { get; set; }
        public bool SuppressCaptureLossEnd { get; set; }
        public TopBarDragFeedback? Feedback { get; set; }
        public IntPtr FullscreenAvoidanceWindow { get; set; }
    }

    private TopBarDragState? _topBarDrag;

    private void ConfigureTopBarDragGesture(
        FrameworkElement handle,
        TopBarDragBehavior behavior)
    {
        handle.PreviewMouseLeftButtonDown +=
            (_, e) => BeginTopBarDragGesture(handle, behavior, e);
        handle.PreviewMouseMove +=
            (_, e) => UpdateTopBarDragGesture(e);
        handle.PreviewMouseLeftButtonUp +=
            (_, e) => EndTopBarDragGestureFromMouseUp(e);
        handle.LostMouseCapture += (_, _) =>
        {
            var state = _topBarDrag;
            if (state?.Handle != handle)
            {
                return;
            }

            if (state.SuppressCaptureLossEnd)
            {
                return;
            }

            // Showing a top-level feedback window can transfer capture. Reacquire it while
            // the gesture is still active; otherwise the same drag behaves differently
            // depending on which target adapter created the feedback visual.
            if (Mouse.LeftButton == MouseButtonState.Pressed &&
                handle.IsVisible &&
                handle.IsEnabled)
            {
                handle.CaptureMouse();
                return;
            }

            EndTopBarDragGesture(commit: false);
        };
    }

    private void BeginTopBarDragGesture(
        FrameworkElement handle,
        TopBarDragBehavior behavior,
        MouseButtonEventArgs e)
    {
        if (!behavior.CanBegin())
        {
            return;
        }

        EndTopBarDragGesture(commit: false);
        _topBarDrag = new TopBarDragState(
            handle,
            behavior,
            DeviceScreenPoint.FromPoint(
                PointToScreen(e.GetPosition(this))));
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void UpdateTopBarDragGesture(MouseEventArgs e)
    {
        var state = _topBarDrag;
        if (state == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Only an explicit MouseUp can commit either target adapter.
            EndTopBarDragGesture(commit: false);
            e.Handled = true;
            return;
        }

        var currentScreenPoint = DeviceScreenPoint.FromPoint(
            PointToScreen(e.GetPosition(this)));
        if (!state.IsDragging)
        {
            if (!WindowWorkAreaHelper.ExceedsDragThreshold(
                    state.StartScreenPoint,
                    currentScreenPoint,
                    this))
            {
                return;
            }

            state.IsDragging = true;
            state.Handle.Opacity = state.Behavior.DraggingOpacity;
            Mouse.OverrideCursor = Cursors.Cross;

            state.SuppressCaptureLossEnd = true;
            try
            {
                state.Behavior.Started();
                var dragFeedback = state.Behavior.CreateFeedback();
                state.Feedback = dragFeedback;
                dragFeedback.Window.Show();
                dragFeedback.Window.UpdateLayout();
                if (Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }
            catch
            {
                CloseTopBarDragFeedback(state);
                state.SuppressCaptureLossEnd = false;
                EndTopBarDragGesture(commit: false);
                e.Handled = true;
                return;
            }
            finally
            {
                state.SuppressCaptureLossEnd = false;
                if (_topBarDrag == state &&
                    Mouse.LeftButton == MouseButtonState.Pressed &&
                    !state.Handle.IsMouseCaptured)
                {
                    state.Handle.CaptureMouse();
                }
            }

            if (_topBarDrag != state || state.Feedback == null)
            {
                e.Handled = true;
                return;
            }
        }

        MoveTopBarDragFeedback(state, currentScreenPoint);
        if (_topBarDrag == state && state.Feedback is { } feedback)
        {
            state.Behavior.Moved(feedback, currentScreenPoint);
        }
        e.Handled = true;
    }

    private void EndTopBarDragGestureFromMouseUp(MouseButtonEventArgs e)
    {
        var state = _topBarDrag;
        if (state == null)
        {
            return;
        }

        if (state.IsDragging && state.Feedback is { } feedback)
        {
            var point = DeviceScreenPoint.FromPoint(
                PointToScreen(e.GetPosition(this)));
            MoveTopBarDragFeedback(state, point);
            if (_topBarDrag == state)
            {
                state.Behavior.Moved(feedback, point);
            }
        }

        EndTopBarDragGesture(commit: state.IsDragging);
        e.Handled = true;
    }

    private void EndTopBarDragGesture(
        bool commit,
        TopBarDragKind? onlyKind = null)
    {
        var state = _topBarDrag;
        if (state == null ||
            state.SuppressCaptureLossEnd ||
            (onlyKind.HasValue && state.Behavior.Kind != onlyKind.Value))
        {
            return;
        }

        _topBarDrag = null;
        if (state.Handle.IsMouseCaptured)
        {
            state.Handle.ReleaseMouseCapture();
        }

        CloseTopBarDragFeedback(state);
        state.Handle.Opacity = 1.0;
        Mouse.OverrideCursor = null;
        state.Behavior.Completed(commit && state.IsDragging);
    }

    private void MoveTopBarDragFeedback(
        TopBarDragState state,
        DeviceScreenPoint pointer)
    {
        var feedbackWindow = state.Feedback?.Window;
        if (feedbackWindow == null ||
            !WindowNative.TryGetWindowDeviceBounds(
                feedbackWindow,
                out var feedbackBounds))
        {
            return;
        }

        DeviceScreenPoint position;
        if (state.Behavior.GhostPlacement ==
            TopBarDragGhostPlacement.Centered)
        {
            position = new DeviceScreenPoint(
                pointer.X - feedbackBounds.Width / 2.0,
                pointer.Y - feedbackBounds.Height / 2.0);
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(state.Handle);
            position = new DeviceScreenPoint(
                pointer.X + 14 * Math.Max(1, dpi.DpiScaleX),
                pointer.Y + 18 * Math.Max(1, dpi.DpiScaleY));
        }

        try
        {
            _ = WindowNative.TryMoveWindowDevicePosition(
                feedbackWindow,
                position);
            RefreshTopBarDragFeedbackTopmost(state);
        }
        catch
        {
            // Feedback windows are disposable and may close during nested capture teardown.
        }
    }

    private void RefreshTopBarDragFeedbackTopmost()
    {
        if (_topBarDrag is { } state)
        {
            RefreshTopBarDragFeedbackTopmost(state);
        }
    }

    private void RefreshTopBarDragFeedbackTopmost(
        TopBarDragState state)
    {
        var feedbackWindow = state.Feedback?.Window;
        if (feedbackWindow == null)
        {
            return;
        }

        var avoidanceWindow =
            _controller.FullscreenAvoidanceWindowFor(feedbackWindow);
        if (state.FullscreenAvoidanceWindow == avoidanceWindow)
        {
            return;
        }

        state.FullscreenAvoidanceWindow = avoidanceWindow;
        var topmost = avoidanceWindow == IntPtr.Zero;
        feedbackWindow.Topmost = topmost;
        if (feedbackWindow.IsVisible)
        {
            WindowNative.ApplyTopmostZOrder(
                feedbackWindow,
                topmost,
                avoidanceWindow);
        }
    }

    private static void CloseTopBarDragFeedback(TopBarDragState state)
    {
        var feedbackWindow = state.Feedback?.Window;
        state.Feedback = null;
        if (feedbackWindow == null)
        {
            return;
        }

        try
        {
            feedbackWindow.Close();
        }
        catch
        {
            // Drag feedback is disposable UI.
        }
    }

    private Window CreateTopBarDragFeedbackWindow(UIElement content)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = content
        };
        window.SourceInitialized += (_, _) =>
        {
            WindowNative.ApplyNoActivateStyle(window);
            WindowNative.SetInputPassthrough(window, enabled: true);
        };
        AppTypography.ApplyTextRendering(window);
        return window;
    }
}
