using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private Panel? _notePaperBackgroundHost;
    private ScrollViewer? _todoPaperBackgroundHost;

    internal void AttachPaperBackgroundHost(Panel host)
    {
        _notePaperBackgroundHost = host;
        RefreshPaperBackground();
    }

    internal void DetachPaperBackgroundHost(Panel host)
    {
        if (ReferenceEquals(_notePaperBackgroundHost, host))
        {
            _notePaperBackgroundHost = null;
        }
    }

    internal void AttachTodoBackgroundHost(ScrollViewer host)
    {
        _todoPaperBackgroundHost = host;
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
}
