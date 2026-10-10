using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private DispatcherTimer? _todoDragEdgeScrollTimer;
    private long _todoDragEdgeScrollLastTick;
    private long _todoDragEdgeScrollEnteredAt;
    private int _todoDragEdgeScrollDirection;

    private void StartTodoDragEdgeScroll()
    {
        if (_todoDragEdgeScrollTimer == null)
        {
            _todoDragEdgeScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _todoDragEdgeScrollTimer.Tick += OnTodoDragEdgeScrollTick;
        }
        _todoDragEdgeScrollDirection = 0;
        _todoDragEdgeScrollLastTick = Stopwatch.GetTimestamp();
        _todoDragEdgeScrollTimer.Start();
    }

    private void StopTodoDragEdgeScroll()
    {
        _todoDragEdgeScrollTimer?.Stop();
        _todoDragEdgeScrollDirection = 0;
    }

    private void OnTodoDragEdgeScrollTick(object? sender, EventArgs e)
    {
        if (_todoDrag?.IsDragging != true ||
            Mouse.LeftButton != MouseButtonState.Pressed ||
            !ReferenceEquals(Mouse.Captured, this))
        {
            if (_todoDrag != null) EndTodoMouseDrag(commit: false);
            else StopTodoDragEdgeScroll();
            return;
        }

        var scroll = FindVisualAncestor<ScrollViewer>(_todoPanel);
        if (scroll == null || scroll.ScrollableHeight <= 0 || scroll.ActualHeight <= 0)
        {
            _todoDragEdgeScrollDirection = 0;
            return;
        }

        var pos = Mouse.GetPosition(scroll);
        var edge = Math.Min(AppTypography.Scale(48), scroll.ActualHeight / 4);
        var direction = 0;
        var proximity = 0d;
        if (pos.X >= 0 && pos.X <= scroll.ActualWidth &&
            pos.Y >= 0 && pos.Y <= scroll.ActualHeight)
        {
            if (pos.Y < edge)
            {
                direction = -1;
                proximity = (edge - pos.Y) / edge;
            }
            else if (pos.Y > scroll.ActualHeight - edge)
            {
                direction = 1;
                proximity = (pos.Y - scroll.ActualHeight + edge) / edge;
            }
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Clamp(
            (now - _todoDragEdgeScrollLastTick) / (double)Stopwatch.Frequency,
            0, 0.05);
        _todoDragEdgeScrollLastTick = now;
        if (direction == 0)
        {
            _todoDragEdgeScrollDirection = 0;
            return;
        }

        if (direction != _todoDragEdgeScrollDirection)
        {
            _todoDragEdgeScrollDirection = direction;
            _todoDragEdgeScrollEnteredAt = now;
        }

        var easeIn = Math.Clamp(
            (now - _todoDragEdgeScrollEnteredAt) /
            (Stopwatch.Frequency * 0.20), 0, 1);
        var speed = 28 + 520 * proximity * proximity;
        // The ScrollChanged handler updates the drop target after layout has
        // applied the new offset, rather than sampling stale row positions here.
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset +
            direction * speed * easeIn * elapsedSeconds);
    }

    private void OnTodoDragScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollToVerticalOffset is applied during layout, not necessarily when
        // it is requested. Use the actual offset change to refresh stationary
        // pointer targets, including the bottom trash area.
        if (_todoDrag?.IsDragging != true || _todoPanel == null ||
            e.VerticalChange == 0 || !ReferenceEquals(e.OriginalSource, sender))
            return;

        UpdateTodoMouseDrag(Mouse.GetPosition(_todoPanel), Mouse.GetPosition(this));
    }

    private void OnTodoDragLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_todoDrag != null && !ReferenceEquals(Mouse.Captured, this))
            EndTodoMouseDrag(commit: false);
    }

    private void OnTodoDragWindowDeactivated(object? sender, EventArgs e)
    {
        if (_todoDrag != null) EndTodoMouseDrag(commit: false);
    }
}
