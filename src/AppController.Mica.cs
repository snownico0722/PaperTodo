using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    // Session policy only, not a second saved preference. AllowsTransparency cannot change
    // after HWND creation; keep live editors/undo stacks instead of rebuilding them on selection.
    internal bool UsesNativeMicaWindows { get; private set; }
    private NativeMicaBackdrop? _settingsMica;
    private bool _nativeMicaPreferenceRefreshQueued;

    // Also refresh decorated skins in fixed light/dark mode when accessibility changes.
    private void QueueNativeMicaPreferenceRefresh()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsExiting || _nativeMicaPreferenceRefreshQueued) return;
            _nativeMicaPreferenceRefreshQueued = true;
            dispatcher.BeginInvoke(new Action(() =>
            {
                _nativeMicaPreferenceRefreshQueued = false;
                if (IsExiting) return;
                // Refresh semantic foreground colors too, including fixed light/dark in HC.
                RefreshThemeSurfaces();
            }), DispatcherPriority.Background);
        }));
    }
}
