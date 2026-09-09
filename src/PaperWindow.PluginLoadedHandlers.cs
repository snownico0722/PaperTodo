using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private static void EnsurePluginLoadedRefreshHandler(
        ref bool registered,
        Action<PaperWindow> refresh)
    {
        if (registered)
        {
            return;
        }

        registered = true;
        EventManager.RegisterClassHandler(
            typeof(PaperWindow),
            LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is PaperWindow window && !window.IsClosed)
                {
                    refresh(window);
                }
            }));
    }
}
