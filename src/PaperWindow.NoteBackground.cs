using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static readonly bool TodoBackgroundLoadedHandlerRegistered =
        RegisterTodoBackgroundLoadedHandler();

    private Panel? _noteBackgroundHost;
    private ScrollViewer? _todoBackgroundHost;

    private static bool RegisterTodoBackgroundLoadedHandler()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnScrollViewerLoadedForPaperBackground));
        return true;
    }

    private static void OnScrollViewerLoadedForPaperBackground(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ScrollViewer viewer ||
            Window.GetWindow(viewer) is not PaperWindow window ||
            !ReferenceEquals(viewer.Content, window._todoPanel))
        {
            return;
        }

        window._todoBackgroundHost = viewer;
        window.RefreshTodoBackground();
    }

    internal void AttachNoteBackgroundHost(Panel host)
    {
        _noteBackgroundHost = host;
        RefreshNoteBackground();
    }

    internal void DetachNoteBackgroundHost(Panel host)
    {
        if (ReferenceEquals(_noteBackgroundHost, host))
        {
            _noteBackgroundHost = null;
        }
    }

    internal void RefreshPaperBackground()
    {
        _ = TodoBackgroundLoadedHandlerRegistered;
        RefreshNoteBackground();
        RefreshTodoBackground();
    }

    internal void RefreshNoteBackground()
    {
        NoteBackground.Apply(_noteBackgroundHost);
    }

    private void RefreshTodoBackground()
    {
        NoteBackground.Apply(_todoBackgroundHost);
    }
}
