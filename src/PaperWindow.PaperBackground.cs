using System.Windows;
using System.Windows.Controls;

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
        PaperBackground.Apply(_notePaperBackgroundHost);
        RefreshTodoBackground();
    }

    private void RefreshTodoBackground()
    {
        PaperBackground.Apply(_todoPaperBackgroundHost);
    }

    private void OnPaperBackgroundHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _notePaperBackgroundHost) &&
            PaperBackground.NeedsSizeRefresh(
                _notePaperBackgroundHost.Background,
                e.NewSize.Width,
                e.NewSize.Height))
        {
            PaperBackground.Apply(_notePaperBackgroundHost);
            return;
        }

        if (ReferenceEquals(sender, _todoPaperBackgroundHost) &&
            PaperBackground.NeedsSizeRefresh(
                _todoPaperBackgroundHost.Background,
                e.NewSize.Width,
                e.NewSize.Height))
        {
            PaperBackground.Apply(_todoPaperBackgroundHost);
        }
    }
}
