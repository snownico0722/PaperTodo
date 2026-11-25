using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Panel? _notePaperBackgroundHost;
    private ScrollViewer? _todoPaperBackgroundHost;

    internal void AttachPaperBackgroundHost(Panel host)
    {
        if (_notePaperBackgroundHost != null)
        {
            _notePaperBackgroundHost.SizeChanged -= OnPaperBackgroundHostSizeChanged;
        }

        _notePaperBackgroundHost = host;
        _notePaperBackgroundHost.SizeChanged += OnPaperBackgroundHostSizeChanged;
        RefreshPaperBackground();
    }

    internal void DetachPaperBackgroundHost(Panel host)
    {
        if (ReferenceEquals(_notePaperBackgroundHost, host))
        {
            _notePaperBackgroundHost.SizeChanged -= OnPaperBackgroundHostSizeChanged;
            _notePaperBackgroundHost = null;
        }
    }

    internal void AttachTodoBackgroundHost(ScrollViewer host)
    {
        if (_todoPaperBackgroundHost != null)
        {
            _todoPaperBackgroundHost.SizeChanged -= OnPaperBackgroundHostSizeChanged;
        }

        _todoPaperBackgroundHost = host;
        _todoPaperBackgroundHost.SizeChanged += OnPaperBackgroundHostSizeChanged;
        RefreshTodoBackground();
    }

    internal void RefreshPaperBackground()
    {
        if (_notePaperBackgroundHost is { } noteHost)
            noteHost.Background = CreatePaperBackgroundBrush(noteHost);
        RefreshTodoBackground();
    }

    private void RefreshTodoBackground()
    {
        if (_todoPaperBackgroundHost is { } todoHost)
            todoHost.Background = CreatePaperBackgroundBrush(todoHost);
    }

    private Brush CreatePaperBackgroundBrush(FrameworkElement host)
    {
        var state = _controller.State;
        return (Brush?)PaperBackground.CreateBrush(
            state.PaperBackgroundBlendWithTheme,
            state.PaperBackgroundLayout,
            state.PaperBackgroundStretch,
            host.ActualWidth,
            host.ActualHeight) ?? Brushes.Transparent;
    }

    private void OnPaperBackgroundHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_notePaperBackgroundHost is { } noteHost && ReferenceEquals(sender, noteHost) &&
            PaperBackground.NeedsSizeRefresh(
                noteHost.Background,
                e.NewSize.Width,
                e.NewSize.Height))
        {
            noteHost.Background = CreatePaperBackgroundBrush(noteHost);
            return;
        }

        if (_todoPaperBackgroundHost is { } todoHost && ReferenceEquals(sender, todoHost) &&
            PaperBackground.NeedsSizeRefresh(
                todoHost.Background,
                e.NewSize.Width,
                e.NewSize.Height))
        {
            todoHost.Background = CreatePaperBackgroundBrush(todoHost);
        }
    }
}
