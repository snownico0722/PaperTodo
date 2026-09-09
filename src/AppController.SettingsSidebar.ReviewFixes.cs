using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    // Keep this helper separate from the page builder so conflict-resolution fixes stay localized.
    // Theme/font changes eventually rebuild the settings surface; notify open plugin popups even
    // when the settings window itself is closed (for example, a system theme change).
    private void RefreshPluginPopupThemeForSettingsRefresh()
    {
        PluginPopupThemeChanged?.Invoke();
    }

    private static (double Width, double Height) SettingsSidebarSizeForWorkArea(Rect workArea)
    {
        var targetWidth = Math.Min(840, Math.Max(360, workArea.Width - 48));
        var targetHeight = Math.Min(620, Math.Max(320, workArea.Height - 48));

        // Preserve the normal minimum size when possible, but never let that minimum push the
        // window outside a tiny/high-DPI work area. Keep 16 DIPs of margin on each side.
        targetWidth = Math.Min(targetWidth, Math.Max(1, workArea.Width - 32));
        targetHeight = Math.Min(targetHeight, Math.Max(1, workArea.Height - 32));
        return (targetWidth, targetHeight);
    }

    private void RefreshSettingsSidebarAfterMonitorChange(Window window)
    {
        _ = window.Dispatcher.BeginInvoke(
            (Action)RefreshSettingsWindowContent,
            DispatcherPriority.Background);
    }
}
